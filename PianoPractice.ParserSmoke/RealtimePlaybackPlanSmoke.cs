using System.Runtime.CompilerServices;
using PianoPractice.Desktop.Models;
using PianoPractice.Desktop.Services;

internal static class RealtimePlaybackPlanSmoke
{
    [ModuleInitializer]
    internal static void Run()
    {
        var score = new ScoreDocument
        {
            SourcePath = "realtime-playback.musicxml",
            Title = "Real-time playback fixture",
            TempoBpm = 120,
            BeatsPerMeasure = 4,
            BeatType = 4,
            MeasureCount = 1,
            TotalBeats = 4,
            PerformanceMeasures =
            [
                new ScoreMeasureOccurrence(0, 0, "1", 0, 0, 4, 1)
            ],
            TempoChanges =
            [
                new ScoreTempoChange(0, 120, "1", 0)
            ],
            MeterChanges =
            [
                new ScoreMeterChange(0, 4, 4, "1", 0)
            ],
            Notes =
            [
                new ScoreNote(
                    60,
                    0,
                    1,
                    1,
                    "1",
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
                    0,
                    PartId: "P1",
                    SourceMeasureIndex: 0,
                    SourceOnsetBeats: 0,
                    Velocity: 96)
            ]
        };

        using var audio = new PianoAudioService();
        var normalPayload = audio.BuildPreviewAsync(
                score,
                includeMetronome: true,
                startBeat: 0,
                endBeat: 4,
                tempoBpm: 120,
                pianoVolumePercent: 100,
                metronomeVolumePercent: 70,
                includedStaffNumber: null,
                cancellationToken: CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        if (!audio.TryGetPreparedPlaybackInfo(normalPayload, out var normalPlan))
            throw new InvalidOperationException("Score playback did not produce a real-time event plan.");
        if (Math.Abs(normalPlan.DurationSeconds - 2) > 0.001 ||
            normalPlan.PianoNoteCount != 1 ||
            normalPlan.MetronomePulseCount != 4 ||
            !normalPlan.HasImmediatePianoEvent)
        {
            throw new InvalidOperationException(
                $"Real-time playback plan mismatch: duration={normalPlan.DurationSeconds:0.###}, " +
                $"notes={normalPlan.PianoNoteCount}, clicks={normalPlan.MetronomePulseCount}, " +
                $"immediate={normalPlan.HasImmediatePianoEvent}.");
        }

        var slowPayload = audio.BuildPreviewAsync(
                score,
                includeMetronome: false,
                startBeat: 0,
                endBeat: 4,
                tempoBpm: 60,
                pianoVolumePercent: 100,
                metronomeVolumePercent: 0,
                includedStaffNumber: null,
                cancellationToken: CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        if (!audio.TryGetPreparedPlaybackInfo(slowPayload, out var slowPlan) ||
            Math.Abs(slowPlan.DurationSeconds - 4) > 0.001)
        {
            throw new InvalidOperationException("Real-time playback planning did not honor the requested effective tempo.");
        }

        var fullPcmBytes = normalPlan.DurationSeconds * 22050d * 2d;
        if (normalPayload.Length >= fullPcmBytes / 8d)
        {
            throw new InvalidOperationException(
                "The prepared playback payload is still large enough to resemble full-song PCM rendering.");
        }
    }
}
