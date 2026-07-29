using PianoPractice.Desktop.Services;

var path = args.Length > 0
    ? args[0]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "olivia-rodrigo-drivers-license.mxl");

var score = new MusicXmlImporter().Import(path);
if (score.MeasureCount != 50 || score.Parts.Count != 1 || score.TotalNoteCount == 0 || score.Notes.Count == 0 || score.TempoBpm != 72)
{
    throw new InvalidOperationException($"Unexpected score shape: parts={score.Parts.Count}, measures={score.MeasureCount}, notes={score.TotalNoteCount}, playable={score.Notes.Count}, tempo={score.TempoBpm}.");
}
if (score.PerformanceMeasures.Count != 77 || Math.Abs(score.TotalBeats - 306) > 0.01)
{
    throw new InvalidOperationException(
        $"Repeat expansion mismatch: occurrences={score.PerformanceMeasures.Count}, beats={score.TotalBeats:0.###}.");
}
if (score.RepeatPairCount != 3 || score.TiePairCount != 9 || score.SlurPairCount != 4)
{
    throw new InvalidOperationException(
        $"Notation semantics mismatch: repeats={score.RepeatPairCount}, ties={score.TiePairCount}, slurs={score.SlurPairCount}.");
}
if (!score.ValidationWarnings.Any(warning => warning.Code == "volta-ending" && warning.BlocksAssessment) ||
    !score.HasBlockingAssessmentWarning(16, 40) ||
    score.HasBlockingAssessmentWarning(1, 15))
{
    throw new InvalidOperationException("Ambiguous volta validation did not block only the affected assessed range.");
}
if (!score.CutsRepeatRegion(49, 49) || score.CutsRepeatRegion(49, 50))
{
    throw new InvalidOperationException("Partial repeat-range validation did not distinguish a cut repeat from the complete repeated section.");
}
if (score.Notes.Any(note => note.TieStop && !note.TieStart))
{
    throw new InvalidOperationException("A tied continuation remained as a required re-articulation.");
}
for (var index = 1; index < score.PerformanceMeasures.Count; index++)
{
    var previous = score.PerformanceMeasures[index - 1];
    var current = score.PerformanceMeasures[index];
    var expectedStart = previous.PerformanceStartBeat + previous.DurationBeats;
    if (Math.Abs(current.PerformanceStartBeat - expectedStart) > 0.01)
    {
        throw new InvalidOperationException(
            $"Performance occurrence timeline has a gap or overlap at occurrence {current.OccurrenceIndex}: " +
            $"expected beat {expectedStart:0.###}, got {current.PerformanceStartBeat:0.###}.");
    }
}
var repeatPassStarts = score.PerformanceMeasures
    .Where((occurrence, index) =>
        occurrence.RepeatPass > 1 &&
        (index == 0 || score.PerformanceMeasures[index - 1].RepeatPass != occurrence.RepeatPass))
    .Select(occurrence => $"m{occurrence.MeasureNumber}@{occurrence.PerformanceStartBeat:0.###}")
    .ToArray();

Console.WriteLine($"Title: {score.Title}");
Console.WriteLine($"Format: {score.FormatVersion} / {score.SourceContainer}");
Console.WriteLine($"Parts: {score.Parts.Count} ({string.Join(", ", score.Parts.Select(part => part.Name))})");
Console.WriteLine($"Measures: {score.MeasureCount}");
Console.WriteLine($"Notes: {score.TotalNoteCount}; rests: {score.TotalRestCount}; lyrics: {score.TotalLyricCount}");
Console.WriteLine($"Playable notes: {score.Notes.Count}; beats: {score.TotalBeats:0.##}; practice groups: {score.GetPracticeGroups(PianoPractice.Desktop.Models.PracticeMode.BothHands).Count}");
Console.WriteLine($"Performance occurrences: {score.PerformanceMeasures.Count}; repeats: {score.RepeatPairCount}; ties: {score.TiePairCount}; slurs: {score.SlurPairCount}");
Console.WriteLine($"Repeat-pass boundaries: {string.Join(", ", repeatPassStarts)}; final beat: {score.TotalBeats:0.###}");
Console.WriteLine($"Validation warnings: {score.ValidationWarnings.Count} ({string.Join(", ", score.ValidationWarnings.Select(warning => warning.Code))})");
foreach (var warning in score.ValidationWarnings)
    Console.WriteLine($"Warning: {warning.Code} measures {warning.StartMeasure}-{warning.EndMeasure}, blocksAssessment={warning.BlocksAssessment}: {warning.Message}");
Console.WriteLine($"Key: {score.KeySignature}; time: {score.TimeSignature}; tempo: {score.Tempo}");
