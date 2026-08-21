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
    ReturnToLivePage,
    ToggleLoop,
    DismissResults,
    RepeatResults
}

public static class AppShortcutRouter
{
    public static readonly IReadOnlyDictionary<AppShortcutAction, string> DefaultBindings =
        new Dictionary<AppShortcutAction, string>
        {
            [AppShortcutAction.SelectListen] = "Control+D1",
            [AppShortcutAction.SelectPractice] = "Control+D2",
            [AppShortcutAction.SelectPerformance] = "Control+D3",
            [AppShortcutAction.TogglePlayback] = "Space",
            [AppShortcutAction.Stop] = "Escape",
            [AppShortcutAction.Restart] = "Control+R",
            [AppShortcutAction.PreviousMeasure] = "Control+Left",
            [AppShortcutAction.NextMeasure] = "Control+Right",
            [AppShortcutAction.PreviousPage] = "Control+PageUp",
            [AppShortcutAction.NextPage] = "Control+PageDown",
            [AppShortcutAction.ReturnToLivePage] = "Control+Home",
            [AppShortcutAction.ToggleLoop] = string.Empty,
            [AppShortcutAction.DismissResults] = "Escape",
            [AppShortcutAction.RepeatResults] = "Enter"
        };

    public static AppShortcutAction Resolve(Key key, ModifierKeys modifiers, bool resultsVisible)
    {
        return Resolve(key, modifiers, resultsVisible, null);
    }

    public static AppShortcutAction Resolve(
        Key key,
        ModifierKeys modifiers,
        bool resultsVisible,
        IReadOnlyDictionary<string, string>? overrides)
    {
        foreach (var pair in DefaultBindings)
        {
            var resultsAction = pair.Key is AppShortcutAction.DismissResults or AppShortcutAction.RepeatResults;
            if (resultsAction != resultsVisible) continue;
            var serialized = overrides is not null && overrides.TryGetValue(pair.Key.ToString(), out var saved)
                ? saved
                : pair.Value;
            if (TryParseGesture(serialized, out var gestureKey, out var gestureModifiers) &&
                KeysEquivalent(gestureKey, key) && gestureModifiers == modifiers)
                return pair.Key;
        }
        return AppShortcutAction.None;
    }

    public static string FormatGesture(string? serialized)
    {
        if (!TryParseGesture(serialized, out var key, out var modifiers)) return "Unassigned";
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key switch
        {
            Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3", Key.D4 => "4",
            Key.D5 => "5", Key.D6 => "6", Key.D7 => "7", Key.D8 => "8", Key.D9 => "9",
            Key.Escape => "Esc",
            _ => key.ToString()
        });
        return string.Join("+", parts);
    }

    public static string SerializeGesture(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Control");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Windows");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    public static bool TryParseGesture(string? serialized, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;
        if (string.IsNullOrWhiteSpace(serialized)) return false;
        var parts = serialized.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !Enum.TryParse(parts[^1], true, out key) || key == Key.None) return false;
        foreach (var part in parts[..^1])
        {
            modifiers |= part.ToLowerInvariant() switch
            {
                "control" or "ctrl" => ModifierKeys.Control,
                "alt" => ModifierKeys.Alt,
                "shift" => ModifierKeys.Shift,
                "windows" or "win" => ModifierKeys.Windows,
                _ => ModifierKeys.None
            };
        }
        return true;
    }

    public static bool AllowsComputerPianoInput(ModifierKeys modifiers) =>
        modifiers == ModifierKeys.None;

    private static bool KeysEquivalent(Key configured, Key pressed) =>
        configured == pressed || configured switch
        {
            Key.D0 => pressed == Key.NumPad0,
            Key.D1 => pressed == Key.NumPad1,
            Key.D2 => pressed == Key.NumPad2,
            Key.D3 => pressed == Key.NumPad3,
            Key.D4 => pressed == Key.NumPad4,
            Key.D5 => pressed == Key.NumPad5,
            Key.D6 => pressed == Key.NumPad6,
            Key.D7 => pressed == Key.NumPad7,
            Key.D8 => pressed == Key.NumPad8,
            Key.D9 => pressed == Key.NumPad9,
            _ => false
        };
}
