namespace PianoPractice.Desktop.Models;

public static class MidiNoteFormatter
{
    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    private static readonly string[] FlatNames = ["C", "D♭", "D", "E♭", "E", "F", "G♭", "G", "A♭", "A", "B♭", "B"];

    public static string Format(int midiNote, bool preferFlats = false)
    {
        var clamped = Math.Clamp(midiNote, 0, 127);
        var names = preferFlats ? FlatNames : Names;
        return $"{names[clamped % 12]}{clamped / 12 - 1}";
    }
}
