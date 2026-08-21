namespace PianoPractice.Desktop.Models;

/// <summary>
/// Identifies the independent sight-reading drills available from the dashboard.
/// </summary>
public enum SightReadingTestKind
{
    GuidedNotes,
    NoteRecognition,
    IntervalReading,
    LookAheadSequences,
    Accidentals,
    KeySignatures,
    LedgerLines,
    MixedChallenge
}

/// <summary>
/// A generated, previously unseen notation prompt and its required MIDI response order.
/// </summary>
public sealed record SightReadingPrompt(
    string Title,
    string Instruction,
    byte[] MusicXml,
    IReadOnlyList<int> MidiNotes,
    IReadOnlyList<double> Beats,
    bool ShowsNoteLabels,
    int StaffNumber);

/// <summary>
/// Requests renderer feedback for one response in the current sight-reading prompt.
/// </summary>
public sealed record SightReadingFeedbackEvent(
    string Kind,
    double Beat,
    int MidiNoteNumber,
    long SessionGeneration,
    long EventId,
    int StaffNumber);
