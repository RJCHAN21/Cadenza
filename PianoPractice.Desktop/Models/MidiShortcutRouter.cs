namespace PianoPractice.Desktop.Models;

public enum MidiShortcutAction
{
    None,
    StartListen,
    StartPractice,
    StartPerformance,
    TogglePlayback,
    Restart,
    PreviousMeasure,
    NextMeasure,
    PreviousPage,
    NextPage,
    DismissResults,
    RepeatResults
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
/// Routes protected MIDI commands through a deliberate tap-to-arm gesture.
/// Ordinary notes pass through untouched; reserved arm/command notes are only
/// consumed while participating in an active remote gesture.
/// </summary>
public sealed class MidiShortcutRouter
{
    public static readonly TimeSpan MaximumArmTapDuration = TimeSpan.FromMilliseconds(750);
    public static readonly TimeSpan ArmedDuration = TimeSpan.FromSeconds(3);

    private readonly HashSet<int> _notesDown = [];
    private readonly HashSet<int> _consumedNotes = [];
    private bool _armTapEligible;
    private DateTimeOffset _armPressedAt;
    private DateTimeOffset? _armedUntil;

    public bool IsArmed(DateTimeOffset now)
    {
        ExpireArm(now);
        return _armedUntil is not null;
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

        if (noteNumber == armNoteNumber)
        {
            _consumedNotes.Add(noteNumber);
            if (context == MidiShortcutContext.Unavailable)
            {
                _armTapEligible = false;
                _armedUntil = null;
                return new MidiShortcutRouteResult(
                    true,
                    MidiShortcutAction.None,
                    MidiShortcutSignal.Blocked,
                    Message: "MIDI Remote is unavailable while the app is busy or a dialog is open.");
            }

            _armTapEligible = _notesDown.Count == 1;
            _armPressedAt = now;
            _armedUntil = null;
            return new MidiShortcutRouteResult(
                true,
                MidiShortcutAction.None,
                _armTapEligible ? MidiShortcutSignal.ModifierPressed : MidiShortcutSignal.Blocked,
                Message: _armTapEligible
                    ? "Release the Remote key to arm MIDI shortcuts."
                    : "Release every other MIDI key before tapping the Remote key.");
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

        if (noteNumber != armNoteNumber)
        {
            return new MidiShortcutRouteResult(consumed, MidiShortcutAction.None, MidiShortcutSignal.None);
        }

        var elapsed = now - _armPressedAt;
        var canArm = _armTapEligible &&
                     elapsed >= TimeSpan.Zero &&
                     elapsed <= MaximumArmTapDuration &&
                     _notesDown.Count == 0;
        _armTapEligible = false;

        if (!canArm)
        {
            _armedUntil = null;
            return new MidiShortcutRouteResult(
                true,
                MidiShortcutAction.None,
                MidiShortcutSignal.Cancelled,
                Message: "MIDI Remote was not armed. Tap and release the Remote key with all other keys up.");
        }

        _armedUntil = now + ArmedDuration;
        return new MidiShortcutRouteResult(
            true,
            MidiShortcutAction.None,
            MidiShortcutSignal.Armed,
            _armedUntil,
            "MIDI Remote armed for 3 seconds.");
    }

    public void CancelArm()
    {
        _armTapEligible = false;
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
            MidiShortcutAction.Restart or
            MidiShortcutAction.PreviousMeasure or
            MidiShortcutAction.NextMeasure or
            MidiShortcutAction.PreviousPage or
            MidiShortcutAction.NextPage,
        MidiShortcutContext.Running => action is
            MidiShortcutAction.TogglePlayback or
            MidiShortcutAction.Restart,
        MidiShortcutContext.Paused => action is
            MidiShortcutAction.TogglePlayback or
            MidiShortcutAction.Restart or
            MidiShortcutAction.PreviousMeasure or
            MidiShortcutAction.NextMeasure or
            MidiShortcutAction.PreviousPage or
            MidiShortcutAction.NextPage,
        MidiShortcutContext.Results => action is
            MidiShortcutAction.DismissResults or
            MidiShortcutAction.RepeatResults,
        _ => false
    };

    public static string GetActionLabel(MidiShortcutAction action) => action switch
    {
        MidiShortcutAction.StartListen => "Start Listen",
        MidiShortcutAction.StartPractice => "Start Practice",
        MidiShortcutAction.StartPerformance => "Start Performance",
        MidiShortcutAction.TogglePlayback => "Play / Pause / Stop",
        MidiShortcutAction.Restart => "Restart",
        MidiShortcutAction.PreviousMeasure => "Previous Measure",
        MidiShortcutAction.NextMeasure => "Next Measure",
        MidiShortcutAction.PreviousPage => "Previous Page",
        MidiShortcutAction.NextPage => "Next Page",
        MidiShortcutAction.DismissResults => "Dismiss Results",
        MidiShortcutAction.RepeatResults => "Repeat Results",
        _ => "No action"
    };

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
