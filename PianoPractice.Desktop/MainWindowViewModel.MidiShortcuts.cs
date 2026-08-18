using PianoPractice.Desktop.Models;
using PianoPractice.Desktop.Services;
using System.Windows;

namespace PianoPractice.Desktop;

public sealed partial class MainWindowViewModel
{
    private const string MidiRemoteArmActionId = "RemoteArm";
    private const string MidiControlLearningPrefix = "Control:";
    private const string MidiNoteLearningPrefix = "Note:";
    private static readonly (MidiShortcutAction Action, string ControlName)[] MidiControllerMapSteps =
    [
        (MidiShortcutAction.ToggleLoop, "Loop"),
        (MidiShortcutAction.Stop, "Stop"),
        (MidiShortcutAction.Play, "Play"),
        (MidiShortcutAction.StartPractice, "Record"),
        (MidiShortcutAction.SetLessonTempo, "Knob 1"),
        (MidiShortcutAction.SetNotationZoom, "Knob 2"),
        (MidiShortcutAction.SetInstrumentalVolume, "Fader 1"),
        (MidiShortcutAction.SetMetronomeVolume, "Fader 2"),
        (MidiShortcutAction.SetMonitorVolume, "Fader 3"),
        (MidiShortcutAction.SetOverallVolume, "Fader 9")
    ];
    private static readonly IReadOnlyDictionary<string, string> LegacyAssumedControllerBindings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(MidiShortcutAction.ToggleLoop)] = "Surface|Note|0|86",
            [nameof(MidiShortcutAction.Stop)] = "Surface|Note|0|93",
            [nameof(MidiShortcutAction.TogglePlayback)] = "Surface|Note|0|94",
            [nameof(MidiShortcutAction.StartPractice)] = "Surface|Note|0|95",
            [nameof(MidiShortcutAction.SetLessonTempo)] = "Surface|ControlChange|0|16",
            [nameof(MidiShortcutAction.SetNotationZoom)] = "Surface|ControlChange|0|17",
            [nameof(MidiShortcutAction.SetInstrumentalVolume)] = "Surface|PitchBend|0|0",
            [nameof(MidiShortcutAction.SetMetronomeVolume)] = "Surface|PitchBend|1|0",
            [nameof(MidiShortcutAction.SetMonitorVolume)] = "Surface|PitchBend|2|0",
            [nameof(MidiShortcutAction.SetOverallVolume)] = "Surface|PitchBend|8|0"
        };
    private readonly MidiShortcutRouter _midiShortcutRouter = new();
    private readonly HashSet<int> _midiLearningConsumedNotes = [];
    private readonly HashSet<string> _pressedMidiControls = [];
    private string? _midiShortcutLearningActionId;
    private string _midiRemoteStatusText = "MIDI Remote ready.";
    private bool _isMidiRemoteArmed;
    private int _midiRemoteArmGeneration;
    private bool _midiShortcutCommandsSuspended;
    private int _midiControllerSaveGeneration;
    private bool _applyingMidiContinuousControl;
    private int _midiControllerMapIndex = -1;
    private int _midiControllerMapCaptureGeneration;
    private MidiControllerBinding? _pendingMidiControllerMapBinding;
    private readonly List<int> _pendingMidiControllerMapValues = [];

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
                ? "MIDI Remote ready. Use the assigned hardware control to arm protected note shortcuts."
                : "MIDI shortcuts are off; every MIDI key is musical input.";
            OnPropertyChanged();
            OnPropertyChanged(nameof(MidiRemoteInstructions));
            SaveProfileSettings();
        }
    }

    public string MidiRemoteArmNoteText => FormatMidiShortcutNote(_profile.Settings.MidiRemoteArmNote);

    public string MidiRemoteInstructions => MidiShortcutsEnabled
        ? HasDedicatedMidiRemoteArmControl
            ? "Press the assigned hardware Remote Arm control, then play one assigned shortcut note within 3 seconds. Piano keys cannot arm shortcuts. Transport buttons, knobs, and faders act directly."
            : "Assign a dedicated hardware button or non-note MIDI control to Remote Arm before piano-note shortcuts can be used. Until then, every piano key remains musical input."
        : "Enable MIDI controller shortcuts, then assign a dedicated hardware Remote Arm control to use protected piano-note commands.";

    public string MidiRemoteArmControlText => IsLearningTarget($"{MidiControlLearningPrefix}{MidiRemoteArmActionId}")
        ? "Listening…"
        : FormatControllerBinding(MidiRemoteArmActionId, GetControllerBinding(MidiRemoteArmActionId));

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

    public bool IsMidiShortcutLearning => _midiShortcutLearningActionId is not null || IsMidiControllerAutoMapping;

    public bool IsMidiControllerAutoMapping => _midiControllerMapIndex >= 0;

    public string MidiShortcutLearningText => IsMidiControllerAutoMapping
        ? _pendingMidiControllerMapBinding is null
            ? $"Step {_midiControllerMapIndex + 1}/{MidiControllerMapSteps.Length}: press or move {MidiControllerMapSteps[_midiControllerMapIndex].ControlName}."
            : $"Detected {MidiControllerMapSteps[_midiControllerMapIndex].ControlName}; release the control."
        : _midiShortcutLearningActionId is null
            ? "Select a binding to change it."
        : IsControlLearning(_midiShortcutLearningActionId)
            ? $"Move or press the MIDI control for {LearningActionLabel(_midiShortcutLearningActionId)}."
            : $"Press a piano key for {MidiShortcutRouter.GetActionLabel(ParseLearningAction(_midiShortcutLearningActionId))}.";

    public void StartMidiControllerAutoMapping()
    {
        CancelMidiShortcutLearning();
        ResetMidiShortcutState();
        _midiControllerMapIndex = 0;
        _pendingMidiControllerMapBinding = null;
        _pendingMidiControllerMapValues.Clear();
        Interlocked.Increment(ref _midiControllerMapCaptureGeneration);
        MidiRemoteStatusText = "Controller mapping started. Use the physical controls shown in order.";
        OnPropertyChanged(nameof(IsMidiShortcutLearning));
        OnPropertyChanged(nameof(IsMidiControllerAutoMapping));
        OnPropertyChanged(nameof(MidiShortcutLearningText));
    }

    public IReadOnlyList<MidiShortcutBindingItem> MidiShortcutBindings =>
    [
        BindingItem(MidiShortcutAction.StartListen, "Ready only"),
        BindingItem(MidiShortcutAction.StartPractice, "Ready only"),
        BindingItem(MidiShortcutAction.StartPerformance, "Ready only"),
        BindingItem(MidiShortcutAction.Play, "Ready or paused"),
        BindingItem(MidiShortcutAction.Pause, "Listen playback only"),
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
        if (baseActionId == MidiRemoteArmActionId && !IsControlLearning(actionId))
        {
            MidiRemoteStatusText = "Remote Arm must be a dedicated hardware button or non-note MIDI control, not a piano key.";
            return;
        }
        CancelMidiControllerAutoMapping();
        ResetMidiShortcutState();
        _midiShortcutLearningActionId = actionId;
        MidiRemoteStatusText = IsControlLearning(actionId)
            ? "MIDI learn is listening for one button, knob, or fader movement."
            : "MIDI learn is listening for one piano key press.";
        RefreshMidiShortcutPresentation();
        OnPropertyChanged(nameof(IsMidiShortcutLearning));
        OnPropertyChanged(nameof(MidiShortcutLearningText));
    }

    public void CancelMidiShortcutLearning()
    {
        if (_midiShortcutLearningActionId is null && !IsMidiControllerAutoMapping) return;
        _midiShortcutLearningActionId = null;
        CancelMidiControllerAutoMapping();
        _midiLearningConsumedNotes.Clear();
        MidiRemoteStatusText = MidiShortcutsEnabled
            ? "MIDI Remote ready. Use the assigned hardware control to arm protected note shortcuts."
            : "MIDI shortcuts are off; every MIDI key is musical input.";
        RefreshMidiShortcutPresentation();
        OnPropertyChanged(nameof(IsMidiShortcutLearning));
        OnPropertyChanged(nameof(MidiShortcutLearningText));
    }

    public bool UnbindCurrentMidiShortcutLearning()
    {
        if (_midiShortcutLearningActionId is null) return false;

        var learningActionId = _midiShortcutLearningActionId;
        var actionId = StripLearningPrefix(learningActionId);
        _midiShortcutLearningActionId = null;
        _midiLearningConsumedNotes.Clear();

        if (actionId == MidiRemoteArmActionId && IsControlLearning(learningActionId))
        {
            _profile.Settings.MidiControllerBindings[MidiRemoteArmActionId] = string.Empty;
            MidiRemoteStatusText = "Remote Arm is now unassigned.";
        }
        else
        {
            UnbindMidiShortcut(learningActionId);
        }

        RefreshMidiShortcutPresentation();
        SaveProfileSettings();
        OnPropertyChanged(nameof(IsMidiShortcutLearning));
        OnPropertyChanged(nameof(MidiShortcutLearningText));
        return true;
    }

    private void CancelMidiControllerAutoMapping()
    {
        if (!IsMidiControllerAutoMapping) return;
        _midiControllerMapIndex = -1;
        _pendingMidiControllerMapBinding = null;
        _pendingMidiControllerMapValues.Clear();
        Interlocked.Increment(ref _midiControllerMapCaptureGeneration);
        OnPropertyChanged(nameof(IsMidiControllerAutoMapping));
    }

    public void SetMidiShortcutCommandsSuspended(bool suspended)
    {
        if (_midiShortcutCommandsSuspended == suspended) return;
        _midiShortcutCommandsSuspended = suspended;
        ResetMidiShortcutState();
        MidiRemoteStatusText = suspended
            ? "MIDI Remote commands are paused while this dialog is open. MIDI Learn remains available."
            : MidiShortcutsEnabled
                ? "MIDI Remote ready. Use the assigned hardware control to arm protected note shortcuts."
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
        var changed = false;
        if (settings.MidiControllerMappingVersion < 1)
        {
            foreach (var assumed in LegacyAssumedControllerBindings)
            {
                if (settings.MidiControllerBindings.TryGetValue(assumed.Key, out var saved) && saved == assumed.Value)
                    settings.MidiControllerBindings[assumed.Key] = string.Empty;
            }
            settings.MidiControllerMappingVersion = 1;
            changed = true;
        }
        if (settings.MidiControllerMappingVersion < 2)
        {
            if (settings.MidiControllerBindings.TryGetValue(nameof(MidiShortcutAction.TogglePlayback), out var legacyPlayBinding) &&
                !string.IsNullOrWhiteSpace(legacyPlayBinding) &&
                (!settings.MidiControllerBindings.TryGetValue(nameof(MidiShortcutAction.Play), out var currentPlayBinding) ||
                 string.IsNullOrWhiteSpace(currentPlayBinding)))
            {
                settings.MidiControllerBindings[nameof(MidiShortcutAction.Play)] = legacyPlayBinding;
            }
            settings.MidiControllerBindings[nameof(MidiShortcutAction.TogglePlayback)] = string.Empty;
            settings.MidiControllerMappingVersion = 2;
            changed = true;
        }
        changed |= settings.MidiRemoteArmNote != -1;
        settings.MidiRemoteArmNote = -1;

        var usedNotes = new HashSet<int>();
        foreach (var action in BindingPriority())
        {
            var note = GetMidiShortcutNote(action);
            if (note is < 0 or > 127) continue;
            if (usedNotes.Add(note)) continue;
            SetMidiShortcutNote(action, -1);
            changed = true;
        }

        MidiRemoteStatusText = settings.MidiShortcutsEnabled
            ? "MIDI Remote ready. Use the assigned hardware control to arm protected note shortcuts."
            : "MIDI shortcuts are off; every MIDI key is musical input.";
        if (changed) TrySaveProfile();
    }

    private bool TryCaptureMidiShortcutLearning(int midiNoteNumber)
    {
        if (_midiShortcutLearningActionId is null || IsControlLearning(_midiShortcutLearningActionId)) return false;
        _midiLearningConsumedNotes.Add(midiNoteNumber);

        var learningActionId = StripLearningPrefix(_midiShortcutLearningActionId);
        if (learningActionId == MidiRemoteArmActionId)
        {
            MidiRemoteStatusText = "A piano key cannot be assigned as Remote Arm. Learn a dedicated hardware control instead.";
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
        if (simulation || !MidiShortcutsEnabled || !IsPlayerVisible || !HasDedicatedMidiRemoteArmControl) return false;
        var now = DateTimeOffset.UtcNow;
        var route = _midiShortcutRouter.ProcessNoteOn(
            midiNoteNumber,
            -1,
            BuildMidiShortcutMap(),
            GetMidiShortcutContext(),
            now);
        ApplyMidiShortcutRoute(route, midiNoteNumber);
        return route.Consumed;
    }

    private bool TryHandleMidiShortcutNoteOff(int midiNoteNumber)
    {
        if (_midiLearningConsumedNotes.Remove(midiNoteNumber)) return true;
        if (!MidiShortcutsEnabled || !HasDedicatedMidiRemoteArmControl) return false;
        var route = _midiShortcutRouter.ProcessNoteOff(
            midiNoteNumber,
            -1,
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
                    if (IsLessonActive)
                        StatusMessage = "Play / Pause does not stop an active practice or performance. Use Stop instead.";
                    else
                        await TogglePreviewAsync();
                    break;
                case MidiShortcutAction.Play:
                    if (IsLessonActive)
                        StatusMessage = "Play does not stop or restart an active practice or performance. Use Stop first.";
                    else if (!IsPreviewPlaying)
                        await TogglePreviewAsync();
                    break;
                case MidiShortcutAction.Pause:
                    if (IsLessonActive)
                        StatusMessage = "Pause is available for Listen playback. Use Stop to end practice or performance.";
                    else if (IsPreviewPlaying)
                        await TogglePreviewAsync();
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

        if (IsMidiControllerAutoMapping)
        {
            CaptureMidiControllerMapMessage(binding, message.Data2, isNoteRelease);
            return;
        }

        if (_midiShortcutLearningActionId is not null && IsControlLearning(_midiShortcutLearningActionId))
        {
            if (isNoteRelease || (!controlSurface && binding.Kind == MidiControllerMessageKind.Note)) return;
            var actionId = StripLearningPrefix(_midiShortcutLearningActionId);
            var existingControl = _profile.Settings.MidiControllerBindings
                .Select(pair => (pair.Key, Binding: MidiControllerBinding.Parse(pair.Value)))
                .FirstOrDefault(pair => pair.Binding is not null && SamePhysicalControl(pair.Binding, binding));
            var sharedActions = _profile.Settings.MidiControllerBindings
                .Where(pair => pair.Key != actionId &&
                               MidiControllerBinding.Parse(pair.Value) is { } saved &&
                               SamePhysicalControl(saved, binding))
                .Select(pair => LearningActionLabel(pair.Key))
                .ToArray();
            var learnedBinding = binding with
            {
                DisplayName = existingControl.Binding?.DisplayName ??
                              PhysicalControlName(existingControl.Key) ??
                              RecognizePhysicalControl(binding)
            };
            _profile.Settings.MidiControllerBindings[actionId] = learnedBinding.Serialize();
            if (actionId == MidiRemoteArmActionId)
            {
                _profile.Settings.MidiRemoteArmNote = -1;
            }
            CompleteMidiShortcutLearning(
                (actionId == MidiRemoteArmActionId
                    ? "Remote Arm is now assigned to the control you pressed."
                    : $"{LearningActionLabel(actionId)} is now assigned to the control you used.") +
                SharedBindingWarning(sharedActions));
            return;
        }

        if (!MidiShortcutsEnabled || _midiShortcutCommandsSuspended) return;
        var matches = _profile.Settings.MidiControllerBindings
            .Select(pair => (pair.Key, Binding: MidiControllerBinding.Parse(pair.Value)))
            .Where(pair => pair.Binding is not null &&
                           pair.Binding.ControlSurface == controlSurface &&
                           pair.Binding.Matches(message.Status, message.Data1))
            .Select(pair => (pair.Key, Binding: pair.Binding!))
            .ToArray();
        if (matches.Length == 0) return;

        var triggeredActions = new List<string>();
        foreach (var match in matches)
        {
            var controlId = match.Key;
            if (match.Binding.Kind == MidiControllerMessageKind.Note)
            {
                if (isNoteRelease)
                {
                    _pressedMidiControls.Remove(controlId);
                    continue;
                }
                if (!_pressedMidiControls.Add(controlId)) continue;
            }
            else if (match.Binding.Kind == MidiControllerMessageKind.ControlChange &&
                     !MidiShortcutRouter.IsContinuousAction(ParseAction(controlId)))
            {
                if (message.Data2 < 64)
                {
                    _pressedMidiControls.Remove(controlId);
                    continue;
                }
                if (!_pressedMidiControls.Add(controlId)) continue;
            }

            if (controlId == MidiRemoteArmActionId)
            {
                var route = _midiShortcutRouter.ArmFromDedicatedControl(
                    GetMidiShortcutContext(),
                    DateTimeOffset.UtcNow);
                ApplyMidiShortcutRoute(route, message.Data1);
                triggeredActions.Add("Remote Arm");
                continue;
            }

            var action = ParseAction(controlId);
            if (action == MidiShortcutAction.None) continue;
            if (!IsPlayerVisible && !IsAppWideMidiControllerAction(action)) continue;
            if (!IsAppWideMidiControllerAction(action) &&
                !MidiShortcutRouter.IsActionAllowed(action, GetMidiShortcutContext()))
            {
                StatusMessage = $"{MidiShortcutRouter.GetActionLabel(action)} is unavailable in the current state.";
                continue;
            }

            if (MidiShortcutRouter.IsContinuousAction(action))
            {
                ApplyMidiContinuousControl(action, match.Binding, message.Data1, message.Data2);
                triggeredActions.Add($"{MidiShortcutRouter.GetActionLabel(action)} {CurrentContinuousValue(action)}%");
                continue;
            }

            triggeredActions.Add(MidiShortcutRouter.GetActionLabel(action));
            _ = ExecuteProtectedMidiShortcutAsync(action);
        }

        if (triggeredActions.Count > 0)
            InputActivityLabel = $"{ControllerDisplayName(matches[0].Key, matches[0].Binding)} · {string.Join(" + ", triggeredActions)}.";
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
            if (binding.Kind == MidiControllerMessageKind.ControlChange && binding.Relative)
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

        InputActivityLabel = $"{ControllerDisplayName(action.ToString(), binding)} · {MidiShortcutRouter.GetActionLabel(action)} {CurrentContinuousValue(action)}%.";
        ScheduleMidiControllerSettingsSave();
    }

    private void CaptureMidiControllerMapMessage(MidiControllerBinding binding, int value, bool isNoteRelease)
    {
        if (!binding.ControlSurface && binding.Kind == MidiControllerMessageKind.Note) return;
        if (_pendingMidiControllerMapBinding is not null && _pendingMidiControllerMapBinding != binding) return;
        if (_pendingMidiControllerMapBinding is null)
        {
            if (isNoteRelease) return;
            _pendingMidiControllerMapBinding = binding;
            MidiRemoteStatusText = $"Detected {MidiControllerMapSteps[_midiControllerMapIndex].ControlName}. Release or stop moving it.";
            OnPropertyChanged(nameof(MidiShortcutLearningText));
        }
        if (binding.Kind == MidiControllerMessageKind.ControlChange)
            _pendingMidiControllerMapValues.Add(Math.Clamp(value, 0, 127));

        var generation = Interlocked.Increment(ref _midiControllerMapCaptureGeneration);
        _ = CompleteMidiControllerMapStepAfterIdleAsync(generation);
    }

    private async Task CompleteMidiControllerMapStepAfterIdleAsync(int generation)
    {
        await Task.Delay(450);
        if (generation != _midiControllerMapCaptureGeneration ||
            !IsMidiControllerAutoMapping ||
            _pendingMidiControllerMapBinding is null)
        {
            return;
        }

        void CompleteStep()
        {
            if (generation != _midiControllerMapCaptureGeneration ||
                !IsMidiControllerAutoMapping ||
                _pendingMidiControllerMapBinding is null)
            {
                return;
            }

            var step = MidiControllerMapSteps[_midiControllerMapIndex];
            var learnedBinding = _pendingMidiControllerMapBinding with
            {
                Relative = IsRelativeControllerMapStep(step.Action, _pendingMidiControllerMapBinding, _pendingMidiControllerMapValues),
                DisplayName = step.ControlName
            };
            var serialized = learnedBinding.Serialize();
            var sharedActions = _profile.Settings.MidiControllerBindings
                .Where(pair => pair.Key != step.Action.ToString() &&
                               MidiControllerBinding.Parse(pair.Value) is { } saved &&
                               SamePhysicalControl(saved, learnedBinding))
                .Select(pair => LearningActionLabel(pair.Key))
                .ToArray();
            _profile.Settings.MidiControllerBindings[step.Action.ToString()] = serialized;
            SaveProfileSettings();
            _midiControllerMapIndex++;
            _pendingMidiControllerMapBinding = null;
            _pendingMidiControllerMapValues.Clear();

            if (_midiControllerMapIndex >= MidiControllerMapSteps.Length)
            {
                _midiControllerMapIndex = -1;
                MidiRemoteStatusText = "Connected controller map complete and saved from live MIDI input." +
                                       SharedBindingWarning(sharedActions);
                SaveProfileSettings();
            }
            else
            {
                MidiRemoteStatusText = $"Mapped {step.ControlName}.{SharedBindingWarning(sharedActions)} Now press or move {MidiControllerMapSteps[_midiControllerMapIndex].ControlName}.";
            }
            RefreshMidiShortcutPresentation();
            OnPropertyChanged(nameof(IsMidiShortcutLearning));
            OnPropertyChanged(nameof(IsMidiControllerAutoMapping));
            OnPropertyChanged(nameof(MidiShortcutLearningText));
        }

        if (Application.Current is null) CompleteStep();
        else await Application.Current.Dispatcher.InvokeAsync(CompleteStep);
    }

    private static bool IsRelativeControllerMapStep(
        MidiShortcutAction action,
        MidiControllerBinding binding,
        IReadOnlyCollection<int> values)
    {
        if (binding.Kind != MidiControllerMessageKind.ControlChange ||
            action is not (MidiShortcutAction.SetLessonTempo or MidiShortcutAction.SetNotationZoom) ||
            values.Count == 0)
        {
            return false;
        }

        var distinct = values.Distinct().ToArray();
        return distinct.Length == 1 && distinct[0] is 1 or 127 ||
               distinct.All(value => value is >= 1 and <= 7 or >= 121 and <= 127);
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

    private static bool IsAppWideMidiControllerAction(MidiShortcutAction action) => action is
        MidiShortcutAction.SetOverallVolume or
        MidiShortcutAction.SetInstrumentalVolume or
        MidiShortcutAction.SetMetronomeVolume or
        MidiShortcutAction.SetMonitorVolume;

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
            IsMidiRemoteArmed = false;
            MidiRemoteStatusText = "MIDI Remote ready. Use the assigned hardware control to arm protected note shortcuts.";
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
        _pressedMidiControls.Clear();
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
        if (!HasDedicatedMidiRemoteArmControl) return result;
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
            IsLearningTarget($"{MidiControlLearningPrefix}{action}")
                ? "Listening…"
                : FormatControllerBinding(action.ToString(), GetControllerBinding(action.ToString())),
            IsLearningTarget($"{MidiNoteLearningPrefix}{action}")
                ? "Listening…"
                : SupportsProtectedNoteBinding(action) ? FormatMidiShortcutNote(GetMidiShortcutNote(action)) : "—",
            SupportsProtectedNoteBinding(action) && HasDedicatedMidiRemoteArmControl);

    private static bool SupportsProtectedNoteBinding(MidiShortcutAction action) => action is
        MidiShortcutAction.StartListen or
        MidiShortcutAction.StartPractice or
        MidiShortcutAction.StartPerformance or
        MidiShortcutAction.Play or
        MidiShortcutAction.Pause or
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
        MidiShortcutAction.Play => _profile.Settings.MidiShortcutTogglePlayNote,
        MidiShortcutAction.Pause => _profile.Settings.MidiShortcutPauseNote,
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
            case MidiShortcutAction.Play: _profile.Settings.MidiShortcutTogglePlayNote = noteNumber; break;
            case MidiShortcutAction.Pause: _profile.Settings.MidiShortcutPauseNote = noteNumber; break;
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

    private bool IsLearningTarget(string actionId) =>
        string.Equals(_midiShortcutLearningActionId, actionId, StringComparison.Ordinal);

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

    private bool HasDedicatedMidiRemoteArmControl
    {
        get
        {
            var binding = GetControllerBinding(MidiRemoteArmActionId);
            return binding is not null &&
                   (binding.ControlSurface || binding.Kind != MidiControllerMessageKind.Note);
        }
    }

    private string FormatControllerBinding(string actionId, MidiControllerBinding? binding)
    {
        if (binding is null) return "Unassigned";
        return ControllerDisplayName(actionId, binding);
    }

    private string FormatUserFacingMidiActivity(MidiRawEvent message, bool controlSurface)
    {
        if (IsMidiControllerAutoMapping)
            return $"Mapping {MidiControllerMapSteps[_midiControllerMapIndex].ControlName}";

        var incoming = MidiControllerBinding.FromRaw(message.Status, message.Data1, controlSurface);
        if (incoming is not null)
        {
            var match = _profile.Settings.MidiControllerBindings
                .Select(pair => (pair.Key, Binding: MidiControllerBinding.Parse(pair.Value)))
                .FirstOrDefault(pair => pair.Binding is not null &&
                                        pair.Binding.ControlSurface == controlSurface &&
                                        pair.Binding.Matches(message.Status, message.Data1));
            if (match.Binding is not null)
            {
                var action = ParseAction(match.Key);
                var controlName = ControllerDisplayName(match.Key, match.Binding);
                return MidiShortcutRouter.IsContinuousAction(action)
                    ? $"{controlName} · {MidiShortcutRouter.GetActionLabel(action)} {CurrentContinuousValue(action)}%"
                    : controlName;
            }
        }

        return (message.Status & 0xF0) switch
        {
            _ when incoming is not null && RecognizePhysicalControl(incoming) is { } controlName => controlName,
            0xB0 => "Unassigned knob or fader",
            0xE0 => "Unassigned fader",
            0x80 or 0x90 => "Unassigned button",
            _ => "Unassigned MIDI control"
        };
    }

    private static bool SamePhysicalControl(MidiControllerBinding left, MidiControllerBinding right) =>
        left.ControlSurface == right.ControlSurface &&
        left.Kind == right.Kind &&
        left.Channel == right.Channel &&
        left.Number == right.Number;

    private string ControllerDisplayName(string actionId, MidiControllerBinding binding) =>
        binding.DisplayName ?? RecognizePhysicalControl(binding) ?? PhysicalControlName(actionId) ?? FriendlyControlType(binding);

    private string? RecognizePhysicalControl(MidiControllerBinding binding)
    {
        var deviceName = SelectedMidiDevice?.Name ?? _profile.Settings.PreferredMidiDeviceName;
        if (string.IsNullOrWhiteSpace(deviceName) ||
            !deviceName.Contains("Oxygen 49 MKV", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!binding.ControlSurface &&
            binding.Kind == MidiControllerMessageKind.ControlChange &&
            binding.Channel == 0 &&
            HasConfirmedOxygenPresetTransportLayout())
        {
            return binding.Number switch
            {
                114 => "Loop",
                115 => "Rewind",
                116 => "Fast Forward",
                117 => "Stop",
                118 => "Play",
                119 => "Record",
                _ => null
            };
        }

        if (binding.ControlSurface && binding.Channel == 0 && binding.Kind == MidiControllerMessageKind.Note)
        {
            return binding.Number switch
            {
                86 => "Loop",
                91 => "Rewind",
                92 => "Fast Forward",
                93 => "Stop",
                94 => "Play",
                95 => "Record",
                _ => null
            };
        }

        if (binding.ControlSurface &&
            binding.Kind == MidiControllerMessageKind.PitchBend &&
            binding.Channel is >= 0 and <= 8)
        {
            return $"Fader {binding.Channel + 1}";
        }

        return null;
    }

    private bool HasConfirmedOxygenPresetTransportLayout() =>
        _profile.Settings.MidiControllerBindings.Values
            .Select(MidiControllerBinding.Parse)
            .Any(binding => binding is
            {
                ControlSurface: false,
                Kind: MidiControllerMessageKind.ControlChange,
                Channel: 0,
                Number: 119,
                DisplayName: "Record"
            });

    private static string FriendlyControlType(MidiControllerBinding binding) => binding.Kind switch
    {
        MidiControllerMessageKind.Note => "Assigned button",
        MidiControllerMessageKind.ControlChange => "Assigned knob or fader",
        MidiControllerMessageKind.PitchBend => "Assigned fader",
        _ => "Assigned control"
    };

    private static string SharedBindingWarning(IReadOnlyCollection<string> sharedActions) =>
        sharedActions.Count == 0
            ? string.Empty
            : $" Warning: this control is also assigned to {string.Join(", ", sharedActions)}; every assignment was kept.";

    private static string? PhysicalControlName(string actionId) => MidiControllerMapSteps
        .FirstOrDefault(step => step.Action.ToString() == actionId)
        .ControlName;

    private static IReadOnlyList<MidiShortcutAction> BindingPriority() =>
    [
        MidiShortcutAction.Restart,
        MidiShortcutAction.StartListen,
        MidiShortcutAction.StartPractice,
        MidiShortcutAction.StartPerformance,
        MidiShortcutAction.Play,
        MidiShortcutAction.Pause,
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
