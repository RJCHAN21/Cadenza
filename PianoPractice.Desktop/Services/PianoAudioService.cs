using System.Collections.Concurrent;
using System.IO;
using System.Media;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop.Services;

public sealed class PianoAudioService : IDisposable
{
    private const int SampleRate = 22050;
    private const double ReleaseTailSeconds = 1.25;
    private const double MaxPreviewSeconds = 30 * 60;

    private readonly ConcurrentDictionary<Guid, (SoundPlayer Player, MemoryStream Stream)> _livePlayers = new();
    private SoundPlayer? _previewPlayer;
    private MemoryStream? _previewStream;
    private SoundPlayer? _clickPlayer;
    private MemoryStream? _clickStream;
    private bool _disposed;

    public event EventHandler<string>? AudioError;

    public Task<byte[]> BuildPreviewAsync(
        ScoreDocument score,
        bool includeMetronome,
        CancellationToken cancellationToken) =>
        BuildPreviewAsync(score, includeMetronome, 0, score.TotalPerformanceBeats, cancellationToken);

    public Task<byte[]> BuildPreviewAsync(
        ScoreDocument score,
        bool includeMetronome,
        double startBeat,
        double endBeat,
        CancellationToken cancellationToken) =>
        BuildPreviewAsync(score, includeMetronome, startBeat, endBeat, score.TempoBpm, cancellationToken);

    public Task<byte[]> BuildPreviewAsync(
        ScoreDocument score,
        bool includeMetronome,
        double startBeat,
        double endBeat,
        double tempoBpm,
        CancellationToken cancellationToken) =>
        BuildPreviewAsync(score, includeMetronome, startBeat, endBeat, tempoBpm, 100, 70, null, cancellationToken);

    public Task<byte[]> BuildPreviewAsync(
        ScoreDocument score,
        bool includeMetronome,
        double startBeat,
        double endBeat,
        double tempoBpm,
        int pianoVolumePercent,
        int metronomeVolumePercent,
        int? includedStaffNumber,
        CancellationToken cancellationToken) =>
        BuildPreviewAsync(
            score,
            includeMetronome,
            startBeat,
            endBeat,
            tempoBpm,
            pianoVolumePercent,
            metronomeVolumePercent,
            includedStaffNumber,
            "acoustic_grand",
            cancellationToken);

    public Task<byte[]> BuildPreviewAsync(
        ScoreDocument score,
        bool includeMetronome,
        double startBeat,
        double endBeat,
        double tempoBpm,
        int pianoVolumePercent,
        int metronomeVolumePercent,
        int? includedStaffNumber,
        string presetId,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => BuildPreview(
                score,
                includeMetronome,
                startBeat,
                endBeat,
                tempoBpm,
                pianoVolumePercent,
                metronomeVolumePercent,
                includedStaffNumber,
                presetId,
                cancellationToken),
            cancellationToken);

    public Task<byte[]> BuildMidiPreviewAsync(
        MidiReference midi,
        IReadOnlySet<int> trackIndexes,
        bool includeMetronome,
        CancellationToken cancellationToken) =>
        BuildMidiPreviewAsync(midi, trackIndexes, includeMetronome, 100, 70, "acoustic_grand", cancellationToken);

    public Task<byte[]> BuildMidiPreviewAsync(
        MidiReference midi,
        IReadOnlySet<int> trackIndexes,
        bool includeMetronome,
        int pianoVolumePercent,
        int metronomeVolumePercent,
        CancellationToken cancellationToken) =>
        BuildMidiPreviewAsync(
            midi,
            trackIndexes,
            includeMetronome,
            pianoVolumePercent,
            metronomeVolumePercent,
            "acoustic_grand",
            cancellationToken);

    public Task<byte[]> BuildMidiPreviewAsync(
        MidiReference midi,
        IReadOnlySet<int> trackIndexes,
        bool includeMetronome,
        int pianoVolumePercent,
        int metronomeVolumePercent,
        string presetId,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => BuildMidiPreview(
                midi,
                trackIndexes,
                includeMetronome,
                pianoVolumePercent,
                metronomeVolumePercent,
                presetId,
                cancellationToken),
            cancellationToken);

    public void PlayPreview(byte[] waveData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(waveData);
        StopPreview();

        _previewStream = new MemoryStream(waveData, writable: false);
        _previewPlayer = new SoundPlayer(_previewStream);
        _previewPlayer.Load();
        _previewPlayer.Play();
    }

    public void StopPreview()
    {
        _previewPlayer?.Stop();
        _previewPlayer?.Dispose();
        _previewPlayer = null;
        _previewStream?.Dispose();
        _previewStream = null;
    }

    public void PlayMetronomeClick(bool accent, int volumePercent = 70)
    {
        if (_disposed || _previewPlayer is not null)
            return;

        _clickPlayer?.Stop();
        _clickPlayer?.Dispose();
        _clickStream?.Dispose();

        _clickStream = new MemoryStream(CreateClickWave(accent, volumePercent), writable: false);
        _clickPlayer = new SoundPlayer(_clickStream);
        _clickPlayer.Load();
        _clickPlayer.Play();
    }

    public void PlayLiveNote(int midiNote, int velocity, int volumePercent, string presetId)
    {
        if (_disposed || volumePercent <= 0 || _previewPlayer is not null)
            return;

        var id = Guid.NewGuid();
        _ = Task.Run(() =>
        {
            SoundPlayer? player = null;
            MemoryStream? stream = null;
            try
            {
                var samples = new float[(int)(1.25 * SampleRate)];
                AddPianoVoice(
                    samples,
                    midiNote,
                    velocity,
                    onsetSeconds: 0,
                    soundingDurationSeconds: 0.8,
                    elapsedSeconds: 0,
                    gain: Math.Clamp(volumePercent, 0, 100) / 100d,
                    presetId,
                    CancellationToken.None);

                stream = new MemoryStream(ToWaveFile(samples), writable: false);
                player = new SoundPlayer(stream);
                _livePlayers[id] = (player, stream);
                player.Load();
                player.PlaySync();
            }
            catch (Exception exception)
            {
                AudioError?.Invoke(this, $"Live note playback failed: {exception.Message}");
            }
            finally
            {
                _livePlayers.TryRemove(id, out _);
                player?.Dispose();
                stream?.Dispose();
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        StopPreview();
        _clickPlayer?.Stop();
        _clickPlayer?.Dispose();
        _clickStream?.Dispose();
        _clickPlayer = null;
        _clickStream = null;

        foreach (var (_, entry) in _livePlayers)
        {
            entry.Player.Stop();
            entry.Player.Dispose();
            entry.Stream.Dispose();
        }
        _livePlayers.Clear();
    }

    private static byte[] BuildPreview(
        ScoreDocument score,
        bool includeMetronome,
        double startBeat,
        double endBeat,
        double requestedInitialTempoBpm,
        int pianoVolumePercent,
        int metronomeVolumePercent,
        int? includedStaffNumber,
        string presetId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(score);

        var maxBeats = Math.Max(0.01, score.TotalPerformanceBeats);
        startBeat = Math.Clamp(startBeat, 0, maxBeats);
        if (startBeat >= maxBeats - 1e-9)
            startBeat = Math.Max(0, maxBeats - 0.01);
        endBeat = Math.Clamp(endBeat, startBeat + 0.01, maxBeats);

        var tempoScale = Math.Max(0.01, requestedInitialTempoBpm / Math.Max(1d, score.TempoBpm));
        var selectionStartSeconds = score.SecondsAtPerformanceBeat(startBeat, tempoScale);
        var selectionEndSeconds = score.SecondsAtPerformanceBeat(endBeat, tempoScale);
        var selectionSeconds = Math.Max(0.01, selectionEndSeconds - selectionStartSeconds);
        if (selectionSeconds > MaxPreviewSeconds)
            throw new InvalidOperationException(
                $"The requested preview is {selectionSeconds / 60d:0.0} minutes long and exceeds the {MaxPreviewSeconds / 60d:0} minute safety limit.");

        var totalSamplesLong = checked((long)Math.Ceiling((selectionSeconds + ReleaseTailSeconds) * SampleRate));
        if (totalSamplesLong > int.MaxValue)
            throw new InvalidOperationException("The requested preview is too large to render safely.");

        var samples = new float[(int)Math.Max(SampleRate, totalSamplesLong)];
        var pianoGain = Math.Clamp(pianoVolumePercent, 0, 100) / 100d;
        var metronomeGain = Math.Clamp(metronomeVolumePercent, 0, 100) / 100d;

        foreach (var note in score.Notes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (includedStaffNumber.HasValue &&
                ScoreDocument.ResolveStaffNumber(note.StaffNumber, note.MidiNoteNumber) != includedStaffNumber.Value)
                continue;

            var noteEndBeat = note.OnsetBeats + Math.Max(0.01, note.DurationBeats);
            if (noteEndBeat <= startBeat + 1e-9 || note.OnsetBeats >= endBeat - 1e-9)
                continue;

            var absoluteOnsetSeconds = score.SecondsAtPerformanceBeat(note.OnsetBeats, tempoScale);
            var absoluteEndSeconds = score.SecondsAtPerformanceBeat(noteEndBeat, tempoScale);
            var relativeOnsetSeconds = absoluteOnsetSeconds - selectionStartSeconds;
            var soundingDurationSeconds = Math.Max(0.05, absoluteEndSeconds - absoluteOnsetSeconds);
            var elapsedSeconds = relativeOnsetSeconds < 0 ? -relativeOnsetSeconds : 0;
            var outputOnsetSeconds = Math.Max(0, relativeOnsetSeconds);

            AddPianoVoice(
                samples,
                note.MidiNoteNumber,
                note.Velocity,
                outputOnsetSeconds,
                soundingDurationSeconds,
                elapsedSeconds,
                pianoGain,
                presetId,
                cancellationToken);
        }

        if (includeMetronome && metronomeGain > 0)
            AddScoreMetronome(
                samples,
                score,
                startBeat,
                endBeat,
                selectionStartSeconds,
                tempoScale,
                metronomeGain,
                cancellationToken);

        return ToWaveFile(samples);
    }

    private static void AddScoreMetronome(
        float[] samples,
        ScoreDocument score,
        double startBeat,
        double endBeat,
        double selectionStartSeconds,
        double tempoScale,
        double gain,
        CancellationToken cancellationToken)
    {
        var scheduledSamples = new HashSet<int>();
        IReadOnlyList<ScoreMeasureOccurrence> occurrences = score.PerformanceMeasures.Count > 0
            ? score.PerformanceMeasures
            : [new ScoreMeasureOccurrence(0, 0, "1", 0, 0, score.TotalPerformanceBeats, 1)];

        foreach (var occurrence in occurrences)
        {
            var occurrenceEnd = occurrence.PerformanceStartBeat + occurrence.DurationBeats;
            if (occurrenceEnd < startBeat - 1e-9 || occurrence.PerformanceStartBeat > endBeat + 1e-9)
                continue;

            var meter = score.MeterAtBeat(occurrence.PerformanceStartBeat);
            var quarterBeatsPerPulse = 4d / Math.Max(1, meter.BeatType);
            if (!double.IsFinite(quarterBeatsPerPulse) || quarterBeatsPerPulse <= 0)
                quarterBeatsPerPulse = 1d;

            var pulseIndex = 0;
            for (var offset = 0d; offset < occurrence.DurationBeats - 1e-9; offset += quarterBeatsPerPulse)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var beat = occurrence.PerformanceStartBeat + offset;
                if (beat < startBeat - 1e-9 || beat > endBeat + 1e-9)
                {
                    pulseIndex++;
                    continue;
                }

                var absoluteSeconds = score.SecondsAtPerformanceBeat(beat, tempoScale);
                var sample = (int)Math.Round((absoluteSeconds - selectionStartSeconds) * SampleRate);
                if (sample >= 0 && sample < samples.Length && scheduledSamples.Add(sample))
                    AddClick(samples, sample, pulseIndex == 0, gain);
                pulseIndex++;
            }
        }
    }

    private static byte[] BuildMidiPreview(
        MidiReference midi,
        IReadOnlySet<int> trackIndexes,
        bool includeMetronome,
        int pianoVolumePercent,
        int metronomeVolumePercent,
        string presetId,
        CancellationToken cancellationToken)
    {
        var selectedNotes = midi.Notes
            .Where(note => trackIndexes.Contains(note.TrackIndex) && note.Channel != 9)
            .ToArray();
        var beats = Math.Max(1, selectedNotes.Length == 0
            ? 1
            : selectedNotes.Max(note => note.OnsetBeats + note.DurationBeats));
        var secondsPerBeat = 60d / Math.Max(1d, midi.TempoBpm);
        var totalSeconds = beats * secondsPerBeat + ReleaseTailSeconds;
        if (totalSeconds > MaxPreviewSeconds)
            throw new InvalidOperationException("The MIDI preview exceeds the safe render duration.");

        var samples = new float[(int)Math.Max(SampleRate, Math.Ceiling(totalSeconds * SampleRate))];
        foreach (var note in selectedNotes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddPianoVoice(
                samples,
                note.NoteNumber,
                note.Velocity,
                note.OnsetBeats * secondsPerBeat,
                Math.Max(0.05, note.DurationBeats * secondsPerBeat),
                0,
                Math.Clamp(pianoVolumePercent, 0, 100) / 100d,
                presetId,
                cancellationToken);
        }

        if (includeMetronome)
        {
            for (var beat = 0d; beat <= beats + 1e-9; beat += 1d)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddClick(
                    samples,
                    (int)Math.Round(beat * secondsPerBeat * SampleRate),
                    Math.Abs(beat % 4d) < 1e-9,
                    Math.Clamp(metronomeVolumePercent, 0, 100) / 100d);
            }
        }

        return ToWaveFile(samples);
    }

    private static void AddPianoVoice(
        float[] samples,
        int midiNote,
        int velocity,
        double onsetSeconds,
        double soundingDurationSeconds,
        double elapsedSeconds,
        double gain,
        string presetId,
        CancellationToken cancellationToken)
    {
        var start = Math.Max(0, (int)Math.Round(onsetSeconds * SampleRate));
        var durationSeconds = Math.Max(0.05, soundingDurationSeconds);
        var voiceSeconds = Math.Max(0.12, durationSeconds + ReleaseTailSeconds - elapsedSeconds);
        var length = Math.Min(samples.Length - start, (int)Math.Ceiling(voiceSeconds * SampleRate));
        if (length <= 0)
            return;

        var frequency = 440d * Math.Pow(2d, (midiNote - 69) / 12d);
        var level = Math.Clamp(velocity / 127d, 0.05, 1d);
        for (var index = 0; index < length; index++)
        {
            if ((index & 2047) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var time = elapsedSeconds + index / (double)SampleRate;
            var sampleValue = PianoSample(presetId, frequency, time, durationSeconds, level, gain);
            samples[start + index] += (float)sampleValue;
        }
    }

    private static double PianoSample(
        string presetId,
        double frequency,
        double time,
        double durationSeconds,
        double level,
        double gain)
    {
        var release = time <= durationSeconds
            ? 1d
            : Math.Exp(-(time - durationSeconds) * (presetId == "church_organ" ? 15d : 6d));

        if (presetId == "soft_synth")
        {
            var attack = Math.Min(1d, time / 0.008d);
            var envelope = attack * Math.Exp(-time * 2.4d) * release * 0.12d * level * gain;
            return (
                Math.Sin(2d * Math.PI * frequency * time) +
                Math.Sin(2d * Math.PI * frequency * 2d * time) * 0.32d +
                Math.Sin(2d * Math.PI * frequency * 3d * time) * 0.13d) * envelope;
        }

        if (presetId == "bright_piano")
        {
            var attack = Math.Min(1d, time / 0.002d);
            var envelope = attack * Math.Exp(-time * 2d) * release * 0.14d * level * gain;
            return (
                Math.Sin(2d * Math.PI * frequency * time) +
                Math.Sin(4d * Math.PI * frequency * time) * 0.55d +
                Math.Sin(6d * Math.PI * frequency * time) * 0.35d +
                Math.Sin(8d * Math.PI * frequency * time) * 0.20d) * envelope;
        }

        if (presetId == "electric_grand")
        {
            var attack = Math.Min(1d, time / 0.003d);
            var envelope = attack * Math.Exp(-time * 1.9d) * release * 0.14d * level * gain;
            return (
                Math.Sin(2d * Math.PI * frequency * time) +
                Math.Sin(4d * Math.PI * frequency * time) * 0.40d +
                Math.Sin(3d * Math.PI * frequency * time) * 0.25d) * envelope;
        }

        if (presetId == "honky_tonk")
        {
            var attack = Math.Min(1d, time / 0.004d);
            var envelope = attack * Math.Exp(-time * 2.2d) * release * 0.13d * level * gain;
            return (
                Math.Sin(2d * Math.PI * frequency * time) +
                Math.Sin(2d * Math.PI * frequency * 1.004d * time) * 0.85d +
                Math.Sin(4d * Math.PI * frequency * time) * 0.35d) * envelope;
        }

        if (presetId == "electric_piano")
        {
            var attack = Math.Min(1d, time / 0.003d);
            var envelope = attack * Math.Exp(-time * 1.8d) * release * 0.14d * level * gain;
            var tine = Math.Sin(2d * Math.PI * frequency * 7d * time) * Math.Exp(-time * 25d) * 0.35d;
            return (
                Math.Sin(2d * Math.PI * frequency * time) +
                Math.Sin(4d * Math.PI * frequency * time) * 0.20d +
                tine) * envelope;
        }

        if (presetId == "harpsichord")
        {
            var attack = Math.Min(1d, time / 0.001d);
            var envelope = attack * Math.Exp(-time * 3.5d) * release * 0.11d * level * gain;
            return (
                Math.Sin(2d * Math.PI * frequency * time) +
                Math.Sin(4d * Math.PI * frequency * time) * 0.60d +
                Math.Sin(6d * Math.PI * frequency * time) * 0.40d +
                Math.Sin(8d * Math.PI * frequency * time) * 0.25d) * envelope;
        }

        if (presetId == "church_organ")
        {
            var attack = Math.Min(1d, time / 0.035d);
            var envelope = attack * release * 0.08d * level * gain;
            return (
                Math.Sin(Math.PI * frequency * time) * 0.40d +
                Math.Sin(2d * Math.PI * frequency * time) +
                Math.Sin(4d * Math.PI * frequency * time) * 0.50d +
                Math.Sin(6d * Math.PI * frequency * time) * 0.30d +
                Math.Sin(8d * Math.PI * frequency * time) * 0.20d) * envelope;
        }

        const double stiffness = 0.00025d;
        var acousticAttack = Math.Min(1d, time / 0.0035d);
        var bodyDecay = Math.Exp(-time * 1.8d) * 0.65d + Math.Exp(-time * 0.35d) * 0.35d;
        var acousticEnvelope = acousticAttack * bodyDecay * release * 0.13d * level * gain;
        var sum = Math.Sin(2d * Math.PI * frequency * Math.Sqrt(1d + stiffness) * time);
        var weights = new[] { 0d, 0d, 0.42d, 0.28d, 0.18d, 0.12d, 0.08d, 0.05d, 0.03d };
        for (var harmonic = 2; harmonic <= 8; harmonic++)
        {
            var partialFrequency = harmonic * frequency *
                                   Math.Sqrt(1d + stiffness * harmonic * harmonic);
            sum += Math.Sin(2d * Math.PI * partialFrequency * time) * weights[harmonic];
        }

        sum += Math.Sin(2d * Math.PI * frequency * 1.0018d * time) * 0.14d;
        sum += Math.Sin(2d * Math.PI * frequency * 14.5d * time) *
               Math.Exp(-time * 110d) * 0.22d;
        return sum * acousticEnvelope;
    }

    private static void AddClick(float[] samples, int start, bool accent, double gain)
    {
        var length = Math.Min(samples.Length - start, (int)((accent ? 0.09 : 0.065) * SampleRate));
        if (length <= 0)
            return;

        var frequency = accent ? 1320d : 880d;
        for (var index = 0; index < length; index++)
        {
            var time = index / (double)SampleRate;
            var envelope = Math.Exp(-time * 48d) * (accent ? 0.25d : 0.18d) * gain;
            samples[start + index] += (float)(Math.Sin(2d * Math.PI * frequency * time) * envelope);
        }
    }

    private static byte[] CreateClickWave(bool accent, int volumePercent)
    {
        var seconds = accent ? 0.09 : 0.065;
        var samples = new float[(int)(seconds * SampleRate)];
        AddClick(samples, 0, accent, Math.Clamp(volumePercent, 0, 100) / 100d);
        return ToWaveFile(samples);
    }

    private static byte[] ToWaveFile(float[] samples)
    {
        using var stream = new MemoryStream(44 + samples.Length * 2);
        using var writer = new BinaryWriter(stream);
        writer.Write(new[] { 'R', 'I', 'F', 'F' });
        writer.Write(36 + samples.Length * 2);
        writer.Write(new[] { 'W', 'A', 'V', 'E' });
        writer.Write(new[] { 'f', 'm', 't', ' ' });
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(new[] { 'd', 'a', 't', 'a' });
        writer.Write(samples.Length * 2);

        foreach (var sample in samples)
        {
            var softSample = (float)Math.Tanh(sample);
            writer.Write((short)Math.Clamp(
                softSample * short.MaxValue,
                short.MinValue,
                short.MaxValue));
        }

        return stream.ToArray();
    }
}
