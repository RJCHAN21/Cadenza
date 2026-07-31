using System.IO;
using System.Media;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop.Services;

public sealed class PianoAudioService : IDisposable
{
    private const int SampleRate = 22050;
    private SoundPlayer? _previewPlayer;
    private MemoryStream? _previewStream;
    private SoundPlayer? _clickPlayer;
    private MemoryStream? _clickStream;

    public Task<byte[]> BuildPreviewAsync(ScoreDocument score, bool includeMetronome, CancellationToken cancellationToken) =>
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

    public Task<byte[]> BuildMidiPreviewAsync(MidiReference midi, IReadOnlySet<int> trackIndexes, bool includeMetronome, CancellationToken cancellationToken) =>
        BuildMidiPreviewAsync(midi, trackIndexes, includeMetronome, 100, 70, "acoustic_grand", cancellationToken);

    public Task<byte[]> BuildMidiPreviewAsync(
        MidiReference midi,
        IReadOnlySet<int> trackIndexes,
        bool includeMetronome,
        int pianoVolumePercent,
        int metronomeVolumePercent,
        CancellationToken cancellationToken) =>
        BuildMidiPreviewAsync(midi, trackIndexes, includeMetronome, pianoVolumePercent, metronomeVolumePercent, "acoustic_grand", cancellationToken);

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
        if (_previewPlayer is not null) return;
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
        if (volumePercent <= 0 || _previewPlayer is not null) return;
        Task.Run(() =>
        {
            try
            {
                var seconds = 1.0;
                var samples = new float[(int)(seconds * SampleRate)];
                AddPianoVoice(
                    samples,
                    midiNote,
                    velocity,
                    0,
                    0.8,
                    1.0,
                    Math.Clamp(volumePercent, 0, 100) / 100d,
                    presetId,
                    CancellationToken.None);
                var wave = ToWaveFile(samples);
                using var ms = new MemoryStream(wave, writable: false);
                using var player = new SoundPlayer(ms);
                player.Load();
                player.Play();
            }
            catch { }
        });
    }

    public void Dispose()
    {
        StopPreview();
        _clickPlayer?.Stop();
        _clickPlayer?.Dispose();
        _clickStream?.Dispose();
    }

    private static byte[] BuildPreview(
        ScoreDocument score,
        bool includeMetronome,
        double startBeat,
        double endBeat,
        double tempoBpm,
        int pianoVolumePercent,
        int metronomeVolumePercent,
        int? includedStaffNumber,
        string presetId,
        CancellationToken cancellationToken)
    {
        var maxBeats = score.TotalPerformanceBeats;
        startBeat = Math.Clamp(startBeat, 0, maxBeats);
        endBeat = Math.Clamp(endBeat, startBeat + 0.01, Math.Max(startBeat + 0.01, maxBeats));
        var beats = endBeat - startBeat;
        var secondsPerBeat = 60d / Math.Max(1d, tempoBpm);
        var totalSamples = Math.Clamp((int)Math.Ceiling((beats + 1.5) * secondsPerBeat * SampleRate), SampleRate, SampleRate * 600);
        var samples = new float[totalSamples];

        var pianoGain = Math.Clamp(pianoVolumePercent, 0, 100) / 100d;
        var metronomeGain = Math.Clamp(metronomeVolumePercent, 0, 100) / 100d;

        foreach (var note in score.Notes)
        {
            if (includedStaffNumber.HasValue && note.StaffNumber != includedStaffNumber.Value) continue;
            if (note.OnsetBeats >= startBeat && note.OnsetBeats < endBeat)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddPianoVoice(
                    samples,
                    note.MidiNoteNumber,
                    96,
                    note.OnsetBeats - startBeat,
                    Math.Min(note.DurationBeats, endBeat - note.OnsetBeats),
                    secondsPerBeat,
                    pianoGain,
                    presetId,
                    cancellationToken);
            }
        }

        if (includeMetronome)
        {
            var beatsPerMeasure = Math.Max(1, score.BeatsPerMeasure);
            for (var beat = Math.Ceiling(startBeat); beat <= endBeat; beat += 1d)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddClick(samples, (int)((beat - startBeat) * secondsPerBeat * SampleRate), (int)beat % beatsPerMeasure == 0, metronomeGain);
            }
        }

        return ToWaveFile(samples);
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
        var selectedNotes = midi.Notes.Where(note => trackIndexes.Contains(note.TrackIndex) && note.Channel != 9).ToArray();
        var beats = Math.Max(1, selectedNotes.Length == 0 ? 1 : selectedNotes.Max(note => note.OnsetBeats + note.DurationBeats));
        var secondsPerBeat = 60d / Math.Max(1d, midi.TempoBpm);
        var totalSamples = Math.Clamp((int)Math.Ceiling((beats + 1.5) * secondsPerBeat * SampleRate), SampleRate, SampleRate * 600);
        var samples = new float[totalSamples];
        foreach (var note in selectedNotes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddPianoVoice(
                samples,
                note.NoteNumber,
                note.Velocity,
                note.OnsetBeats,
                note.DurationBeats,
                secondsPerBeat,
                Math.Clamp(pianoVolumePercent, 0, 100) / 100d,
                presetId,
                cancellationToken);
        }

        if (includeMetronome)
        {
            for (var beat = 0d; beat <= beats; beat += 1d)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddClick(
                    samples,
                    (int)(beat * secondsPerBeat * SampleRate),
                    beat % 4 == 0,
                    Math.Clamp(metronomeVolumePercent, 0, 100) / 100d);
            }
        }

        return ToWaveFile(samples);
    }

    private static void AddPianoVoice(
        float[] samples,
        int midiNote,
        int velocity,
        double onsetBeats,
        double durationBeats,
        double secondsPerBeat,
        double gain,
        string presetId,
        CancellationToken cancellationToken)
    {
        var start = Math.Max(0, (int)(onsetBeats * secondsPerBeat * SampleRate));
        var durationSeconds = Math.Max(0.12, durationBeats * secondsPerBeat);
        var voiceSeconds = Math.Min(3.5, durationSeconds + 0.4);
        var length = Math.Min(samples.Length - start, (int)(voiceSeconds * SampleRate));
        if (length <= 0) return;

        var frequency = 440d * Math.Pow(2d, (midiNote - 69) / 12d);
        var level = Math.Clamp(velocity / 127d, 0.15, 1d);
        for (var index = 0; index < length; index++)
        {
            if ((index & 2047) == 0) cancellationToken.ThrowIfCancellationRequested();
            var time = index / (double)SampleRate;

            double sampleVal;
            if (presetId == "soft_synth")
            {
                var attack = Math.Min(1d, time / 0.008d);
                var release = time <= durationSeconds ? 1d : Math.Exp(-(time - durationSeconds) * 7d);
                var envelope = attack * Math.Exp(-time * 2.4d) * release * 0.12d * level * gain;
                var fundamental = Math.Sin(2d * Math.PI * frequency * time);
                var secondHarmonic = Math.Sin(2d * Math.PI * frequency * 2d * time) * 0.32d;
                var thirdHarmonic = Math.Sin(2d * Math.PI * frequency * 3d * time) * 0.13d;
                sampleVal = (fundamental + secondHarmonic + thirdHarmonic) * envelope;
            }
            else if (presetId == "bright_piano")
            {
                var attack = Math.Min(1d, time / 0.002d);
                var release = time <= durationSeconds ? 1d : Math.Exp(-(time - durationSeconds) * 7d);
                var envelope = attack * Math.Exp(-time * 2.0d) * release * 0.14d * level * gain;
                var fundamental = Math.Sin(2d * Math.PI * frequency * time);
                var h2 = Math.Sin(4d * Math.PI * frequency * time) * 0.55d;
                var h3 = Math.Sin(6d * Math.PI * frequency * time) * 0.35d;
                var h4 = Math.Sin(8d * Math.PI * frequency * time) * 0.20d;
                sampleVal = (fundamental + h2 + h3 + h4) * envelope;
            }
            else if (presetId == "electric_grand")
            {
                var attack = Math.Min(1d, time / 0.003d);
                var release = time <= durationSeconds ? 1d : Math.Exp(-(time - durationSeconds) * 6d);
                var envelope = attack * Math.Exp(-time * 1.9d) * release * 0.14d * level * gain;
                var fundamental = Math.Sin(2d * Math.PI * frequency * time);
                var octave = Math.Sin(4d * Math.PI * frequency * time) * 0.40d;
                var subPulse = Math.Sin(3d * Math.PI * frequency * time) * 0.25d;
                sampleVal = (fundamental + octave + subPulse) * envelope;
            }
            else if (presetId == "honky_tonk")
            {
                var attack = Math.Min(1d, time / 0.004d);
                var release = time <= durationSeconds ? 1d : Math.Exp(-(time - durationSeconds) * 5d);
                var envelope = attack * Math.Exp(-time * 2.2d) * release * 0.13d * level * gain;
                var voice1 = Math.Sin(2d * Math.PI * frequency * time);
                var voice2 = Math.Sin(2d * Math.PI * (frequency * 1.004d) * time) * 0.85d;
                var h2 = Math.Sin(4d * Math.PI * frequency * time) * 0.35d;
                sampleVal = (voice1 + voice2 + h2) * envelope;
            }
            else if (presetId == "electric_piano")
            {
                var attack = Math.Min(1d, time / 0.003d);
                var release = time <= durationSeconds ? 1d : Math.Exp(-(time - durationSeconds) * 8d);
                var envelope = attack * Math.Exp(-time * 1.8d) * release * 0.14d * level * gain;
                var tine = Math.Sin(2d * Math.PI * frequency * 7d * time) * Math.Exp(-time * 25d) * 0.35d;
                var fundamental = Math.Sin(2d * Math.PI * frequency * time);
                var secondHarmonic = Math.Sin(4d * Math.PI * frequency * time) * 0.20d;
                sampleVal = (fundamental + secondHarmonic + tine) * envelope;
            }
            else if (presetId == "harpsichord")
            {
                var attack = Math.Min(1d, time / 0.001d);
                var release = time <= durationSeconds ? 1d : Math.Exp(-(time - durationSeconds) * 12d);
                var envelope = attack * Math.Exp(-time * 3.5d) * release * 0.11d * level * gain;
                var fundamental = Math.Sin(2d * Math.PI * frequency * time);
                var h2 = Math.Sin(4d * Math.PI * frequency * time) * 0.60d;
                var h3 = Math.Sin(6d * Math.PI * frequency * time) * 0.40d;
                var h4 = Math.Sin(8d * Math.PI * frequency * time) * 0.25d;
                sampleVal = (fundamental + h2 + h3 + h4) * envelope;
            }
            else if (presetId == "church_organ")
            {
                var attack = Math.Min(1d, time / 0.035d);
                var release = time <= durationSeconds ? 1d : Math.Exp(-(time - durationSeconds) * 15d);
                var envelope = attack * release * 0.08d * level * gain;
                var sub = Math.Sin(Math.PI * frequency * time) * 0.40d;
                var fundamental = Math.Sin(2d * Math.PI * frequency * time);
                var h2 = Math.Sin(4d * Math.PI * frequency * time) * 0.50d;
                var h3 = Math.Sin(6d * Math.PI * frequency * time) * 0.30d;
                var h4 = Math.Sin(8d * Math.PI * frequency * time) * 0.20d;
                sampleVal = (sub + fundamental + h2 + h3 + h4) * envelope;
            }
            else
            {
                // Acoustic Grand Piano (Physical Acoustic Model: 8 partials, inharmonicity dispersion, hammer attack transient & soundboard resonance)
                const double B = 0.00025d; // Physical string stiffness inharmonicity coefficient
                var attack = Math.Min(1d, time / 0.0035d);
                var release = time <= durationSeconds ? 1d : Math.Exp(-(time - durationSeconds) * 6d);
                var bodyDecay = Math.Exp(-time * 1.8d) * 0.65d + Math.Exp(-time * 0.35d) * 0.35d;
                var envelope = attack * bodyDecay * release * 0.13d * level * gain;

                var hammerNoise = Math.Sin(2d * Math.PI * frequency * 14.5d * time) * Math.Exp(-time * 110d) * 0.22d;
                var fundamental = Math.Sin(2d * Math.PI * (frequency * Math.Sqrt(1d + B)) * time);
                var h2 = Math.Sin(2d * Math.PI * (2d * frequency * Math.Sqrt(1d + B * 4d)) * time) * 0.42d;
                var h3 = Math.Sin(2d * Math.PI * (3d * frequency * Math.Sqrt(1d + B * 9d)) * time) * 0.28d;
                var h4 = Math.Sin(2d * Math.PI * (4d * frequency * Math.Sqrt(1d + B * 16d)) * time) * 0.18d;
                var h5 = Math.Sin(2d * Math.PI * (5d * frequency * Math.Sqrt(1d + B * 25d)) * time) * 0.12d;
                var h6 = Math.Sin(2d * Math.PI * (6d * frequency * Math.Sqrt(1d + B * 36d)) * time) * 0.08d;
                var h7 = Math.Sin(2d * Math.PI * (7d * frequency * Math.Sqrt(1d + B * 49d)) * time) * 0.05d;
                var h8 = Math.Sin(2d * Math.PI * (8d * frequency * Math.Sqrt(1d + B * 64d)) * time) * 0.03d;
                var stringBeating = Math.Sin(2d * Math.PI * (frequency * 1.0018d) * time) * 0.14d;

                sampleVal = (fundamental + h2 + h3 + h4 + h5 + h6 + h7 + h8 + stringBeating + hammerNoise) * envelope;
            }

            samples[start + index] += (float)sampleVal;
        }
    }

    private static void AddClick(float[] samples, int start, bool accent, double gain)
    {
        var length = Math.Min(samples.Length - start, (int)((accent ? 0.09 : 0.065) * SampleRate));
        if (length <= 0) return;
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
            writer.Write((short)(softSample * short.MaxValue));
        }

        return stream.ToArray();
    }
}
