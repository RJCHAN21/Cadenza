using PianoPractice.Desktop.Models;
using PianoPractice.Desktop.Services;
using System.Windows;

namespace PianoPractice.Desktop;

public sealed partial class MainWindowViewModel
{
    private const string MidiRemoteArmActionId = "RemoteArm";
    private const string MidiControlLearningPrefix = "Control:";
    private const string MidiNoteLearningPrefix = "Note:";
    private readonly MidiShortcutRouter _midiShortcutRouter = new();
    private readonly HashSet<int> _midiLearningConsumedNotes = [];
    private readonly HashSet<int> _hardwareShortcutConsumedNotes = [];
    private readonly HashSet<string> _pressedMidiControls = [];
    private string? _midiShortcutLearningActionId;
    private string _midiRemoteStatusText = "MIDI Remote ready.";
    private bool _isMidiRemoteArmed;
    private int _midiRemoteArmGeneration;
    private bool _midiShortcutCommandsSuspended;
    private DateTimeOffset? _hardwareRemoteArmedUntil;
    private int _midiControllerSaveGeneration;
    private bool _applyingMidiContinuousControl;

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
        ? _profile.Settings.MidiRemoteArmNote is >= 0 and <= 127
            ? $"Press the assigned Remote Arm button, or tap and release {MidiRemoteArmNoteText}, then play one assigned shortcut note within 3 seconds. Transport buttons, knobs, and faders act directly."
            : "Press the assigned Remote Arm button, then play one assigned shortcut note within 3 seconds. Transport buttons, knobs, and faders act directly."
        : "Enable protected MIDI shortcuts to use a MIDI key as a deliberate remote command.";

    public string MidiRemoteArmControlText => FormatControllerBinding(MidiRemoteArmActionId, GetControllerBinding(MidiRemoteArmActionId));

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
        : IsControlLearning(_midiShortcutLearningActionId)
            ? $"Move or press the MIDI control for {LearningActionLabel(_midiShortcutLearningActionId)}."
            : _midiShortcutLearningActionId.EndsWith(MidiRemoteArmActionId, StringComparison.Ordinal)
                ? "Press the piano key that should arm protected note shortcuts."
                : $"Press a piano key for {MidiShortcutRouter.GetActionLabel(ParseLearningAction(_midiShortcutLearningActionId))}.";

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
        BindingItem(MidiShortcutAction.RepeatResults, "Results only"),
        BindingItem(MidiShortcutAction.Stop, "Any player state"),
        BindingItem(MidiShortcutAction.ToggleLoop, "Any player state"),
        BindingItem(MidiShortcutAction.SetLessonTempo, "Ready, running, or paused"),
        BindingItem(MidiShortcutAction.SetNotationZoom, "Any player state"),
        BindingItem(MidiShortcutAction.SetOverallVolume, "Any player state"),
        BindingItem(MidiShortcutAction.SetInstrumentalVolume, "Any player state"),
        BindingItem(MidiShortcutAction.SetMetronomeVolume, "Any player state"),
        BindingItem(MidiShortcutAction.SetMonitorVolume, "Any player state")
    ];

    public void StartMidiShortcutLearning(string actionId)
    {
        var baseActionId = StripLearningPrefix(actionId);
        if (baseActionId != MidiRemoteArmActionId && ParseAction(baseActionId) == MidiShortcutAction.None) return;
        ResetMidiShortcutState();
        _midiShortcutLearningActionId = actionId;
        MidiRemoteStatusText = IsControlLearning(actionId)
            ? "MIDI learn is listening for one button, knob, or fader movement."
            : "MIDI learn is listening for one piano key press.";
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
        var action = ParseAction(StripLearningPrefix(actionId));
        if (action == MidiShortcutAction.None) return;
        if (IsControlLearning(actionId))
        {
            _profile.Settings.MidiControllerBindings[action.ToString()] = string.Empty;
            MidiRemoteStatusText = $"Direct {MidiShortcutRouter.GetActionLabel(action)} control is now unassigned.";
        }
        else
        {
            SetMidiShortcutNote(action, -1);
            MidiRemoteStatusText = $"Protected-note {MidiShortcutRouter.GetActionLabel(action)} is now unassigned.";
        }
        RefreshMidiShortcutPresentation();
        SaveProfileSettings();
    }

    private void InitializeMidiShortcutSettings()
    {
        var settings = _profile.Settings;
        settings.MidiControllerBindings ??= new Dictionary<string, string>(StringComparer.Ordinal);
        AddDefaultControllerBinding(MidiShortcutAction.ToggleLoop.ToString(), true, MidiControllerMessageKind.Note, 0, 86);
        AddDefaultControllerBinding(MidiShortcutAction.Stop.ToString(), true, MidiControllerMessageKind.Note, 0, 93);
        AddDefaultControllerBinding(MidiShortcutAction.TogglePlayback.ToString(), true, MidiControllerMessageKind.Note, 0, 94);
        AddDefaultControllerBinding(MidiShortcutAction.StartPractice.ToString(), true, MidiControllerMessageKind.Note, 0, 95);
        AddDefaultControllerBinding(MidiShortcutAction.SetLessonTempo.ToString(), true, MidiControllerMessageKind.ControlChange, 0, 16);
        AddDefaultControllerBinding(MidiShortcutAction.SetNotationZoom.ToString(), true, MidiControllerMessageKind.ControlChange, 0, 17);
        AddDefaultControllerBinding(MidiShortcutAction.SetInstrumentalVolume.ToString(), true, MidiControllerMessageKind.PitchBend, 0, 0);
        AddDefaultControllerBinding(MidiShortcutAction.SetMetronomeVolume.ToString(), true, MidiControllerMessageKind.PitchBend, 1, 0);
        AddDefaultControllerBinding(MidiShortcutAction.SetMonitorVolume.ToString(), true, MidiControllerMessageKind.PitchBend, 2, 0);
        AddDefaultControllerBinding(MidiShortcutAction.SetOverallVolume.ToString(), true, MidiControllerMessageKind.PitchBend, 8, 0);
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

    private void AddDefaultControllerBinding(
        string actionId,
        bool controlSurface,
        MidiControllerMessageKind kind,
        int channel,
        int number)
    {
        _profile.Settings.MidiControllerBindings.TryAdd(
            actionId,
            new MidiControllerBinding(controlSurface, kind, channel, number).Serialize());
    }

    private bool TryCaptureMidiShortcutLearning(int midiNoteNumber)
    {
        if (_midiShortcutLearningActionId is null || IsControlLearning(_midiShortcutLearningActionId)) return false;
        _midiLearningConsumedNotes.Add(midiNoteNumber);

        var learningActionId = StripLearningPrefix(_midiShortcutLearningActionId);
        if (learningActionId == MidiRemoteArmActionId)
        {
            var displaced = FindActionForNote(midiNoteNumber);
            if (displaced != MidiShortcutAction.None) SetMidiShortcutNote(displaced, -1);
            _profile.Settings.MidiRemoteArmNote = midiNoteNumber;
            CompleteMidiShortcutLearning($"{FormatMidiShortcutNote(midiNoteNumber)} is now the MIDI Remote key.");
            return true;
        }

        var action = ParseAction(learningActionId);
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
        var now = DateTimeOffset.UtcNow;
        if (_hardwareRemoteArmedUntil is not null && now <= _hardwareRemoteArmedUntil)
        {
            _hardwareRemoteArmedUntil = null;
            if (BuildMidiShortcutMap().TryGetValue(midiNoteNumber, out var hardwareArmedAction))
            {
                _hardwareShortcutConsumedNotes.Add(midiNoteNumber);
                var allowed = MidiShortcutRouter.IsActionAllowed(hardwareArmedAction, GetMidiShortcutContext());
                ApplyMidiShortcutRoute(new MidiShortcutRouteResult(
                    true,
                    allowed ? hardwareArmedAction : MidiShortcutAction.None,
                    allowed ? MidiShortcutSignal.Triggered : MidiShortcutSignal.Blocked,
                    Message: allowed
                        ? $"Remote button triggered {MidiShortcutRouter.GetActionLabel(hardwareArmedAction)}."
                        : $"{MidiShortcutRouter.GetActionLabel(hardwareArmedAction)} is unavailable in the current state."), midiNoteNumber);
                return true;
            }
        }
        var route = _midiShortcutRouter.ProcessNoteOn(
            midiNoteNumber,
            _profile.Settings.MidiRemoteArmNote,
            BuildMidiShortcutMap(),
            GetMidiShortcutContext(),
            now);
        ApplyMidiShortcutRoute(route, midiNoteNumber);
        return route.Consumed;
    }

    private bool TryHandleMidiShortcutNoteOff(int midiNoteNumber)
    {
        if (_hardwareShortcutConsumedNotes.Remove(midiNoteNumber)) return true;
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
                    if (ResultsVisible) DismissResults();
                    if (await SwitchLessonModeAsync(LessonMode.Listen)) await StartSelectedModeAsync();
                    break;
                case MidiShortcutAction.StartPractice:
                    if (ResultsVisible) DismissResults();
                    if (await SwitchLessonModeAsync(LessonMode.WaitForYou)) await StartSelectedModeAsync();
                    break;
                case MidiShortcutAction.StartPerformance:
                    if (ResultsVisible) DismissResults();
                    if (await SwitchLessonModeAsync(LessonMode.TimedPlay)) await StartSelectedModeAsync();
                    break;
                case MidiShortcutAction.TogglePlayback:
                    if (IsLessonActive) StopLesson();
                    else await TogglePreviewAsync();
                    break;
                case MidiShortcutAction.Restart:
                    if (ResultsVisible) await TriggerAutoRepeatAsync();
                    else await RestartPreviewAsync();
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
                case MidiShortcutAction.Stop:
                    if (ResultsVisible) DismissResults();
                    else StopTransport();
                    break;
                case MidiShortcutAction.ToggleLoop:
                    IsLoopEnabled = !IsLoopEnabled;
                    break;
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"MIDI shortcut failed safely: {exception.Message}";
        }
    }

    private void HandleMidiControllerMessage(MidiRawEvent message, bool controlSurface)
    {
        var binding = MidiControllerBinding.FromRaw(message.Status, message.Data1, controlSurface);
        if (binding is null) return;
        var command = message.Status & 0xF0;
        var isNoteRelease = command == 0x80 || (command == 0x90 && message.Data2 == 0);

        if (_midiShortcutLearningActionId is not null && IsControlLearning(_midiShortcutLearningActionId))
        {
            if (isNoteRelease || (!controlSurface && binding.Kind == MidiControllerMessageKind.Note)) return;
            var actionId = StripLearningPrefix(_midiShortcutLearningActionId);
            foreach (var duplicate in _profile.Settings.MidiControllerBindings
                         .Where(pair => pair.Key != actionId && MidiControllerBinding.Parse(pair.Value) == binding)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _profile.Settings.MidiControllerBindings[duplicate] = string.Empty;
            }
            _profile.Settings.MidiControllerBindings[actionId] = binding.Serialize();
            if (actionId == MidiRemoteArmActionId)
            {
                _profile.Settings.MidiRemoteArmNote = -1;
            }
            CompleteMidiShortcutLearning(
                $"{binding.Format()} now controls {LearningActionLabel(actionId)} directly; no Remote Arm step is required.");
            return;
        }

        if (!MidiShortcutsEnabled || !IsPlayerVisible || _midiShortcutCommandsSuspended) return;
        var match = _profile.Settings.MidiControllerBindings
            .Select(pair => (pair.Key, Binding: MidiControllerBinding.Parse(pair.Value)))
            .FirstOrDefault(pair => pair.Binding is not null &&
                                    pair.Binding.ControlSurface == controlSurface &&
                                    pair.Binding.Matches(message.Status, message.Data1));
        if (match.Binding is null) return;

        var controlId = match.Key;
        if (match.Binding.Kind == MidiControllerMessageKind.Note)
        {
            if (isNoteRelease)
            {
                _pressedMidiControls.Remove(controlId);
                return;
            }
            if (!_pressedMidiControls.Add(controlId)) return;
        }
        else if (match.Binding.Kind == MidiControllerMessageKind.ControlChange &&
                 !MidiShortcutRouter.IsContinuousAction(ParseAction(controlId)))
        {
            if (message.Data2 < 64)
            {
                _pressedMidiControls.Remove(controlId);
                return;
            }
            if (!_pressedMidiControls.Add(controlId)) return;
        }

        if (controlId == MidiRemoteArmActionId)
        {
            var armedUntil = DateTimeOffset.UtcNow + MidiShortcutRouter.ArmedDuration;
            _hardwareRemoteArmedUntil = armedUntil;
            IsMidiRemoteArmed = true;
            MidiRemoteStatusText = "Remote Arm button pressed; play one assigned shortcut note within 3 seconds.";
            InputActivityLabel = $"MIDI Remote armed from {match.Binding.Format()}.";
            ScheduleMidiRemoteExpiry(armedUntil);
            return;
        }

        var action = ParseAction(controlId);
        if (action == MidiShortcutAction.None) return;
        if (!MidiShortcutRouter.IsActionAllowed(action, GetMidiShortcutContext()))
        {
            StatusMessage = $"{MidiShortcutRouter.GetActionLabel(action)} is unavailable in the current state.";
            return;
        }

        if (MidiShortcutRouter.IsContinuousAction(action))
        {
            ApplyMidiContinuousControl(action, match.Binding, message.Data1, message.Data2);
            return;
        }

        InputActivityLabel = $"Direct MIDI control: {MidiShortcutRouter.GetActionLabel(action)} from {match.Binding.Format()}.";
        _ = ExecuteProtectedMidiShortcutAsync(action);
    }

    private void ApplyMidiContinuousControl(
        MidiShortcutAction action,
        MidiControllerBinding binding,
        int data1,
        int data2)
    {
        _applyingMidiContinuousControl = true;
        try
        {
            if (binding.Kind == MidiControllerMessageKind.ControlChange && binding.ControlSurface)
            {
                var delta = data2 switch
                {
                    >= 1 and <= 63 => 1,
                    >= 65 and <= 127 => -1,
                    _ => 0
                };
                if (delta == 0) return;
                if (action == MidiShortcutAction.SetLessonTempo) LessonTempoPercent += delta;
                else if (action == MidiShortcutAction.SetNotationZoom) NotationZoomPercent += delta;
                else ApplyAbsoluteMidiValue(action, Math.Clamp(CurrentContinuousValue(action) + delta, 0, 100));
            }
            else
            {
                var normalized = binding.Kind == MidiControllerMessageKind.PitchBend
                    ? ((data2 << 7) | data1) / 16383d
                    : data2 / 127d;
                var value = action switch
                {
                    MidiShortcutAction.SetLessonTempo => 50 + (int)Math.Round(normalized * 70),
                    MidiShortcutAction.SetNotationZoom => 80 + (int)Math.Round(normalized * 85),
                    _ => (int)Math.Round(normalized * 100)
                };
                ApplyAbsoluteMidiValue(action, value);
            }
        }
        finally
        {
            _applyingMidiContinuousControl = false;
        }

        InputActivityLabel = $"Direct MIDI control: {MidiShortcutRouter.GetActionLabel(action)} = {CurrentContinuousValue(action)}.";
        ScheduleMidiControllerSettingsSave();
    }

    private void ApplyAbsoluteMidiValue(MidiShortcutAction action, int value)
    {
        switch (action)
        {
            case MidiShortcutAction.SetLessonTempo: LessonTempoPercent = value; break;
            case MidiShortcutAction.SetNotationZoom: NotationZoomPercent = value; break;
            case MidiShortcutAction.SetOverallVolume: OverallVolume = value; break;
            case MidiShortcutAction.SetInstrumentalVolume: InstrumentalVolume = value; break;
            case MidiShortcutAction.SetMetronomeVolume: MetronomeVolume = value; break;
            case MidiShortcutAction.SetMonitorVolume: MonitorVolume = value; break;
        }
    }

    private int CurrentContinuousValue(MidiShortcutAction action) => action switch
    {
        MidiShortcutAction.SetLessonTempo => LessonTempoPercent,
        MidiShortcutAction.SetNotationZoom => NotationZoomPercent,
        MidiShortcutAction.SetOverallVolume => OverallVolume,
        MidiShortcutAction.SetInstrumentalVolume => InstrumentalVolume,
        MidiShortcutAction.SetMetronomeVolume => MetronomeVolume,
        MidiShortcutAction.SetMonitorVolume => MonitorVolume,
        _ => 0
    };

    private void ScheduleMidiControllerSettingsSave()
    {
        var generation = Interlocked.Increment(ref _midiControllerSaveGeneration);
        _ = SaveMidiControllerSettingsAfterIdleAsync(generation);
    }

    private async Task SaveMidiControllerSettingsAfterIdleAsync(int generation)
    {
        await Task.Delay(350);
        if (generation != _midiControllerSaveGeneration) return;
        if (Application.Current is null)
        {
            SaveProfileSettings();
            return;
        }
        await Application.Current.Dispatcher.InvokeAsync(SaveProfileSettings);
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
            _hardwareRemoteArmedUntil = null;
            IsMidiRemoteArmed = false;
            MidiRemoteStatusText = "MIDI Remote ready. Tap the Remote key to arm it.";
        });
    }

    private void DisarmMidiRemoteHud()
    {
        Interlocked.Increment(ref _midiRemoteArmGeneration);
        IsMidiRemoteArmed = false;
        _hardwareRemoteArmedUntil = null;
    }

    private void ResetMidiShortcutState()
    {
        _midiShortcutRouter.Reset();
        _midiLearningConsumedNotes.Clear();
        _hardwareShortcutConsumedNotes.Clear();
        _pressedMidiControls.Clear();
        _hardwareRemoteArmedUntil = null;
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
            FormatControllerBinding(action.ToString(), GetControllerBinding(action.ToString())),
            SupportsProtectedNoteBinding(action) ? FormatMidiShortcutNote(GetMidiShortcutNote(action)) : "—",
            SupportsProtectedNoteBinding(action));

    private static bool SupportsProtectedNoteBinding(MidiShortcutAction action) => action is
        MidiShortcutAction.StartListen or
        MidiShortcutAction.StartPractice or
        MidiShortcutAction.StartPerformance or
        MidiShortcutAction.TogglePlayback or
        MidiShortcutAction.Restart or
        MidiShortcutAction.PreviousMeasure or
        MidiShortcutAction.NextMeasure or
        MidiShortcutAction.PreviousPage or
        MidiShortcutAction.NextPage or
        MidiShortcutAction.DismissResults or
        MidiShortcutAction.RepeatResults;

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
        OnPropertyChanged(nameof(MidiRemoteArmControlText));
        OnPropertyChanged(nameof(MidiRemoteInstructions));
        OnPropertyChanged(nameof(MidiShortcutBindings));
    }

    private static int ValidMidiNoteOrDefault(int noteNumber, int fallback) =>
        noteNumber is >= -1 and <= 127 ? noteNumber : fallback;

    private static string FormatMidiShortcutNote(int noteNumber) => noteNumber is >= 0 and <= 127
        ? MidiNoteFormatter.Format(noteNumber)
        : "Unassigned";

    private static MidiShortcutAction ParseAction(string actionId) =>
        Enum.TryParse<MidiShortcutAction>(actionId, out var action) ? action : MidiShortcutAction.None;

    private static MidiShortcutAction ParseLearningAction(string actionId) => ParseAction(StripLearningPrefix(actionId));

    private static string StripLearningPrefix(string actionId) =>
        actionId.StartsWith(MidiControlLearningPrefix, StringComparison.Ordinal)
            ? actionId[MidiControlLearningPrefix.Length..]
            : actionId.StartsWith(MidiNoteLearningPrefix, StringComparison.Ordinal)
                ? actionId[MidiNoteLearningPrefix.Length..]
                : actionId;

    private static bool IsControlLearning(string actionId) =>
        actionId.StartsWith(MidiControlLearningPrefix, StringComparison.Ordinal);

    private static string LearningActionLabel(string actionId)
    {
        var stripped = StripLearningPrefix(actionId);
        return stripped == MidiRemoteArmActionId
            ? "Remote Arm"
            : MidiShortcutRouter.GetActionLabel(ParseAction(stripped));
    }

    private MidiControllerBinding? GetControllerBinding(string actionId) =>
        _profile.Settings.MidiControllerBindings.TryGetValue(actionId, out var serialized)
            ? MidiControllerBinding.Parse(serialized)
            : null;

    private static string FormatControllerBinding(MidiControllerBinding? binding) =>
        binding?.Format() ?? "Unassigned";

    private static string FormatControllerBinding(string actionId, MidiControllerBinding? binding)
    {
        if (binding is not { ControlSurface: true }) return FormatControllerBinding(binding);
        if (binding.Kind == MidiControllerMessageKind.Note && binding.Channel == 0)
        {
            if (actionId == MidiShortcutAction.ToggleLoop.ToString() && binding.Number == 86) return "Oxygen Loop";
            if (actionId == MidiShortcutAction.Stop.ToString() && binding.Number == 93) return "Oxygen Stop";
            if (actionId == MidiShortcutAction.TogglePlayback.ToString() && binding.Number == 94) return "Oxygen Play";
            if (actionId == MidiShortcutAction.StartPractice.ToString() && binding.Number == 95) return "Oxygen Record";
        }
        if (binding.Kind == MidiControllerMessageKind.ControlChange && binding.Channel == 0)
        {
            if (binding.Number == 16) return "Oxygen Knob 1";
            if (binding.Number == 17) return "Oxygen Knob 2";
        }
        if (binding.Kind == MidiControllerMessageKind.PitchBend)
        {
            return $"Oxygen Fader {binding.Channel + 1}";
        }
        return binding.Format();
    }

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
    string DirectControlText,
    string NoteText,
    bool CanUseNoteBinding)
{
    public string ControlActionId => $"Control:{ActionId}";
    public string NoteActionId => $"Note:{ActionId}";
}
