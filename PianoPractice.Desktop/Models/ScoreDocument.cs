namespace PianoPractice.Desktop.Models;

public sealed class ScoreDocument
{
    public required string SourcePath { get; init; }
    public required string Title { get; init; }
    public string ComposerOrCreator { get; init; } = "Unknown creator";
    public string FormatVersion { get; init; } = "MusicXML";
    public string SourceContainer { get; init; } = "MusicXML";
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
    public double TotalBeats { get; init; }
    public IReadOnlyList<ScorePart> Parts { get; init; } = [];
    public IReadOnlyList<MeasureSummary> Measures { get; init; } = [];
    public IReadOnlyList<ScoreNote> Notes { get; init; } = [];
    public IReadOnlyList<ScoreRest> Rests { get; init; } = [];
    public IReadOnlyList<ScoreMark> Marks { get; init; } = [];
    public IReadOnlyList<ScoreMeasureOccurrence> PerformanceMeasures { get; init; } = [];
    public IReadOnlyList<ScoreValidationWarning> ValidationWarnings { get; init; } = [];
    public int RepeatPairCount { get; init; }
    public int TiePairCount { get; init; }
    public int SlurPairCount { get; init; }

    public ScoreMeasureOccurrence? OccurrenceAtBeat(double beat) => PerformanceMeasures
        .Where(occurrence => occurrence.PerformanceStartBeat <= beat + 0.0001)
        .OrderBy(occurrence => occurrence.PerformanceStartBeat)
        .LastOrDefault();

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

    private IReadOnlyList<(int Start, int End)> RepeatedMeasureRegions()
    {
        var repeated = PerformanceMeasures
            .Select(occurrence => int.TryParse(occurrence.MeasureNumber, out var measure) ? measure : 0)
            .Where(measure => measure > 0)
            .GroupBy(measure => measure)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(measure => measure)
            .ToArray();

        if (repeated.Length == 0)
            return [];

        var regions = new List<(int Start, int End)>();
        var start = repeated[0];
        var end = repeated[0];
        for (var index = 1; index < repeated.Length; index++)
        {
            if (repeated[index] == end + 1)
            {
                end = repeated[index];
                continue;
            }

            regions.Add((start, end));
            start = end = repeated[index];
        }

        regions.Add((start, end));
        return regions;
    }

    public IReadOnlyList<ScoreNoteGroup> GetPracticeGroups(PracticeMode mode)
    {
        var filtered = mode switch
        {
            PracticeMode.LeftHand => Notes.Where(note => note.StaffNumber == 2),
            PracticeMode.RightHand => Notes.Where(note => note.StaffNumber == 1),
            _ => Notes
        };

        return filtered
            .GroupBy(note => Math.Round(note.OnsetBeats, 5))
            .OrderBy(group => group.Key)
            .Select(group => new ScoreNoteGroup(group.Key, group.First().MeasureNumber, group.OrderBy(note => note.MidiNoteNumber).ToArray()))
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
    int PerformanceOccurrence);

public sealed record ScoreRest(
    double OnsetBeats,
    double DurationBeats,
    int StaffNumber,
    string MeasureNumber,
    string NoteType,
    int DotCount,
    string Voice);

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
    string Text);

public sealed record ScoreNoteGroup(
    double OnsetBeats,
    string MeasureNumber,
    IReadOnlyList<ScoreNote> Notes)
{
    public IReadOnlyList<int> MidiNotes => Notes.Select(note => note.MidiNoteNumber).Distinct().ToArray();
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
    int RepeatPass);

public sealed record ScoreValidationWarning(
    string Code,
    string Message,
    int StartMeasure,
    int EndMeasure,
    bool BlocksAssessment);
