using System.Windows.Input;

namespace PianoPractice.Desktop.Models;

public enum AppShortcutAction
{
    None,
    SelectListen,
    SelectPractice,
    SelectPerformance,
    TogglePlayback,
    Stop,
    Restart,
    PreviousMeasure,
    NextMeasure,
    PreviousPage,
    NextPage,
    DismissResults,
    RepeatResults
}

public static class AppShortcutRouter
{
    public static AppShortcutAction Resolve(Key key, ModifierKeys modifiers, bool resultsVisible)
    {
        if (resultsVisible)
        {
            if (modifiers != ModifierKeys.None) return AppShortcutAction.None;

            return key switch
            {
                Key.Escape => AppShortcutAction.DismissResults,
                Key.Enter => AppShortcutAction.RepeatResults,
                _ => AppShortcutAction.None
            };
        }

        if (modifiers == ModifierKeys.Control)
        {
            return key switch
            {
                Key.D1 or Key.NumPad1 => AppShortcutAction.SelectListen,
                Key.D2 or Key.NumPad2 => AppShortcutAction.SelectPractice,
                Key.D3 or Key.NumPad3 => AppShortcutAction.SelectPerformance,
                Key.R => AppShortcutAction.Restart,
                Key.Left => AppShortcutAction.PreviousMeasure,
                Key.Right => AppShortcutAction.NextMeasure,
                Key.PageUp => AppShortcutAction.PreviousPage,
                Key.PageDown => AppShortcutAction.NextPage,
                _ => AppShortcutAction.None
            };
        }

        if (modifiers != ModifierKeys.None) return AppShortcutAction.None;

        return key switch
        {
            Key.Space => AppShortcutAction.TogglePlayback,
            Key.Escape => AppShortcutAction.Stop,
            _ => AppShortcutAction.None
        };
    }

    public static bool AllowsComputerPianoInput(ModifierKeys modifiers) =>
        modifiers == ModifierKeys.None;
}
