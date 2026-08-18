using System.Windows.Input;
using PianoPractice.Desktop.Models;

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

var unavailableArm = midiRouter.ProcessNoteOn(84, 84, midiBindings, MidiShortcutContext.Unavailable, now);
Assert(unavailableArm.Consumed && unavailableArm.Signal == MidiShortcutSignal.Blocked,
    "The Remote key can arm while the app is busy or a dialog is open.");
var unavailableArmRelease = midiRouter.ProcessNoteOff(84, 84, now.AddMilliseconds(100));
Assert(unavailableArmRelease.Consumed && !midiRouter.IsArmed(now.AddMilliseconds(100)),
    "A blocked Remote-key press armed on release.");

var ordinaryC4 = midiRouter.ProcessNoteOn(60, 84, midiBindings, MidiShortcutContext.Running, now);
Assert(!ordinaryC4.Consumed && ordinaryC4.Action == MidiShortcutAction.None,
    "An unarmed C4 can restart a running session.");
midiRouter.ProcessNoteOff(60, 84, now.AddMilliseconds(80));

var armDown = midiRouter.ProcessNoteOn(84, 84, midiBindings, MidiShortcutContext.Running, now.AddSeconds(1));
Assert(armDown.Consumed && armDown.Signal == MidiShortcutSignal.ModifierPressed,
    "The Remote key was not reserved on a fresh press.");
var armed = midiRouter.ProcessNoteOff(84, 84, now.AddSeconds(1.2));
Assert(armed.Consumed && armed.Signal == MidiShortcutSignal.Armed,
    "A clean Remote-key tap did not arm shortcuts.");
var protectedRestart = midiRouter.ProcessNoteOn(60, 84, midiBindings, MidiShortcutContext.Running, now.AddSeconds(1.3));
Assert(protectedRestart.Consumed && protectedRestart.Action == MidiShortcutAction.Restart,
    "Armed C4 did not resolve to Restart while running.");
var repeatedHeldC4 = midiRouter.ProcessNoteOn(60, 84, midiBindings, MidiShortcutContext.Running, now.AddSeconds(1.4));
Assert(repeatedHeldC4.Consumed && repeatedHeldC4.Action == MidiShortcutAction.None,
    "A repeated note-on while C4 remained held retriggered Restart.");
Assert(midiRouter.ProcessNoteOff(60, 84, now.AddSeconds(1.5)).Consumed,
    "The executed command key leaked into musical note-off handling.");

midiRouter.Reset();
midiRouter.ProcessNoteOn(84, 84, midiBindings, MidiShortcutContext.Running, now.AddSeconds(2));
var heldArm = midiRouter.ProcessNoteOff(84, 84, now.AddSeconds(3));
Assert(heldArm.Signal == MidiShortcutSignal.Cancelled && !midiRouter.IsArmed(now.AddSeconds(3)),
    "Holding the Remote key can arm shortcuts.");

midiRouter.Reset();
midiRouter.ProcessNoteOn(55, 84, midiBindings, MidiShortcutContext.Running, now.AddSeconds(4));
var chordedArm = midiRouter.ProcessNoteOn(84, 84, midiBindings, MidiShortcutContext.Running, now.AddSeconds(4.1));
Assert(chordedArm.Consumed && chordedArm.Signal == MidiShortcutSignal.Blocked,
    "The Remote key armed while another musical note was held.");
midiRouter.ProcessNoteOff(84, 84, now.AddSeconds(4.2));
midiRouter.ProcessNoteOff(55, 84, now.AddSeconds(4.3));

midiRouter.Reset();
midiRouter.ProcessNoteOn(84, 84, midiBindings, MidiShortcutContext.Running, now.AddSeconds(5));
midiRouter.ProcessNoteOff(84, 84, now.AddSeconds(5.1));
var blockedModeSwitch = midiRouter.ProcessNoteOn(50, 84, midiBindings, MidiShortcutContext.Running, now.AddSeconds(5.2));
Assert(blockedModeSwitch.Consumed && blockedModeSwitch.Signal == MidiShortcutSignal.Blocked,
    "A MIDI mode shortcut can interrupt a running session.");
midiRouter.ProcessNoteOff(50, 84, now.AddSeconds(5.3));

midiRouter.Reset();
midiRouter.ProcessNoteOn(84, 84, midiBindings, MidiShortcutContext.Results, now.AddSeconds(6));
midiRouter.ProcessNoteOff(84, 84, now.AddSeconds(6.1));
var repeatResults = midiRouter.ProcessNoteOn(67, 84, midiBindings, MidiShortcutContext.Results, now.AddSeconds(6.2));
Assert(repeatResults.Action == MidiShortcutAction.RepeatResults,
    "The protected Repeat Results binding did not work in its valid context.");

Console.WriteLine("PASS deterministic shortcut safety fixtures");
return;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
