using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Text;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop.Services;

public sealed class PianoAudioService : IDisposable
{
    private static readonly TimeSpan CompletedPlaybackReleaseTail = TimeSpan.FromMilliseconds(900);
    private const int CompatibilitySampleRate = 22050;
    private const double MaximumPlaybackSeconds = 30 * 60;
    private const int MaximumPlaybackEvents = 2_000_000;
    private const double CompatibilityReleaseTailSeconds = 1.25;
    private const int PlanMarkerOffset = 46;
    private const string PlanMarker = "CADENZA-RT-PLAN-1";

    private static readonly byte[] PlanMarkerBytes = Encoding.ASCII.GetBytes(PlanMarker);

    private readonly ConcurrentDictionary<Guid, PlaybackPlan> _pendingPlans = new();
    private readonly MidiOutSynthService _previewSynth = new();
    private readonly MidiOutSynthService _metronomeSynth = new();
    private readonly MidiOutSynthService _liveNoteSynth = new();
    private readonly object _previewGate = new();
    private readonly object _synthGate = new();
    private readonly Dictionary<int, int> _liveNoteReferenceCounts = [];

    private SoundPlayer? _legacyPreviewPlayer;
    private MemoryStream? _legacyPreviewStream;
    private CancellationTokenSource? _activePlaybackCancellation;
    private long _playbackGeneration;
    private bool _realtimePlaybackActive;
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
            AudioSoundPreset.AcousticGrand.Id,
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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(score);
        return Task.Run(
            () => PrepareScorePlayback(
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
    }

    public Task<byte[]> BuildMidiPreviewAsync(
        MidiReference midi,
        IReadOnlySet<int> trackIndexes,
        bool includeMetronome,
        CancellationToken cancellationToken) =>
        BuildMidiPreviewAsync(midi, trackIndexes, includeMetronome, 100, 70, AudioSoundPreset.AcousticGrand.Id, cancellationToken);

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
            AudioSoundPreset.AcousticGrand.Id,
            cancellationToken);

    public Task<byte[]> BuildMidiPreviewAsync(
        MidiReference midi,
        IReadOnlySet<int> trackIndexes,
        bool includeMetronome,
        int pianoVolumePercent,
        int metronomeVolumePercent,
        string presetId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(midi);
        ArgumentNullException.ThrowIfNull(trackIndexes);
        return Task.Run(
            () => PrepareMidiPlayback(
                midi,
                trackIndexes,
                includeMetronome,
                pianoVolumePercent,
                metronomeVolumePercent,
                presetId,
                cancellationToken),
            cancellationToken);
    }

    public void PlayPreview(byte[] playbackData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(playbackData);

        if (TryReadPlanId(playbackData, out var planId))
        {
            if (!_pendingPlans.TryRemove(planId, out var plan))
                throw new InvalidOperationException("The prepared real-time playback plan is no longer available.");

            StartRealtimePlayback(plan);
            return;
        }

        StopPreview();
        _legacyPreviewStream = new MemoryStream(playbackData, writable: false);
        _legacyPreviewPlayer = new SoundPlayer(_legacyPreviewStream);
        _legacyPreviewPlayer.Load();
        _legacyPreviewPlayer.Play();
    }

    public void StopPreview()
    {
        CancellationTokenSource? cancellation;
        lock (_previewGate)
        {
            _playbackGeneration++;
            cancellation = _activePlaybackCancellation;
            _activePlaybackCancellation = null;
            _realtimePlaybackActive = false;
        }

        cancellation?.Cancel();

        lock (_synthGate)
        {
            _previewSynth.AllNotesOff();
            _metronomeSynth.AllNotesOff();
        }

        _legacyPreviewPlayer?.Stop();
        _legacyPreviewPlayer?.Dispose();
        _legacyPreviewPlayer = null;
        _legacyPreviewStream?.Dispose();
        _legacyPreviewStream = null;
    }

    public void PlayMetronomeClick(bool accent, int volumePercent = 70)
    {
        if (_disposed || IsPreviewOutputActive())
            return;

        var note = accent ? 76 : 77;
        lock (_synthGate)
        {
            _metronomeSynth.VolumePercent = Math.Clamp(volumePercent, 0, 100);
            var result = _metronomeSynth.NoteOn(note, accent ? 118 : 96, channel: 9);
            if (!result.Success)
            {
                AudioError?.Invoke(this, $"Metronome output failed: {result.Message}");
                return;
            }
        }

        _ = ReleaseStandaloneClickAsync(note);
    }

    public void PlayLiveNote(int midiNote, int velocity, int volumePercent, string presetId)
    {
        if (_disposed || volumePercent <= 0 || IsPreviewOutputActive())
            return;

        var preset = AudioSoundPreset.FromId(presetId, AudioSoundPreset.AcousticGrand);
        var patch = ResolveRealtimePatch(preset);
        lock (_synthGate)
        {
            _liveNoteSynth.VolumePercent = Math.Clamp(volumePercent, 0, 100);
            var programResult = _liveNoteSynth.SetProgram(patch);
            if (!programResult.Success)
            {
                AudioError?.Invoke(this, $"Live note output failed: {programResult.Message}");
                return;
            }

            var noteResult = _liveNoteSynth.NoteOn(midiNote, velocity);
            if (!noteResult.Success)
            {
                AudioError?.Invoke(this, $"Live note output failed: {noteResult.Message}");
                return;
            }

            _liveNoteReferenceCounts[midiNote] =
                _liveNoteReferenceCounts.GetValueOrDefault(midiNote) + 1;
        }

        _ = ReleaseLiveNoteAsync(midiNote);
    }

    public bool TryGetPreparedPlaybackInfo(
        byte[] playbackData,
        out PreparedPlaybackInfo info)
    {
        info = default;
        if (!TryReadPlanId(playbackData, out var planId) ||
            !_pendingPlans.TryGetValue(planId, out var plan))
        {
            return false;
        }

        var activeNotes = new Dictionary<int, int>();
        var activeCount = 0;
        var maximumConcurrentNotes = 0;
        var finiteAndMonotonic = true;
        var previousSeconds = 0d;
        foreach (var playbackEvent in plan.Events)
        {
            finiteAndMonotonic &= double.IsFinite(playbackEvent.AtSeconds) &&
                                  playbackEvent.AtSeconds >= previousSeconds - 0.000001 &&
                                  playbackEvent.AtSeconds >= -0.000001 &&
                                  playbackEvent.AtSeconds <= plan.DurationSeconds + 0.000001;
            previousSeconds = playbackEvent.AtSeconds;
            if (playbackEvent.Kind == PlaybackEventKind.PianoOn)
            {
                activeNotes[playbackEvent.Note] = activeNotes.GetValueOrDefault(playbackEvent.Note) + 1;
                activeCount++;
                maximumConcurrentNotes = Math.Max(maximumConcurrentNotes, activeCount);
            }
            else if (playbackEvent.Kind == PlaybackEventKind.PianoOff &&
                     activeNotes.TryGetValue(playbackEvent.Note, out var noteCount) && noteCount > 0)
            {
                if (noteCount == 1)
                    activeNotes.Remove(playbackEvent.Note);
                else
                    activeNotes[playbackEvent.Note] = noteCount - 1;
                activeCount--;
            }
        }

        info = new PreparedPlaybackInfo(
            plan.DurationSeconds,
            plan.PianoNoteCount,
            plan.MetronomePulseCount,
            plan.Events.Any(playbackEvent =>
                playbackEvent.Kind == PlaybackEventKind.PianoOn &&
                playbackEvent.AtSeconds <= 0.001))
        {
            EventCount = plan.Events.Count,
            HasFiniteMonotonicEvents = finiteAndMonotonic,
            LastEventSeconds = plan.Events.Count == 0 ? 0 : plan.Events[^1].AtSeconds,
            MaximumConcurrentPianoNotes = maximumConcurrentNotes,
            HasBalancedPianoLifecycle = activeCount == 0
        };
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopPreview();
        _pendingPlans.Clear();

        lock (_synthGate)
        {
            _liveNoteReferenceCounts.Clear();
            _liveNoteSynth.AllNotesOff();
            _previewSynth.Dispose();
            _metronomeSynth.Dispose();
            _liveNoteSynth.Dispose();
        }
    }

    private byte[] PrepareScorePlayback(
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
        cancellationToken.ThrowIfCancellationRequested();

        var maximumBeat = Math.Max(0.01, score.TotalPerformanceBeats);
        startBeat = Math.Clamp(startBeat, 0, maximumBeat);
        if (startBeat >= maximumBeat - 1e-9)
            startBeat = Math.Max(0, maximumBeat - 0.01);
        endBeat = Math.Clamp(endBeat, startBeat + 0.01, maximumBeat);

        var tempoScale = Math.Max(0.01, requestedInitialTempoBpm / Math.Max(1d, score.TempoBpm));
        var selectionStartSeconds = score.SecondsAtPerformanceBeat(startBeat, tempoScale);
        var selectionEndSeconds = score.SecondsAtPerformanceBeat(endBeat, tempoScale);
        var durationSeconds = Math.Max(0.01, selectionEndSeconds - selectionStartSeconds);
        ValidateDuration(durationSeconds);

        var events = new List<PlaybackEvent>();
        var pianoNoteCount = 0;
        if (pianoVolumePercent > 0)
        {
            foreach (var note in score.Notes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (includedStaffNumber.HasValue &&
                    ScoreDocument.ResolveStaffNumber(note.StaffNumber, note.MidiNoteNumber) != includedStaffNumber.Value)
                {
                    continue;
                }

                var noteEndBeat = note.OnsetBeats + Math.Max(0.01, note.DurationBeats);
                if (noteEndBeat <= startBeat + 1e-9 || note.OnsetBeats >= endBeat - 1e-9)
                    continue;

                var audibleStartBeat = Math.Max(startBeat, note.OnsetBeats);
                var audibleEndBeat = Math.Min(endBeat, noteEndBeat);
                var noteOnSeconds = Math.Max(
                    0,
                    score.SecondsAtPerformanceBeat(audibleStartBeat, tempoScale) - selectionStartSeconds);
                var noteOffSeconds = Math.Min(
                    durationSeconds,
                    Math.Max(
                        noteOnSeconds + 0.03,
                        score.SecondsAtPerformanceBeat(audibleEndBeat, tempoScale) - selectionStartSeconds));

                events.Add(new PlaybackEvent(
                    noteOnSeconds,
                    PlaybackEventKind.PianoOn,
                    note.MidiNoteNumber,
                    Math.Clamp(note.Velocity, 1, 127)));
                events.Add(new PlaybackEvent(
                    noteOffSeconds,
                    PlaybackEventKind.PianoOff,
                    note.MidiNoteNumber,
                    0));
                pianoNoteCount++;
            }
        }

        var metronomePulseCount = includeMetronome && metronomeVolumePercent > 0
            ? AddScoreMetronomeEvents(
                events,
                score,
                startBeat,
                endBeat,
                selectionStartSeconds,
                tempoScale,
                durationSeconds,
                cancellationToken)
            : 0;

        SortEvents(events);
        ValidateEvents(events, durationSeconds);
        var preset = AudioSoundPreset.FromId(presetId, AudioSoundPreset.AcousticGrand);
        return RegisterPlan(new PlaybackPlan(
            events,
            durationSeconds,
            ResolveRealtimePatch(preset),
            Math.Clamp(pianoVolumePercent, 0, 100),
            Math.Clamp(metronomeVolumePercent, 0, 100),
            pianoNoteCount,
            metronomePulseCount,
            DateTimeOffset.UtcNow));
    }

    private byte[] PrepareMidiPlayback(
        MidiReference midi,
        IReadOnlySet<int> trackIndexes,
        bool includeMetronome,
        int pianoVolumePercent,
        int metronomeVolumePercent,
        string presetId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var secondsPerBeat = 60d / Math.Max(1d, midi.TempoBpm);
        var durationSeconds = Math.Max(0.01, Math.Max(1d, midi.TotalBeats) * secondsPerBeat);
        ValidateDuration(durationSeconds);

        var events = new List<PlaybackEvent>();
        var pianoNoteCount = 0;
        if (pianoVolumePercent > 0)
        {
            foreach (var note in midi.Notes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!trackIndexes.Contains(note.TrackIndex) || note.Channel == 9)
                    continue;

                var noteOnSeconds = Math.Max(0, note.OnsetBeats * secondsPerBeat);
                var noteOffSeconds = Math.Min(
                    durationSeconds,
                    Math.Max(noteOnSeconds + 0.03, (note.OnsetBeats + Math.Max(0.01, note.DurationBeats)) * secondsPerBeat));
                events.Add(new PlaybackEvent(
                    noteOnSeconds,
                    PlaybackEventKind.PianoOn,
                    note.NoteNumber,
                    Math.Clamp(note.Velocity, 1, 127)));
                events.Add(new PlaybackEvent(
                    noteOffSeconds,
                    PlaybackEventKind.PianoOff,
                    note.NoteNumber,
                    0));
                pianoNoteCount++;
            }
        }

        var metronomePulseCount = 0;
        if (includeMetronome && metronomeVolumePercent > 0)
        {
            for (var beat = 0d; beat <= midi.TotalBeats + 1e-9; beat += 1d)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddMetronomePulse(
                    events,
                    beat * secondsPerBeat,
                    Math.Abs(beat % 4d) < 1e-9,
                    durationSeconds);
                metronomePulseCount++;
            }
        }

        SortEvents(events);
        ValidateEvents(events, durationSeconds);
        var preset = AudioSoundPreset.FromId(presetId, AudioSoundPreset.AcousticGrand);
        return RegisterPlan(new PlaybackPlan(
            events,
            durationSeconds,
            ResolveRealtimePatch(preset),
            Math.Clamp(pianoVolumePercent, 0, 100),
            Math.Clamp(metronomeVolumePercent, 0, 100),
            pianoNoteCount,
            metronomePulseCount,
            DateTimeOffset.UtcNow));
    }

    private static int AddScoreMetronomeEvents(
        List<PlaybackEvent> events,
        ScoreDocument score,
        double startBeat,
        double endBeat,
        double selectionStartSeconds,
        double tempoScale,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        var scheduledPulses = new HashSet<long>();
        var pulseCount = 0;
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
                if (beat < startBeat - 1e-9 || beat >= endBeat - 1e-9)
                {
                    pulseIndex++;
                    continue;
                }

                var atSeconds = Math.Max(
                    0,
                    score.SecondsAtPerformanceBeat(beat, tempoScale) - selectionStartSeconds);
                var pulseKey = (long)Math.Round(atSeconds * 1_000_000d);
                if (scheduledPulses.Add(pulseKey))
                {
                    AddMetronomePulse(events, atSeconds, pulseIndex == 0, durationSeconds);
                    pulseCount++;
                }
                pulseIndex++;
            }
        }

        return pulseCount;
    }

    private static void AddMetronomePulse(
        List<PlaybackEvent> events,
        double atSeconds,
        bool accent,
        double durationSeconds)
    {
        if (atSeconds > durationSeconds + 1e-9)
            return;

        var note = accent ? 76 : 77;
        events.Add(new PlaybackEvent(
            Math.Max(0, atSeconds),
            PlaybackEventKind.MetronomeOn,
            note,
            accent ? 118 : 96));
        events.Add(new PlaybackEvent(
            Math.Min(durationSeconds, Math.Max(0, atSeconds) + (accent ? 0.08 : 0.055)),
            PlaybackEventKind.MetronomeOff,
            note,
            0));
    }

    private byte[] RegisterPlan(PlaybackPlan plan)
    {
        PrunePendingPlans();
        var planId = Guid.NewGuid();
        if (!_pendingPlans.TryAdd(planId, plan))
            throw new InvalidOperationException("A unique real-time playback plan could not be registered.");
        return CreateCompatibilityEnvelope(planId, plan.DurationSeconds);
    }

    private void StartRealtimePlayback(PlaybackPlan plan)
    {
        StopPreview();

        if (plan.PianoNoteCount > 0)
        {
            lock (_synthGate)
            {
                _previewSynth.VolumePercent = plan.PianoVolumePercent;
                var programResult = _previewSynth.SetProgram(plan.PianoPatch);
                if (!programResult.Success)
                    throw new InvalidOperationException(programResult.Message);
            }
        }

        if (plan.MetronomePulseCount > 0)
        {
            lock (_synthGate)
            {
                _metronomeSynth.VolumePercent = plan.MetronomeVolumePercent;
                var metronomeResult = _metronomeSynth.Open();
                if (!metronomeResult.Success)
                    throw new InvalidOperationException(metronomeResult.Message);
            }
        }

        lock (_synthGate)
        {
            _liveNoteSynth.AllNotesOff();
            _liveNoteReferenceCounts.Clear();
        }

        var cancellation = new CancellationTokenSource();
        long generation;
        lock (_previewGate)
        {
            _activePlaybackCancellation = cancellation;
            _realtimePlaybackActive = true;
            generation = ++_playbackGeneration;
        }

        _ = RunPlaybackPlanSafelyAsync(plan, generation, cancellation);
    }

    private async Task RunPlaybackPlanSafelyAsync(
        PlaybackPlan plan,
        long generation,
        CancellationTokenSource cancellation)
    {
        var completedNaturally = false;
        try
        {
            await RunPlaybackPlanAsync(plan, generation, cancellation.Token);
            completedNaturally = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AudioError?.Invoke(this, $"Real-time playback failed: {exception.Message}");
        }
        finally
        {
            var ownsOutput = false;
            lock (_previewGate)
            {
                if (_playbackGeneration == generation)
                {
                    _activePlaybackCancellation = null;
                    _realtimePlaybackActive = false;
                    ownsOutput = true;
                }
            }

            if (ownsOutput)
            {
                if (completedNaturally)
                {
                    _ = SilenceCompletedPlaybackAfterReleaseAsync(generation);
                }
                else
                {
                    lock (_synthGate)
                    {
                        _previewSynth.AllNotesOff();
                        _metronomeSynth.AllNotesOff();
                    }
                }
            }

            cancellation.Dispose();
        }
    }

    private async Task SilenceCompletedPlaybackAfterReleaseAsync(long generation)
    {
        await Task.Delay(CompletedPlaybackReleaseTail);
        lock (_previewGate)
        {
            if (_playbackGeneration != generation || _realtimePlaybackActive)
                return;
        }

        lock (_synthGate)
        {
            _previewSynth.AllNotesOff();
            _metronomeSynth.AllNotesOff();
        }
    }

    private async Task RunPlaybackPlanAsync(
        PlaybackPlan plan,
        long generation,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var activePianoNotes = new Dictionary<int, int>();
        var eventIndex = 0;

        while (eventIndex < plan.Events.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scheduledSeconds = plan.Events[eventIndex].AtSeconds;
            await WaitUntilAsync(clock, scheduledSeconds, cancellationToken);

            while (eventIndex < plan.Events.Count &&
                   Math.Abs(plan.Events[eventIndex].AtSeconds - scheduledSeconds) <= 0.0005)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentPlaybackGeneration(generation))
                    return;

                DispatchPlaybackEvent(plan.Events[eventIndex], activePianoNotes);
                eventIndex++;
            }
        }

        await WaitUntilAsync(
            clock,
            plan.DurationSeconds,
            cancellationToken);
    }

    private void DispatchPlaybackEvent(
        PlaybackEvent playbackEvent,
        Dictionary<int, int> activePianoNotes)
    {
        lock (_synthGate)
        {
            switch (playbackEvent.Kind)
            {
                case PlaybackEventKind.PianoOff:
                    if (!activePianoNotes.TryGetValue(playbackEvent.Note, out var activeCount))
                        return;
                    if (activeCount <= 1)
                    {
                        activePianoNotes.Remove(playbackEvent.Note);
                        var offResult = _previewSynth.NoteOff(playbackEvent.Note);
                        if (!offResult.Success)
                            throw new InvalidOperationException(offResult.Message);
                    }
                    else
                    {
                        activePianoNotes[playbackEvent.Note] = activeCount - 1;
                    }
                    break;

                case PlaybackEventKind.MetronomeOff:
                    var clickOffResult = _metronomeSynth.NoteOff(playbackEvent.Note, channel: 9);
                    if (!clickOffResult.Success)
                        throw new InvalidOperationException(clickOffResult.Message);
                    break;

                case PlaybackEventKind.MetronomeOn:
                    var clickOnResult = _metronomeSynth.NoteOn(
                        playbackEvent.Note,
                        playbackEvent.Velocity,
                        channel: 9);
                    if (!clickOnResult.Success)
                        throw new InvalidOperationException(clickOnResult.Message);
                    break;

                case PlaybackEventKind.PianoOn:
                    var noteOnResult = _previewSynth.NoteOn(
                        playbackEvent.Note,
                        playbackEvent.Velocity);
                    if (!noteOnResult.Success)
                        throw new InvalidOperationException(noteOnResult.Message);
                    activePianoNotes[playbackEvent.Note] =
                        activePianoNotes.GetValueOrDefault(playbackEvent.Note) + 1;
                    break;
            }
        }
    }

    private bool IsCurrentPlaybackGeneration(long generation)
    {
        lock (_previewGate)
        {
            return !_disposed &&
                   _realtimePlaybackActive &&
                   _playbackGeneration == generation;
        }
    }

    private bool IsPreviewOutputActive()
    {
        lock (_previewGate)
        {
            return _realtimePlaybackActive || _legacyPreviewPlayer is not null;
        }
    }

    private async Task ReleaseStandaloneClickAsync(int note)
    {
        try
        {
            await Task.Delay(90);
            if (_disposed || IsPreviewOutputActive())
                return;

            lock (_synthGate)
            {
                _metronomeSynth.NoteOff(note, channel: 9);
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ReleaseLiveNoteAsync(int midiNote)
    {
        try
        {
            await Task.Delay(850);
            if (_disposed)
                return;

            lock (_synthGate)
            {
                if (!_liveNoteReferenceCounts.TryGetValue(midiNote, out var activeCount))
                    return;

                if (activeCount <= 1)
                {
                    _liveNoteReferenceCounts.Remove(midiNote);
                    _liveNoteSynth.NoteOff(midiNote);
                }
                else
                {
                    _liveNoteReferenceCounts[midiNote] = activeCount - 1;
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task WaitUntilAsync(
        Stopwatch clock,
        double targetSeconds,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingSeconds = targetSeconds - clock.Elapsed.TotalSeconds;
            if (remainingSeconds <= 0)
                return;

            if (remainingSeconds > 0.012)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(0.025, remainingSeconds - 0.004));
                await Task.Delay(delay, cancellationToken);
            }
            else if (remainingSeconds > 0.002)
            {
                await Task.Delay(1, cancellationToken);
            }
            else
            {
                await Task.Yield();
            }
        }
    }

    private void PrunePendingPlans()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2);
        foreach (var entry in _pendingPlans)
        {
            if (entry.Value.CreatedUtc < cutoff)
                _pendingPlans.TryRemove(entry.Key, out _);
        }

        if (_pendingPlans.Count <= 16)
            return;

        foreach (var planId in _pendingPlans
                     .OrderBy(entry => entry.Value.CreatedUtc)
                     .Take(_pendingPlans.Count - 16)
                     .Select(entry => entry.Key))
        {
            _pendingPlans.TryRemove(planId, out _);
        }
    }

    private static void SortEvents(List<PlaybackEvent> events) =>
        events.Sort(static (left, right) =>
        {
            var timeComparison = left.AtSeconds.CompareTo(right.AtSeconds);
            return timeComparison != 0
                ? timeComparison
                : left.Kind.CompareTo(right.Kind);
        });

    private static void ValidateDuration(double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
            throw new InvalidOperationException("The requested playback duration is invalid.");
        if (durationSeconds > MaximumPlaybackSeconds)
        {
            throw new InvalidOperationException(
                $"The requested playback is {durationSeconds / 60d:0.0} minutes long and exceeds the {MaximumPlaybackSeconds / 60d:0} minute safety limit.");
        }
    }

    private static void ValidateEvents(IReadOnlyList<PlaybackEvent> events, double durationSeconds)
    {
        if (events.Count > MaximumPlaybackEvents)
            throw new InvalidOperationException(
                $"The requested playback exceeds the {MaximumPlaybackEvents:N0} event safety limit.");

        var previousSeconds = 0d;
        foreach (var playbackEvent in events)
        {
            if (!double.IsFinite(playbackEvent.AtSeconds) ||
                playbackEvent.AtSeconds < previousSeconds - 0.000001 ||
                playbackEvent.AtSeconds < -0.000001 ||
                playbackEvent.AtSeconds > durationSeconds + 0.000001 ||
                playbackEvent.Note is < 0 or > 127 ||
                playbackEvent.Velocity is < 0 or > 127)
            {
                throw new InvalidOperationException("The requested playback contains an invalid scheduled event.");
            }
            previousSeconds = playbackEvent.AtSeconds;
        }
    }

    private static int ResolveRealtimePatch(AudioSoundPreset preset) =>
        preset.IsSoftSynth ? 88 : Math.Clamp(preset.PatchNumber, 0, 127);

    private static byte[] CreateCompatibilityEnvelope(Guid planId, double durationSeconds)
    {
        var approximatePayloadLength = Math.Clamp(
            (int)Math.Ceiling(durationSeconds * 512d),
            PlanMarkerOffset + PlanMarkerBytes.Length + 16,
            4 * 1024 * 1024);
        var envelope = new byte[approximatePayloadLength];
        var claimedDataBytes = envelope.Length - 44;

        WriteAscii(envelope, 0, "RIFF");
        BitConverter.GetBytes(36 + claimedDataBytes).CopyTo(envelope, 4);
        WriteAscii(envelope, 8, "WAVE");
        WriteAscii(envelope, 12, "fmt ");
        BitConverter.GetBytes(16).CopyTo(envelope, 16);
        BitConverter.GetBytes((short)1).CopyTo(envelope, 20);
        BitConverter.GetBytes((short)1).CopyTo(envelope, 22);
        BitConverter.GetBytes(CompatibilitySampleRate).CopyTo(envelope, 24);
        BitConverter.GetBytes(CompatibilitySampleRate * 2).CopyTo(envelope, 28);
        BitConverter.GetBytes((short)2).CopyTo(envelope, 32);
        BitConverter.GetBytes((short)16).CopyTo(envelope, 34);
        WriteAscii(envelope, 36, "data");
        BitConverter.GetBytes(claimedDataBytes).CopyTo(envelope, 40);
        BitConverter.GetBytes((short)256).CopyTo(envelope, 44);
        PlanMarkerBytes.CopyTo(envelope, PlanMarkerOffset);
        planId.ToByteArray().CopyTo(envelope, PlanMarkerOffset + PlanMarkerBytes.Length);
        return envelope;
    }

    private static bool TryReadPlanId(byte[] playbackData, out Guid planId)
    {
        planId = Guid.Empty;
        var requiredLength = PlanMarkerOffset + PlanMarkerBytes.Length + 16;
        if (playbackData.Length < requiredLength ||
            !playbackData.AsSpan(PlanMarkerOffset, PlanMarkerBytes.Length).SequenceEqual(PlanMarkerBytes))
        {
            return false;
        }

        planId = new Guid(playbackData.AsSpan(PlanMarkerOffset + PlanMarkerBytes.Length, 16));
        return true;
    }

    private static void WriteAscii(byte[] destination, int offset, string value) =>
        Encoding.ASCII.GetBytes(value).CopyTo(destination, offset);

    private enum PlaybackEventKind
    {
        PianoOff = 0,
        MetronomeOff = 1,
        MetronomeOn = 2,
        PianoOn = 3
    }

    private readonly record struct PlaybackEvent(
        double AtSeconds,
        PlaybackEventKind Kind,
        int Note,
        int Velocity);

    private sealed record PlaybackPlan(
        IReadOnlyList<PlaybackEvent> Events,
        double DurationSeconds,
        int PianoPatch,
        int PianoVolumePercent,
        int MetronomeVolumePercent,
        int PianoNoteCount,
        int MetronomePulseCount,
        DateTimeOffset CreatedUtc);
}

public readonly record struct PreparedPlaybackInfo(
    double DurationSeconds,
    int PianoNoteCount,
    int MetronomePulseCount,
    bool HasImmediatePianoEvent)
{
    public int EventCount { get; init; }
    public bool HasFiniteMonotonicEvents { get; init; }
    public double LastEventSeconds { get; init; }
    public int MaximumConcurrentPianoNotes { get; init; }
    public bool HasBalancedPianoLifecycle { get; init; }
}
