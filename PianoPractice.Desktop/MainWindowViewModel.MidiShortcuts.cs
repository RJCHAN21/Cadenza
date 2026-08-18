using PianoPractice.Desktop.Models;
using System.Windows;

namespace PianoPractice.Desktop;

public sealed partial class MainWindowViewModel
{
    private const string MidiRemoteArmActionId = "RemoteArm";
    private readonly MidiShortcutRouter _midiShortcutRouter = new();
    private readonly HashSet<int> _midiLearningConsumedNotes = [];
    private string? _midiShortcutLearningActionId;
    private string _midiRemoteStatusText = "MIDI Remote ready.";
    private bool _isMidiRemoteArmed;
    private int _midiRemoteArmGeneration;
    private bool _midiShortcutCommandsSuspended;

    public bool MidiShortcutsEnabled
    {
        get => _profile.Settings.MidiShortcutsEnabled;
        set
        {
            if (_profile.Settings.MidiShortcutsEnabled == value) return;
            _profile.Settings.MidiShortcutsEnabled = value;
            ResetMidiShortcutState();
            CancelMidiShortcutLearning();
            MidiRemoteStatusText = value
                ? "MIDI Remote ready. Tap the Remote key to arm it."
                : "MIDI shortcuts are off; every MIDI key is musical input.";
            OnPropertyChanged();
            OnPropertyChanged(nameof(MidiRemoteInstructions));
            SaveProfileSettings();
        }
    }

    public string MidiRemoteArmNoteText => FormatMidiShortcutNote(_profile.Settings.MidiRemoteArmNote);

    public string MidiRemoteInstructions => MidiShortcutsEnabled
        ? $"Tap and release {MidiRemoteArmNoteText} with all other keys up. Then press one assigned MIDI key within 3 seconds. Held notes never trigger commands; the Remote key is reserved while this feature is enabled."
        : "Enable protected MIDI shortcuts to use a MIDI key as a deliberate remote command.";

    public string MidiRemoteStatusText
    {
        get => _midiRemoteStatusText;
        private set => SetField(ref _midiRemoteStatusText, value);
    }

    public bool IsMidiRemoteArmed
    {
        get => _isMidiRemoteArmed;
        private set => SetField(ref _isMidiRemoteArmed, value);
    }

    public bool IsMidiShortcutLearning => _midiShortcutLearningActionId is not null;

    public string MidiShortcutLearningText => _midiShortcutLearningActionId is null
        ? "Select a binding to change it."
        : _midiShortcutLearningActionId == MidiRemoteArmActionId
            ? "Press the MIDI key that should arm the protected remote layer."
            : $"Press a MIDI key for {MidiShortcutRouter.GetActionLabel(ParseAction(_midiShortcutLearningActionId))}.";

    public IReadOnlyList<MidiShortcutBindingItem> MidiShortcutBindings =>
    [
        BindingItem(MidiShortcutAction.StartListen, "Ready only"),
        BindingItem(MidiShortcutAction.StartPractice, "Ready only"),
        BindingItem(MidiShortcutAction.StartPerformance, "Ready only"),
        BindingItem(MidiShortcutAction.TogglePlayback, "Ready, running, or paused"),
        BindingItem(MidiShortcutAction.Restart, "Ready, running, or paused"),
        BindingItem(MidiShortcutAction.PreviousMeasure, "Ready or paused"),
        BindingItem(MidiShortcutAction.NextMeasure, "Ready or paused"),
        BindingItem(MidiShortcutAction.PreviousPage, "Ready or paused"),
        BindingItem(MidiShortcutAction.NextPage, "Ready or paused"),
        BindingItem(MidiShortcutAction.DismissResults, "Results only"),
        BindingItem(MidiShortcutAction.RepeatResults, "Results only")
    ];

    public void StartMidiShortcutLearning(string actionId)
    {
        if (actionId != MidiRemoteArmActionId && ParseAction(actionId) == MidiShortcutAction.None) return;
        ResetMidiShortcutState();
        _midiShortcutLearningActionId = actionId;
        MidiRemoteStatusText = "MIDI learn is listening for one physical key press.";
        OnPropertyChanged(nameof(IsMidiShortcutLearning));
        OnPropertyChanged(nameof(MidiShortcutLearningText));
    }

    public void CancelMidiShortcutLearning()
    {
        if (_midiShortcutLearningActionId is null) return;
        _midiShortcutLearningActionId = null;
        _midiLearningConsumedNotes.Clear();
        MidiRemoteStatusText = MidiShortcutsEnabled
            ? "MIDI Remote ready. Tap the Remote key to arm it."
            : "MIDI shortcuts are off; every MIDI key is musical input.";
        OnPropertyChanged(nameof(IsMidiShortcutLearning));
        OnPropertyChanged(nameof(MidiShortcutLearningText));
    }

    public void SetMidiShortcutCommandsSuspended(bool suspended)
    {
        if (_midiShortcutCommandsSuspended == suspended) return;
        _midiShortcutCommandsSuspended = suspended;
        ResetMidiShortcutState();
        MidiRemoteStatusText = suspended
            ? "MIDI Remote commands are paused while this dialog is open. MIDI Learn remains available."
            : MidiShortcutsEnabled
                ? "MIDI Remote ready. Tap the Remote key to arm it."
                : "MIDI shortcuts are off; every MIDI key is musical input.";
    }

    public void UnbindMidiShortcut(string actionId)
    {
        var action = ParseAction(actionId);
        if (action == MidiShortcutAction.None) return;
        SetMidiShortcutNote(action, -1);
        MidiRemoteStatusText = $"{MidiShortcutRouter.GetActionLabel(action)} is now unassigned.";
        RefreshMidiShortcutPresentation();
        SaveProfileSettings();
    }

    private void InitializeMidiShortcutSettings()
    {
        var settings = _profile.Settings;
        settings.MidiRemoteArmNote = ValidMidiNoteOrDefault(settings.MidiRemoteArmNote, 84);

        var usedNotes = new HashSet<int> { settings.MidiRemoteArmNote };
        var changed = false;
        foreach (var action in BindingPriority())
        {
            var note = GetMidiShortcutNote(action);
            if (note is < 0 or > 127) continue;
            if (usedNotes.Add(note)) continue;
            SetMidiShortcutNote(action, -1);
            changed = true;
        }

        MidiRemoteStatusText = settings.MidiShortcutsEnabled
            ? "MIDI Remote ready. Tap the Remote key to arm it."
            : "MIDI shortcuts are off; every MIDI key is musical input.";
        if (changed) TrySaveProfile();
    }

    private bool TryCaptureMidiShortcutLearning(int midiNoteNumber)
    {
        if (_midiShortcutLearningActionId is null) return false;
        _midiLearningConsumedNotes.Add(midiNoteNumber);

        if (_midiShortcutLearningActionId == MidiRemoteArmActionId)
        {
            var displaced = FindActionForNote(midiNoteNumber);
            if (displaced != MidiShortcutAction.None) SetMidiShortcutNote(displaced, -1);
            _profile.Settings.MidiRemoteArmNote = midiNoteNumber;
            CompleteMidiShortcutLearning($"{FormatMidiShortcutNote(midiNoteNumber)} is now the MIDI Remote key.");
            return true;
        }

        var action = ParseAction(_midiShortcutLearningActionId);
        if (midiNoteNumber == _profile.Settings.MidiRemoteArmNote)
        {
            MidiRemoteStatusText = $"{FormatMidiShortcutNote(midiNoteNumber)} is reserved as the Remote key. Choose a different action key.";
            return true;
        }

        var previousAction = FindActionForNote(midiNoteNumber);
        if (previousAction != MidiShortcutAction.None && previousAction != action)
        {
            SetMidiShortcutNote(previousAction, -1);
        }

        SetMidiShortcutNote(action, midiNoteNumber);
        var replacement = previousAction == MidiShortcutAction.None || previousAction == action
            ? string.Empty
            : $" {MidiShortcutRouter.GetActionLabel(previousAction)} was unassigned to prevent a conflict.";
        CompleteMidiShortcutLearning(
            $"{FormatMidiShortcutNote(midiNoteNumber)} now triggers {MidiShortcutRouter.GetActionLabel(action)} after arming.{replacement}");
        return true;
    }

    private void CompleteMidiShortcutLearning(string status)
    {
        _midiShortcutLearningActionId = null;
        MidiRemoteStatusText = status;
        RefreshMidiShortcutPresentation();
        SaveProfileSettings();
        OnPropertyChanged(nameof(IsMidiShortcutLearning));
        OnPropertyChanged(nameof(MidiShortcutLearningText));
    }

    private bool TryHandleMidiShortcutNoteOn(int midiNoteNumber, bool simulation)
    {
        if (simulation || !MidiShortcutsEnabled || !IsPlayerVisible) return false;
        var route = _midiShortcutRouter.ProcessNoteOn(
            midiNoteNumber,
            _profile.Settings.MidiRemoteArmNote,
            BuildMidiShortcutMap(),
            GetMidiShortcutContext(),
            DateTimeOffset.UtcNow);
        ApplyMidiShortcutRoute(route, midiNoteNumber);
        return route.Consumed;
    }

    private bool TryHandleMidiShortcutNoteOff(int midiNoteNumber)
    {
        if (_midiLearningConsumedNotes.Remove(midiNoteNumber)) return true;
        if (!MidiShortcutsEnabled) return false;
        var route = _midiShortcutRouter.ProcessNoteOff(
            midiNoteNumber,
            _profile.Settings.MidiRemoteArmNote,
            DateTimeOffset.UtcNow);
        ApplyMidiShortcutRoute(route, midiNoteNumber);
        return route.Consumed;
    }

    private void ApplyMidiShortcutRoute(MidiShortcutRouteResult route, int noteNumber)
    {
        if (!string.IsNullOrWhiteSpace(route.Message)) MidiRemoteStatusText = route.Message;

        switch (route.Signal)
        {
            case MidiShortcutSignal.ModifierPressed:
                DisarmMidiRemoteHud();
                InputActivityLabel = $"MIDI Remote key down: {FormatMidiShortcutNote(noteNumber)}";
                break;
            case MidiShortcutSignal.Armed:
                IsMidiRemoteArmed = true;
                InputActivityLabel = "MIDI Remote armed; waiting for one assigned action key.";
                ScheduleMidiRemoteExpiry(route.ArmedUntil);
                break;
            case MidiShortcutSignal.Cancelled:
                DisarmMidiRemoteHud();
                break;
            case MidiShortcutSignal.Blocked:
                DisarmMidiRemoteHud();
                StatusMessage = route.Message ?? "That MIDI shortcut is unavailable in the current context.";
                break;
            case MidiShortcutSignal.Triggered:
                DisarmMidiRemoteHud();
                InputActivityLabel = $"Protected MIDI shortcut: {MidiShortcutRouter.GetActionLabel(route.Action)} from {FormatMidiShortcutNote(noteNumber)}";
                _ = ExecuteProtectedMidiShortcutAsync(route.Action);
                break;
        }
    }

    private async Task ExecuteProtectedMidiShortcutAsync(MidiShortcutAction action)
    {
        try
        {
            switch (action)
            {
                case MidiShortcutAction.StartListen:
                    if (await SwitchLessonModeAsync(LessonMode.Listen)) await StartSelectedModeAsync();
                    break;
                case MidiShortcutAction.StartPractice:
                    if (await SwitchLessonModeAsync(LessonMode.WaitForYou)) await StartSelectedModeAsync();
                    break;
                case MidiShortcutAction.StartPerformance:
                    if (await SwitchLessonModeAsync(LessonMode.TimedPlay)) await StartSelectedModeAsync();
                    break;
                case MidiShortcutAction.TogglePlayback:
                    if (IsLessonActive) StopLesson();
                    else await TogglePreviewAsync();
                    break;
                case MidiShortcutAction.Restart:
                    await RestartPreviewAsync();
                    break;
                case MidiShortcutAction.PreviousMeasure:
                    await SeekDisplayMeasureAsync(-1);
                    break;
                case MidiShortcutAction.NextMeasure:
                    await SeekDisplayMeasureAsync(1);
                    break;
                case MidiShortcutAction.PreviousPage:
                    await SeekDisplayPageAsync(-1);
                    break;
                case MidiShortcutAction.NextPage:
                    await SeekDisplayPageAsync(1);
                    break;
                case MidiShortcutAction.DismissResults:
                    DismissResults();
                    break;
                case MidiShortcutAction.RepeatResults:
                    await TriggerAutoRepeatAsync();
                    break;
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"MIDI shortcut failed safely: {exception.Message}";
        }
    }

    private void ScheduleMidiRemoteExpiry(DateTimeOffset? armedUntil)
    {
        if (armedUntil is null) return;
        var generation = Interlocked.Increment(ref _midiRemoteArmGeneration);
        _ = ExpireMidiRemoteHudAsync(armedUntil.Value, generation);
    }

    private async Task ExpireMidiRemoteHudAsync(DateTimeOffset armedUntil, int generation)
    {
        var delay = armedUntil - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay);
        if (Application.Current is null) return;
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (generation != _midiRemoteArmGeneration) return;
            _midiShortcutRouter.CancelArm();
            IsMidiRemoteArmed = false;
            MidiRemoteStatusText = "MIDI Remote ready. Tap the Remote key to arm it.";
        });
    }

    private void DisarmMidiRemoteHud()
    {
        Interlocked.Increment(ref _midiRemoteArmGeneration);
        IsMidiRemoteArmed = false;
    }

    private void ResetMidiShortcutState()
    {
        _midiShortcutRouter.Reset();
        _midiLearningConsumedNotes.Clear();
        DisarmMidiRemoteHud();
    }

    private MidiShortcutContext GetMidiShortcutContext()
    {
        if (!IsPlayerVisible || _midiShortcutCommandsSuspended || IsPreviewBuilding || _isStartingLesson)
            return MidiShortcutContext.Unavailable;
        if (ResultsVisible) return MidiShortcutContext.Results;
        if (IsLessonActive || IsPreviewPlaying) return MidiShortcutContext.Running;
        if (IsPreviewPaused) return MidiShortcutContext.Paused;
        return MidiShortcutContext.Ready;
    }

    private IReadOnlyDictionary<int, MidiShortcutAction> BuildMidiShortcutMap()
    {
        var result = new Dictionary<int, MidiShortcutAction>();
        foreach (var action in BindingPriority())
        {
            var note = GetMidiShortcutNote(action);
            if (note is >= 0 and <= 127 && note != _profile.Settings.MidiRemoteArmNote)
                result.TryAdd(note, action);
        }
        return result;
    }

    private MidiShortcutBindingItem BindingItem(MidiShortcutAction action, string contexts) =>
        new(
            action.ToString(),
            MidiShortcutRouter.GetActionLabel(action),
            contexts,
            FormatMidiShortcutNote(GetMidiShortcutNote(action)));

    private MidiShortcutAction FindActionForNote(int midiNoteNumber) =>
        BindingPriority().FirstOrDefault(action => GetMidiShortcutNote(action) == midiNoteNumber);

    private int GetMidiShortcutNote(MidiShortcutAction action) => action switch
    {
        MidiShortcutAction.StartListen => _profile.Settings.MidiShortcutListenNote,
        MidiShortcutAction.StartPractice => _profile.Settings.MidiShortcutPracticeNote,
        MidiShortcutAction.StartPerformance => _profile.Settings.MidiShortcutPerformanceNote,
        MidiShortcutAction.TogglePlayback => _profile.Settings.MidiShortcutTogglePlayNote,
        MidiShortcutAction.Restart => _profile.Settings.MidiShortcutRestartNote,
        MidiShortcutAction.PreviousMeasure => _profile.Settings.MidiShortcutPreviousMeasureNote,
        MidiShortcutAction.NextMeasure => _profile.Settings.MidiShortcutNextMeasureNote,
        MidiShortcutAction.PreviousPage => _profile.Settings.MidiShortcutPreviousPageNote,
        MidiShortcutAction.NextPage => _profile.Settings.MidiShortcutNextPageNote,
        MidiShortcutAction.DismissResults => _profile.Settings.MidiShortcutDismissResultsNote,
        MidiShortcutAction.RepeatResults => _profile.Settings.MidiShortcutRepeatResultsNote,
        _ => -1
    };

    private void SetMidiShortcutNote(MidiShortcutAction action, int noteNumber)
    {
        switch (action)
        {
            case MidiShortcutAction.StartListen: _profile.Settings.MidiShortcutListenNote = noteNumber; break;
            case MidiShortcutAction.StartPractice: _profile.Settings.MidiShortcutPracticeNote = noteNumber; break;
            case MidiShortcutAction.StartPerformance: _profile.Settings.MidiShortcutPerformanceNote = noteNumber; break;
            case MidiShortcutAction.TogglePlayback: _profile.Settings.MidiShortcutTogglePlayNote = noteNumber; break;
            case MidiShortcutAction.Restart: _profile.Settings.MidiShortcutRestartNote = noteNumber; break;
            case MidiShortcutAction.PreviousMeasure: _profile.Settings.MidiShortcutPreviousMeasureNote = noteNumber; break;
            case MidiShortcutAction.NextMeasure: _profile.Settings.MidiShortcutNextMeasureNote = noteNumber; break;
            case MidiShortcutAction.PreviousPage: _profile.Settings.MidiShortcutPreviousPageNote = noteNumber; break;
            case MidiShortcutAction.NextPage: _profile.Settings.MidiShortcutNextPageNote = noteNumber; break;
            case MidiShortcutAction.DismissResults: _profile.Settings.MidiShortcutDismissResultsNote = noteNumber; break;
            case MidiShortcutAction.RepeatResults: _profile.Settings.MidiShortcutRepeatResultsNote = noteNumber; break;
        }
    }

    private void RefreshMidiShortcutPresentation()
    {
        OnPropertyChanged(nameof(MidiRemoteArmNoteText));
        OnPropertyChanged(nameof(MidiRemoteInstructions));
        OnPropertyChanged(nameof(MidiShortcutBindings));
    }

    private static int ValidMidiNoteOrDefault(int noteNumber, int fallback) =>
        noteNumber is >= 0 and <= 127 ? noteNumber : fallback;

    private static string FormatMidiShortcutNote(int noteNumber) => noteNumber is >= 0 and <= 127
        ? MidiNoteFormatter.Format(noteNumber)
        : "Unassigned";

    private static MidiShortcutAction ParseAction(string actionId) =>
        Enum.TryParse<MidiShortcutAction>(actionId, out var action) ? action : MidiShortcutAction.None;

    private static IReadOnlyList<MidiShortcutAction> BindingPriority() =>
    [
        MidiShortcutAction.Restart,
        MidiShortcutAction.StartListen,
        MidiShortcutAction.StartPractice,
        MidiShortcutAction.StartPerformance,
        MidiShortcutAction.TogglePlayback,
        MidiShortcutAction.PreviousMeasure,
        MidiShortcutAction.NextMeasure,
        MidiShortcutAction.PreviousPage,
        MidiShortcutAction.NextPage,
        MidiShortcutAction.DismissResults,
        MidiShortcutAction.RepeatResults
    ];
}

public sealed record MidiShortcutBindingItem(
    string ActionId,
    string Label,
    string Contexts,
    string NoteText);
