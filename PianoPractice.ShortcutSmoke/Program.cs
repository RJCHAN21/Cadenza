using System.Windows.Input;
using PianoPractice.Desktop.Models;
using PianoPractice.Desktop.Services;
using System.IO;
using System.Reflection;

Assert(AppShortcutRouter.Resolve(Key.D1, ModifierKeys.Control, false) == AppShortcutAction.SelectListen,
    "Ctrl+1 did not select Listen mode.");
Assert(AppShortcutRouter.Resolve(Key.D2, ModifierKeys.Control, false) == AppShortcutAction.SelectPractice,
    "Ctrl+2 did not select Practice mode.");
Assert(AppShortcutRouter.Resolve(Key.D3, ModifierKeys.Control, false) == AppShortcutAction.SelectPerformance,
    "Ctrl+3 did not select Performance mode.");

foreach (var pianoKey in ComputerKeyboardPianoMap.MidiNotes.Keys)
{
    Assert(AppShortcutRouter.Resolve(pianoKey, ModifierKeys.None, false) == AppShortcutAction.None,
        $"Computer-piano key {pianoKey} also resolves to an app command.");
}

Assert(AppShortcutRouter.Resolve(Key.R, ModifierKeys.None, false) == AppShortcutAction.None,
    "An unmodified letter key can restart the session.");
Assert(AppShortcutRouter.Resolve(Key.F4, ModifierKeys.None, false) == AppShortcutAction.None,
    "The removed F4 fallback can still switch modes.");
Assert(AppShortcutRouter.Resolve(Key.Space, ModifierKeys.None, true) == AppShortcutAction.None,
    "Space can accidentally repeat a completed run.");
Assert(AppShortcutRouter.Resolve(Key.Enter, ModifierKeys.None, true) == AppShortcutAction.RepeatResults,
    "Enter did not provide the explicit results repeat action.");
Assert(AppShortcutRouter.Resolve(Key.Escape, ModifierKeys.None, true) == AppShortcutAction.DismissResults,
    "Escape did not dismiss results.");
Assert(!AppShortcutRouter.AllowsComputerPianoInput(ModifierKeys.Control),
    "Modified key chords can leak into computer-piano input.");

var midiBindings = new Dictionary<int, MidiShortcutAction>
{
    [48] = MidiShortcutAction.StartListen,
    [50] = MidiShortcutAction.StartPractice,
    [60] = MidiShortcutAction.Restart,
    [67] = MidiShortcutAction.RepeatResults
};
var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
var midiRouter = new MidiShortcutRouter();

var unavailableArm = midiRouter.ArmFromDedicatedControl(MidiShortcutContext.Unavailable, now);
Assert(unavailableArm.Consumed && unavailableArm.Signal == MidiShortcutSignal.Blocked,
    "A dedicated Remote Arm control can arm while the app is busy or a dialog is open.");
Assert(!midiRouter.IsArmed(now.AddMilliseconds(100)),
    "A blocked hardware Remote Arm press left shortcuts armed.");

var ordinaryC4 = midiRouter.ProcessNoteOn(60, 84, midiBindings, MidiShortcutContext.Running, now);
Assert(!ordinaryC4.Consumed && ordinaryC4.Action == MidiShortcutAction.None,
    "An unarmed C4 can restart a running session.");
midiRouter.ProcessNoteOff(60, 84, now.AddMilliseconds(80));

var pianoArmAttempt = midiRouter.ProcessNoteOn(84, 84, midiBindings, MidiShortcutContext.Running, now.AddSeconds(1));
Assert(!pianoArmAttempt.Consumed && pianoArmAttempt.Signal == MidiShortcutSignal.None,
    "A piano key can still act as Remote Arm.");
midiRouter.ProcessNoteOff(84, 84, now.AddSeconds(1.1));
Assert(!midiRouter.IsArmed(now.AddSeconds(1.1)),
    "Releasing a piano key armed protected shortcuts.");

var armed = midiRouter.ArmFromDedicatedControl(MidiShortcutContext.Running, now.AddSeconds(1.2));
Assert(armed.Consumed && armed.Signal == MidiShortcutSignal.Armed,
    "A dedicated hardware control did not arm protected shortcuts.");
var protectedRestart = midiRouter.ProcessNoteOn(60, 84, midiBindings, MidiShortcutContext.Running, now.AddSeconds(1.3));
Assert(protectedRestart.Consumed && protectedRestart.Action == MidiShortcutAction.Restart,
    "Armed C4 did not resolve to Restart while running.");
var repeatedHeldC4 = midiRouter.ProcessNoteOn(60, 84, midiBindings, MidiShortcutContext.Running, now.AddSeconds(1.4));
Assert(repeatedHeldC4.Consumed && repeatedHeldC4.Action == MidiShortcutAction.None,
    "A repeated note-on while C4 remained held retriggered Restart.");
Assert(midiRouter.ProcessNoteOff(60, 84, now.AddSeconds(1.5)).Consumed,
    "The executed command key leaked into musical note-off handling.");

midiRouter.Reset();
midiRouter.ArmFromDedicatedControl(MidiShortcutContext.Running, now.AddSeconds(5.1));
var blockedModeSwitch = midiRouter.ProcessNoteOn(50, 84, midiBindings, MidiShortcutContext.Running, now.AddSeconds(5.2));
Assert(blockedModeSwitch.Consumed && blockedModeSwitch.Signal == MidiShortcutSignal.Blocked,
    "A MIDI mode shortcut can interrupt a running session.");
midiRouter.ProcessNoteOff(50, 84, now.AddSeconds(5.3));

midiRouter.Reset();
midiRouter.ArmFromDedicatedControl(MidiShortcutContext.Results, now.AddSeconds(6.1));
var repeatResults = midiRouter.ProcessNoteOn(67, 84, midiBindings, MidiShortcutContext.Results, now.AddSeconds(6.2));
Assert(repeatResults.Action == MidiShortcutAction.RepeatResults,
    "The protected Repeat Results binding did not work in its valid context.");

midiRouter.Reset();
midiRouter.ArmFromDedicatedControl(MidiShortcutContext.Results, now.AddSeconds(7.1));
var restartFromResults = midiRouter.ProcessNoteOn(60, 84, midiBindings, MidiShortcutContext.Results, now.AddSeconds(7.2));
Assert(restartFromResults.Action == MidiShortcutAction.Restart,
    "The protected C4 Restart binding was blocked after a completed measure.");

var oxygenPlay = new MidiControllerBinding(true, MidiControllerMessageKind.Note, 0, 94, DisplayName: "Play");
Assert(oxygenPlay.Matches(0x90, 94) && oxygenPlay.Matches(0x80, 94) && !oxygenPlay.Matches(0x90, 93),
    "The Oxygen Play transport binding did not match its Mackie note messages exactly.");
Assert(MidiControllerBinding.Parse(oxygenPlay.Serialize()) == oxygenPlay,
    "The Oxygen transport binding did not survive settings serialization.");

var oxygenKnob = new MidiControllerBinding(true, MidiControllerMessageKind.ControlChange, 0, 16);
Assert(oxygenKnob.Matches(0xB0, 16) && !oxygenKnob.Matches(0xB1, 16),
    "The Oxygen knob binding ignored its MIDI channel or CC number.");

var oxygenMasterFader = new MidiControllerBinding(true, MidiControllerMessageKind.PitchBend, 8, 0);
Assert(oxygenMasterFader.Matches(0xE8, 0) && !oxygenMasterFader.Matches(0xE7, 0),
    "The Oxygen master-fader binding ignored its MIDI channel.");

Assert(MidiShortcutRouter.IsActionAllowed(MidiShortcutAction.TogglePlayback, MidiShortcutContext.Results) &&
       MidiShortcutRouter.IsActionAllowed(MidiShortcutAction.Play, MidiShortcutContext.Paused) &&
       MidiShortcutRouter.IsActionAllowed(MidiShortcutAction.Pause, MidiShortcutContext.Running) &&
       MidiShortcutRouter.IsActionAllowed(MidiShortcutAction.Stop, MidiShortcutContext.Results) &&
       MidiShortcutRouter.IsActionAllowed(MidiShortcutAction.ToggleLoop, MidiShortcutContext.Running),
    "Direct transport controls are not available in their intended player contexts.");

var smokeRoot = Path.Combine(Path.GetTempPath(), $"cadenza-midi-shortcut-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(smokeRoot);
try
{
    var legacyProfilePath = Path.Combine(smokeRoot, "legacy-profile.json");
    var legacyProfile = CadenzaUserProfile.CreateDefault();
    legacyProfile.Settings.PreferredMidiDeviceName = "Oxygen 49 MKV";
    legacyProfile.Settings.MidiControllerBindings[nameof(MidiShortcutAction.ToggleLoop)] = "Surface|Note|0|86";
    legacyProfile.Settings.MidiControllerBindings[nameof(MidiShortcutAction.TogglePlayback)] = "Keyboard|ControlChange|0|118|Absolute";
    legacyProfile.Settings.MidiControllerBindings[nameof(MidiShortcutAction.StartPractice)] = "Keyboard|ControlChange|0|119|Absolute|Record";
    legacyProfile.Settings.MidiControllerBindings[nameof(MidiShortcutAction.StartPerformance)] = "Keyboard|ControlChange|0|118|Absolute";
    new UserProfileStore(legacyProfilePath).Save(legacyProfile);
    using (var migrated = new PianoPractice.Desktop.MainWindowViewModel(
               legacyProfilePath,
               Path.Combine(smokeRoot, "legacy-library")))
    {
        var migratedBindings = migrated.MidiShortcutBindings.ToDictionary(item => item.ActionId);
        Assert(migratedBindings[nameof(MidiShortcutAction.ToggleLoop)].DirectControlText == "Unassigned",
            "The assumption-seeded Loop placeholder survived controller-map migration.");
        Assert(migratedBindings[nameof(MidiShortcutAction.StartPractice)].DirectControlText == "Record",
            "A genuinely learned Record message was removed during controller-map migration.");
        Assert(migratedBindings[nameof(MidiShortcutAction.StartPerformance)].DirectControlText == "Play",
            "The confirmed Oxygen transport layout did not identify the saved Play button.");
        Assert(migratedBindings[nameof(MidiShortcutAction.Play)].DirectControlText == "Play" &&
               !migratedBindings.ContainsKey(nameof(MidiShortcutAction.TogglePlayback)),
            "The legacy combined transport binding was not migrated to the separate Play action.");

        var formatLegacyActivity = typeof(PianoPractice.Desktop.MainWindowViewModel).GetMethod(
            "FormatUserFacingMidiActivity",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var stopActivity = formatLegacyActivity?.Invoke(migrated,
            [new MidiRawEvent(0xB0, 117, 127, DateTimeOffset.UtcNow), false]) as string;
        Assert(stopActivity == "Stop",
            $"The live badge did not name the Oxygen Stop button: {stopActivity}");
    }

    var smokeProfilePath = Path.Combine(smokeRoot, "profile.json");
    using var viewModel = new PianoPractice.Desktop.MainWindowViewModel(
        smokeProfilePath,
        Path.Combine(smokeRoot, "library"));
    Assert(viewModel.MidiShortcutBindings.All(item => item.DirectControlText == "Unassigned"),
        "A fresh profile still claims assumption-based controller assignments.");
    Assert(viewModel.MidiShortcutBindings.Any(item => item.ActionId == nameof(MidiShortcutAction.Play) && item.Label == "Play") &&
           viewModel.MidiShortcutBindings.Any(item => item.ActionId == nameof(MidiShortcutAction.Pause) && item.Label == "Pause") &&
           viewModel.MidiShortcutBindings.Any(item => item.ActionId == nameof(MidiShortcutAction.Stop) && item.Label == "Stop") &&
           viewModel.MidiShortcutBindings.All(item => item.ActionId != nameof(MidiShortcutAction.TogglePlayback)),
        "Play, Pause, and Stop are not exposed as three separate bindings.");
    Assert(!viewModel.IsPlayerVisible, "The app-wide fader fixture unexpectedly opened the score player.");
    var handleControllerMessage = typeof(PianoPractice.Desktop.MainWindowViewModel).GetMethod(
        "HandleMidiControllerMessage",
        BindingFlags.Instance | BindingFlags.NonPublic);
    Assert(handleControllerMessage is not null, "The MIDI controller message route could not be exercised.");

    viewModel.StartMidiShortcutLearning($"Control:{nameof(MidiShortcutAction.StartListen)}");
    Assert(viewModel.MidiShortcutBindings.Single(item => item.ActionId == nameof(MidiShortcutAction.StartListen)).DirectControlText == "Listening…",
        "The selected controller binding does not visibly show that it is listening.");
    viewModel.CancelMidiShortcutLearning();

    viewModel.StartMidiShortcutLearning($"Control:{nameof(MidiShortcutAction.StartListen)}");
    Assert(viewModel.UnbindCurrentMidiShortcutLearning() &&
           viewModel.MidiShortcutBindings.Single(item => item.ActionId == nameof(MidiShortcutAction.StartListen)).DirectControlText == "Unassigned" &&
           !viewModel.IsMidiShortcutLearning,
        "Backspace-style unbinding did not clear the active learning target and leave learning mode.");

    viewModel.StartMidiControllerAutoMapping();
    handleControllerMessage!.Invoke(viewModel,
        [new MidiRawEvent(0x90, 86, 127, DateTimeOffset.UtcNow), true]);
    handleControllerMessage.Invoke(viewModel,
        [new MidiRawEvent(0x80, 86, 0, DateTimeOffset.UtcNow), true]);
    await Task.Delay(550);
    var learnedLoop = viewModel.MidiShortcutBindings.Single(item => item.ActionId == nameof(MidiShortcutAction.ToggleLoop));
    Assert(learnedLoop.DirectControlText == "Loop",
        $"Live mapping did not retain the exact Loop input message: {learnedLoop.DirectControlText}");
    viewModel.CancelMidiShortcutLearning();

    viewModel.StartMidiShortcutLearning($"Control:{nameof(MidiShortcutAction.StartListen)}");
    handleControllerMessage.Invoke(viewModel,
        [new MidiRawEvent(0x90, 86, 127, DateTimeOffset.UtcNow), true]);
    var reassignedLoop = viewModel.MidiShortcutBindings.Single(item => item.ActionId == nameof(MidiShortcutAction.StartListen));
    Assert(reassignedLoop.DirectControlText == "Loop",
        $"The physical Loop name did not follow its control when reassigned: {reassignedLoop.DirectControlText}");

    var formatActivity = typeof(PianoPractice.Desktop.MainWindowViewModel).GetMethod(
        "FormatUserFacingMidiActivity",
        BindingFlags.Instance | BindingFlags.NonPublic);
    var loopActivity = formatActivity?.Invoke(viewModel,
        [new MidiRawEvent(0x90, 86, 127, DateTimeOffset.UtcNow), true]) as string;
    Assert(loopActivity == "Loop",
        $"The live badge did not identify the reassigned physical Loop control: {loopActivity}");

    viewModel.StartMidiShortcutLearning($"Control:{nameof(MidiShortcutAction.SetOverallVolume)}");
    handleControllerMessage!.Invoke(viewModel,
        [new MidiRawEvent(0xE8, 127, 127, DateTimeOffset.UtcNow), true]);
    handleControllerMessage.Invoke(viewModel,
        [new MidiRawEvent(0xE8, 127, 127, DateTimeOffset.UtcNow), true]);
    Assert(viewModel.OverallVolume == 100,
        "The Oxygen master fader cannot control app volume outside the score player.");
    Assert(viewModel.InputActivityLabel == "Fader 9 · Overall Volume 100%.",
        $"The detailed activity label did not identify the physical fader: {viewModel.InputActivityLabel}");
    var activity = formatActivity?.Invoke(viewModel,
        [new MidiRawEvent(0xE8, 127, 127, DateTimeOffset.UtcNow), true]) as string;
    Assert(activity == "Fader 9 · Overall Volume 100%",
        $"The live controller badge exposed raw MIDI data instead of friendly text: {activity}");
    Assert(File.Exists(smokeProfilePath) && File.ReadAllText(smokeProfilePath).Contains("Surface|PitchBend|8|0|Absolute"),
        "The learned physical controller message was not persisted.");

    viewModel.StartMidiShortcutLearning($"Control:{nameof(MidiShortcutAction.SetInstrumentalVolume)}");
    handleControllerMessage.Invoke(viewModel,
        [new MidiRawEvent(0xE8, 127, 127, DateTimeOffset.UtcNow), true]);
    Assert(viewModel.MidiRemoteStatusText.Contains("also assigned to Overall Volume", StringComparison.Ordinal) &&
           viewModel.MidiRemoteStatusText.Contains("every assignment was kept", StringComparison.Ordinal),
        $"Sharing a controller binding did not produce the required warning: {viewModel.MidiRemoteStatusText}");
    handleControllerMessage.Invoke(viewModel,
        [new MidiRawEvent(0xE8, 127, 127, DateTimeOffset.UtcNow), true]);
    Assert(viewModel.OverallVolume == 100 && viewModel.InstrumentalVolume == 100,
        "A shared fader did not run every assigned action.");
}
finally
{
    Directory.Delete(smokeRoot, recursive: true);
}

Console.WriteLine("PASS deterministic shortcut safety fixtures");
return;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
