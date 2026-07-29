namespace PianoPractice.Desktop.Models;

public sealed record MidiReference(
    string SourcePath,
    int Format,
    int TicksPerQuarter,
    double TempoBpm,
    IReadOnlyList<MidiTrackReference> Tracks,
    IReadOnlyList<MidiReferenceNote> Notes)
{
    public double TotalBeats => Notes.Count == 0 ? 0 : Notes.Max(note => note.OnsetBeats + note.DurationBeats);
}

public sealed record MidiTrackReference(int Index, string Name, int NoteCount, bool IsPercussion);

public sealed record MidiReferenceNote(
    int TrackIndex,
    int Channel,
    int NoteNumber,
    int Velocity,
    double OnsetBeats,
    double DurationBeats);
