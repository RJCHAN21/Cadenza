using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop.Services;

public sealed partial class MusicXmlImporter
{
    private static ParsedPart ParsePart(
        XElement partElement,
        string partId,
        string partName,
        ICollection<ScoreValidationWarning> warnings)
    {
        var measures = new List<ParsedMeasure>();
        var notes = new List<ScoreNote>();
        var rests = new List<ScoreRest>();
        var marks = new List<ScoreMark>();
        var tempos = new List<ParsedTempoChange>();
        var meters = new List<ParsedMeterChange>();

        var sourceBeat = 0d;
        var divisions = 1;
        var meter = (Beats: 4, BeatType: 4);
        var measureIndex = 0;
        foreach (var measureElement in Children(partElement, "measure"))
        {
            var parsed = ParseMeasure(
                measureElement,
                partId,
                partName,
                measureIndex,
                sourceBeat,
                divisions,
                meter,
                warnings);
            divisions = parsed.Divisions;
            meter = parsed.EndingMeter;
            measures.Add(parsed);
            notes.AddRange(parsed.Notes);
            rests.AddRange(parsed.Rests);
            marks.AddRange(parsed.Marks);
            tempos.AddRange(parsed.TempoChanges);
            meters.AddRange(parsed.MeterChanges);
            sourceBeat += parsed.DurationBeats;
            measureIndex++;
        }

        return new ParsedPart(
            partId,
            partName,
            measures,
            notes,
            rests,
            marks,
            tempos,
            meters,
            sourceBeat);
    }

    private static ParsedMeasure ParseMeasure(
        XElement measure,
        string partId,
        string partName,
        int sourceMeasureIndex,
        double measureStartBeat,
        int inheritedDivisions,
        (int Beats, int BeatType) inheritedMeter,
        ICollection<ScoreValidationWarning> warnings)
    {
        var number = (string?)measure.Attribute("number")
                     ?? (sourceMeasureIndex + 1).ToString(CultureInfo.InvariantCulture);
        var displayMeasure = MeasureNumberOf(number, sourceMeasureIndex + 1);
        var attributes = Descendant(measure, "attributes");
        var divisions = Math.Max(1, ParseInt(Value(Descendant(attributes, "divisions"))) ?? inheritedDivisions);

        var meter = inheritedMeter;
        var meterChanges = new List<ParsedMeterChange>();
        var timeElement = Descendant(attributes, "time");
        var parsedBeats = ParseInt(Value(Descendant(timeElement, "beats")));
        var parsedBeatType = ParseInt(Value(Descendant(timeElement, "beat-type")));
        if (parsedBeats is > 0 && parsedBeatType is > 0)
        {
            meter = (parsedBeats.Value, parsedBeatType.Value);
            meterChanges.Add(new ParsedMeterChange(
                measureStartBeat,
                meter.Beats,
                meter.BeatType,
                number,
                sourceMeasureIndex));
        }

        var notes = new List<ScoreNote>();
        var rests = new List<ScoreRest>();
        var marks = new List<ScoreMark>();
        var tempos = new List<ParsedTempoChange>();
        var cursor = 0;
        var maximumCursor = 0;
        var lastStarts = new Dictionary<(string Voice, int Staff), int>();
        var noteCount = 0;
        var restCount = 0;
        var chordCount = 0;
        var lyricCount = 0;
        var staffOne = 0;
        var staffTwo = 0;

        foreach (var element in measure.Elements())
        {
            switch (element.Name.LocalName.ToLowerInvariant())
            {
                case "note":
                {
                    noteCount++;
                    var durationDivisions = Math.Max(0, ParseInt(Value(Descendant(element, "duration"))) ?? 0);
                    var voice = Value(Descendant(element, "voice")) ?? "1";
                    var staff = ParseInt(Value(Descendant(element, "staff"))) ?? 0;
                    var key = (voice, staff);
                    var isChord = Descendant(element, "chord") is not null;
                    if (isChord)
                        chordCount++;

                    var start = isChord && lastStarts.TryGetValue(key, out var priorStart)
                        ? priorStart
                        : cursor;
                    if (!isChord)
                        lastStarts[key] = start;

                    var isRest = Descendant(element, "rest") is not null;
                    if (isRest)
                        restCount++;
                    lyricCount += Descendants(element, "lyric").Count();
                    if (staff == 1)
                        staffOne++;
                    else if (staff == 2)
                        staffTwo++;

                    var sourceOnset = measureStartBeat + start / (double)divisions;
                    var sourceDuration = Math.Max(0.05, durationDivisions / (double)divisions);
                    var noteType = Value(Descendant(element, "type"))
                                   ?? InferNoteType(durationDivisions, divisions);
                    var dotCount = Descendants(element, "dot").Count();
                    var pitch = ParsePitch(element);
                    var notations = Descendant(element, "notations");
                    var articulations = Descendants(notations, "articulations")
                        .SelectMany(parent => parent.Elements())
                        .ToArray();
                    var isStaccato = articulations.Any(item =>
                        string.Equals(item.Name.LocalName, "staccato", StringComparison.OrdinalIgnoreCase));
                    var isAccent = articulations.Any(item =>
                        string.Equals(item.Name.LocalName, "accent", StringComparison.OrdinalIgnoreCase));
                    var isTenuto = articulations.Any(item =>
                        string.Equals(item.Name.LocalName, "tenuto", StringComparison.OrdinalIgnoreCase));
                    var isSlurred = Descendants(notations, "slur").Any();

                    if (!isRest && pitch is not null)
                    {
                        var sourceNoteId = (string?)element.Attribute("id") ?? string.Empty;
                        notes.Add(new ScoreNote(
                            pitch.MidiNoteNumber,
                            sourceOnset,
                            sourceDuration,
                            staff,
                            number,
                            pitch.Step,
                            pitch.Octave,
                            pitch.Alter,
                            noteType,
                            dotCount,
                            voice,
                            Value(Descendant(element, "stem")) ?? string.Empty,
                            isChord,
                            Descendants(element, "beam")
                                .Select(beam => beam.Value.Trim().ToLowerInvariant())
                                .ToArray(),
                            Descendants(element, "lyric")
                                .Select(lyric => Value(Descendant(lyric, "text")))
                                .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)),
                            HasTieType(element, "start"),
                            HasTieType(element, "stop"),
                            0,
                            isStaccato,
                            isAccent,
                            isTenuto,
                            isSlurred,
                            partId,
                            sourceMeasureIndex,
                            sourceOnset,
                            sourceNoteId,
                            string.IsNullOrWhiteSpace(sourceNoteId) ? [] : [sourceNoteId]));

                        foreach (var articulation in articulations)
                        {
                            marks.Add(new ScoreMark(
                                sourceOnset,
                                staff,
                                number,
                                ScoreMarkKind.Articulation,
                                articulation.Name.LocalName,
                                partId,
                                sourceMeasureIndex,
                                sourceOnset));
                        }
                    }
                    else if (isRest)
                    {
                        rests.Add(new ScoreRest(
                            sourceOnset,
                            sourceDuration,
                            staff,
                            number,
                            noteType,
                            dotCount,
                            voice,
                            partId,
                            sourceMeasureIndex,
                            sourceOnset));
                    }

                    maximumCursor = Math.Max(maximumCursor, start + durationDivisions);
                    if (!isChord)
                    {
                        cursor += durationDivisions;
                        maximumCursor = Math.Max(maximumCursor, cursor);
                    }
                    break;
                }

                case "direction":
                {
                    var offset = ParseInt(Value(Descendant(element, "offset"))) ?? 0;
                    var directionCursor = Math.Max(0, cursor + offset);
                    var sourceOnset = measureStartBeat + directionCursor / (double)divisions;
                    var staff = ParseInt(Value(Descendant(element, "staff"))) ?? 1;

                    foreach (var dynamics in Descendants(element, "dynamics"))
                    {
                        foreach (var dynamic in dynamics.Elements())
                        {
                            marks.Add(new ScoreMark(
                                sourceOnset,
                                staff,
                                number,
                                ScoreMarkKind.Dynamic,
                                dynamic.Name.LocalName,
                                partId,
                                sourceMeasureIndex,
                                sourceOnset));
                        }
                    }

                    foreach (var pedal in Descendants(element, "pedal"))
                    {
                        marks.Add(new ScoreMark(
                            sourceOnset,
                            staff,
                            number,
                            ScoreMarkKind.Pedal,
                            (string?)pedal.Attribute("type") ?? "start",
                            partId,
                            sourceMeasureIndex,
                            sourceOnset));
                    }

                    var words = Descendants(element, "words")
                        .Select(Value)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                    if (!string.IsNullOrWhiteSpace(words))
                    {
                        marks.Add(new ScoreMark(
                            sourceOnset,
                            staff,
                            number,
                            ScoreMarkKind.Direction,
                            words,
                            partId,
                            sourceMeasureIndex,
                            sourceOnset));
                    }

                    var tempo = TempoFromDirection(element);
                    if (tempo is > 0)
                        tempos.Add(new ParsedTempoChange(sourceOnset, tempo.Value, number, sourceMeasureIndex));
                    break;
                }

                case "backup":
                    cursor = Math.Max(0, cursor - Math.Max(0, ParseInt(Value(Descendant(element, "duration"))) ?? 0));
                    break;

                case "forward":
                    cursor += Math.Max(0, ParseInt(Value(Descendant(element, "duration"))) ?? 0);
                    maximumCursor = Math.Max(maximumCursor, cursor);
                    break;
            }
        }

        var rawDuration = maximumCursor / (double)divisions;
        var nominalDuration = Math.Max(0.25, meter.Beats * 4d / Math.Max(1, meter.BeatType));
        var implicitMeasure = string.Equals(
            (string?)measure.Attribute("implicit"),
            "yes",
            StringComparison.OrdinalIgnoreCase);
        var duration = rawDuration > BeatEpsilon ? rawDuration : nominalDuration;
        if (!implicitMeasure && rawDuration > nominalDuration + 0.01)
        {
            warnings.Add(new ScoreValidationWarning(
                "measure-overflow",
                $"Part {partName}, measure {number} serializes {rawDuration:0.###} beats in a {meter.Beats}/{meter.BeatType} bar. " +
                "Playback is bounded at the written barline; assessment is disabled until the source voices are corrected.",
                displayMeasure,
                displayMeasure,
                true));
            duration = nominalDuration;
        }

        var boundedNotes = new List<ScoreNote>();
        foreach (var note in notes)
        {
            var localStart = note.SourceOnsetBeats - measureStartBeat;
            if (localStart >= duration - BeatEpsilon)
            {
                warnings.Add(new ScoreValidationWarning(
                    "event-past-barline",
                    $"Part {partName}, measure {number} contains a note after the written barline. It was excluded from playback.",
                    displayMeasure,
                    displayMeasure,
                    true));
                continue;
            }

            var boundedDuration = Math.Min(note.DurationBeats, Math.Max(0.05, duration - localStart));
            boundedNotes.Add(note with { DurationBeats = boundedDuration });
        }

        var boundedRests = new List<ScoreRest>();
        foreach (var rest in rests)
        {
            var localStart = rest.SourceOnsetBeats - measureStartBeat;
            if (localStart >= duration - BeatEpsilon)
                continue;
            boundedRests.Add(rest with
            {
                DurationBeats = Math.Min(rest.DurationBeats, Math.Max(0.05, duration - localStart))
            });
        }

        var repeatDirectives = new List<ParsedRepeatDirective>();
        var endingDirectives = new List<ParsedEndingDirective>();
        foreach (var barline in Children(measure, "barline"))
        {
            var location = ParseBarlineLocation((string?)barline.Attribute("location"));
            foreach (var repeat in Children(barline, "repeat"))
            {
                var direction = (string?)repeat.Attribute("direction") ?? string.Empty;
                var times = ParseInt((string?)repeat.Attribute("times")) ?? 2;
                repeatDirectives.Add(new ParsedRepeatDirective(direction, times, location));
            }

            foreach (var ending in Children(barline, "ending"))
            {
                endingDirectives.Add(new ParsedEndingDirective(
                    (string?)ending.Attribute("number") ?? string.Empty,
                    (string?)ending.Attribute("type") ?? string.Empty,
                    location));
            }
        }

        var boundedMarks = marks
            .Where(mark => mark.SourceOnsetBeats - measureStartBeat <= duration + BeatEpsilon)
            .ToArray();
        var boundedTempos = tempos
            .Where(change => change.SourceBeat - measureStartBeat <= duration + BeatEpsilon)
            .ToArray();
        if (boundedTempos.Length != tempos.Count)
        {
            warnings.Add(new ScoreValidationWarning(
                "tempo-past-barline",
                $"Part {partName}, measure {number} contains a tempo event after the written barline. It was excluded.",
                displayMeasure,
                displayMeasure,
                true));
        }

        var firstTempo = boundedTempos.FirstOrDefault()?.Bpm;
        return new ParsedMeasure(
            new MeasureSummary(
                number,
                noteCount,
                restCount,
                chordCount,
                lyricCount,
                staffOne,
                staffTwo,
                firstTempo is > 0 ? $"{firstTempo:0.##} BPM" : null,
                measureStartBeat,
                duration),
            boundedNotes,
            boundedRests,
            boundedMarks,
            boundedTempos,
            meterChanges,
            divisions,
            duration,
            repeatDirectives,
            endingDirectives,
            [],
            meter);
    }

    private static BarlineLocation ParseBarlineLocation(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "left" => BarlineLocation.Left,
            "middle" => BarlineLocation.Middle,
            _ => BarlineLocation.Right
        };

    private static bool HasTieType(XElement note, string type) =>
        Descendants(note, "tie").Any(tie =>
            string.Equals((string?)tie.Attribute("type"), type, StringComparison.OrdinalIgnoreCase)) ||
        Descendants(note, "tied").Any(tied =>
            string.Equals((string?)tied.Attribute("type"), type, StringComparison.OrdinalIgnoreCase));

    private static double? TempoFromDirection(XElement direction)
    {
        var soundTempo = Descendants(direction, "sound")
            .Select(sound => (string?)sound.Attribute("tempo"))
            .FirstOrDefault(value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        if (double.TryParse(soundTempo, NumberStyles.Float, CultureInfo.InvariantCulture, out var tempo) && tempo > 0)
            return tempo;

        var metronomeTempo = Descendants(direction, "metronome")
            .Select(metronome => Value(Descendant(metronome, "per-minute")))
            .FirstOrDefault(value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        return double.TryParse(metronomeTempo, NumberStyles.Float, CultureInfo.InvariantCulture, out tempo) && tempo > 0
            ? tempo
            : null;
    }
}
