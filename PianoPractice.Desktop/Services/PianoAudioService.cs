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
        BuildPreviewAsync(score, includeMetronome, 0, score.TotalBeats, cancellationToken);

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
                cancellationToken),
            cancellationToken);

    public Task<byte[]> BuildMidiPreviewAsync(MidiReference midi, IReadOnlySet<int> trackIndexes, bool includeMetronome, CancellationToken cancellationToken) =>
        BuildMidiPreviewAsync(midi, trackIndexes, includeMetronome, 100, 70, cancellationToken);

    public Task<byte[]> BuildMidiPreviewAsync(
        MidiReference midi,
        IReadOnlySet<int> trackIndexes,
        bool includeMetronome,
        int pianoVolumePercent,
        int metronomeVolumePercent,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => BuildMidiPreview(
                midi,
                trackIndexes,
                includeMetronome,
                pianoVolumePercent,
                metronomeVolumePercent,
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
        _clickPlayer?.Stop();
        _clickPlayer?.Dispose();
        _clickStream?.Dispose();
        _clickStream = new MemoryStream(CreateClickWave(accent, volumePercent), writable: false);
        _clickPlayer = new SoundPlayer(_clickStream);
        _clickPlayer.Load();
        _clickPlayer.Play();
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
        CancellationToken cancellationToken)
    {
        startBeat = Math.Clamp(startBeat, 0, score.TotalBeats);
        endBeat = Math.Clamp(endBeat, startBeat + 0.01, Math.Max(startBeat + 0.01, score.TotalBeats));
        var beats = endBeat - startBeat;
        var secondsPerBeat = 60d / Math.Max(1d, tempoBpm);
        var totalSamples = Math.Clamp((int)Math.Ceiling((beats + 1.5) * secondsPerBeat * SampleRate), SampleRate, SampleRate * 600);
        var samples = new float[totalSamples];

        var pianoGain = Math.Clamp(pianoVolumePercent, 0, 100) / 100d;
        var metronomeGain = Math.Clamp(metronomeVolumePercent, 0, 100) / 100d;
        foreach (var note in score.Notes.Where(note =>
                     note.OnsetBeats >= startBeat &&
                     note.OnsetBeats < endBeat &&
                     (!includedStaffNumber.HasValue || note.StaffNumber == includedStaffNumber.Value)))
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
                cancellationToken);
        }

        if (includeMetronome)
        {
            for (var beat = Math.Ceiling(startBeat); beat <= endBeat; beat += 1d)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddClick(samples, (int)((beat - startBeat) * secondsPerBeat * SampleRate), beat % 4 == 0, metronomeGain);
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
            var attack = Math.Min(1d, time / 0.008d);
            var release = time <= durationSeconds ? 1d : Math.Exp(-(time - durationSeconds) * 7d);
            var envelope = attack * Math.Exp(-time * 2.4d) * release * 0.12d * level * gain;
            var fundamental = Math.Sin(2d * Math.PI * frequency * time);
            var secondHarmonic = Math.Sin(2d * Math.PI * frequency * 2d * time) * 0.32d;
            var thirdHarmonic = Math.Sin(2d * Math.PI * frequency * 3d * time) * 0.13d;
            samples[start + index] += (float)((fundamental + secondHarmonic + thirdHarmonic) * envelope);
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
            writer.Write((short)(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
        }

        return stream.ToArray();
    }
}
