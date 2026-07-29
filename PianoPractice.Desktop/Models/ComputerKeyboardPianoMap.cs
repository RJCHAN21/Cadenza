using System.Windows.Input;

namespace PianoPractice.Desktop.Models;

public static class ComputerKeyboardPianoMap
{
    public static IReadOnlyDictionary<Key, int> MidiNotes { get; } = new Dictionary<Key, int>
    {
        [Key.A] = 60,
        [Key.W] = 61,
        [Key.S] = 62,
        [Key.E] = 63,
        [Key.D] = 64,
        [Key.F] = 65,
        [Key.T] = 66,
        [Key.G] = 67,
        [Key.Y] = 68,
        [Key.H] = 69,
        [Key.U] = 70,
        [Key.J] = 71,
        [Key.K] = 72,
        [Key.O] = 73,
        [Key.L] = 74,
        [Key.P] = 75,
        [Key.OemSemicolon] = 76
    };
}
