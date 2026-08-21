namespace PianoPractice.Desktop.Models;

public enum MidiShortcutAction
{
    None,
    StartListen,
    StartPractice,
    StartPerformance,
    TogglePlayback,
    Play,
    Pause,
    Restart,
    PreviousMeasure,
    NextMeasure,
    PreviousPage,
    NextPage,
    ReturnToLivePage,
    DismissResults,
    RepeatResults,
    Stop,
    ToggleLoop,
    SetLessonTempo,
    SetNotationZoom,
    SetOverallVolume,
    SetInstrumentalVolume,
    SetMetronomeVolume,
    SetMonitorVolume
}

public enum MidiShortcutContext
{
    Unavailable,
    Ready,
    Running,
    Paused,
    Results
}

public enum MidiShortcutSignal
{
    None,
    ModifierPressed,
    Armed,
    Cancelled,
    Triggered,
    Blocked
}

public sealed record MidiShortcutRouteResult(
    bool Consumed,
    MidiShortcutAction Action,
    MidiShortcutSignal Signal,
    DateTimeOffset? ArmedUntil = null,
    string? Message = null);

/// <summary>
/// Routes protected MIDI-note commands after a dedicated controller control
/// arms the remote. Ordinary piano notes cannot arm shortcuts themselves.
/// </summary>
public sealed class MidiShortcutRouter
{
    public static readonly TimeSpan MaximumArmTapDuration = TimeSpan.FromMilliseconds(750);
    public static readonly TimeSpan ArmedDuration = TimeSpan.FromSeconds(3);

    private readonly HashSet<int> _notesDown = [];
    private readonly HashSet<int> _consumedNotes = [];
    private DateTimeOffset? _armedUntil;

    public bool IsArmed(DateTimeOffset now)
    {
        ExpireArm(now);
        return _armedUntil is not null;
    }

    public MidiShortcutRouteResult ArmFromDedicatedControl(
        MidiShortcutContext context,
        DateTimeOffset now)
    {
        if (context == MidiShortcutContext.Unavailable)
        {
            _armedUntil = null;
            return new MidiShortcutRouteResult(
                true,
                MidiShortcutAction.None,
                MidiShortcutSignal.Blocked,
                Message: "MIDI Remote is unavailable while the app is busy or a dialog is open.");
        }

        _armedUntil = now + ArmedDuration;
        return new MidiShortcutRouteResult(
            true,
            MidiShortcutAction.None,
            MidiShortcutSignal.Armed,
            _armedUntil,
            "MIDI Remote armed from a dedicated controller control for 3 seconds.");
    }

    public MidiShortcutRouteResult ProcessNoteOn(
        int noteNumber,
        int armNoteNumber,
        IReadOnlyDictionary<int, MidiShortcutAction> bindings,
        MidiShortcutContext context,
        DateTimeOffset now)
    {
        ExpireArm(now);

        if (!_notesDown.Add(noteNumber))
        {
            return new MidiShortcutRouteResult(
                _consumedNotes.Contains(noteNumber),
                MidiShortcutAction.None,
                MidiShortcutSignal.None);
        }

        if (_armedUntil is null)
        {
            return new MidiShortcutRouteResult(false, MidiShortcutAction.None, MidiShortcutSignal.None);
        }

        _armedUntil = null;
        if (!bindings.TryGetValue(noteNumber, out var action) || action == MidiShortcutAction.None)
        {
            return new MidiShortcutRouteResult(
                false,
                MidiShortcutAction.None,
                MidiShortcutSignal.Cancelled,
                Message: "MIDI Remote cancelled; that key is not assigned to an action.");
        }

        _consumedNotes.Add(noteNumber);
        if (!IsActionAllowed(action, context))
        {
            return new MidiShortcutRouteResult(
                true,
                MidiShortcutAction.None,
                MidiShortcutSignal.Blocked,
                Message: GetBlockedReason(action, context));
        }

        return new MidiShortcutRouteResult(
            true,
            action,
            MidiShortcutSignal.Triggered,
            Message: $"MIDI Remote triggered {GetActionLabel(action)}.");
    }

    public MidiShortcutRouteResult ProcessNoteOff(
        int noteNumber,
        int armNoteNumber,
        DateTimeOffset now)
    {
        _notesDown.Remove(noteNumber);
        var consumed = _consumedNotes.Remove(noteNumber);

        return new MidiShortcutRouteResult(consumed, MidiShortcutAction.None, MidiShortcutSignal.None);
    }

    public void CancelArm()
    {
        _armedUntil = null;
    }

    public void Reset()
    {
        CancelArm();
        _notesDown.Clear();
        _consumedNotes.Clear();
    }

    public static bool IsActionAllowed(MidiShortcutAction action, MidiShortcutContext context) => context switch
    {
        MidiShortcutContext.Ready => action is
            MidiShortcutAction.StartListen or
            MidiShortcutAction.StartPractice or
            MidiShortcutAction.StartPerformance or
            MidiShortcutAction.TogglePlayback or
            MidiShortcutAction.Play or
            MidiShortcutAction.Restart or
            MidiShortcutAction.PreviousMeasure or
            MidiShortcutAction.NextMeasure or
            MidiShortcutAction.PreviousPage or
            MidiShortcutAction.NextPage or
            MidiShortcutAction.ReturnToLivePage or
            MidiShortcutAction.Stop or
            MidiShortcutAction.ToggleLoop or
            MidiShortcutAction.SetLessonTempo or
            MidiShortcutAction.SetNotationZoom or
            MidiShortcutAction.SetOverallVolume or
            MidiShortcutAction.SetInstrumentalVolume or
            MidiShortcutAction.SetMetronomeVolume or
            MidiShortcutAction.SetMonitorVolume,
        MidiShortcutContext.Running => action is
            MidiShortcutAction.TogglePlayback or
            MidiShortcutAction.Play or
            MidiShortcutAction.Pause or
            MidiShortcutAction.Restart or
            MidiShortcutAction.ReturnToLivePage or
            MidiShortcutAction.Stop or
            MidiShortcutAction.ToggleLoop or
            MidiShortcutAction.SetLessonTempo or
            MidiShortcutAction.SetNotationZoom or
            MidiShortcutAction.SetOverallVolume or
            MidiShortcutAction.SetInstrumentalVolume or
            MidiShortcutAction.SetMetronomeVolume or
            MidiShortcutAction.SetMonitorVolume,
        MidiShortcutContext.Paused => action is
            MidiShortcutAction.TogglePlayback or
            MidiShortcutAction.Play or
            MidiShortcutAction.Pause or
            MidiShortcutAction.Restart or
            MidiShortcutAction.PreviousMeasure or
            MidiShortcutAction.NextMeasure or
            MidiShortcutAction.PreviousPage or
            MidiShortcutAction.NextPage or
            MidiShortcutAction.ReturnToLivePage or
            MidiShortcutAction.Stop or
            MidiShortcutAction.ToggleLoop or
            MidiShortcutAction.SetLessonTempo or
            MidiShortcutAction.SetNotationZoom or
            MidiShortcutAction.SetOverallVolume or
            MidiShortcutAction.SetInstrumentalVolume or
            MidiShortcutAction.SetMetronomeVolume or
            MidiShortcutAction.SetMonitorVolume,
        MidiShortcutContext.Results => action is
            MidiShortcutAction.DismissResults or
            MidiShortcutAction.RepeatResults or
            MidiShortcutAction.Restart or
            MidiShortcutAction.TogglePlayback or
            MidiShortcutAction.Play or
            MidiShortcutAction.StartListen or
            MidiShortcutAction.StartPractice or
            MidiShortcutAction.StartPerformance or
            MidiShortcutAction.Stop or
            MidiShortcutAction.ToggleLoop or
            MidiShortcutAction.ReturnToLivePage or
            MidiShortcutAction.SetNotationZoom or
            MidiShortcutAction.SetOverallVolume or
            MidiShortcutAction.SetInstrumentalVolume or
            MidiShortcutAction.SetMetronomeVolume or
            MidiShortcutAction.SetMonitorVolume,
        _ => false
    };

    public static string GetActionLabel(MidiShortcutAction action) => action switch
    {
        MidiShortcutAction.StartListen => "Start Listen",
        MidiShortcutAction.StartPractice => "Start Practice",
        MidiShortcutAction.StartPerformance => "Start Performance",
        MidiShortcutAction.TogglePlayback => "Play / Pause",
        MidiShortcutAction.Play => "Play",
        MidiShortcutAction.Pause => "Pause",
        MidiShortcutAction.Restart => "Restart",
        MidiShortcutAction.PreviousMeasure => "Previous Measure",
        MidiShortcutAction.NextMeasure => "Next Measure",
        MidiShortcutAction.PreviousPage => "Previous Page",
        MidiShortcutAction.NextPage => "Next Page",
        MidiShortcutAction.ReturnToLivePage => "Return to Live Page",
        MidiShortcutAction.DismissResults => "Dismiss Results",
        MidiShortcutAction.RepeatResults => "Repeat Results",
        MidiShortcutAction.Stop => "Stop",
        MidiShortcutAction.ToggleLoop => "Toggle Loop",
        MidiShortcutAction.SetLessonTempo => "Lesson Tempo",
        MidiShortcutAction.SetNotationZoom => "Notation Zoom",
        MidiShortcutAction.SetOverallVolume => "Overall Volume",
        MidiShortcutAction.SetInstrumentalVolume => "Song Volume",
        MidiShortcutAction.SetMetronomeVolume => "Metronome Volume",
        MidiShortcutAction.SetMonitorVolume => "Live Piano Volume",
        _ => "No action"
    };

    public static bool IsContinuousAction(MidiShortcutAction action) => action is
        MidiShortcutAction.SetLessonTempo or
        MidiShortcutAction.SetNotationZoom or
        MidiShortcutAction.SetOverallVolume or
        MidiShortcutAction.SetInstrumentalVolume or
        MidiShortcutAction.SetMetronomeVolume or
        MidiShortcutAction.SetMonitorVolume;

    private static string GetBlockedReason(MidiShortcutAction action, MidiShortcutContext context) =>
        $"{GetActionLabel(action)} is unavailable while the app is {GetContextLabel(context)}. MIDI notes were not forwarded as music.";

    private static string GetContextLabel(MidiShortcutContext context) => context switch
    {
        MidiShortcutContext.Ready => "ready",
        MidiShortcutContext.Running => "running a session",
        MidiShortcutContext.Paused => "paused",
        MidiShortcutContext.Results => "showing results",
        _ => "busy"
    };

    private void ExpireArm(DateTimeOffset now)
    {
        if (_armedUntil is not null && now > _armedUntil)
        {
            _armedUntil = null;
        }
    }
}
