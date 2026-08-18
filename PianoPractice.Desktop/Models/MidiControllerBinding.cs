namespace PianoPractice.Desktop.Models;

public enum MidiControllerMessageKind
{
    Note,
    ControlChange,
    PitchBend
}

/// <summary>
/// Identifies one physical MIDI control. Control-surface bindings are received
/// from the controller's dedicated DAW endpoint and never enter piano scoring.
/// </summary>
public sealed record MidiControllerBinding(
    bool ControlSurface,
    MidiControllerMessageKind Kind,
    int Channel,
    int Number)
{
    public bool Matches(int status, int data1)
    {
        var command = status & 0xF0;
        var channel = status & 0x0F;
        if (channel != Channel) return false;
        return Kind switch
        {
            MidiControllerMessageKind.Note => command is 0x80 or 0x90 && data1 == Number,
            MidiControllerMessageKind.ControlChange => command == 0xB0 && data1 == Number,
            MidiControllerMessageKind.PitchBend => command == 0xE0,
            _ => false
        };
    }

    public string Serialize() => $"{(ControlSurface ? "Surface" : "Keyboard")}|{Kind}|{Channel}|{Number}";

    public string Format() => Kind switch
    {
        MidiControllerMessageKind.Note => $"{(ControlSurface ? "DAW" : "MIDI")} Note {Number}",
        MidiControllerMessageKind.ControlChange => $"{(ControlSurface ? "DAW" : "MIDI")} CC{Number} · ch {Channel + 1}",
        MidiControllerMessageKind.PitchBend => $"{(ControlSurface ? "DAW" : "MIDI")} fader · ch {Channel + 1}",
        _ => "Unassigned"
    };

    public static MidiControllerBinding? Parse(string? value)
    {
        var parts = value?.Split('|');
        if (parts is not { Length: 4 } ||
            !Enum.TryParse<MidiControllerMessageKind>(parts[1], out var kind) ||
            !int.TryParse(parts[2], out var channel) ||
            !int.TryParse(parts[3], out var number) ||
            channel is < 0 or > 15 || number is < 0 or > 127)
        {
            return null;
        }

        return new MidiControllerBinding(
            string.Equals(parts[0], "Surface", StringComparison.OrdinalIgnoreCase),
            kind,
            channel,
            number);
    }

    public static MidiControllerBinding? FromRaw(int status, int data1, bool controlSurface)
    {
        var kind = (status & 0xF0) switch
        {
            0x80 or 0x90 => MidiControllerMessageKind.Note,
            0xB0 => MidiControllerMessageKind.ControlChange,
            0xE0 => MidiControllerMessageKind.PitchBend,
            _ => (MidiControllerMessageKind?)null
        };
        return kind is null
            ? null
            : new MidiControllerBinding(controlSurface, kind.Value, status & 0x0F, kind == MidiControllerMessageKind.PitchBend ? 0 : data1);
    }
}
