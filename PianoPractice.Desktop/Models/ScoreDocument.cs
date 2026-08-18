namespace PianoPractice.Desktop.Models;

public sealed class ScoreDocument
{
    private const double BeatEpsilon = 0.0001;

    public required string SourcePath { get; init; }
    public required string Title { get; init; }
    public string ComposerOrCreator { get; init; } = "Unknown creator";
    public string FormatVersion { get; init; } = "MusicXML";
    public string SourceContainer { get; init; } = "MusicXML";
    /// <summary>
    /// Exact validated MusicXML document consumed by the semantic importer.
    /// Renderer integrations must use this payload instead of reopening the
    /// original container and selecting a score independently.
    /// </summary>
    public byte[] ValidatedMusicXml { get; init; } = [];
    public string ContentSha256 { get; init; } = string.Empty;
    public string KeySignature { get; init; } = "Not specified";
    public int KeyFifths { get; init; }
    public string TimeSignature { get; init; } = "Not specified";
    public int BeatsPerMeasure { get; init; } = 4;
    public int BeatType { get; init; } = 4;
    public string Tempo { get; init; } = "Not specified";
    public double TempoBpm { get; init; } = 120;
    public int MeasureCount { get; init; }
    public int TotalNoteCount { get; init; }
    public int TotalRestCount { get; init; }
    public int TotalLyricCount { get; init; }

    /// <summary>
    /// Written-score duration, before repeat/navigation expansion.
    /// </summary>
    public double TotalBeats { get; init; }

    /// <summary>
    /// Chronological performance duration after repeat and ending expansion.
    /// </summary>
    public double TotalPerformanceBeats =>
        PerformanceMeasures.Count > 0
            ? PerformanceMeasures.Max(m => m.PerformanceStartBeat + m.DurationBeats)
            : TotalBeats;

    public IReadOnlyList<ScorePart> Parts { get; init; } = [];
    public IReadOnlyList<MeasureSummary> Measures { get; init; } = [];
    public IReadOnlyList<ScoreNote> Notes { get; init; } = [];
    public IReadOnlyList<ScoreRest> Rests { get; init; } = [];
    public IReadOnlyList<ScoreMark> Marks { get; init; } = [];
    public IReadOnlyList<ScoreMeasureOccurrence> PerformanceMeasures { get; init; } = [];
    public IReadOnlyList<ScoreTempoChange> TempoChanges { get; init; } = [];
    public IReadOnlyList<ScoreMeterChange> MeterChanges { get; init; } = [];
    public IReadOnlyList<ScoreValidationWarning> ValidationWarnings { get; init; } = [];
    public int RepeatPairCount { get; init; }
    public int TiePairCount { get; init; }
    public int SlurPairCount { get; init; }

    public ScoreMeasureOccurrence? OccurrenceAtBeat(double beat)
    {
        if (PerformanceMeasures.Count == 0)
            return null;

        var clamped = Math.Clamp(beat, 0, TotalPerformanceBeats);
        return PerformanceMeasures
            .Where(occurrence =>
                occurrence.PerformanceStartBeat <= clamped + BeatEpsilon &&
                clamped <= occurrence.PerformanceStartBeat + occurrence.DurationBeats + BeatEpsilon)
            .OrderBy(occurrence => occurrence.PerformanceStartBeat)
            .LastOrDefault()
            ?? PerformanceMeasures[^1];
    }

    public double PerformanceToSourceBeat(double performanceBeat)
    {
        var occurrence = OccurrenceAtBeat(performanceBeat);
        if (occurrence is null)
            return Math.Clamp(performanceBeat, 0, TotalBeats);

        var offset = Math.Clamp(
            performanceBeat - occurrence.PerformanceStartBeat,
            0,
            occurrence.DurationBeats);
        return occurrence.SourceStartBeat + offset;
    }

    public double SourceToPerformanceBeat(double sourceBeat, int preferredOccurrence = 0)
    {
        if (PerformanceMeasures.Count == 0)
            return Math.Clamp(sourceBeat, 0, TotalBeats);

        var matches = PerformanceMeasures
            .Where(occurrence =>
                sourceBeat >= occurrence.SourceStartBeat - BeatEpsilon &&
                sourceBeat <= occurrence.SourceStartBeat + occurrence.DurationBeats + BeatEpsilon)
            .ToArray();
        if (matches.Length == 0)
            return Math.Clamp(sourceBeat, 0, TotalPerformanceBeats);

        var selected = matches
            .OrderBy(occurrence => Math.Abs(occurrence.OccurrenceIndex - preferredOccurrence))
            .ThenBy(occurrence => occurrence.OccurrenceIndex)
            .First();
        return selected.PerformanceStartBeat +
               Math.Clamp(sourceBeat - selected.SourceStartBeat, 0, selected.DurationBeats);
    }

    public double SecondsAtPerformanceBeat(double beat, double tempoScale = 1d)
    {
        var target = Math.Clamp(beat, 0, TotalPerformanceBeats);
        var scale = Math.Max(0.01, tempoScale);
        var changes = NormalizedTempoChanges();

        var seconds = 0d;
        var priorBeat = 0d;
        var bpm = Math.Max(1d, changes[0].Bpm * scale);
        foreach (var change in changes.Skip(1))
        {
            if (change.PerformanceBeat >= target - BeatEpsilon)
                break;

            var nextBeat = Math.Max(priorBeat, change.PerformanceBeat);
            seconds += (nextBeat - priorBeat) * 60d / bpm;
            priorBeat = nextBeat;
            bpm = Math.Max(1d, change.Bpm * scale);
        }

        seconds += Math.Max(0, target - priorBeat) * 60d / bpm;
        return seconds;
    }

    public double PerformanceBeatAtSeconds(double seconds, double tempoScale = 1d)
    {
        var remaining = Math.Max(0, seconds);
        var scale = Math.Max(0.01, tempoScale);
        var changes = NormalizedTempoChanges();

        var currentBeat = 0d;
        var bpm = Math.Max(1d, changes[0].Bpm * scale);
        foreach (var change in changes.Skip(1))
        {
            var segmentBeats = Math.Max(0, change.PerformanceBeat - currentBeat);
            var segmentSeconds = segmentBeats * 60d / bpm;
            if (remaining <= segmentSeconds + 1e-9)
                return Math.Clamp(currentBeat + remaining * bpm / 60d, 0, TotalPerformanceBeats);

            remaining -= segmentSeconds;
            currentBeat = change.PerformanceBeat;
            bpm = Math.Max(1d, change.Bpm * scale);
        }

        return Math.Clamp(currentBeat + remaining * bpm / 60d, 0, TotalPerformanceBeats);
    }

    public ScoreMeterChange MeterAtBeat(double beat)
    {
        var fallback = new ScoreMeterChange(0, Math.Max(1, BeatsPerMeasure), Math.Max(1, BeatType), "1", 0);
        return MeterChanges
            .Where(change => change.PerformanceBeat <= beat + BeatEpsilon)
            .OrderBy(change => change.PerformanceBeat)
            .LastOrDefault()
            ?? fallback;
    }

    public double TempoScaleFor(double effectiveInitialBpm) =>
        Math.Max(0.01, effectiveInitialBpm / Math.Max(1d, TempoBpm));

    public double PerformanceBeatAfterElapsed(
        double anchorBeat,
        double elapsedSeconds,
        double effectiveInitialBpm)
    {
        var scale = TempoScaleFor(effectiveInitialBpm);
        var anchorSeconds = SecondsAtPerformanceBeat(anchorBeat, scale);
        return PerformanceBeatAtSeconds(anchorSeconds + Math.Max(0, elapsedSeconds), scale);
    }

    public double PerformanceDurationSeconds(
        double startBeat,
        double endBeat,
        double effectiveInitialBpm)
    {
        var scale = TempoScaleFor(effectiveInitialBpm);
        return Math.Max(
            0,
            SecondsAtPerformanceBeat(endBeat, scale) - SecondsAtPerformanceBeat(startBeat, scale));
    }

    public bool IsMeasureDownbeat(double beat)
    {
        var occurrence = OccurrenceAtBeat(beat);
        return occurrence is not null &&
               Math.Abs(beat - occurrence.PerformanceStartBeat) <= BeatEpsilon;
    }

    public double NextMeterPulseBeat(double beat)
    {
        var occurrence = OccurrenceAtBeat(beat);
        if (occurrence is null)
            return Math.Clamp(beat + 1, 0, TotalPerformanceBeats);

        var occurrenceEnd = occurrence.PerformanceStartBeat + occurrence.DurationBeats;
        var meter = MeterAtBeat(occurrence.PerformanceStartBeat);
        var pulse = 4d / Math.Max(1, meter.BeatType);
        if (!double.IsFinite(pulse) || pulse <= 0)
            pulse = 1;
        var local = Math.Max(0, beat - occurrence.PerformanceStartBeat);
        var pulseIndex = Math.Floor((local + BeatEpsilon) / pulse) + 1;
        var next = occurrence.PerformanceStartBeat + pulseIndex * pulse;
        if (next < occurrenceEnd - BeatEpsilon)
            return next;

        var nextOccurrence = PerformanceMeasures
            .FirstOrDefault(item => item.OccurrenceIndex == occurrence.OccurrenceIndex + 1);
        return nextOccurrence?.PerformanceStartBeat ?? occurrenceEnd;
    }

    public IReadOnlyList<ScorePerformanceSpan> PerformanceSpansForMeasureRange(
        int startMeasure,
        int endMeasure)
    {
        var selected = PerformanceMeasures
            .Where(occurrence =>
                int.TryParse(occurrence.MeasureNumber, out var measure) &&
                measure >= startMeasure &&
                measure <= endMeasure)
            .OrderBy(occurrence => occurrence.PerformanceStartBeat)
            .ToArray();
        if (selected.Length == 0)
            return [];

        var spans = new List<ScorePerformanceSpan>();
        var start = selected[0].PerformanceStartBeat;
        var end = start + selected[0].DurationBeats;
        var firstOccurrence = selected[0].OccurrenceIndex;
        var lastOccurrence = firstOccurrence;
        foreach (var occurrence in selected.Skip(1))
        {
            var occurrenceEnd = occurrence.PerformanceStartBeat + occurrence.DurationBeats;
            if (Math.Abs(occurrence.PerformanceStartBeat - end) <= BeatEpsilon)
            {
                end = occurrenceEnd;
                lastOccurrence = occurrence.OccurrenceIndex;
                continue;
            }

            spans.Add(new ScorePerformanceSpan(start, end, firstOccurrence, lastOccurrence));
            start = occurrence.PerformanceStartBeat;
            end = occurrenceEnd;
            firstOccurrence = lastOccurrence = occurrence.OccurrenceIndex;
        }

        spans.Add(new ScorePerformanceSpan(start, end, firstOccurrence, lastOccurrence));
        return spans;
    }

    private IReadOnlyList<ScoreTempoChange> NormalizedTempoChanges()
    {
        var normalized = TempoChanges
            .Where(change => double.IsFinite(change.PerformanceBeat) && double.IsFinite(change.Bpm) && change.Bpm > 0)
            .OrderBy(change => change.PerformanceBeat)
            .GroupBy(change => Math.Round(change.PerformanceBeat, 6))
            .Select(group => group.Last())
            .ToList();

        if (normalized.Count == 0 || normalized[0].PerformanceBeat > BeatEpsilon)
            normalized.Insert(0, new ScoreTempoChange(0, Math.Max(1, TempoBpm), "1", 0));

        return normalized;
    }

    public bool HasBlockingAssessmentWarning(int startMeasure, int endMeasure) =>
        ValidationWarnings.Any(warning =>
            warning.BlocksAssessment &&
            warning.StartMeasure <= endMeasure &&
            warning.EndMeasure >= startMeasure);

    public string? BlockingAssessmentReason(int startMeasure, int endMeasure) =>
        ValidationWarnings.FirstOrDefault(warning =>
            warning.BlocksAssessment &&
            warning.StartMeasure <= endMeasure &&
            warning.EndMeasure >= startMeasure)?.Message;

    public bool HasBlockingPlaybackWarning(int startMeasure, int endMeasure) =>
        ValidationWarnings.Any(warning =>
            warning.BlocksPlayback &&
            warning.StartMeasure <= endMeasure &&
            warning.EndMeasure >= startMeasure);

    public string? BlockingPlaybackReason(int startMeasure, int endMeasure) =>
        ValidationWarnings.FirstOrDefault(warning =>
            warning.BlocksPlayback &&
            warning.StartMeasure <= endMeasure &&
            warning.EndMeasure >= startMeasure)?.Message;

    public bool CutsRepeatRegion(int startMeasure, int endMeasure)
    {
        foreach (var region in RepeatedMeasureRegions())
        {
            var intersects = startMeasure <= region.End && endMeasure >= region.Start;
            var containsWholeRegion = startMeasure <= region.Start && endMeasure >= region.End;
            if (intersects && !containsWholeRegion)
                return true;
        }

        return false;
    }

    public string? PartialRepeatReason(int startMeasure, int endMeasure) =>
        CutsRepeatRegion(startMeasure, endMeasure)
            ? "This range cuts through a repeat. Select the complete repeated section so playback and assessment follow one unambiguous musical sequence."
            : null;

    /// <summary>
    /// Finds the largest contiguous written range that has playable groups,
    /// contains no assessment-blocking diagnostics, and does not cut a repeat.
    /// </summary>
    public ScoreMeasureRange? LargestAssessableRange(PracticeMode mode)
    {
        if (MeasureCount <= 0)
            return null;

        var blocked = new bool[MeasureCount + 1];
        foreach (var warning in ValidationWarnings.Where(warning => warning.BlocksAssessment))
        {
            var start = Math.Clamp(warning.StartMeasure, 1, MeasureCount);
            var end = Math.Clamp(warning.EndMeasure, start, MeasureCount);
            for (var measure = start; measure <= end; measure++)
                blocked[measure] = true;
        }

        foreach (var region in RepeatedMeasureRegions())
        {
            var start = Math.Clamp(region.Start, 1, MeasureCount);
            var end = Math.Clamp(region.End, start, MeasureCount);
            if (!Enumerable.Range(start, end - start + 1).Any(measure => blocked[measure]))
                continue;
            for (var measure = start; measure <= end; measure++)
                blocked[measure] = true;
        }

        var groupCounts = new int[MeasureCount + 1];
        foreach (var group in GetPracticeGroups(mode))
        {
            if (int.TryParse(group.MeasureNumber, out var measure) && measure >= 1 && measure <= MeasureCount)
                groupCounts[measure]++;
        }

        ScoreMeasureRange? best = null;
        for (var measure = 1; measure <= MeasureCount;)
        {
            while (measure <= MeasureCount && blocked[measure])
                measure++;
            if (measure > MeasureCount)
                break;

            var start = measure;
            var groups = 0;
            while (measure <= MeasureCount && !blocked[measure])
            {
                groups += groupCounts[measure];
                measure++;
            }

            var candidate = new ScoreMeasureRange(start, measure - 1, groups);
            if (candidate.GroupCount > 0 &&
                (best is null || candidate.GroupCount > best.GroupCount ||
                 (candidate.GroupCount == best.GroupCount && candidate.StartMeasure < best.StartMeasure)))
            {
                best = candidate;
            }
        }
        return best;
    }

    private IReadOnlyList<(int Start, int End)> RepeatedMeasureRegions()
    {
        var repeated = PerformanceMeasures
            .GroupBy(occurrence => occurrence.SourceMeasureIndex)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(index => index)
            .ToArray();

        if (repeated.Length == 0)
            return [];

        var regions = new List<(int Start, int End)>();
        var startIndex = repeated[0];
        var endIndex = repeated[0];
        for (var index = 1; index < repeated.Length; index++)
        {
            if (repeated[index] == endIndex + 1)
            {
                endIndex = repeated[index];
                continue;
            }

            regions.Add((
                ParseMeasureNumber(Measures.ElementAtOrDefault(startIndex)?.Number, startIndex + 1),
                ParseMeasureNumber(Measures.ElementAtOrDefault(endIndex)?.Number, endIndex + 1)));
            startIndex = endIndex = repeated[index];
        }

        regions.Add((
            ParseMeasureNumber(Measures.ElementAtOrDefault(startIndex)?.Number, startIndex + 1),
            ParseMeasureNumber(Measures.ElementAtOrDefault(endIndex)?.Number, endIndex + 1)));
        return regions;
    }

    private static int ParseMeasureNumber(string? value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    public static int ResolveStaffNumber(int staffNumber, int midiNoteNumber) =>
        staffNumber is 1 or 2 ? staffNumber : (midiNoteNumber >= 60 ? 1 : 2);

    public IReadOnlyList<ScoreNoteGroup> GetPracticeGroups(PracticeMode mode)
    {
        var filtered = mode switch
        {
            PracticeMode.LeftHand => Notes.Where(note => ResolveStaffNumber(note.StaffNumber, note.MidiNoteNumber) == 2),
            PracticeMode.RightHand => Notes.Where(note => ResolveStaffNumber(note.StaffNumber, note.MidiNoteNumber) == 1),
            _ => Notes
        };

        return filtered
            .GroupBy(note => Math.Round(note.OnsetBeats, 5))
            .OrderBy(group => group.Key)
            .Select(group => new ScoreNoteGroup(
                group.Key,
                group.OrderBy(note => note.PartId, StringComparer.Ordinal)
                    .ThenBy(note => note.StaffNumber)
                    .ThenBy(note => note.MidiNoteNumber)
                    .First().MeasureNumber,
                group.OrderBy(note => note.PartId, StringComparer.Ordinal)
                    .ThenBy(note => note.StaffNumber)
                    .ThenBy(note => note.MidiNoteNumber)
                    .ToArray()))
            .ToArray();
    }
}

public sealed record ScorePart(
    string Id,
    string Name,
    int MeasureCount,
    int NoteCount,
    int RestCount,
    int LyricCount,
    int StaffOneNoteCount,
    int StaffTwoNoteCount);

public sealed record MeasureSummary(
    string Number,
    int NoteCount,
    int RestCount,
    int ChordNoteCount,
    int LyricCount,
    int StaffOneNoteCount,
    int StaffTwoNoteCount,
    string? Tempo,
    double StartBeat,
    double DurationBeats)
{
    public int SoundingNoteCount => Math.Max(0, NoteCount - RestCount);
    public string StaffBreakdown => $"Staff 1 {StaffOneNoteCount} | Staff 2 {StaffTwoNoteCount}";
    public string DensityLabel => NoteCount == 0 ? "Empty measure" : $"{SoundingNoteCount} sounding notes";
}

public sealed record ScoreNote(
    int MidiNoteNumber,
    double OnsetBeats,
    double DurationBeats,
    int StaffNumber,
    string MeasureNumber,
    string Step,
    int Octave,
    int Alter,
    string NoteType,
    int DotCount,
    string Voice,
    string Stem,
    bool IsChord,
    IReadOnlyList<string> Beams,
    string? Lyric,
    bool TieStart,
    bool TieStop,
    int PerformanceOccurrence,
    bool IsStaccato = false,
    bool IsAccent = false,
    bool IsTenuto = false,
    bool IsSlurred = false,
    string PartId = "",
    int SourceMeasureIndex = 0,
    double SourceOnsetBeats = 0,
    string SourceNoteId = "",
    IReadOnlyList<string>? TiedSourceNoteIds = null,
    int Velocity = 96);

public sealed record ScoreRest(
    double OnsetBeats,
    double DurationBeats,
    int StaffNumber,
    string MeasureNumber,
    string NoteType,
    int DotCount,
    string Voice,
    string PartId = "",
    int SourceMeasureIndex = 0,
    double SourceOnsetBeats = 0,
    int PerformanceOccurrence = 0);

public enum ScoreMarkKind
{
    Dynamic,
    Articulation,
    Pedal,
    Direction
}

public sealed record ScoreMark(
    double OnsetBeats,
    int StaffNumber,
    string MeasureNumber,
    ScoreMarkKind Kind,
    string Text,
    string PartId = "",
    int SourceMeasureIndex = 0,
    double SourceOnsetBeats = 0,
    int PerformanceOccurrence = 0);

public sealed record ScoreNoteGroup(
    double OnsetBeats,
    string MeasureNumber,
    IReadOnlyList<ScoreNote> Notes)
{
    public IReadOnlyList<int> MidiNotes => Notes.Select(note => note.MidiNoteNumber).Distinct().Order().ToArray();
    public int NoteCount => MidiNotes.Count;
    public int PerformanceOccurrence => Notes
        .Select(note => note.PerformanceOccurrence)
        .Distinct()
        .DefaultIfEmpty(0)
        .Single();
}

public sealed record ScoreMeasureOccurrence(
    int OccurrenceIndex,
    int SourceMeasureIndex,
    string MeasureNumber,
    double SourceStartBeat,
    double PerformanceStartBeat,
    double DurationBeats,
    int RepeatPass,
    int RepeatSectionId = -1);

public sealed record ScoreTempoChange(
    double PerformanceBeat,
    double Bpm,
    string MeasureNumber,
    int PerformanceOccurrence);

public sealed record ScoreMeterChange(
    double PerformanceBeat,
    int Beats,
    int BeatType,
    string MeasureNumber,
    int PerformanceOccurrence);

public sealed record ScorePerformanceSpan(
    double StartBeat,
    double EndBeat,
    int FirstOccurrenceIndex,
    int LastOccurrenceIndex)
{
    public double DurationBeats => Math.Max(0, EndBeat - StartBeat);
    public bool Contains(double beat) => beat >= StartBeat - 0.0001 && beat < EndBeat - 0.0001;
}

public sealed record ScoreMeasureRange(int StartMeasure, int EndMeasure, int GroupCount);

public sealed record ScoreValidationWarning(
    string Code,
    string Message,
    int StartMeasure,
    int EndMeasure,
    bool BlocksAssessment,
    bool BlocksPlayback = false,
    ScoreCapabilityDisposition Capability = ScoreCapabilityDisposition.VisuallySupportedSemanticLimitation);

/// <summary>
/// Describes how Cadenza treats notation that is not fully represented by the
/// performed score model. Malformed input is rejected before a ScoreDocument
/// is created; the value remains part of the policy for stable diagnostics and
/// tests.
/// </summary>
public enum ScoreCapabilityDisposition
{
    SemanticallySupported,
    VisuallySupportedSemanticLimitation,
    UnsupportedVisualOnly,
    BlocksPlaybackAndAssessment,
    MalformedRejected
}
