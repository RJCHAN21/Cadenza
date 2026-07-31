using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop.Services;

public sealed record PlaybackSelectionBoundary(
    double StartBeat,
    double EndBeat,
    int StartOccurrenceIndex,
    int EndOccurrenceIndex,
    int StartBar,
    int EndBar)
{
    public double DurationBeats => Math.Max(0, EndBeat - StartBeat);
}

public static class PlaybackSelectionBoundaryResolver
{
    private const double BeatEpsilon = 0.0001;

    public static PlaybackSelectionBoundary Resolve(
        ScoreDocument score,
        int requestedStartBar,
        int requestedEndBar,
        double? cursorBeat = null)
    {
        ArgumentNullException.ThrowIfNull(score);
        var maxBar = Math.Max(1, score.MeasureCount);
        var startBar = Math.Clamp(requestedStartBar, 1, maxBar);
        var endBar = Math.Clamp(requestedEndBar, startBar, maxBar);
        var occurrences = score.PerformanceMeasures
            .OrderBy(occurrence => occurrence.PerformanceStartBeat)
            .ThenBy(occurrence => occurrence.OccurrenceIndex)
            .ToArray();

        if (occurrences.Length == 0)
        {
            var fallbackEnd = Math.Max(0.01, score.TotalPerformanceBeats > 0
                ? score.TotalPerformanceBeats
                : score.TotalBeats);
            return new PlaybackSelectionBoundary(0, fallbackEnd, 0, 0, startBar, endBar);
        }

        var startOccurrence = occurrences.FirstOrDefault(occurrence =>
                                  occurrence.SourceMeasureIndex + 1 == startBar)
                              ?? occurrences.FirstOrDefault(occurrence =>
                                  occurrence.SourceMeasureIndex + 1 >= startBar)
                              ?? occurrences[0];
        var searchBeat = Math.Max(
            startOccurrence.PerformanceStartBeat,
            cursorBeat ?? startOccurrence.PerformanceStartBeat);

        var matchingEndOccurrences = occurrences
            .Where(occurrence =>
                occurrence.SourceMeasureIndex + 1 == endBar &&
                occurrence.PerformanceStartBeat >= startOccurrence.PerformanceStartBeat - BeatEpsilon &&
                occurrence.PerformanceStartBeat + occurrence.DurationBeats > searchBeat + BeatEpsilon)
            .OrderBy(occurrence => occurrence.PerformanceStartBeat)
            .ThenBy(occurrence => occurrence.OccurrenceIndex)
            .ToArray();

        // A focused range ending before the true final written bar is a hard user boundary:
        // stop on the first reachable completion of that bar. When the selected end is the
        // score's actual final bar, preserve the score's own terminal repeat/navigation and
        // stop only after the last performed occurrence of that final bar.
        var endOccurrence = endBar == maxBar
            ? matchingEndOccurrences.LastOrDefault()
            : matchingEndOccurrences.FirstOrDefault();

        if (endOccurrence is null)
        {
            endOccurrence = occurrences
                                .Where(occurrence =>
                                    occurrence.PerformanceStartBeat >= startOccurrence.PerformanceStartBeat - BeatEpsilon &&
                                    occurrence.PerformanceStartBeat + occurrence.DurationBeats > searchBeat + BeatEpsilon &&
                                    occurrence.SourceMeasureIndex + 1 >= startBar &&
                                    occurrence.SourceMeasureIndex + 1 <= endBar)
                                .OrderBy(occurrence => occurrence.PerformanceStartBeat)
                                .LastOrDefault()
                            ?? startOccurrence;
        }

        var endBeat = Math.Max(
            startOccurrence.PerformanceStartBeat + BeatEpsilon,
            endOccurrence.PerformanceStartBeat + endOccurrence.DurationBeats);
        return new PlaybackSelectionBoundary(
            startOccurrence.PerformanceStartBeat,
            endBeat,
            startOccurrence.OccurrenceIndex,
            endOccurrence.OccurrenceIndex,
            startBar,
            endBar);
    }

    public static TimeSpan RemainingDuration(
        ScoreDocument score,
        PlaybackSelectionBoundary boundary,
        double currentBeat,
        double effectiveTempoBpm)
    {
        ArgumentNullException.ThrowIfNull(score);
        var fromBeat = Math.Clamp(currentBeat, boundary.StartBeat, boundary.EndBeat);
        var tempoScale = Math.Max(0.01, effectiveTempoBpm / Math.Max(1d, score.TempoBpm));
        var startSeconds = score.SecondsAtPerformanceBeat(fromBeat, tempoScale);
        var endSeconds = score.SecondsAtPerformanceBeat(boundary.EndBeat, tempoScale);
        return TimeSpan.FromSeconds(Math.Max(0, endSeconds - startSeconds));
    }
}
