using System.Runtime.CompilerServices;
using PianoPractice.Desktop.Models;
using PianoPractice.Desktop.Services;

internal static class AudioRenderSmoke
{
    [ModuleInitializer]
    internal static void Run()
    {
        var score = new ScoreDocument
        {
            SourcePath = "deterministic.musicxml",
            Title = "Audio timing fixture",
            TempoBpm = 120,
            BeatsPerMeasure = 4,
            BeatType = 4,
            MeasureCount = 2,
            TotalBeats = 5.5,
            Measures =
            [
                new MeasureSummary("1", 1, 0, 0, 0, 1, 0, "120 BPM", 0, 4),
                new MeasureSummary("2", 1, 0, 0, 0, 1, 0, "60 BPM", 4, 1.5)
            ],
            PerformanceMeasures =
            [
                new ScoreMeasureOccurrence(0, 0, "1", 0, 0, 4, 1),
                new ScoreMeasureOccurrence(1, 1, "2", 4, 4, 1.5, 1)
            ],
            TempoChanges =
            [
                new ScoreTempoChange(0, 120, "1", 0),
                new ScoreTempoChange(4, 60, "2", 1)
            ],
            MeterChanges =
            [
                new ScoreMeterChange(0, 4, 4, "1", 0),
                new ScoreMeterChange(4, 3, 8, "2", 1)
            ],
            Notes =
            [
                Note(60, 0, 2, "1", 0),
                Note(64, 4, 1.5, "2", 1)
            ]
        };

        using var audio = new PianoAudioService();
        var fullWave = audio.BuildPreviewAsync(
                score,
                includeMetronome: true,
                startBeat: 0,
                endBeat: score.TotalPerformanceBeats,
                tempoBpm: 120,
                pianoVolumePercent: 100,
                metronomeVolumePercent: 70,
                includedStaffNumber: null,
                cancellationToken: CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertWave(fullWave, minimumSeconds: 4.7, "full tempo-map preview");

        var seekWave = audio.BuildPreviewAsync(
                score,
                includeMetronome: false,
                startBeat: 1,
                endBeat: 3,
                tempoBpm: 120,
                pianoVolumePercent: 100,
                metronomeVolumePercent: 0,
                includedStaffNumber: null,
                cancellationToken: CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertWave(seekWave, minimumSeconds: 2.2, "seek preview");
        if (!ContainsAudibleSampleNearStart(seekWave))
            throw new InvalidOperationException("A note sustaining across the seek point was omitted from the rendered audio.");
    }

    private static ScoreNote Note(
        int midi,
        double onset,
        double duration,
        string measure,
        int sourceMeasureIndex) =>
        new(
            midi,
            onset,
            duration,
            1,
            measure,
            midi == 60 ? "C" : "E",
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
            sourceMeasureIndex,
            PartId: "P1",
            SourceMeasureIndex: sourceMeasureIndex,
            SourceOnsetBeats: onset,
            Velocity: 96);

    private static void AssertWave(byte[] wave, double minimumSeconds, string name)
    {
        if (wave.Length < 44 ||
            System.Text.Encoding.ASCII.GetString(wave, 0, 4) != "RIFF" ||
            System.Text.Encoding.ASCII.GetString(wave, 8, 4) != "WAVE")
            throw new InvalidOperationException($"{name} did not produce a valid PCM WAVE header.");

        var sampleRate = BitConverter.ToInt32(wave, 24);
        var dataBytes = BitConverter.ToInt32(wave, 40);
        var seconds = dataBytes / (sampleRate * 2d);
        if (seconds < minimumSeconds)
            throw new InvalidOperationException($"{name} was truncated to {seconds:0.###} seconds.");
    }

    private static bool ContainsAudibleSampleNearStart(byte[] wave)
    {
        var end = Math.Min(wave.Length - 1, 44 + 22050 / 2);
        for (var offset = 44; offset + 1 < end; offset += 2)
        {
            if (Math.Abs(BitConverter.ToInt16(wave, offset)) > 8)
                return true;
        }
        return false;
    }
}
