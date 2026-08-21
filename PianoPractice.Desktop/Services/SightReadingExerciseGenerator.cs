using System.Security;
using System.Text;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop.Services;

/// <summary>
/// Produces randomized, disposable MusicXML exercises without adding them to the user's library.
/// </summary>
public static class SightReadingExerciseGenerator
{
    private static readonly int[] TrebleNaturalNotes = NaturalNotesBetween(60, 77);
    private static readonly int[] BassNaturalNotes = NaturalNotesBetween(43, 60);
    private static readonly int[] AllNaturalNotes = NaturalNotesBetween(43, 77);
    private static readonly int[] BlackKeyNotes = Enumerable.Range(43, 35)
        .Where(note => note % 12 is 1 or 3 or 6 or 8 or 10)
        .ToArray();
    private static readonly int[] LedgerLineNotes = NaturalNotesBetween(36, 47)
        .Concat(NaturalNotesBetween(74, 84))
        .ToArray();
    private static readonly KeySignature[] KeySignatures =
    [
        new("G major", 1, new Dictionary<string, int> { ["F"] = 1 }),
        new("D major", 2, new Dictionary<string, int> { ["F"] = 1, ["C"] = 1 }),
        new("A major", 3, new Dictionary<string, int> { ["F"] = 1, ["C"] = 1, ["G"] = 1 }),
        new("F major", -1, new Dictionary<string, int> { ["B"] = -1 }),
        new("B-flat major", -2, new Dictionary<string, int> { ["B"] = -1, ["E"] = -1 }),
        new("E-flat major", -3, new Dictionary<string, int> { ["B"] = -1, ["E"] = -1, ["A"] = -1 })
    ];

    public static IReadOnlyList<SightReadingPrompt> CreateSession(SightReadingTestKind kind, int seed)
    {
        var random = new Random(seed);
        return kind switch
        {
            SightReadingTestKind.GuidedNotes => CreateGuidedSession(random),
            SightReadingTestKind.NoteRecognition => CreateRecognitionSession(random),
            SightReadingTestKind.IntervalReading => CreatePatternSession(random, intervalOnly: true),
            SightReadingTestKind.LookAheadSequences => CreatePatternSession(random, intervalOnly: false),
            SightReadingTestKind.Accidentals => CreateAccidentalSession(random),
            SightReadingTestKind.KeySignatures => CreateKeySignatureSession(random),
            SightReadingTestKind.LedgerLines => CreateLedgerLineSession(random),
            SightReadingTestKind.MixedChallenge => CreateMixedChallengeSession(random),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown sight-reading test.")
        };
    }

    private static IReadOnlyList<SightReadingPrompt> CreateGuidedSession(Random random)
    {
        var introducedNotes = AllNaturalNotes.ToArray();
        Shuffle(introducedNotes, random);
        introducedNotes = introducedNotes.Take(8).ToArray();
        var introductions = introducedNotes.Select((note, index) => CreatePrompt(
            $"New note {index + 1} of {introducedNotes.Length}",
            "Read the printed note name, find that key, and connect its staff position to the keyboard.",
            [NaturalPitchFor(note)],
            showLabels: true)).ToList();

        var reviewNotes = introducedNotes.ToArray();
        Shuffle(reviewNotes, random);
        introductions.AddRange(reviewNotes.Select((note, index) => CreatePrompt(
            $"Label-free review {index + 1} of {reviewNotes.Length}",
            "The label is gone. Identify the staff position and play the note.",
            [NaturalPitchFor(note)],
            showLabels: false)));
        return introductions;
    }

    private static IReadOnlyList<SightReadingPrompt> CreateRecognitionSession(Random random)
    {
        var pool = AllNaturalNotes.ToArray();
        Shuffle(pool, random);
        return pool.Take(12).Select((note, index) => CreatePrompt(
            $"Quick note {index + 1} of 12",
            "Name it silently from the notation, then play the matching key.",
            [NaturalPitchFor(note)],
            showLabels: false)).ToList();
    }

    private static IReadOnlyList<SightReadingPrompt> CreatePatternSession(Random random, bool intervalOnly)
    {
        int[][] shapes = intervalOnly
            ? [[0, 1, 2], [0, 2, 1], [0, -1, -2], [0, 2, 4], [0, 3, 1], [0, -2, 0]]
            : [[0, 1, 2, 3], [0, 2, 1, 3], [0, 0, 1, 2], [0, 2, 4, 2], [0, -1, 1, 0], [0, 3, 2, 4]];
        var prompts = new List<SightReadingPrompt>();
        for (var index = 0; index < 8; index++)
        {
            var scale = index % 3 == 2 ? BassNaturalNotes : TrebleNaturalNotes;
            var shape = shapes[random.Next(shapes.Length)];
            var minimumOffset = shape.Min();
            var maximumOffset = shape.Max();
            var baseIndex = random.Next(-minimumOffset, scale.Length - maximumOffset);
            var notes = shape.Select(offset => NaturalPitchFor(scale[baseIndex + offset])).ToArray();
            prompts.Add(CreatePrompt(
                intervalOnly ? $"Interval pattern {index + 1} of 8" : $"Look-ahead phrase {index + 1} of 8",
                intervalOnly
                    ? "Keep the first note as your landmark and read the distances between the remaining notes."
                    : "Scan the whole pattern first, then keep your eyes moving ahead as you play it in order.",
                notes,
                showLabels: false));
        }
        return prompts;
    }

    private static IReadOnlyList<SightReadingPrompt> CreateAccidentalSession(Random random)
    {
        var blackKeys = BlackKeyNotes.ToArray();
        var naturalKeys = AllNaturalNotes.ToArray();
        Shuffle(blackKeys, random);
        Shuffle(naturalKeys, random);
        var notes = new List<NotatedPitch>();
        for (var index = 0; index < 8; index++)
            notes.Add(SpellChromatic(blackKeys[index], preferFlats: index % 2 == 1));
        notes.AddRange(naturalKeys.Take(4).Select(note => NaturalPitchFor(note) with { Accidental = "natural" }));
        Shuffle(notes, random);
        return notes.Select((note, index) => CreatePrompt(
            $"Accidental {index + 1} of {notes.Count}",
            "Read the accidental sign before choosing the key. Sharps raise, flats lower, and naturals cancel an alteration.",
            [note],
            showLabels: false)).ToList();
    }

    private static IReadOnlyList<SightReadingPrompt> CreateKeySignatureSession(Random random)
    {
        var keys = KeySignatures.ToArray();
        Shuffle(keys, random);
        var prompts = new List<SightReadingPrompt>();
        for (var index = 0; index < 8; index++)
        {
            var key = keys[index % keys.Length];
            var scale = index % 3 == 2 ? BassNaturalNotes : TrebleNaturalNotes;
            var startIndex = random.Next(0, scale.Length - 4);
            var notes = scale.Skip(startIndex).Take(4)
                .Select(note => ApplyKeySignature(NaturalPitchFor(note), key))
                .ToArray();
            prompts.Add(CreatePrompt(
                $"{key.Name} · phrase {index + 1} of 8",
                "Apply the key signature to every matching staff position while you read the phrase from left to right.",
                notes,
                showLabels: false,
                key.Fifths));
        }
        return prompts;
    }

    private static IReadOnlyList<SightReadingPrompt> CreateLedgerLineSession(Random random)
    {
        var notes = LedgerLineNotes.ToArray();
        Shuffle(notes, random);
        return notes.Take(12).Select((note, index) => CreatePrompt(
            $"Ledger-line note {index + 1} of 12",
            "Count outward from the nearest staff line, then play the note without relying on a label.",
            [NaturalPitchFor(note)],
            showLabels: false)).ToList();
    }

    private static IReadOnlyList<SightReadingPrompt> CreateMixedChallengeSession(Random random)
    {
        var prompts = new List<SightReadingPrompt>();
        prompts.AddRange(CreateRecognitionSession(random).Take(3));
        prompts.AddRange(CreatePatternSession(random, intervalOnly: true).Take(2));
        prompts.AddRange(CreatePatternSession(random, intervalOnly: false).Take(2));
        prompts.AddRange(CreateAccidentalSession(random).Take(3));
        prompts.AddRange(CreateKeySignatureSession(random).Take(2));
        Shuffle(prompts, random);
        return prompts;
    }

    private static SightReadingPrompt CreatePrompt(
        string title,
        string instruction,
        IReadOnlyList<NotatedPitch> notes,
        bool showLabels,
        int keyFifths = 0)
    {
        var staffNumber = notes.Average(note => note.MidiNote) < 60 ? 2 : 1;
        var beats = Enumerable.Range(0, notes.Count).Select(index => (double)index).ToArray();
        return new SightReadingPrompt(
            title,
            instruction,
            Encoding.UTF8.GetBytes(BuildMusicXml(title, notes, showLabels, staffNumber, keyFifths)),
            notes.Select(note => note.MidiNote).ToArray(),
            beats,
            showLabels,
            staffNumber);
    }

    private static string BuildMusicXml(
        string title,
        IReadOnlyList<NotatedPitch> pitches,
        bool showLabels,
        int staffNumber,
        int keyFifths)
    {
        var notes = new StringBuilder();
        for (var index = 0; index < pitches.Count; index++)
        {
            var pitch = pitches[index];
            var duration = pitches.Count == 1 ? 4 : 1;
            var type = pitches.Count == 1 ? "whole" : "quarter";
            var alter = pitch.Alter == 0 ? string.Empty : $"<alter>{pitch.Alter}</alter>";
            var accidental = pitch.Accidental is null ? string.Empty : $"<accidental>{pitch.Accidental}</accidental>";
            var lyric = showLabels
                ? $"<lyric relative-x=\"45\" placement=\"below\"><syllabic>single</syllabic><text>{pitch.Step}{pitch.Octave}</text></lyric>"
                : string.Empty;
            notes.Append($"<note id=\"sr-note-{index + 1}\"><pitch><step>{pitch.Step}</step>{alter}<octave>{pitch.Octave}</octave></pitch><duration>{duration}</duration><voice>1</voice><type>{type}</type>{accidental}{lyric}</note>");
        }

        var escapedTitle = SecurityElement.Escape(title) ?? "Sight Reading";
        var clefSign = staffNumber == 2 ? "F" : "G";
        var clefLine = staffNumber == 2 ? 4 : 2;
        return $"""
               <?xml version="1.0" encoding="UTF-8"?>
               <score-partwise version="4.0">
                 <work><work-title>{escapedTitle}</work-title></work>
                 <identification><creator type="composer">Cadenza Sight Reading</creator></identification>
                 <defaults>
                   <scaling><millimeters>7</millimeters><tenths>40</tenths></scaling>
                   <page-layout><page-height>900</page-height><page-width>1500</page-width></page-layout>
                 </defaults>
                 <part-list><score-part id="P1"><part-name>Piano</part-name></score-part></part-list>
                 <part id="P1">
                   <measure number="1">
                     <attributes>
                       <divisions>1</divisions>
                       <key><fifths>{keyFifths}</fifths></key>
                       <time><beats>4</beats><beat-type>4</beat-type></time>
                       <clef><sign>{clefSign}</sign><line>{clefLine}</line></clef>
                     </attributes>
                     {notes}
                   </measure>
                 </part>
               </score-partwise>
               """;
    }

    private static NotatedPitch NaturalPitchFor(int midiNote)
    {
        var step = (midiNote % 12) switch
        {
            0 => "C",
            2 => "D",
            4 => "E",
            5 => "F",
            7 => "G",
            9 => "A",
            11 => "B",
            _ => throw new InvalidOperationException("Expected a natural-key MIDI note.")
        };
        return new NotatedPitch(midiNote, step, 0, midiNote / 12 - 1, null);
    }

    private static NotatedPitch SpellChromatic(int midiNote, bool preferFlats)
    {
        var octave = midiNote / 12 - 1;
        return (midiNote % 12, preferFlats) switch
        {
            (1, false) => new(midiNote, "C", 1, octave, "sharp"),
            (1, true) => new(midiNote, "D", -1, octave, "flat"),
            (3, false) => new(midiNote, "D", 1, octave, "sharp"),
            (3, true) => new(midiNote, "E", -1, octave, "flat"),
            (6, false) => new(midiNote, "F", 1, octave, "sharp"),
            (6, true) => new(midiNote, "G", -1, octave, "flat"),
            (8, false) => new(midiNote, "G", 1, octave, "sharp"),
            (8, true) => new(midiNote, "A", -1, octave, "flat"),
            (10, false) => new(midiNote, "A", 1, octave, "sharp"),
            (10, true) => new(midiNote, "B", -1, octave, "flat"),
            _ => throw new InvalidOperationException("Expected a black-key MIDI note.")
        };
    }

    private static NotatedPitch ApplyKeySignature(NotatedPitch naturalPitch, KeySignature key)
    {
        var alter = key.AlteredSteps.GetValueOrDefault(naturalPitch.Step);
        return naturalPitch with { MidiNote = naturalPitch.MidiNote + alter, Alter = alter };
    }

    private static int[] NaturalNotesBetween(int minimum, int maximum) => Enumerable.Range(minimum, maximum - minimum + 1)
        .Where(note => note % 12 is 0 or 2 or 4 or 5 or 7 or 9 or 11)
        .ToArray();

    private static void Shuffle<T>(IList<T> items, Random random)
    {
        for (var index = items.Count - 1; index > 0; index--)
        {
            var swapIndex = random.Next(index + 1);
            (items[index], items[swapIndex]) = (items[swapIndex], items[index]);
        }
    }

    private sealed record NotatedPitch(int MidiNote, string Step, int Alter, int Octave, string? Accidental);
    private sealed record KeySignature(string Name, int Fifths, IReadOnlyDictionary<string, int> AlteredSteps);
}
