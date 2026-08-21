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
Assert(AppShortcutRouter.Resolve(Key.Home, ModifierKeys.Control, false) == AppShortcutAction.ReturnToLivePage,
    "Ctrl+Home did not return to the live score page.");

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
        Assert(migratedBindings[nameof(MidiShortcutAction.ToggleLoop)].MidiText == "Unassigned",
            "The assumption-seeded Loop placeholder survived controller-map migration.");
        Assert(migratedBindings[nameof(MidiShortcutAction.StartPractice)].MidiText == "MIDI CC119",
            "A learned controller binding did not display its raw MIDI identity.");
        Assert(migratedBindings[nameof(MidiShortcutAction.StartPerformance)].MidiText == "MIDI CC118",
            "A saved controller binding did not display its raw MIDI identity.");
        Assert(migratedBindings[nameof(MidiShortcutAction.TogglePlayback)].MidiText == "MIDI CC118" &&
               !migratedBindings.ContainsKey(nameof(MidiShortcutAction.Play)),
            "The saved transport binding was not migrated to the unified Play / Pause action.");

        var formatLegacyActivity = typeof(PianoPractice.Desktop.MainWindowViewModel).GetMethod(
            "FormatUserFacingMidiActivity",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var stopActivity = formatLegacyActivity?.Invoke(migrated,
            [new MidiRawEvent(0xB0, 117, 127, DateTimeOffset.UtcNow), false]) as string;
        Assert(stopActivity == "MIDI CC117",
            $"The live badge did not show the raw MIDI input name: {stopActivity}");
        var rawStatusActivity = formatLegacyActivity?.Invoke(migrated,
            [new MidiRawEvent(0xF8, 0, 0, DateTimeOffset.UtcNow), false]) as string;
        Assert(rawStatusActivity == "MIDI status 0xF8 · data 0, 0",
            $"The live badge replaced an unrecognized raw MIDI status: {rawStatusActivity}");
    }

    var smokeProfilePath = Path.Combine(smokeRoot, "profile.json");
    using var viewModel = new PianoPractice.Desktop.MainWindowViewModel(
        smokeProfilePath,
        Path.Combine(smokeRoot, "library"));
    Assert(viewModel.MidiShortcutBindings
               .Where(item => item.ActionId is nameof(MidiShortcutAction.ToggleLoop) or
                    nameof(MidiShortcutAction.SetOverallVolume))
               .All(item => item.MidiText == "Unassigned"),
        "A fresh profile still claims assumption-based controller assignments.");
    Assert(viewModel.MidiShortcutBindings.Any(item => item.ActionId == nameof(MidiShortcutAction.TogglePlayback) && item.Label == "Play / Pause") &&
           viewModel.MidiShortcutBindings.Any(item => item.ActionId == nameof(MidiShortcutAction.Stop) && item.Label == "Stop") &&
           viewModel.MidiShortcutBindings.All(item => item.ActionId is not nameof(MidiShortcutAction.Play) and not nameof(MidiShortcutAction.Pause)),
        "Play / Pause and Stop are not exposed as unified transport bindings.");
    var returnToLiveBinding = viewModel.MidiShortcutBindings.Single(
        item => item.ActionId == nameof(MidiShortcutAction.ReturnToLivePage));
    Assert(returnToLiveBinding.KeyboardText == "Ctrl+Home" && returnToLiveBinding.MidiText == "Unassigned",
        "Return to Live Page is missing its default keyboard binding or unified MIDI cell.");
    viewModel.StartKeyboardShortcutLearning(nameof(AppShortcutAction.ReturnToLivePage));
    Assert(viewModel.TryAssignKeyboardShortcut(Key.L, ModifierKeys.Control) &&
           AppShortcutRouter.Resolve(Key.L, ModifierKeys.Control, false, viewModel.KeyboardShortcutOverrides) == AppShortcutAction.ReturnToLivePage,
        "A configured Return to Live Page keyboard binding was not routed.");
    Assert(!viewModel.IsPlayerVisible, "The app-wide fader fixture unexpectedly opened the score player.");
    var handleControllerMessage = typeof(PianoPractice.Desktop.MainWindowViewModel).GetMethod(
        "HandleMidiControllerMessage",
        BindingFlags.Instance | BindingFlags.NonPublic);
    Assert(handleControllerMessage is not null, "The MIDI controller message route could not be exercised.");

    viewModel.StartMidiShortcutLearning($"Midi:{nameof(MidiShortcutAction.StartListen)}");
    Assert(viewModel.MidiShortcutBindings.Single(item => item.ActionId == nameof(MidiShortcutAction.StartListen)).MidiText == "Listening…",
        "The selected controller binding does not visibly show that it is listening.");
    viewModel.CancelMidiShortcutLearning();

    viewModel.StartMidiShortcutLearning($"Midi:{nameof(MidiShortcutAction.StartListen)}");
    Assert(viewModel.UnbindCurrentMidiShortcutLearning() &&
           viewModel.MidiShortcutBindings.Single(item => item.ActionId == nameof(MidiShortcutAction.StartListen)).MidiText == "Unassigned" &&
           !viewModel.IsMidiShortcutLearning,
        "Backspace-style unbinding did not clear the active learning target and leave learning mode.");

    viewModel.StartMidiControllerAutoMapping();
    handleControllerMessage!.Invoke(viewModel,
        [new MidiRawEvent(0x90, 86, 127, DateTimeOffset.UtcNow), true]);
    handleControllerMessage.Invoke(viewModel,
        [new MidiRawEvent(0x80, 86, 0, DateTimeOffset.UtcNow), true]);
    await Task.Delay(550);
    var learnedLoop = viewModel.MidiShortcutBindings.Single(item => item.ActionId == nameof(MidiShortcutAction.ToggleLoop));
    Assert(learnedLoop.MidiText == "MIDIIN2 Note 86",
        $"Live mapping did not retain the exact Loop input message: {learnedLoop.MidiText}");
    viewModel.CancelMidiShortcutLearning();

    viewModel.StartMidiShortcutLearning($"Midi:{nameof(MidiShortcutAction.StartListen)}");
    handleControllerMessage.Invoke(viewModel,
        [new MidiRawEvent(0x90, 86, 127, DateTimeOffset.UtcNow), true]);
    var reassignedLoop = viewModel.MidiShortcutBindings.Single(item => item.ActionId == nameof(MidiShortcutAction.StartListen));
    Assert(reassignedLoop.MidiText == "MIDIIN2 Note 86",
        $"The reassigned control did not show its raw MIDI input name: {reassignedLoop.MidiText}");

    var formatActivity = typeof(PianoPractice.Desktop.MainWindowViewModel).GetMethod(
        "FormatUserFacingMidiActivity",
        BindingFlags.Instance | BindingFlags.NonPublic);
    var loopActivity = formatActivity?.Invoke(viewModel,
        [new MidiRawEvent(0x90, 86, 127, DateTimeOffset.UtcNow), true]) as string;
    Assert(loopActivity == "MIDIIN2 Note 86",
        $"The live badge did not show the reassigned raw MIDI input name: {loopActivity}");

    viewModel.StartMidiShortcutLearning($"Control:{nameof(MidiShortcutAction.SetOverallVolume)}");
    handleControllerMessage!.Invoke(viewModel,
        [new MidiRawEvent(0xE8, 127, 127, DateTimeOffset.UtcNow), true]);
    handleControllerMessage.Invoke(viewModel,
        [new MidiRawEvent(0xE8, 127, 127, DateTimeOffset.UtcNow), true]);
    Assert(viewModel.OverallVolume == 100,
        "The Oxygen master fader cannot control app volume outside the score player.");
    Assert(viewModel.InputActivityLabel == "MIDIIN2 Pitch Bend · Overall Volume 100%.",
        $"The detailed activity label did not show the raw MIDI input name: {viewModel.InputActivityLabel}");
    var activity = formatActivity?.Invoke(viewModel,
        [new MidiRawEvent(0xE8, 127, 127, DateTimeOffset.UtcNow), true]) as string;
    Assert(activity == "MIDIIN2 Pitch Bend",
        $"The live controller badge did not show the raw MIDI input name: {activity}");
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

    var sharedNoteProfilePath = Path.Combine(smokeRoot, "shared-note-profile.json");
    var sharedNoteProfile = CadenzaUserProfile.CreateDefault();
    sharedNoteProfile.Settings.MidiControllerMappingVersion = 2;
    sharedNoteProfile.Settings.MidiShortcutListenNote = 48;
    sharedNoteProfile.Settings.MidiShortcutPracticeNote = 48;
    sharedNoteProfile.Settings.MidiControllerBindings[nameof(MidiShortcutAction.Play)] =
        "Keyboard|ControlChange|0|118|Absolute|Play";
    new UserProfileStore(sharedNoteProfilePath).Save(sharedNoteProfile);
    using (var sharedNoteViewModel = new PianoPractice.Desktop.MainWindowViewModel(
               sharedNoteProfilePath,
               Path.Combine(smokeRoot, "shared-note-library")))
    {
        sharedNoteViewModel.StartMidiShortcutLearning($"Midi:{nameof(MidiShortcutAction.StartPerformance)}");
        var captureNote = typeof(PianoPractice.Desktop.MainWindowViewModel).GetMethod(
            "TryCaptureMidiShortcutLearning",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(captureNote?.Invoke(sharedNoteViewModel, [48]) is true,
            "Protected-note learning did not accept a shared MIDI key.");
        var noteBindings = sharedNoteViewModel.MidiShortcutBindings.ToDictionary(item => item.ActionId);
        Assert(noteBindings[nameof(MidiShortcutAction.StartListen)].MidiText == "C3" &&
               noteBindings[nameof(MidiShortcutAction.StartPractice)].MidiText == "C3" &&
               noteBindings[nameof(MidiShortcutAction.StartPerformance)].MidiText == "C3",
            "Startup discarded one of two intentionally shared protected-note bindings.");
        Assert(sharedNoteViewModel.MidiRemoteStatusText.Contains("every assignment was kept", StringComparison.Ordinal),
            "Shared protected-note learning did not warn while preserving every assignment.");
    }

    var reloadedSharedProfile = new UserProfileStore(sharedNoteProfilePath).Load();
    Assert(reloadedSharedProfile.Settings.MidiShortcutListenNote == 48 &&
           reloadedSharedProfile.Settings.MidiShortcutPracticeNote == 48 &&
           reloadedSharedProfile.Settings.MidiShortcutPerformanceNote == 48,
        "Shared protected-note bindings did not survive a full save, shutdown, and reload.");
    Assert(reloadedSharedProfile.Settings.MidiControllerBindings.TryGetValue(
               nameof(MidiShortcutAction.TogglePlayback), out var reloadedPlayBinding) &&
           reloadedPlayBinding == "Keyboard|ControlChange|0|118|Absolute|Play",
        "A direct controller binding did not survive a full save, shutdown, and reload.");

    var shutdownProfilePath = Path.Combine(smokeRoot, "shutdown-profile.json");
    var shutdownViewModel = new PianoPractice.Desktop.MainWindowViewModel(
        shutdownProfilePath,
        Path.Combine(smokeRoot, "shutdown-library"));
    shutdownViewModel.StartMidiShortcutLearning($"Control:{nameof(MidiShortcutAction.SetOverallVolume)}");
    handleControllerMessage.Invoke(shutdownViewModel,
        [new MidiRawEvent(0xE8, 127, 127, DateTimeOffset.UtcNow), true]);
    handleControllerMessage.Invoke(shutdownViewModel,
        [new MidiRawEvent(0xE8, 127, 127, DateTimeOffset.UtcNow), true]);
    shutdownViewModel.Dispose();
    Assert(new UserProfileStore(shutdownProfilePath).Load().Settings.OverallVolume == 100,
        "Closing inside the delayed controller-save window lost the last fader change.");
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
