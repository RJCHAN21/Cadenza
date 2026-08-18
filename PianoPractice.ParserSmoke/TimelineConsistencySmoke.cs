using PianoPractice.Desktop.Models;

internal static class TimelineConsistencySmoke
{
    internal static void Run()
    {
        TempoConversionsUseOnePerformanceClock();
        MeterPulsesFollowTheOccurrenceMeter();
        MeterPulseMatrixUsesWrittenDenominators();
        RepeatedSelectionsRemainExplicitSpans();
        DefaultAssessableRangeIsLinearAndRepeatSafe();
    }

    private static void MeterPulseMatrixUsesWrittenDenominators()
    {
        var meters = new[]
        {
            (2, 4), (3, 4), (4, 4), (6, 8), (9, 8), (12, 8), (5, 8), (7, 8)
        };
        var occurrences = new List<ScoreMeasureOccurrence>();
        var changes = new List<ScoreMeterChange>();
        var start = 0d;
        for (var index = 0; index < meters.Length; index++)
        {
            var meter = meters[index];
            var duration = meter.Item1 * 4d / meter.Item2;
            occurrences.Add(new ScoreMeasureOccurrence(index, index, (index + 1).ToString(), start, start, duration, 1));
            changes.Add(new ScoreMeterChange(start, meter.Item1, meter.Item2, (index + 1).ToString(), index));
            start += duration;
        }

        var score = Score(
            occurrences,
            [new ScoreTempoChange(0, 120, "1", 0)],
            changes);
        for (var index = 0; index < occurrences.Count; index++)
        {
            var occurrence = occurrences[index];
            var meter = meters[index];
            Assert(score.IsMeasureDownbeat(occurrence.PerformanceStartBeat),
                $"{meter.Item1}/{meter.Item2} occurrence did not start with a downbeat");
            AssertNear(
                score.NextMeterPulseBeat(occurrence.PerformanceStartBeat),
                occurrence.PerformanceStartBeat + 4d / meter.Item2,
                $"{meter.Item1}/{meter.Item2} pulse ignored the denominator");
        }
    }

    private static void TempoConversionsUseOnePerformanceClock()
    {
        var score = Score(
            occurrences:
            [
                new ScoreMeasureOccurrence(0, 0, "1", 0, 0, 4, 1),
                new ScoreMeasureOccurrence(1, 1, "2", 4, 4, 4, 1)
            ],
            tempos:
            [
                new ScoreTempoChange(0, 120, "1", 0),
                new ScoreTempoChange(4, 60, "2", 1)
            ],
            meters: [new ScoreMeterChange(0, 4, 4, "1", 0)]);

        AssertNear(score.PerformanceBeatAfterElapsed(0, 5, 120), 7,
            "tempo-aware elapsed time did not reach the authoritative performed beat");
        AssertNear(score.PerformanceBeatAfterElapsed(4, 2, 120), 6,
            "anchored tempo-aware elapsed time ignored the active tempo segment");
        AssertNear(score.PerformanceDurationSeconds(4, 6, 120), 2,
            "performed duration disagrees with the tempo map");
        AssertNear(
            score.PerformanceBeatAtSeconds(score.SecondsAtPerformanceBeat(6)),
            6,
            "tempo conversion was not reversible");
    }

    private static void MeterPulsesFollowTheOccurrenceMeter()
    {
        var score = Score(
            occurrences:
            [
                new ScoreMeasureOccurrence(0, 0, "1", 0, 0, 3, 1),
                new ScoreMeasureOccurrence(1, 1, "2", 3, 3, 3, 1)
            ],
            tempos: [new ScoreTempoChange(0, 120, "1", 0)],
            meters:
            [
                new ScoreMeterChange(0, 3, 4, "1", 0),
                new ScoreMeterChange(3, 6, 8, "2", 1)
            ]);

        Assert(score.IsMeasureDownbeat(0), "first 3/4 downbeat was not accented");
        Assert(!score.IsMeasureDownbeat(1), "an interior 3/4 beat was incorrectly accented");
        Assert(score.IsMeasureDownbeat(3), "6/8 meter change did not create a downbeat");
        AssertNear(score.NextMeterPulseBeat(0), 1, "3/4 did not advance by quarter-note pulse");
        AssertNear(score.NextMeterPulseBeat(3), 3.5, "6/8 did not advance by denominator-aware pulse");
    }

    private static void RepeatedSelectionsRemainExplicitSpans()
    {
        var score = Score(
            occurrences:
            [
                new ScoreMeasureOccurrence(0, 0, "1", 0, 0, 1, 1),
                new ScoreMeasureOccurrence(1, 1, "2", 1, 1, 1, 1),
                new ScoreMeasureOccurrence(2, 0, "1", 0, 2, 1, 2),
                new ScoreMeasureOccurrence(3, 1, "2", 1, 3, 1, 2)
            ],
            tempos: [new ScoreTempoChange(0, 120, "1", 0)],
            meters: [new ScoreMeterChange(0, 4, 4, "1", 0)]);

        var partial = score.PerformanceSpansForMeasureRange(2, 2);
        Assert(partial.Count == 2, "disjoint repeat occurrences were collapsed into one leaking interval");
        AssertNear(partial[0].StartBeat, 1, "first selected repeat span starts at the wrong beat");
        AssertNear(partial[0].EndBeat, 2, "first selected repeat span includes unselected music");
        AssertNear(partial[1].StartBeat, 3, "second selected repeat span starts at the wrong beat");
        AssertNear(partial[1].EndBeat, 4, "second selected repeat span ends at the wrong beat");

        var complete = score.PerformanceSpansForMeasureRange(1, 2);
        Assert(complete.Count == 1, "a contiguous complete repeat was unnecessarily fragmented");
        AssertNear(complete[0].StartBeat, 0, "complete repeat span starts incorrectly");
        AssertNear(complete[0].EndBeat, 4, "complete repeat span ends incorrectly");
    }

    private static void DefaultAssessableRangeIsLinearAndRepeatSafe()
    {
        var occurrences = new[]
        {
            new ScoreMeasureOccurrence(0, 0, "1", 0, 0, 1, 1),
            new ScoreMeasureOccurrence(1, 1, "2", 1, 1, 1, 1),
            new ScoreMeasureOccurrence(2, 0, "1", 0, 2, 1, 2),
            new ScoreMeasureOccurrence(3, 1, "2", 1, 3, 1, 2),
            new ScoreMeasureOccurrence(4, 2, "3", 2, 4, 1, 1),
            new ScoreMeasureOccurrence(5, 3, "4", 3, 5, 1, 1)
        };
        var notes = occurrences.Select(occurrence => new ScoreNote(
            60 + occurrence.SourceMeasureIndex,
            occurrence.PerformanceStartBeat,
            1,
            1,
            occurrence.MeasureNumber,
            "C",
            4,
            0,
            "quarter",
            0,
            "1",
            "up",
            false,
            [],
            null,
            false,
            false,
            occurrence.OccurrenceIndex,
            PartId: "P1",
            SourceMeasureIndex: occurrence.SourceMeasureIndex,
            SourceOnsetBeats: occurrence.SourceStartBeat)).ToArray();
        var score = Score(
            occurrences,
            [new ScoreTempoChange(0, 120, "1", 0)],
            [new ScoreMeterChange(0, 4, 4, "1", 0)],
            notes,
            [new ScoreValidationWarning("blocked-measure", "fixture", 2, 2, true)]);

        var range = score.LargestAssessableRange(PracticeMode.BothHands);
        Assert(range is { StartMeasure: 3, EndMeasure: 4, GroupCount: 2 },
            "default assessment range retained part of a blocked repeat or chose a smaller range");
    }

    private static ScoreDocument Score(
        IReadOnlyList<ScoreMeasureOccurrence> occurrences,
        IReadOnlyList<ScoreTempoChange> tempos,
        IReadOnlyList<ScoreMeterChange> meters,
        IReadOnlyList<ScoreNote>? notes = null,
        IReadOnlyList<ScoreValidationWarning>? warnings = null) =>
        new()
        {
            SourcePath = "timeline.musicxml",
            Title = "Timeline fixture",
            TempoBpm = tempos[0].Bpm,
            BeatsPerMeasure = meters[0].Beats,
            BeatType = meters[0].BeatType,
            MeasureCount = occurrences.Select(item => item.SourceMeasureIndex).Distinct().Count(),
            TotalBeats = occurrences
                .GroupBy(item => item.SourceMeasureIndex)
                .Select(group => group.First().DurationBeats)
                .Sum(),
            PerformanceMeasures = occurrences,
            TempoChanges = tempos,
            MeterChanges = meters,
            Notes = notes ?? [],
            ValidationWarnings = warnings ?? []
        };

    private static void AssertNear(double actual, double expected, string message)
    {
        if (Math.Abs(actual - expected) > 0.0001)
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
