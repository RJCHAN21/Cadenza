using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop.Services;

public sealed class MusicXmlImporter
{
    public ScoreDocument Import(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A MusicXML file path is required.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The MusicXML file could not be found.", path);
        }

        using var stream = File.OpenRead(path);
        XDocument document;
        var sourceContainer = "MusicXML XML file";
        if (IsZipArchive(stream))
        {
            sourceContainer = "MXL archive";
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var scoreEntry = ResolveScoreEntry(archive);
            using var scoreStream = scoreEntry.Open();
            document = XDocument.Load(scoreStream, LoadOptions.None);
        }
        else
        {
            stream.Position = 0;
            document = XDocument.Load(stream, LoadOptions.None);
        }

        return ParseDocument(document, path, sourceContainer);
    }

    private static bool IsZipArchive(Stream stream)
    {
        Span<byte> signature = stackalloc byte[2];
        var bytesRead = stream.Read(signature);
        stream.Position = 0;
        return bytesRead == 2 && signature[0] == (byte)'P' && signature[1] == (byte)'K';
    }

    private static ZipArchiveEntry ResolveScoreEntry(ZipArchive archive)
    {
        var container = archive.Entries.FirstOrDefault(entry =>
            string.Equals(entry.FullName, "META-INF/container.xml", StringComparison.OrdinalIgnoreCase));

        if (container is not null)
        {
            using var containerStream = container.Open();
            var containerDocument = XDocument.Load(containerStream);
            var rootFile = Descendants(containerDocument.Root, "rootfile").FirstOrDefault();
            var fullPath = rootFile?.Attribute("full-path")?.Value;
            if (!string.IsNullOrWhiteSpace(fullPath))
            {
                var resolved = archive.Entries.FirstOrDefault(entry =>
                    string.Equals(entry.FullName, fullPath, StringComparison.OrdinalIgnoreCase));
                if (resolved is not null)
                {
                    return resolved;
                }
            }
        }

        var fallback = archive.Entries.FirstOrDefault(entry =>
            entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
            !entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase));

        return fallback ?? throw new InvalidDataException("The MXL archive does not contain a score XML file.");
    }

    private static ScoreDocument ParseDocument(XDocument document, string sourcePath, string sourceContainer)
    {
        var root = document.Root ?? throw new InvalidDataException("The MusicXML document has no root element.");
        if (!string.Equals(root.Name.LocalName, "score-partwise", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"This prototype currently imports score-partwise MusicXML, not {root.Name.LocalName}.");
        }

        var title = Value(Descendant(root, "work-title")) ?? Value(Descendant(root, "movement-title")) ?? Path.GetFileNameWithoutExtension(sourcePath);
        var creators = Descendants(root, "creator")
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var partDefinitions = Descendants(root, "score-part")
            .ToDictionary(
                element => (string?)element.Attribute("id") ?? string.Empty,
                element => Value(Descendant(element, "part-name")) ?? "Unnamed part",
                StringComparer.OrdinalIgnoreCase);

        var parts = new List<ScorePart>();
        var measureSummaries = new List<MeasureSummary>();
        var playableNotes = new List<ScoreNote>();
        var playableRests = new List<ScoreRest>();
        var scoreMarks = new List<ScoreMark>();
        var performanceMeasures = new List<ScoreMeasureOccurrence>();
        var validationWarnings = new List<ScoreValidationWarning>();
        var repeatPairCount = 0;
        var tiePairCount = 0;
        var slurPairCount = 0;
        var totalBeats = 0d;
        foreach (var part in Children(root, "part"))
        {
            var partId = (string?)part.Attribute("id") ?? $"P{parts.Count + 1}";
            var partName = partDefinitions.GetValueOrDefault(partId, "Unnamed part");
            var parsedMeasures = new List<ParsedMeasure>();
            var partNotes = new List<ScoreNote>();
            var partRests = new List<ScoreRest>();
            var partMarks = new List<ScoreMark>();
            var partBeats = 0d;
            var divisions = 1;

            foreach (var measure in Children(part, "measure"))
            {
                var parsed = ParseMeasure(measure, partBeats, divisions);
                divisions = parsed.Divisions;
                parsedMeasures.Add(parsed);
                partNotes.AddRange(parsed.Notes);
                partRests.AddRange(parsed.Rests);
                partMarks.AddRange(parsed.Marks);
                partBeats += parsed.DurationBeats;
            }

            parts.Add(new ScorePart(
                partId,
                partName,
                parsedMeasures.Count,
                parsedMeasures.Sum(measure => measure.Summary.NoteCount),
                parsedMeasures.Sum(measure => measure.Summary.RestCount),
                parsedMeasures.Sum(measure => measure.Summary.LyricCount),
                parsedMeasures.Sum(measure => measure.Summary.StaffOneNoteCount),
                parsedMeasures.Sum(measure => measure.Summary.StaffTwoNoteCount)));

            if (measureSummaries.Count == 0)
            {
                measureSummaries.AddRange(parsedMeasures.Select(measure => measure.Summary));
                ValidateNavigationDirectives(root, validationWarnings, parsedMeasures.Count);
                ValidateSlurs(part, validationWarnings, out slurPairCount);
                var tiedNotes = MergeTiedNotes(partNotes, validationWarnings, out tiePairCount);
                var occurrences = BuildPerformanceOccurrences(parsedMeasures, validationWarnings, out repeatPairCount);
                performanceMeasures.AddRange(occurrences);
                playableNotes.AddRange(ExpandNotes(tiedNotes, occurrences));
                playableRests.AddRange(ExpandRests(partRests, occurrences));
                scoreMarks.AddRange(ExpandMarks(partMarks, occurrences));
                totalBeats = occurrences.Sum(occurrence => occurrence.DurationBeats);
            }
        }

        var firstMeasure = Children(Children(root, "part").FirstOrDefault(), "measure").FirstOrDefault();
        var firstAttributes = Descendant(firstMeasure, "attributes");
        var keyElement = Descendant(firstAttributes, "key");
        var keyFifths = ParseInt(Value(Descendant(keyElement, "fifths"))) ?? 0;
        var keySignature = FormatKeySignature(keyFifths, Value(Descendant(keyElement, "mode")));

        var timeElement = Descendant(firstAttributes, "time");
        var beats = Value(Descendant(timeElement, "beats"));
        var beatType = Value(Descendant(timeElement, "beat-type"));
        var beatsPerMeasure = ParseInt(beats) ?? 4;
        var parsedBeatType = ParseInt(beatType) ?? 4;
        var timeSignature = !string.IsNullOrWhiteSpace(beats) && !string.IsNullOrWhiteSpace(beatType)
            ? $"{beats}/{beatType}"
            : "Not specified";

        var tempoBpm = FirstTempoBpm(root) ?? 120;
        return new ScoreDocument
        {
            SourcePath = sourcePath,
            Title = title,
            ComposerOrCreator = creators.Length > 0 ? string.Join(" / ", creators) : "Unknown creator",
            FormatVersion = root.Attribute("version")?.Value is { Length: > 0 } version ? $"MusicXML {version}" : "MusicXML",
            SourceContainer = sourceContainer,
            KeySignature = keySignature,
            KeyFifths = keyFifths,
            TimeSignature = timeSignature,
            BeatsPerMeasure = beatsPerMeasure,
            BeatType = parsedBeatType,
            Tempo = FirstTempo(root) ?? "Not specified",
            TempoBpm = tempoBpm,
            MeasureCount = measureSummaries.Count,
            TotalNoteCount = parts.Sum(part => part.NoteCount),
            TotalRestCount = parts.Sum(part => part.RestCount),
            TotalLyricCount = parts.Sum(part => part.LyricCount),
            TotalBeats = totalBeats,
            Parts = parts,
            Measures = measureSummaries,
            Notes = playableNotes,
            Rests = playableRests
            ,
            Marks = scoreMarks,
            PerformanceMeasures = performanceMeasures,
            ValidationWarnings = validationWarnings,
            RepeatPairCount = repeatPairCount,
            TiePairCount = tiePairCount,
            SlurPairCount = slurPairCount
        };
    }

    private static ParsedMeasure ParseMeasure(XElement measure, double measureStartBeats, int inheritedDivisions)
    {
        var attributes = Descendant(measure, "attributes");
        var divisions = ParseInt(Value(Descendant(attributes, "divisions"))) ?? inheritedDivisions;
        divisions = divisions > 0 ? divisions : 1;

        var noteCount = 0;
        var restCount = 0;
        var chordCount = 0;
        var lyricCount = 0;
        var staffOne = 0;
        var staffTwo = 0;
        var cursor = 0;
        var maximumCursor = 0;
        var lastNoteStart = 0;
        var playableNotes = new List<ScoreNote>();
        var playableRests = new List<ScoreRest>();
        var marks = new List<ScoreMark>();

        foreach (var element in measure.Elements())
        {
            switch (element.Name.LocalName.ToLowerInvariant())
            {
                case "note":
                {
                    noteCount++;
                    var isChord = Descendant(element, "chord") is not null;
                    if (isChord) chordCount++;

                    var durationDivisions = ParseInt(Value(Descendant(element, "duration"))) ?? 0;
                    var start = isChord ? lastNoteStart : cursor;
                    if (!isChord) lastNoteStart = start;

                    var isRest = Descendant(element, "rest") is not null;
                    if (isRest) restCount++;
                    lyricCount += Descendants(element, "lyric").Count();
                    var noteType = Value(Descendant(element, "type")) ?? InferNoteType(durationDivisions, divisions);
                    var dotCount = Descendants(element, "dot").Count();
                    var voice = Value(Descendant(element, "voice")) ?? "1";

                    var staff = ParseInt(Value(Descendant(element, "staff"))) ?? 0;
                    if (staff == 1) staffOne++;
                    if (staff == 2) staffTwo++;

                    var pitch = ParsePitch(element);
                    var notations = Descendant(element, "notations");
                    var articulations = Descendants(notations, "articulations").SelectMany(parent => parent.Elements()).ToArray();
                    var isStaccato = articulations.Any(el => string.Equals(el.Name.LocalName, "staccato", StringComparison.OrdinalIgnoreCase));
                    var isAccent = articulations.Any(el => string.Equals(el.Name.LocalName, "accent", StringComparison.OrdinalIgnoreCase));
                    var isTenuto = articulations.Any(el => string.Equals(el.Name.LocalName, "tenuto", StringComparison.OrdinalIgnoreCase));
                    var isSlurred = Descendants(notations, "slur").Any();

                    if (!isRest && pitch is not null)
                    {
                        playableNotes.Add(new ScoreNote(
                            pitch.MidiNoteNumber,
                            measureStartBeats + start / (double)divisions,
                            Math.Max(0.05, durationDivisions / (double)divisions),
                            staff,
                            (string?)measure.Attribute("number") ?? "?",
                            pitch.Step,
                            pitch.Octave,
                            pitch.Alter,
                            noteType,
                            dotCount,
                            voice,
                            Value(Descendant(element, "stem")) ?? string.Empty,
                            isChord,
                            Descendants(element, "beam").Select(beam => beam.Value.Trim().ToLowerInvariant()).ToArray(),
                            Descendants(element, "lyric").Select(lyric => Value(Descendant(lyric, "text"))).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)),
                            Descendants(element, "tie").Any(tie => string.Equals((string?)tie.Attribute("type"), "start", StringComparison.OrdinalIgnoreCase)),
                            Descendants(element, "tie").Any(tie => string.Equals((string?)tie.Attribute("type"), "stop", StringComparison.OrdinalIgnoreCase)),
                            0,
                            isStaccato,
                            isAccent,
                            isTenuto,
                            isSlurred));
                        foreach (var articulation in Descendants(Descendant(element, "notations"), "articulations").SelectMany(parent => parent.Elements()))
                        {
                            marks.Add(new ScoreMark(
                                measureStartBeats + start / (double)divisions,
                                staff,
                                (string?)measure.Attribute("number") ?? "?",
                                ScoreMarkKind.Articulation,
                                articulation.Name.LocalName));
                        }
                    }
                    else if (isRest)
                    {
                        playableRests.Add(new ScoreRest(
                            measureStartBeats + start / (double)divisions,
                            Math.Max(0.05, durationDivisions / (double)divisions),
                            staff,
                            (string?)measure.Attribute("number") ?? "?",
                            noteType,
                            dotCount,
                            voice));
                    }

                    if (!isChord)
                    {
                        cursor += durationDivisions;
                        maximumCursor = Math.Max(maximumCursor, cursor);
                    }

                    break;
                }
                case "direction":
                {
                    var staff = ParseInt(Value(Descendant(element, "staff"))) ?? 1;
                    var onset = measureStartBeats + cursor / (double)divisions;
                    var measureNumber = (string?)measure.Attribute("number") ?? "?";
                    foreach (var dynamics in Descendants(element, "dynamics"))
                    {
                        foreach (var dynamic in dynamics.Elements())
                        {
                            marks.Add(new ScoreMark(onset, staff, measureNumber, ScoreMarkKind.Dynamic, dynamic.Name.LocalName));
                        }
                    }

                    foreach (var pedal in Descendants(element, "pedal"))
                    {
                        var type = (string?)pedal.Attribute("type") ?? "start";
                        marks.Add(new ScoreMark(onset, staff, measureNumber, ScoreMarkKind.Pedal, type));
                    }

                    var words = Descendants(element, "words").Select(Value).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                    if (!string.IsNullOrWhiteSpace(words))
                    {
                        marks.Add(new ScoreMark(onset, staff, measureNumber, ScoreMarkKind.Direction, words!));
                    }
                    break;
                }
                case "backup":
                    cursor = Math.Max(0, cursor - (ParseInt(Value(Descendant(element, "duration"))) ?? 0));
                    break;
                case "forward":
                    cursor += ParseInt(Value(Descendant(element, "duration"))) ?? 0;
                    maximumCursor = Math.Max(maximumCursor, cursor);
                    break;
            }
        }

        var number = (string?)measure.Attribute("number") ?? "?";
        var durationBeats = maximumCursor / (double)divisions;
        var repeats = Children(measure, "barline").SelectMany(barline => Children(barline, "repeat")).ToArray();
        var repeatForward = repeats.Any(repeat => string.Equals((string?)repeat.Attribute("direction"), "forward", StringComparison.OrdinalIgnoreCase));
        var backwardRepeat = repeats.FirstOrDefault(repeat => string.Equals((string?)repeat.Attribute("direction"), "backward", StringComparison.OrdinalIgnoreCase));
        var repeatTimes = ParseInt((string?)backwardRepeat?.Attribute("times")) ?? 2;
        var endings = Children(measure, "barline")
            .SelectMany(barline => Children(barline, "ending"))
            .Select(ending => new ParsedEnding(
                (string?)ending.Attribute("number") ?? string.Empty,
                (string?)ending.Attribute("type") ?? string.Empty))
            .ToArray();
        return new ParsedMeasure(
            new MeasureSummary(number, noteCount, restCount, chordCount, lyricCount, staffOne, staffTwo, FirstTempo(measure), measureStartBeats, durationBeats),
            playableNotes,
            playableRests,
            marks,
            divisions,
            durationBeats,
            repeatForward,
            backwardRepeat is not null,
            repeatTimes,
            endings);
    }

    private static IReadOnlyList<ScoreMeasureOccurrence> BuildPerformanceOccurrences(
        IReadOnlyList<ParsedMeasure> measures,
        ICollection<ScoreValidationWarning> warnings,
        out int repeatPairCount)
    {
        var occurrences = new List<ScoreMeasureOccurrence>();
        var repeatVisits = new Dictionary<int, int>();
        var measureVisits = new Dictionary<int, int>();
        var repeatStartIndex = 0;
        repeatPairCount = measures.Count(measure => measure.RepeatBackward);
        var index = 0;
        var performanceBeat = 0d;
        var safety = Math.Max(64, measures.Count * 16);

        while (index >= 0 && index < measures.Count && safety-- > 0)
        {
            var measure = measures[index];
            var pass = measureVisits.GetValueOrDefault(index) + 1;

            var isEndingOne = measure.Endings.Any(e =>
                e.Number.Split(',', ' ').Select(s => s.Trim()).Contains("1"));
            var isEndingTwoOnly = measure.Endings.Any(e => {
                var nums = e.Number.Split(',', ' ').Select(s => s.Trim()).ToArray();
                return nums.Contains("2") && !nums.Contains("1");
            });

            if (pass > 1 && isEndingOne && !isEndingTwoOnly)
            {
                var endingTwoIndex = Enumerable.Range(index + 1, measures.Count - (index + 1))
                    .FirstOrDefault(candidate => measures[candidate].Endings.Any(e =>
                        e.Number.Split(',', ' ').Select(s => s.Trim()).Contains("2")), -1);
                if (endingTwoIndex > index)
                {
                    index = endingTwoIndex;
                    continue;
                }
            }
            else if (pass == 1 && isEndingTwoOnly)
            {
                var postEndingIndex = Enumerable.Range(index + 1, measures.Count - (index + 1))
                    .FirstOrDefault(candidate => !measures[candidate].Endings.Any(e =>
                        e.Number.Split(',', ' ').Select(s => s.Trim()).Contains("2")), measures.Count);
                index = postEndingIndex;
                continue;
            }

            measureVisits[index] = pass;
            occurrences.Add(new ScoreMeasureOccurrence(
                occurrences.Count,
                index,
                measure.Summary.Number,
                measure.Summary.StartBeat,
                performanceBeat,
                measure.DurationBeats,
                pass));
            performanceBeat += measure.DurationBeats;

            if (measure.RepeatForward) repeatStartIndex = index;
            if (measure.RepeatBackward)
            {
                var repetitionsCompleted = repeatVisits.GetValueOrDefault(index);
                var repeatTimes = Math.Clamp(measure.RepeatTimes, 2, 8);
                if (measure.RepeatTimes is < 2 or > 8)
                {
                    warnings.Add(new ScoreValidationWarning(
                        "repeat-times",
                        $"Repeat at measure {measure.Summary.Number} has unsupported times=\"{measure.RepeatTimes}\".",
                        MeasureNumberOf(measures[repeatStartIndex].Summary.Number),
                        MeasureNumberOf(measure.Summary.Number),
                        false));
                }

                if (repetitionsCompleted < repeatTimes - 1)
                {
                    repeatVisits[index] = repetitionsCompleted + 1;
                    index = repeatStartIndex;
                    continue;
                }

                repeatStartIndex = index + 1;
            }

            index++;
        }

        if (safety <= 0)
        {
            warnings.Add(new ScoreValidationWarning(
                "repeat-cycle",
                "Repeat navigation produced a cycle that could not be resolved safely.",
                1,
                measures.Count,
                false));
        }

        return occurrences;
    }

    private static IReadOnlyList<ScoreNote> MergeTiedNotes(
        IReadOnlyList<ScoreNote> notes,
        ICollection<ScoreValidationWarning> warnings,
        out int tiePairCount)
    {
        var merged = new List<ScoreNote>();
        var active = new Dictionary<(int Midi, int Staff, string Voice), int>();
        tiePairCount = 0;
        foreach (var note in notes.OrderBy(note => note.OnsetBeats).ThenBy(note => note.MidiNoteNumber))
        {
            var key = (note.MidiNoteNumber, note.StaffNumber, note.Voice);
            if (note.TieStop && active.TryGetValue(key, out var activeIndex))
            {
                var start = merged[activeIndex];
                var endBeat = Math.Max(
                    start.OnsetBeats + start.DurationBeats,
                    note.OnsetBeats + note.DurationBeats);
                merged[activeIndex] = start with { DurationBeats = endBeat - start.OnsetBeats };
                tiePairCount++;
                if (!note.TieStart) active.Remove(key);
                continue;
            }

            var outputIndex = merged.Count;
            merged.Add(note);
            if (note.TieStart) active[key] = outputIndex;
            if (note.TieStop && !note.TieStart)
            {
                var measure = MeasureNumberOf(note.MeasureNumber);
                warnings.Add(new ScoreValidationWarning(
                    "unmatched-tie-stop",
                    $"An unmatched tie stop was found at measure {note.MeasureNumber}. Assessment is disabled for that measure.",
                    measure,
                    measure,
                    true));
            }
        }

        foreach (var activeIndex in active.Values.Distinct())
        {
            var note = merged[activeIndex];
            var measure = MeasureNumberOf(note.MeasureNumber);
            warnings.Add(new ScoreValidationWarning(
                "unmatched-tie-start",
                $"An unmatched tie start was found at measure {note.MeasureNumber}. Assessment is disabled for that measure.",
                measure,
                measure,
                true));
        }

        return merged;
    }

    private static IReadOnlyList<ScoreNote> ExpandNotes(
        IReadOnlyList<ScoreNote> notes,
        IReadOnlyList<ScoreMeasureOccurrence> occurrences)
    {
        var byMeasure = notes.GroupBy(note => note.MeasureNumber).ToDictionary(group => group.Key, group => group.ToArray());
        return occurrences.SelectMany(occurrence =>
                byMeasure.GetValueOrDefault(occurrence.MeasureNumber, [])
                    .Select(note => note with
                    {
                        OnsetBeats = occurrence.PerformanceStartBeat + note.OnsetBeats - occurrence.SourceStartBeat,
                        PerformanceOccurrence = occurrence.OccurrenceIndex
                    }))
            .OrderBy(note => note.OnsetBeats)
            .ThenBy(note => note.MidiNoteNumber)
            .ToArray();
    }

    private static IReadOnlyList<ScoreRest> ExpandRests(
        IReadOnlyList<ScoreRest> rests,
        IReadOnlyList<ScoreMeasureOccurrence> occurrences)
    {
        var byMeasure = rests.GroupBy(rest => rest.MeasureNumber).ToDictionary(group => group.Key, group => group.ToArray());
        return occurrences.SelectMany(occurrence =>
                byMeasure.GetValueOrDefault(occurrence.MeasureNumber, [])
                    .Select(rest => rest with
                    {
                        OnsetBeats = occurrence.PerformanceStartBeat + rest.OnsetBeats - occurrence.SourceStartBeat
                    }))
            .OrderBy(rest => rest.OnsetBeats)
            .ToArray();
    }

    private static IReadOnlyList<ScoreMark> ExpandMarks(
        IReadOnlyList<ScoreMark> marks,
        IReadOnlyList<ScoreMeasureOccurrence> occurrences)
    {
        var byMeasure = marks.GroupBy(mark => mark.MeasureNumber).ToDictionary(group => group.Key, group => group.ToArray());
        return occurrences.SelectMany(occurrence =>
                byMeasure.GetValueOrDefault(occurrence.MeasureNumber, [])
                    .Select(mark => mark with
                    {
                        OnsetBeats = occurrence.PerformanceStartBeat + mark.OnsetBeats - occurrence.SourceStartBeat
                    }))
            .OrderBy(mark => mark.OnsetBeats)
            .ToArray();
    }

    private static void ValidateNavigationDirectives(
        XElement root,
        ICollection<ScoreValidationWarning> warnings,
        int measureCount)
    {
        var navigationAttributes = new[] { "dacapo", "dalsegno", "segno", "coda", "tocoda", "fine" };
        var unsupported = Descendants(root, "sound")
            .SelectMany(sound => navigationAttributes.Where(attribute => sound.Attribute(attribute) is not null))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unsupported.Length == 0) return;
        warnings.Add(new ScoreValidationWarning(
            "navigation-directive",
            $"Unsupported score navigation was found ({string.Join(", ", unsupported)}). Assessed lessons are disabled until reviewed.",
            1,
            measureCount,
            true));
    }

    private static void ValidateSlurs(
        XElement part,
        ICollection<ScoreValidationWarning> warnings,
        out int slurPairCount)
    {
        var active = new Dictionary<string, int>();
        slurPairCount = 0;
        foreach (var note in Descendants(part, "note"))
        {
            var measureNumber = MeasureNumberOf((string?)note.Parent?.Attribute("number") ?? "1");
            foreach (var slur in Descendants(Descendant(note, "notations"), "slur"))
            {
                var number = (string?)slur.Attribute("number") ?? "1";
                var type = (string?)slur.Attribute("type") ?? string.Empty;
                if (string.Equals(type, "start", StringComparison.OrdinalIgnoreCase))
                    active[number] = measureNumber;
                else if (string.Equals(type, "stop", StringComparison.OrdinalIgnoreCase) && active.Remove(number))
                    slurPairCount++;
                else if (string.Equals(type, "stop", StringComparison.OrdinalIgnoreCase))
                    warnings.Add(new ScoreValidationWarning(
                        "unmatched-slur-stop",
                        $"An unmatched slur stop was found at measure {measureNumber}. Assessment is disabled for that measure.",
                        measureNumber,
                        measureNumber,
                        true));
            }
        }

        foreach (var measureNumber in active.Values)
        {
            warnings.Add(new ScoreValidationWarning(
                "unmatched-slur-start",
                $"An unmatched slur start was found at measure {measureNumber}. Assessment is disabled for that phrase.",
                measureNumber,
                measureNumber,
                true));
        }
    }

    private static int MeasureNumberOf(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 1;

    private static ParsedPitch? ParsePitch(XElement note)
    {
        var pitch = Descendant(note, "pitch");
        var step = Value(Descendant(pitch, "step"));
        var octave = ParseInt(Value(Descendant(pitch, "octave")));
        if (step is null || octave is null) return null;

        var pitchClass = step.ToUpperInvariant() switch
        {
            "C" => 0,
            "D" => 2,
            "E" => 4,
            "F" => 5,
            "G" => 7,
            "A" => 9,
            "B" => 11,
            _ => -1
        };
        if (pitchClass < 0) return null;

        var alter = double.TryParse(Value(Descendant(pitch, "alter")), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedAlter)
            ? (int)Math.Round(parsedAlter)
            : 0;
        return new ParsedPitch(
            Math.Clamp((octave.Value + 1) * 12 + pitchClass + alter, 0, 127),
            step.ToUpperInvariant(),
            octave.Value,
            alter);
    }

    private static string InferNoteType(int durationDivisions, int divisions)
    {
        var beats = durationDivisions / (double)Math.Max(1, divisions);
        if (beats >= 4) return "whole";
        if (beats >= 2) return "half";
        if (beats >= 1) return "quarter";
        if (beats >= 0.5) return "eighth";
        if (beats >= 0.25) return "16th";
        return "32nd";
    }

    private static string? FirstTempo(XElement? root)
    {
        if (root is null) return null;
        var soundTempo = Descendants(root, "sound")
            .Select(element => (string?)element.Attribute("tempo"))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (!string.IsNullOrWhiteSpace(soundTempo)) return $"{soundTempo} BPM";

        var metronomeTempo = Descendants(root, "metronome")
            .Select(element => Value(Descendant(element, "per-minute")))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return string.IsNullOrWhiteSpace(metronomeTempo) ? null : $"{metronomeTempo} BPM";
    }

    private static double? FirstTempoBpm(XElement? root)
    {
        if (root is null) return null;
        var soundTempo = Descendants(root, "sound")
            .Select(element => (string?)element.Attribute("tempo"))
            .FirstOrDefault(value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        if (double.TryParse(soundTempo, NumberStyles.Float, CultureInfo.InvariantCulture, out var tempo)) return tempo;

        var metronomeTempo = Descendants(root, "metronome")
            .Select(element => Value(Descendant(element, "per-minute")))
            .FirstOrDefault(value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        return double.TryParse(metronomeTempo, NumberStyles.Float, CultureInfo.InvariantCulture, out tempo) ? tempo : null;
    }

    private static string FormatKeySignature(int? fifths, string? mode)
    {
        if (fifths is null) return "Not specified";
        var majorKeys = new[] { "Cb", "Gb", "Db", "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#" };
        var index = Math.Clamp(fifths.Value + 7, 0, majorKeys.Length - 1);
        var majorKey = majorKeys[index];
        return string.Equals(mode, "minor", StringComparison.OrdinalIgnoreCase) ? $"{majorKey} minor" : $"{majorKey} major";
    }

    private static int? ParseInt(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static string? Value(XElement? element) => element?.Value.Trim();

    private static XElement? Descendant(XElement? root, string localName) => root?.Descendants().FirstOrDefault(element => string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<XElement> Descendants(XElement? root, string localName) => root is null
        ? []
        : root.Descendants().Where(element => string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<XElement> Children(XElement? root, string localName) => root is null
        ? []
        : root.Elements().Where(element => string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private sealed record ParsedMeasure(
        MeasureSummary Summary,
        IReadOnlyList<ScoreNote> Notes,
        IReadOnlyList<ScoreRest> Rests,
        IReadOnlyList<ScoreMark> Marks,
        int Divisions,
        double DurationBeats,
        bool RepeatForward,
        bool RepeatBackward,
        int RepeatTimes,
        IReadOnlyList<ParsedEnding> Endings);

    private sealed record ParsedPitch(int MidiNoteNumber, string Step, int Octave, int Alter);
    private sealed record ParsedEnding(string Number, string Type);
}
