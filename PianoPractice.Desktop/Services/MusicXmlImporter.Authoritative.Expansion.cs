using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop.Services;

public sealed partial class MusicXmlImporter
{
    private static IReadOnlyList<ScoreNote> ExpandNotes(
        IReadOnlyList<ScoreNote> notes,
        IReadOnlyList<ScoreMeasureOccurrence> occurrences,
        ICollection<ScoreValidationWarning> warnings)
    {
        var byMeasure = notes
            .GroupBy(note => note.SourceMeasureIndex)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var result = new List<ScoreNote>();
        foreach (var occurrence in occurrences)
        {
            foreach (var note in byMeasure.GetValueOrDefault(occurrence.SourceMeasureIndex, []))
            {
                var localStart = note.SourceOnsetBeats - occurrence.SourceStartBeat;
                if (localStart < -BeatEpsilon || localStart >= occurrence.DurationBeats - BeatEpsilon)
                    continue;
                var duration = Math.Min(note.DurationBeats, Math.Max(0.05, occurrence.DurationBeats - localStart));
                result.Add(note with
                {
                    OnsetBeats = occurrence.PerformanceStartBeat + localStart,
                    DurationBeats = duration,
                    PerformanceOccurrence = occurrence.OccurrenceIndex
                });
            }
        }

        if (result.Any(note => note.OnsetBeats < -BeatEpsilon))
            warnings.Add(new ScoreValidationWarning(
                "negative-performance-event",
                "The expanded performance contains a negative note position.",
                1,
                1,
                true));
        return result;
    }

    private static IReadOnlyList<ScoreRest> ExpandRests(
        IReadOnlyList<ScoreRest> rests,
        IReadOnlyList<ScoreMeasureOccurrence> occurrences,
        ICollection<ScoreValidationWarning> warnings)
    {
        var byMeasure = rests
            .GroupBy(rest => rest.SourceMeasureIndex)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var result = new List<ScoreRest>();
        foreach (var occurrence in occurrences)
        {
            foreach (var rest in byMeasure.GetValueOrDefault(occurrence.SourceMeasureIndex, []))
            {
                var localStart = rest.SourceOnsetBeats - occurrence.SourceStartBeat;
                if (localStart < -BeatEpsilon || localStart >= occurrence.DurationBeats - BeatEpsilon)
                    continue;
                result.Add(rest with
                {
                    OnsetBeats = occurrence.PerformanceStartBeat + localStart,
                    DurationBeats = Math.Min(rest.DurationBeats, Math.Max(0.05, occurrence.DurationBeats - localStart)),
                    PerformanceOccurrence = occurrence.OccurrenceIndex
                });
            }
        }

        if (result.Any(rest => rest.OnsetBeats < -BeatEpsilon))
            warnings.Add(new ScoreValidationWarning(
                "negative-performance-rest",
                "The expanded performance contains a negative rest position.",
                1,
                1,
                true));
        return result;
    }

    private static IReadOnlyList<ScoreMark> ExpandMarks(
        IReadOnlyList<ScoreMark> marks,
        IReadOnlyList<ScoreMeasureOccurrence> occurrences)
    {
        var byMeasure = marks
            .GroupBy(mark => mark.SourceMeasureIndex)
            .ToDictionary(group => group.Key, group => group.ToArray());
        return occurrences.SelectMany(occurrence =>
                byMeasure.GetValueOrDefault(occurrence.SourceMeasureIndex, [])
                    .Where(mark =>
                    {
                        var local = mark.SourceOnsetBeats - occurrence.SourceStartBeat;
                        return local >= -BeatEpsilon && local <= occurrence.DurationBeats + BeatEpsilon;
                    })
                    .Select(mark => mark with
                    {
                        OnsetBeats = occurrence.PerformanceStartBeat + Math.Clamp(
                            mark.SourceOnsetBeats - occurrence.SourceStartBeat,
                            0,
                            occurrence.DurationBeats),
                        PerformanceOccurrence = occurrence.OccurrenceIndex
                    }))
            .ToArray();
    }

    private static IReadOnlyList<ScoreNote> MergeTiedNotes(
        IReadOnlyList<ScoreNote> notes,
        ICollection<ScoreValidationWarning> warnings,
        ref int tiePairCount)
    {
        var merged = new List<ScoreNote>();
        var active = new Dictionary<(string Part, int Midi, int Staff, string Voice), int>();
        foreach (var note in notes
                     .OrderBy(note => note.OnsetBeats)
                     .ThenBy(note => note.PartId, StringComparer.Ordinal)
                     .ThenBy(note => note.StaffNumber)
                     .ThenBy(note => note.MidiNoteNumber))
        {
            var key = (note.PartId, note.MidiNoteNumber, note.StaffNumber, note.Voice);
            if (note.TieStop && active.TryGetValue(key, out var activeIndex))
            {
                var start = merged[activeIndex];
                var contiguousInPerformance = Math.Abs(
                    note.OnsetBeats - (start.OnsetBeats + start.DurationBeats)) <= 0.01;
                var adjacentInWrittenScore = note.SourceMeasureIndex == start.SourceMeasureIndex ||
                                             note.SourceMeasureIndex == start.SourceMeasureIndex + 1;
                if (contiguousInPerformance && adjacentInWrittenScore)
                {
                    var endBeat = Math.Max(
                        start.OnsetBeats + start.DurationBeats,
                        note.OnsetBeats + note.DurationBeats);
                    var ids = (start.TiedSourceNoteIds ?? [])
                        .Concat(note.TiedSourceNoteIds ?? [])
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    merged[activeIndex] = start with
                    {
                        DurationBeats = endBeat - start.OnsetBeats,
                        TiedSourceNoteIds = ids
                    };
                    tiePairCount++;
                    if (!note.TieStart)
                        active.Remove(key);
                    continue;
                }

                active.Remove(key);
            }

            var outputIndex = merged.Count;
            merged.Add(note);
            if (note.TieStart)
                active[key] = outputIndex;
            else if (note.TieStop)
            {
                var measure = MeasureNumberOf(note.MeasureNumber, note.SourceMeasureIndex + 1);
                warnings.Add(new ScoreValidationWarning(
                    "unmatched-tie-stop",
                    $"An unmatched tie stop was found in part {note.PartId} at measure {note.MeasureNumber}.",
                    measure,
                    measure,
                    true));
            }
        }

        foreach (var activeIndex in active.Values.Distinct())
        {
            var note = merged[activeIndex];
            var measure = MeasureNumberOf(note.MeasureNumber, note.SourceMeasureIndex + 1);
            warnings.Add(new ScoreValidationWarning(
                "unmatched-tie-start",
                $"An unmatched tie start was found in part {note.PartId} at measure {note.MeasureNumber}.",
                measure,
                measure,
                true));
        }
        return merged;
    }

    private static IReadOnlyList<ScoreTempoChange> ExpandTempoChanges(
        IReadOnlyList<ParsedMeasure> measures,
        IReadOnlyList<ParsedTempoChange> sourceChanges,
        IReadOnlyList<ScoreMeasureOccurrence> occurrences)
    {
        var ordered = sourceChanges
            .Where(change => change.Bpm > 0)
            .OrderBy(change => change.SourceBeat)
            .ToArray();
        var fallback = ordered.FirstOrDefault()?.Bpm ?? 120d;
        var result = new List<ScoreTempoChange>();
        foreach (var occurrence in occurrences)
        {
            var sourceStart = occurrence.SourceStartBeat;
            var sourceEnd = sourceStart + occurrence.DurationBeats;
            var active = ordered.LastOrDefault(change => change.SourceBeat <= sourceStart + BeatEpsilon)?.Bpm
                         ?? fallback;
            result.Add(new ScoreTempoChange(
                occurrence.PerformanceStartBeat,
                active,
                occurrence.MeasureNumber,
                occurrence.OccurrenceIndex));

            foreach (var change in ordered.Where(change =>
                         change.SourceBeat > sourceStart + BeatEpsilon &&
                         change.SourceBeat < sourceEnd - BeatEpsilon))
            {
                result.Add(new ScoreTempoChange(
                    occurrence.PerformanceStartBeat + change.SourceBeat - sourceStart,
                    change.Bpm,
                    occurrence.MeasureNumber,
                    occurrence.OccurrenceIndex));
            }
        }

        return result
            .OrderBy(change => change.PerformanceBeat)
            .GroupBy(change => Math.Round(change.PerformanceBeat, 6))
            .Select(group => group.Last())
            .ToArray();
    }

    private static IReadOnlyList<ScoreMeterChange> ExpandMeterChanges(
        IReadOnlyList<ParsedMeasure> measures,
        IReadOnlyList<ParsedMeterChange> sourceChanges,
        IReadOnlyList<ScoreMeasureOccurrence> occurrences)
    {
        var ordered = sourceChanges
            .Where(change => change.Beats > 0 && change.BeatType > 0)
            .OrderBy(change => change.SourceBeat)
            .ToArray();
        var fallback = ordered.FirstOrDefault()
                       ?? new ParsedMeterChange(0, 4, 4, measures[0].Number, 0);
        var result = new List<ScoreMeterChange>();
        foreach (var occurrence in occurrences)
        {
            var sourceStart = occurrence.SourceStartBeat;
            var sourceEnd = sourceStart + occurrence.DurationBeats;
            var active = ordered.LastOrDefault(change => change.SourceBeat <= sourceStart + BeatEpsilon)
                         ?? fallback;
            result.Add(new ScoreMeterChange(
                occurrence.PerformanceStartBeat,
                active.Beats,
                active.BeatType,
                occurrence.MeasureNumber,
                occurrence.OccurrenceIndex));

            foreach (var change in ordered.Where(change =>
                         change.SourceBeat > sourceStart + BeatEpsilon &&
                         change.SourceBeat < sourceEnd - BeatEpsilon))
            {
                result.Add(new ScoreMeterChange(
                    occurrence.PerformanceStartBeat + change.SourceBeat - sourceStart,
                    change.Beats,
                    change.BeatType,
                    occurrence.MeasureNumber,
                    occurrence.OccurrenceIndex));
            }
        }

        return result
            .OrderBy(change => change.PerformanceBeat)
            .GroupBy(change => Math.Round(change.PerformanceBeat, 6))
            .Select(group => group.Last())
            .ToArray();
    }

    private static void ValidateExpandedPlan(
        IReadOnlyList<ScoreMeasureOccurrence> occurrences,
        IReadOnlyList<ScoreNote> notes,
        ICollection<ScoreValidationWarning> warnings)
    {
        var expectedStart = 0d;
        foreach (var occurrence in occurrences)
        {
            if (Math.Abs(occurrence.PerformanceStartBeat - expectedStart) > 0.001)
                throw new InvalidDataException("The expanded performance plan contains a gap or overlap.");
            expectedStart += occurrence.DurationBeats;
        }

        foreach (var note in notes)
        {
            if (note.PerformanceOccurrence < 0 || note.PerformanceOccurrence >= occurrences.Count)
                throw new InvalidDataException("An expanded note references an invalid performance occurrence.");
            var occurrence = occurrences[note.PerformanceOccurrence];
            var end = occurrence.PerformanceStartBeat + occurrence.DurationBeats;
            if (note.OnsetBeats < occurrence.PerformanceStartBeat - BeatEpsilon ||
                note.OnsetBeats >= end - BeatEpsilon)
            {
                warnings.Add(new ScoreValidationWarning(
                    "event-occurrence-mismatch",
                    $"A note in measure {note.MeasureNumber} lies outside its performance occurrence.",
                    MeasureNumberOf(note.MeasureNumber, note.SourceMeasureIndex + 1),
                    MeasureNumberOf(note.MeasureNumber, note.SourceMeasureIndex + 1),
                    true));
            }
        }
    }
}
