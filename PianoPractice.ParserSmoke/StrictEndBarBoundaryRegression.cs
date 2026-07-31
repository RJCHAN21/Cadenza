using System.Runtime.CompilerServices;
using PianoPractice.Desktop.Models;
using PianoPractice.Desktop.Services;

internal static class StrictEndBarBoundaryRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        StopsAtFirstSelectedEndBarOccurrence();
        ResumeUsesNextReachableEndBarOccurrence();
        PreservesRepeatWhenSelectedEndIsTrueFinalBar();
        UsesTempoAwareRemainingDuration();
        Console.WriteLine("PASS strict selected end-bar boundary fixtures");
    }

    private static void StopsAtFirstSelectedEndBarOccurrence()
    {
        var score = RepeatedEndingScore();
        var boundary = PlaybackSelectionBoundaryResolver.Resolve(score, 1, 3);

        Assert(boundary.StartOccurrenceIndex == 0,
            $"Expected start occurrence 0, got {boundary.StartOccurrenceIndex}.");
        Assert(boundary.EndOccurrenceIndex == 2,
            $"The selected non-final end bar leaked into a later repeat occurrence {boundary.EndOccurrenceIndex}.");
        Assert(Math.Abs(boundary.EndBeat - 12d) < 0.0001,
            $"Expected the first completion of non-final bar 3 at beat 12, got {boundary.EndBeat:0.###}.");
    }

    private static void ResumeUsesNextReachableEndBarOccurrence()
    {
        var score = RepeatedEndingScore();
        var boundary = PlaybackSelectionBoundaryResolver.Resolve(score, 1, 3, cursorBeat: 13d);

        Assert(boundary.EndOccurrenceIndex == 4,
            $"A resumed focused preview should stop at occurrence 4, got {boundary.EndOccurrenceIndex}.");
        Assert(Math.Abs(boundary.EndBeat - 20d) < 0.0001,
            $"Expected resumed completion at beat 20, got {boundary.EndBeat:0.###}.");
    }

    private static void PreservesRepeatWhenSelectedEndIsTrueFinalBar()
    {
        var score = FinalBarRepeatScore();
        var boundary = PlaybackSelectionBoundaryResolver.Resolve(score, 1, 3);

        Assert(boundary.EndOccurrenceIndex == 4,
            $"The true final bar must preserve its terminal repeat and end at occurrence 4, got {boundary.EndOccurrenceIndex}.");
        Assert(Math.Abs(boundary.EndBeat - 20d) < 0.0001,
            $"Expected playback to complete the repeated final bar at beat 20, got {boundary.EndBeat:0.###}.");
    }

    private static void UsesTempoAwareRemainingDuration()
    {
        var score = RepeatedEndingScore();
        var boundary = PlaybackSelectionBoundaryResolver.Resolve(score, 1, 3);
        var duration = PlaybackSelectionBoundaryResolver.RemainingDuration(
            score,
            boundary,
            currentBeat: 0,
            effectiveTempoBpm: 120);

        Assert(Math.Abs(duration.TotalSeconds - 6d) < 0.001,
            $"Expected 6 seconds at 120 BPM, got {duration.TotalSeconds:0.###}.");
    }

    private static ScoreDocument RepeatedEndingScore() => new()
    {
        SourcePath = "strict-boundary.musicxml",
        Title = "Strict boundary fixture",
        MeasureCount = 4,
        TotalBeats = 16,
        TempoBpm = 120,
        PerformanceMeasures =
        [
            new ScoreMeasureOccurrence(0, 0, "1", 0, 0, 4, 1),
            new ScoreMeasureOccurrence(1, 1, "2", 4, 4, 4, 1, 0),
            new ScoreMeasureOccurrence(2, 2, "3", 8, 8, 4, 1, 0),
            new ScoreMeasureOccurrence(3, 1, "2", 4, 12, 4, 2, 0),
            new ScoreMeasureOccurrence(4, 2, "3", 8, 16, 4, 2, 0),
            new ScoreMeasureOccurrence(5, 3, "4", 12, 20, 4, 1)
        ]
    };

    private static ScoreDocument FinalBarRepeatScore() => new()
    {
        SourcePath = "terminal-repeat.musicxml",
        Title = "Terminal repeat fixture",
        MeasureCount = 3,
        TotalBeats = 12,
        TempoBpm = 120,
        PerformanceMeasures =
        [
            new ScoreMeasureOccurrence(0, 0, "1", 0, 0, 4, 1),
            new ScoreMeasureOccurrence(1, 1, "2", 4, 4, 4, 1, 0),
            new ScoreMeasureOccurrence(2, 2, "3", 8, 8, 4, 1, 0),
            new ScoreMeasureOccurrence(3, 1, "2", 4, 12, 4, 2, 0),
            new ScoreMeasureOccurrence(4, 2, "3", 8, 16, 4, 2, 0)
        ]
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
