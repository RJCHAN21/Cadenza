using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop.Services;

public sealed class MusicXmlImporter
{
    private const long MaxSourceFileBytes = 64L * 1024 * 1024;
    private const long MaxScoreXmlBytes = 32L * 1024 * 1024;
    private const long MaxArchiveExpandedBytes = 96L * 1024 * 1024;
    private const int MaxArchiveEntries = 256;
    private const double MaxCompressionRatio = 250d;
    private const int MaxRepeatTimes = 16;
    private const int MaxPerformanceOccurrences = 100_000;
    private const double BeatEpsilon = 0.0001;

    public ScoreDocument Import(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A MusicXML file path is required.", nameof(path));

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The MusicXML file could not be found.", fullPath);

        var sourceInfo = new FileInfo(fullPath);
        if (sourceInfo.Length <= 0)
            throw new InvalidDataException("The MusicXML source is empty.");
        if (sourceInfo.Length > MaxSourceFileBytes)
            throw new InvalidDataException($"The MusicXML source exceeds the {MaxSourceFileBytes / (1024 * 1024)} MB import limit.");

        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);

        XDocument document;
        var sourceContainer = "MusicXML XML file";
        if (IsZipArchive(stream))
        {
            sourceContainer = "MXL archive";
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            ValidateArchive(archive);
            var scoreEntry = ResolveScoreEntry(archive);
            ValidateScoreEntry(scoreEntry);
            using var scoreStream = scoreEntry.Open();
            document = LoadXml(scoreStream, Math.Min(MaxScoreXmlBytes, Math.Max(1, scoreEntry.Length)));
        }
        else
        {
            stream.Position = 0;
            document = LoadXml(stream, MaxScoreXmlBytes);
        }

        return ParseDocument(document, fullPath, sourceContainer);
    }

    private static XDocument LoadXml(Stream stream, long maxCharacters)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = Math.Max(1, maxCharacters),
            CloseInput = false
        };
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static bool IsZipArchive(Stream stream)
    {
        Span<byte> signature = stackalloc byte[4];
        var bytesRead = stream.Read(signature);
        stream.Position = 0;
        return bytesRead >= 2 &&
               signature[0] == (byte)'P' &&
               signature[1] == (byte)'K';
    }

    private static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count == 0)
            throw new InvalidDataException("The MXL archive is empty.");
        if (archive.Entries.Count > MaxArchiveEntries)
            throw new InvalidDataException($"The MXL archive contains more than {MaxArchiveEntries} entries.");

        long totalExpanded = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Length < 0 || entry.CompressedLength < 0)
                throw new InvalidDataException("The MXL archive contains an entry with invalid size metadata.");

            totalExpanded = checked(totalExpanded + entry.Length);
            if (totalExpanded > MaxArchiveExpandedBytes)
                throw new InvalidDataException($"The MXL archive expands beyond the {MaxArchiveExpandedBytes / (1024 * 1024)} MB limit.");

            if (entry.Length > 1_048_576 && entry.CompressedLength > 0)
            {
                var ratio = entry.Length / (double)entry.CompressedLength;
                if (ratio > MaxCompressionRatio)
                    throw new InvalidDataException("The MXL archive has a suspicious compression ratio.");
            }
        }
    }

    private static ZipArchiveEntry ResolveScoreEntry(ZipArchive archive)
    {
        var container = archive.Entries.FirstOrDefault(entry =>
            string.Equals(NormalizeArchivePath(entry.FullName), "META-INF/container.xml", StringComparison.OrdinalIgnoreCase));

        if (container is not null)
        {
            if (container.Length > 1_048_576)
                throw new InvalidDataException("The MXL container manifest is unexpectedly large.");

            using var containerStream = container.Open();
            var containerDocument = LoadXml(containerStream, 1_048_576);
            var rootFile = Descendants(containerDocument.Root, "rootfile").FirstOrDefault();
            var requestedPath = rootFile?.Attribute("full-path")?.Value;
            if (!string.IsNullOrWhiteSpace(requestedPath))
            {
                if (requestedPath.StartsWith('/') || requestedPath.StartsWith('\\'))
                    throw new InvalidDataException("The MXL container references an unsafe score path.");
                var normalizedPath = NormalizeArchivePath(requestedPath);
                if (!IsSafeArchivePath(normalizedPath))
                    throw new InvalidDataException("The MXL container references an unsafe score path.");

                var resolved = archive.Entries.FirstOrDefault(entry =>
                    string.Equals(NormalizeArchivePath(entry.FullName), normalizedPath, StringComparison.OrdinalIgnoreCase));
                if (resolved is not null)
                    return resolved;
            }
        }

        var fallback = archive.Entries.FirstOrDefault(entry =>
        {
            var path = NormalizeArchivePath(entry.FullName);
            return IsSafeArchivePath(path) &&
                   (path.EndsWith(".musicxml", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) &&
                   !path.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase);
        });

        return fallback ?? throw new InvalidDataException("The MXL archive does not contain a score XML file.");
    }

    private static void ValidateScoreEntry(ZipArchiveEntry entry)
    {
        var path = NormalizeArchivePath(entry.FullName);
        if (!IsSafeArchivePath(path))
            throw new InvalidDataException("The MXL score entry uses an unsafe path.");
        if (entry.Length <= 0)
            throw new InvalidDataException("The MXL score entry is empty.");
        if (entry.Length > MaxScoreXmlBytes)
            throw new InvalidDataException($"The MXL score XML exceeds the {MaxScoreXmlBytes / (1024 * 1024)} MB limit.");
        if (entry.CompressedLength > 0 && entry.Length > 1_048_576 &&
            entry.Length / (double)entry.CompressedLength > MaxCompressionRatio)
            throw new InvalidDataException("The MXL score entry has a suspicious compression ratio.");
    }

    private static string NormalizeArchivePath(string value) =>
        value.Replace('\\', '/').TrimStart('/');

    private static bool IsSafeArchivePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
            return false;
        return !value.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");
    }

    private static ScoreDocument ParseDocument(XDocument document, string sourcePath, string sourceContainer)
    {
        var root = document.Root ?? throw new InvalidDataException("The MusicXML document has no root element.");
        if (!string.Equals(root.Name.LocalName, "score-partwise", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Cadenza currently imports score-partwise MusicXML, not {root.Name.LocalName}.");

        var title = Value(Descendant(root, "work-title"))
                    ?? Value(Descendant(root, "movement-title"))
                    ?? Path.GetFileNameWithoutExtension(sourcePath);
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

        var validationWarnings = new List<ScoreValidationWarning>();
        ValidateNavigationDirectives(root, validationWarnings);

        var parsedParts = new List<ParsedPart>();
        var slurPairCount = 0;
        foreach (var partElement in Children(root, "part"))
        {
            var partId = (string?)partElement.Attribute("id") ?? $"P{parsedParts.Count + 1}";
            var partName = partDefinitions.GetValueOrDefault(partId, "Unnamed part");
            var parsedPart = ParsePart(partElement, partId, partName);
            parsedParts.Add(parsedPart);
            ValidateSlurs(partElement, validationWarnings, ref slurPairCount);
        }

        if (parsedParts.Count == 0)
            throw new InvalidDataException("The MusicXML score does not contain any parts.");

        var canonicalPart = parsedParts[0];
        if (canonicalPart.Measures.Count == 0)
            throw new InvalidDataException("The MusicXML score does not contain any measures.");

        ResolveEndingPasses(canonicalPart.Measures, validationWarnings);
        ValidatePartAlignment(parsedParts, canonicalPart, validationWarnings);

        var performanceMeasures = BuildPerformanceOccurrences(
            canonicalPart.Measures,
            validationWarnings,
            out var repeatPairCount);

        var playableNotes = new List<ScoreNote>();
        var playableRests = new List<ScoreRest>();
        var scoreMarks = new List<ScoreMark>();
        var tiePairCount = 0;

        foreach (var parsedPart in parsedParts)
        {
            var expandedNotes = ExpandNotes(parsedPart.Notes, performanceMeasures);
            playableNotes.AddRange(MergeTiedNotes(expandedNotes, validationWarnings, ref tiePairCount));
            playableRests.AddRange(ExpandRests(parsedPart.Rests, performanceMeasures));
            scoreMarks.AddRange(ExpandMarks(parsedPart.Marks, performanceMeasures));
        }

        playableNotes = playableNotes
            .OrderBy(note => note.OnsetBeats)
            .ThenBy(note => note.PartId, StringComparer.Ordinal)
            .ThenBy(note => note.StaffNumber)
            .ThenBy(note => note.MidiNoteNumber)
            .ToList();
        playableRests = playableRests
            .OrderBy(rest => rest.OnsetBeats)
            .ThenBy(rest => rest.PartId, StringComparer.Ordinal)
            .ThenBy(rest => rest.StaffNumber)
            .ToList();
        scoreMarks = scoreMarks
            .OrderBy(mark => mark.OnsetBeats)
            .ThenBy(mark => mark.PartId, StringComparer.Ordinal)
            .ToList();

        var tempoChanges = ExpandTempoChanges(
            canonicalPart.Measures,
            canonicalPart.TempoChanges,
            performanceMeasures);
        var meterChanges = ExpandMeterChanges(
            canonicalPart.Measures,
            canonicalPart.MeterChanges,
            performanceMeasures);

        var firstMeasureElement = Children(Children(root, "part").FirstOrDefault(), "measure").FirstOrDefault();
        var firstAttributes = Descendant(firstMeasureElement, "attributes");
        var keyElement = Descendant(firstAttributes, "key");
        var keyFifths = ParseInt(Value(Descendant(keyElement, "fifths"))) ?? 0;
        var keySignature = FormatKeySignature(keyFifths, Value(Descendant(keyElement, "mode")));

        var initialMeter = meterChanges.FirstOrDefault()
                           ?? new ScoreMeterChange(0, 4, 4, canonicalPart.Measures[0].Summary.Number, 0);
        var initialTempo = tempoChanges.FirstOrDefault()
                           ?? new ScoreTempoChange(0, 120, canonicalPart.Measures[0].Summary.Number, 0);

        var parts = parsedParts.Select(part => new ScorePart(
            part.Id,
            part.Name,
            part.Measures.Count,
            part.Measures.Sum(measure => measure.Summary.NoteCount),
            part.Measures.Sum(measure => measure.Summary.RestCount),
            part.Measures.Sum(measure => measure.Summary.LyricCount),
            part.Measures.Sum(measure => measure.Summary.StaffOneNoteCount),
            part.Measures.Sum(measure => measure.Summary.StaffTwoNoteCount))).ToArray();

        return new ScoreDocument
        {
            SourcePath = sourcePath,
            Title = title,
            ComposerOrCreator = creators.Length > 0 ? string.Join(" / ", creators) : "Unknown creator",
            FormatVersion = root.Attribute("version")?.Value is { Length: > 0 } version
                ? $"MusicXML {version}"
                : "MusicXML",
            SourceContainer = sourceContainer,
            KeySignature = keySignature,
            KeyFifths = keyFifths,
            TimeSignature = $"{initialMeter.Beats}/{initialMeter.BeatType}",
            BeatsPerMeasure = initialMeter.Beats,
            BeatType = initialMeter.BeatType,
            Tempo = $"{initialTempo.Bpm:0.##} BPM",
            TempoBpm = initialTempo.Bpm,
            MeasureCount = canonicalPart.Measures.Count,
            TotalNoteCount = parts.Sum(part => part.NoteCount),
            TotalRestCount = parts.Sum(part => part.RestCount),
            TotalLyricCount = parts.Sum(part => part.LyricCount),
            TotalBeats = canonicalPart.TotalBeats,
            Parts = parts,
            Measures = canonicalPart.Measures.Select(measure => measure.Summary).ToArray(),
            Notes = playableNotes,
            Rests = playableRests,
            Marks = scoreMarks,
            PerformanceMeasures = performanceMeasures,
            TempoChanges = tempoChanges,
            MeterChanges = meterChanges,
            ValidationWarnings = validationWarnings,
            RepeatPairCount = repeatPairCount,
            TiePairCount = tiePairCount,
            SlurPairCount = slurPairCount
        };
    }

    private static ParsedPart ParsePart(XElement partElement, string partId, string partName)
    {
        var measures = new List<ParsedMeasure>();
        var notes = new List<ScoreNote>();
        var rests = new List<ScoreRest>();
        var marks = new List<ScoreMark>();
        var tempos = new List<ParsedTempoChange>();
        var meters = new List<ParsedMeterChange>();

        var partBeat = 0d;
        var divisions = 1;
        var currentMeter = (Beats: 4, BeatType: 4);
        var measureIndex = 0;
        foreach (var measureElement in Children(partElement, "measure"))
        {
            var parsed = ParseMeasure(
                measureElement,
                partId,
                measureIndex,
                partBeat,
                divisions,
                currentMeter);
            divisions = parsed.Divisions;
            currentMeter = parsed.EndingMeter;
            measures.Add(parsed);
            notes.AddRange(parsed.Notes);
            rests.AddRange(parsed.Rests);
            marks.AddRange(parsed.Marks);
            tempos.AddRange(parsed.TempoChanges);
            meters.AddRange(parsed.MeterChanges);
            partBeat += parsed.DurationBeats;
            measureIndex++;
        }

        return new ParsedPart(
            partId,
            partName,
            measures,
            notes,
            rests,
            marks,
            tempos,
            meters,
            partBeat);
    }

    private static ParsedMeasure ParseMeasure(
        XElement measure,
        string partId,
        int sourceMeasureIndex,
        double measureStartBeats,
        int inheritedDivisions,
        (int Beats, int BeatType) inheritedMeter)
    {
        var attributes = Descendant(measure, "attributes");
        var divisions = ParseInt(Value(Descendant(attributes, "divisions"))) ?? inheritedDivisions;
        divisions = Math.Max(1, divisions);

        var meter = inheritedMeter;
        var meterChanges = new List<ParsedMeterChange>();
        var timeElement = Descendant(attributes, "time");
        var parsedBeats = ParseInt(Value(Descendant(timeElement, "beats")));
        var parsedBeatType = ParseInt(Value(Descendant(timeElement, "beat-type")));
        if (parsedBeats is > 0 && parsedBeatType is > 0)
        {
            meter = (parsedBeats.Value, parsedBeatType.Value);
            meterChanges.Add(new ParsedMeterChange(
                measureStartBeats,
                meter.Beats,
                meter.BeatType,
                (string?)measure.Attribute("number") ?? (sourceMeasureIndex + 1).ToString(CultureInfo.InvariantCulture),
                sourceMeasureIndex));
        }

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
        var tempoChanges = new List<ParsedTempoChange>();
        var number = (string?)measure.Attribute("number")
                     ?? (sourceMeasureIndex + 1).ToString(CultureInfo.InvariantCulture);

        foreach (var element in measure.Elements())
        {
            switch (element.Name.LocalName.ToLowerInvariant())
            {
                case "note":
                {
                    noteCount++;
                    var isChord = Descendant(element, "chord") is not null;
                    if (isChord)
                        chordCount++;

                    var durationDivisions = ParseInt(Value(Descendant(element, "duration"))) ?? 0;
                    var start = isChord ? lastNoteStart : cursor;
                    if (!isChord)
                        lastNoteStart = start;

                    var isRest = Descendant(element, "rest") is not null;
                    if (isRest)
                        restCount++;
                    lyricCount += Descendants(element, "lyric").Count();

                    var noteType = Value(Descendant(element, "type"))
                                   ?? InferNoteType(durationDivisions, divisions);
                    var dotCount = Descendants(element, "dot").Count();
                    var voice = Value(Descendant(element, "voice")) ?? "1";
                    var staff = ParseInt(Value(Descendant(element, "staff"))) ?? 0;
                    if (staff == 1)
                        staffOne++;
                    if (staff == 2)
                        staffTwo++;

                    var pitch = ParsePitch(element);
                    var notations = Descendant(element, "notations");
                    var articulations = Descendants(notations, "articulations")
                        .SelectMany(parent => parent.Elements())
                        .ToArray();
                    var isStaccato = articulations.Any(item =>
                        string.Equals(item.Name.LocalName, "staccato", StringComparison.OrdinalIgnoreCase));
                    var isAccent = articulations.Any(item =>
                        string.Equals(item.Name.LocalName, "accent", StringComparison.OrdinalIgnoreCase));
                    var isTenuto = articulations.Any(item =>
                        string.Equals(item.Name.LocalName, "tenuto", StringComparison.OrdinalIgnoreCase));
                    var isSlurred = Descendants(notations, "slur").Any();
                    var sourceOnset = measureStartBeats + start / (double)divisions;

                    if (!isRest && pitch is not null)
                    {
                        var sourceNoteId = (string?)element.Attribute("id") ?? string.Empty;
                        playableNotes.Add(new ScoreNote(
                            pitch.MidiNoteNumber,
                            sourceOnset,
                            Math.Max(0.05, durationDivisions / (double)divisions),
                            staff,
                            number,
                            pitch.Step,
                            pitch.Octave,
                            pitch.Alter,
                            noteType,
                            dotCount,
                            voice,
                            Value(Descendant(element, "stem")) ?? string.Empty,
                            isChord,
                            Descendants(element, "beam")
                                .Select(beam => beam.Value.Trim().ToLowerInvariant())
                                .ToArray(),
                            Descendants(element, "lyric")
                                .Select(lyric => Value(Descendant(lyric, "text")))
                                .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)),
                            HasTieType(element, "start"),
                            HasTieType(element, "stop"),
                            0,
                            isStaccato,
                            isAccent,
                            isTenuto,
                            isSlurred,
                            partId,
                            sourceMeasureIndex,
                            sourceOnset,
                            sourceNoteId,
                            string.IsNullOrWhiteSpace(sourceNoteId) ? [] : [sourceNoteId]));

                        foreach (var articulation in articulations)
                        {
                            marks.Add(new ScoreMark(
                                sourceOnset,
                                staff,
                                number,
                                ScoreMarkKind.Articulation,
                                articulation.Name.LocalName,
                                partId,
                                sourceMeasureIndex,
                                sourceOnset));
                        }
                    }
                    else if (isRest)
                    {
                        playableRests.Add(new ScoreRest(
                            sourceOnset,
                            Math.Max(0.05, durationDivisions / (double)divisions),
                            staff,
                            number,
                            noteType,
                            dotCount,
                            voice,
                            partId,
                            sourceMeasureIndex,
                            sourceOnset));
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
                    var sourceOnset = measureStartBeats + cursor / (double)divisions;

                    foreach (var dynamics in Descendants(element, "dynamics"))
                    {
                        foreach (var dynamic in dynamics.Elements())
                        {
                            marks.Add(new ScoreMark(
                                sourceOnset,
                                staff,
                                number,
                                ScoreMarkKind.Dynamic,
                                dynamic.Name.LocalName,
                                partId,
                                sourceMeasureIndex,
                                sourceOnset));
                        }
                    }

                    foreach (var pedal in Descendants(element, "pedal"))
                    {
                        marks.Add(new ScoreMark(
                            sourceOnset,
                            staff,
                            number,
                            ScoreMarkKind.Pedal,
                            (string?)pedal.Attribute("type") ?? "start",
                            partId,
                            sourceMeasureIndex,
                            sourceOnset));
                    }

                    var words = Descendants(element, "words")
                        .Select(Value)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                    if (!string.IsNullOrWhiteSpace(words))
                    {
                        marks.Add(new ScoreMark(
                            sourceOnset,
                            staff,
                            number,
                            ScoreMarkKind.Direction,
                            words!,
                            partId,
                            sourceMeasureIndex,
                            sourceOnset));
                    }

                    var tempo = TempoFromDirection(element);
                    if (tempo is > 0)
                        tempoChanges.Add(new ParsedTempoChange(sourceOnset, tempo.Value, number, sourceMeasureIndex));
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

        var durationBeats = maximumCursor / (double)divisions;
        if (durationBeats <= BeatEpsilon)
            durationBeats = Math.Max(1d, meter.Beats * 4d / Math.Max(1, meter.BeatType));

        var repeats = Children(measure, "barline")
            .SelectMany(barline => Children(barline, "repeat"))
            .ToArray();
        var repeatForward = repeats.Any(repeat =>
            string.Equals((string?)repeat.Attribute("direction"), "forward", StringComparison.OrdinalIgnoreCase));
        var backwardRepeat = repeats.FirstOrDefault(repeat =>
            string.Equals((string?)repeat.Attribute("direction"), "backward", StringComparison.OrdinalIgnoreCase));
        var repeatTimes = ParseInt((string?)backwardRepeat?.Attribute("times")) ?? 2;
        var endings = Children(measure, "barline")
            .SelectMany(barline => Children(barline, "ending"))
            .Select(ending => new ParsedEnding(
                (string?)ending.Attribute("number") ?? string.Empty,
                (string?)ending.Attribute("type") ?? string.Empty))
            .ToArray();

        var firstTempo = tempoChanges.FirstOrDefault()?.Bpm;
        return new ParsedMeasure(
            new MeasureSummary(
                number,
                noteCount,
                restCount,
                chordCount,
                lyricCount,
                staffOne,
                staffTwo,
                firstTempo is > 0 ? $"{firstTempo:0.##} BPM" : null,
                measureStartBeats,
                durationBeats),
            playableNotes,
            playableRests,
            marks,
            tempoChanges,
            meterChanges,
            divisions,
            durationBeats,
            repeatForward,
            backwardRepeat is not null,
            repeatTimes,
            endings,
            [],
            meter);
    }

    private static bool HasTieType(XElement note, string type) =>
        Descendants(note, "tie").Any(tie =>
            string.Equals((string?)tie.Attribute("type"), type, StringComparison.OrdinalIgnoreCase)) ||
        Descendants(note, "tied").Any(tied =>
            string.Equals((string?)tied.Attribute("type"), type, StringComparison.OrdinalIgnoreCase));

    private static double? TempoFromDirection(XElement direction)
    {
        var soundTempo = Descendants(direction, "sound")
            .Select(sound => (string?)sound.Attribute("tempo"))
            .FirstOrDefault(value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        if (double.TryParse(soundTempo, NumberStyles.Float, CultureInfo.InvariantCulture, out var tempo) && tempo > 0)
            return tempo;

        var metronomeTempo = Descendants(direction, "metronome")
            .Select(metronome => Value(Descendant(metronome, "per-minute")))
            .FirstOrDefault(value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        return double.TryParse(metronomeTempo, NumberStyles.Float, CultureInfo.InvariantCulture, out tempo) && tempo > 0
            ? tempo
            : null;
    }

    private static void ResolveEndingPasses(
        IReadOnlyList<ParsedMeasure> measures,
        ICollection<ScoreValidationWarning> warnings)
    {
        var activePasses = new HashSet<int>();
        for (var index = 0; index < measures.Count; index++)
        {
            var measure = measures[index];
            foreach (var ending in measure.Endings.Where(item =>
                         string.Equals(item.Type, "start", StringComparison.OrdinalIgnoreCase)))
            {
                var parsed = ParseEndingPasses(ending.Number);
                if (parsed.Count == 0)
                {
                    warnings.Add(new ScoreValidationWarning(
                        "volta-ending",
                        $"Ending at measure {measure.Summary.Number} has an unsupported number value \"{ending.Number}\".",
                        MeasureNumberOf(measure.Summary.Number, index + 1),
                        MeasureNumberOf(measure.Summary.Number, index + 1),
                        true));
                }
                else
                {
                    activePasses = parsed;
                }
            }

            measure.EndingPasses = activePasses.Order().ToArray();

            if (measure.Endings.Any(item =>
                    string.Equals(item.Type, "stop", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Type, "discontinue", StringComparison.OrdinalIgnoreCase)))
                activePasses = [];
        }

        if (activePasses.Count > 0)
        {
            var last = measures[^1];
            warnings.Add(new ScoreValidationWarning(
                "volta-ending",
                "An alternate ending was not closed before the end of the score.",
                MeasureNumberOf(last.Summary.Number, measures.Count),
                MeasureNumberOf(last.Summary.Number, measures.Count),
                true));
        }
    }

    private static HashSet<int> ParseEndingPasses(string value)
    {
        var result = new HashSet<int>();
        foreach (var token in value.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var range = token.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (range.Length == 1 &&
                int.TryParse(range[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pass) &&
                pass > 0)
            {
                result.Add(pass);
                continue;
            }

            if (range.Length == 2 &&
                int.TryParse(range[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) &&
                int.TryParse(range[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var end) &&
                start > 0 && end >= start && end <= MaxRepeatTimes)
            {
                for (var current = start; current <= end; current++)
                    result.Add(current);
            }
        }

        return result;
    }

    private static void ValidatePartAlignment(
        IReadOnlyList<ParsedPart> parts,
        ParsedPart canonical,
        ICollection<ScoreValidationWarning> warnings)
    {
        foreach (var part in parts.Skip(1))
        {
            ResolveEndingPasses(part.Measures, warnings);
            if (part.Measures.Count != canonical.Measures.Count)
            {
                warnings.Add(new ScoreValidationWarning(
                    "part-measure-count",
                    $"Part {part.Name} contains {part.Measures.Count} measures while the navigation part contains {canonical.Measures.Count}. Assessment is disabled until the score is normalized.",
                    1,
                    Math.Max(part.Measures.Count, canonical.Measures.Count),
                    true));
            }

            var sharedCount = Math.Min(part.Measures.Count, canonical.Measures.Count);
            for (var index = 0; index < sharedCount; index++)
            {
                var expected = canonical.Measures[index];
                var actual = part.Measures[index];
                if (Math.Abs(expected.DurationBeats - actual.DurationBeats) > 0.01)
                {
                    warnings.Add(new ScoreValidationWarning(
                        "part-duration",
                        $"Part {part.Name} has a different duration in measure {actual.Summary.Number}.",
                        MeasureNumberOf(actual.Summary.Number, index + 1),
                        MeasureNumberOf(actual.Summary.Number, index + 1),
                        true));
                }

                if (expected.RepeatForward != actual.RepeatForward ||
                    expected.RepeatBackward != actual.RepeatBackward ||
                    expected.RepeatTimes != actual.RepeatTimes ||
                    !expected.EndingPasses.SequenceEqual(actual.EndingPasses))
                {
                    warnings.Add(new ScoreValidationWarning(
                        "part-navigation",
                        $"Part {part.Name} disagrees with the first part's repeat or ending structure at measure {actual.Summary.Number}.",
                        MeasureNumberOf(actual.Summary.Number, index + 1),
                        MeasureNumberOf(actual.Summary.Number, index + 1),
                        true));
                }
            }
        }
    }

    private static IReadOnlyList<ScoreMeasureOccurrence> BuildPerformanceOccurrences(
        IReadOnlyList<ParsedMeasure> measures,
        ICollection<ScoreValidationWarning> warnings,
        out int repeatPairCount)
    {
        var sections = BuildRepeatSections(measures, warnings);
        repeatPairCount = sections.Count;

        var sectionByStart = sections
            .GroupBy(section => section.StartIndex)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(section => section.ExitIndex - section.StartIndex).First());
        var sectionById = sections.ToDictionary(section => section.Id);
        var endingOwner = new int?[measures.Count];
        foreach (var section in sections.OrderBy(section => section.ExitIndex - section.StartIndex))
        {
            for (var index = section.StartIndex; index <= section.ExitIndex && index < measures.Count; index++)
            {
                if (measures[index].EndingPasses.Count > 0 && endingOwner[index] is null)
                    endingOwner[index] = section.Id;
            }
        }

        var occurrences = new List<ScoreMeasureOccurrence>();
        var activePasses = new Dictionary<int, int>();
        var performanceBeat = 0d;

        void AddMeasure(int sourceIndex)
        {
            if (occurrences.Count >= MaxPerformanceOccurrences)
                throw new InvalidDataException("Repeat expansion exceeded the safe performance-occurrence limit.");

            var owner = endingOwner[sourceIndex];
            if (owner is { } ownerId &&
                activePasses.TryGetValue(ownerId, out var endingPass) &&
                !measures[sourceIndex].EndingPasses.Contains(endingPass))
                return;

            var activeSection = activePasses.Keys
                .Select(id => sectionById[id])
                .Where(section => sourceIndex >= section.StartIndex && sourceIndex <= section.ExitIndex)
                .OrderBy(section => section.ExitIndex - section.StartIndex)
                .FirstOrDefault();
            var repeatPass = activeSection is null ? 1 : activePasses[activeSection.Id];
            var repeatSectionId = activeSection?.Id ?? -1;
            var measure = measures[sourceIndex];

            occurrences.Add(new ScoreMeasureOccurrence(
                occurrences.Count,
                sourceIndex,
                measure.Summary.Number,
                measure.Summary.StartBeat,
                performanceBeat,
                measure.DurationBeats,
                repeatPass,
                repeatSectionId));
            performanceBeat += measure.DurationBeats;
        }

        void ExpandRange(int startIndex, int endIndex, int suppressedSectionId = -1)
        {
            var index = startIndex;
            while (index <= endIndex && index < measures.Count)
            {
                if (sectionByStart.TryGetValue(index, out var section) &&
                    section.Id != suppressedSectionId &&
                    section.ExitIndex <= endIndex)
                {
                    for (var pass = 1; pass <= section.TotalPasses; pass++)
                    {
                        activePasses[section.Id] = pass;
                        ExpandRange(section.StartIndex, section.ExitIndex, section.Id);
                    }

                    activePasses.Remove(section.Id);
                    index = section.ExitIndex + 1;
                    continue;
                }

                AddMeasure(index);
                index++;
            }
        }

        try
        {
            ExpandRange(0, measures.Count - 1);
        }
        catch (InvalidDataException)
        {
            warnings.Add(new ScoreValidationWarning(
                "repeat-cycle",
                "Repeat navigation expanded beyond the safe limit. Playback and assessment are disabled.",
                1,
                measures.Count,
                true));
            throw;
        }

        return occurrences;
    }

    private static IReadOnlyList<RepeatSection> BuildRepeatSections(
        IReadOnlyList<ParsedMeasure> measures,
        ICollection<ScoreValidationWarning> warnings)
    {
        var sections = new List<RepeatSection>();
        var starts = new Stack<int>();
        var id = 0;

        for (var index = 0; index < measures.Count; index++)
        {
            if (measures[index].RepeatForward)
                starts.Push(index);

            if (!measures[index].RepeatBackward)
                continue;

            var startIndex = starts.Count > 0 ? starts.Pop() : 0;
            var requestedTimes = measures[index].RepeatTimes;
            var totalPasses = requestedTimes is >= 1 and <= MaxRepeatTimes ? requestedTimes : 2;
            if (requestedTimes is < 1 or > MaxRepeatTimes)
            {
                warnings.Add(new ScoreValidationWarning(
                    "repeat-times",
                    $"Repeat at measure {measures[index].Summary.Number} has unsupported times=\"{requestedTimes}\"; playback uses 2 passes and assessment is disabled for the section.",
                    MeasureNumberOf(measures[startIndex].Summary.Number, startIndex + 1),
                    MeasureNumberOf(measures[index].Summary.Number, index + 1),
                    true));
            }

            var exitIndex = index;
            while (exitIndex + 1 < measures.Count &&
                   measures[exitIndex + 1].EndingPasses.Count > 0)
                exitIndex++;

            sections.Add(new RepeatSection(id++, startIndex, index, exitIndex, totalPasses));
        }

        foreach (var unmatchedStart in starts)
        {
            warnings.Add(new ScoreValidationWarning(
                "unmatched-repeat-start",
                $"A forward repeat at measure {measures[unmatchedStart].Summary.Number} has no backward repeat.",
                MeasureNumberOf(measures[unmatchedStart].Summary.Number, unmatchedStart + 1),
                MeasureNumberOf(measures[unmatchedStart].Summary.Number, unmatchedStart + 1),
                true));
        }

        return sections
            .OrderBy(section => section.StartIndex)
            .ThenBy(section => section.ExitIndex)
            .ToArray();
    }

    private static IReadOnlyList<ScoreNote> ExpandNotes(
        IReadOnlyList<ScoreNote> notes,
        IReadOnlyList<ScoreMeasureOccurrence> occurrences)
    {
        var byMeasure = notes
            .GroupBy(note => note.SourceMeasureIndex)
            .ToDictionary(group => group.Key, group => group.ToArray());

        return occurrences.SelectMany(occurrence =>
                byMeasure.GetValueOrDefault(occurrence.SourceMeasureIndex, [])
                    .Select(note => note with
                    {
                        OnsetBeats = occurrence.PerformanceStartBeat +
                                     (note.SourceOnsetBeats - occurrence.SourceStartBeat),
                        PerformanceOccurrence = occurrence.OccurrenceIndex
                    }))
            .OrderBy(note => note.OnsetBeats)
            .ThenBy(note => note.PartId, StringComparer.Ordinal)
            .ThenBy(note => note.StaffNumber)
            .ThenBy(note => note.MidiNoteNumber)
            .ToArray();
    }

    private static IReadOnlyList<ScoreRest> ExpandRests(
        IReadOnlyList<ScoreRest> rests,
        IReadOnlyList<ScoreMeasureOccurrence> occurrences)
    {
        var byMeasure = rests
            .GroupBy(rest => rest.SourceMeasureIndex)
            .ToDictionary(group => group.Key, group => group.ToArray());

        return occurrences.SelectMany(occurrence =>
                byMeasure.GetValueOrDefault(occurrence.SourceMeasureIndex, [])
                    .Select(rest => rest with
                    {
                        OnsetBeats = occurrence.PerformanceStartBeat +
                                     (rest.SourceOnsetBeats - occurrence.SourceStartBeat),
                        PerformanceOccurrence = occurrence.OccurrenceIndex
                    }))
            .OrderBy(rest => rest.OnsetBeats)
            .ThenBy(rest => rest.PartId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ScoreMark> ExpandMarks(
        IReadOnlyList<ScoreMark> marks,
        IReadOnlyList<ScoreMeasureOccurrence> occurrences)
    {
        var byMeasure = marks
            .GroupBy(mark => mark.SourceMeasureIndex)
            .ToDictionary(group => group.Key, group => group.ToArray());

        return occurrences.SelectMany(occurrence =>
                byMeasure.GetValueOrDefault(occurrence.SourceMeasureIndex, [])
                    .Select(mark => mark with
                    {
                        OnsetBeats = occurrence.PerformanceStartBeat +
                                     (mark.SourceOnsetBeats - occurrence.SourceStartBeat),
                        PerformanceOccurrence = occurrence.OccurrenceIndex
                    }))
            .OrderBy(mark => mark.OnsetBeats)
            .ThenBy(mark => mark.PartId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ScoreNote> MergeTiedNotes(
        IReadOnlyList<ScoreNote> notes,
        ICollection<ScoreValidationWarning> warnings,
        ref int tiePairCount)
    {
        var merged = new List<ScoreNote>();
        var active = new Dictionary<(string Part, int Midi, int Staff, string Voice), int>();

        foreach (var note in notes
                     .OrderBy(note => note.OnsetBeats)
                     .ThenBy(note => note.PartId, StringComparer.Ordinal)
                     .ThenBy(note => note.StaffNumber)
                     .ThenBy(note => note.MidiNoteNumber))
        {
            var key = (note.PartId, note.MidiNoteNumber, note.StaffNumber, note.Voice);
            if (note.TieStop && active.TryGetValue(key, out var activeIndex))
            {
                var start = merged[activeIndex];
                var endBeat = Math.Max(
                    start.OnsetBeats + start.DurationBeats,
                    note.OnsetBeats + note.DurationBeats);
                var ids = (start.TiedSourceNoteIds ?? [])
                    .Concat(note.TiedSourceNoteIds ?? [])
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                merged[activeIndex] = start with
                {
                    DurationBeats = endBeat - start.OnsetBeats,
                    TiedSourceNoteIds = ids
                };
                tiePairCount++;
                if (!note.TieStart)
                    active.Remove(key);
                continue;
            }

            var outputIndex = merged.Count;
            merged.Add(note);
            if (note.TieStart)
                active[key] = outputIndex;

            if (note.TieStop && !note.TieStart)
            {
                var measure = MeasureNumberOf(note.MeasureNumber, note.SourceMeasureIndex + 1);
                warnings.Add(new ScoreValidationWarning(
                    "unmatched-tie-stop",
                    $"An unmatched tie stop was found in part {note.PartId} at measure {note.MeasureNumber}.",
                    measure,
                    measure,
                    true));
            }
        }

        foreach (var activeIndex in active.Values.Distinct())
        {
            var note = merged[activeIndex];
            var measure = MeasureNumberOf(note.MeasureNumber, note.SourceMeasureIndex + 1);
            warnings.Add(new ScoreValidationWarning(
                "unmatched-tie-start",
                $"An unmatched tie start was found in part {note.PartId} at measure {note.MeasureNumber}.",
                measure,
                measure,
                true));
        }

        return merged;
    }

    private static IReadOnlyList<ScoreTempoChange> ExpandTempoChanges(
        IReadOnlyList<ParsedMeasure> measures,
        IReadOnlyList<ParsedTempoChange> sourceChanges,
        IReadOnlyList<ScoreMeasureOccurrence> occurrences)
    {
        var ordered = sourceChanges
            .Where(change => change.Bpm > 0)
            .OrderBy(change => change.SourceBeat)
            .ToArray();
        var fallback = ordered.FirstOrDefault()?.Bpm ?? 120d;
        var result = new List<ScoreTempoChange>();

        foreach (var occurrence in occurrences)
        {
            var sourceStart = occurrence.SourceStartBeat;
            var sourceEnd = sourceStart + occurrence.DurationBeats;
            var active = ordered.LastOrDefault(change => change.SourceBeat <= sourceStart + BeatEpsilon)?.Bpm
                         ?? fallback;
            result.Add(new ScoreTempoChange(
                occurrence.PerformanceStartBeat,
                active,
                occurrence.MeasureNumber,
                occurrence.OccurrenceIndex));

            foreach (var change in ordered.Where(change =>
                         change.SourceBeat > sourceStart + BeatEpsilon &&
                         change.SourceBeat < sourceEnd + BeatEpsilon))
            {
                result.Add(new ScoreTempoChange(
                    occurrence.PerformanceStartBeat + change.SourceBeat - sourceStart,
                    change.Bpm,
                    occurrence.MeasureNumber,
                    occurrence.OccurrenceIndex));
            }
        }

        return result
            .OrderBy(change => change.PerformanceBeat)
            .GroupBy(change => Math.Round(change.PerformanceBeat, 6))
            .Select(group => group.Last())
            .ToArray();
    }

    private static IReadOnlyList<ScoreMeterChange> ExpandMeterChanges(
        IReadOnlyList<ParsedMeasure> measures,
        IReadOnlyList<ParsedMeterChange> sourceChanges,
        IReadOnlyList<ScoreMeasureOccurrence> occurrences)
    {
        var ordered = sourceChanges
            .Where(change => change.Beats > 0 && change.BeatType > 0)
            .OrderBy(change => change.SourceBeat)
            .ToArray();
        var fallback = ordered.FirstOrDefault()
                       ?? new ParsedMeterChange(0, 4, 4, measures[0].Summary.Number, 0);
        var result = new List<ScoreMeterChange>();

        foreach (var occurrence in occurrences)
        {
            var sourceStart = occurrence.SourceStartBeat;
            var sourceEnd = sourceStart + occurrence.DurationBeats;
            var active = ordered.LastOrDefault(change => change.SourceBeat <= sourceStart + BeatEpsilon)
                         ?? fallback;
            result.Add(new ScoreMeterChange(
                occurrence.PerformanceStartBeat,
                active.Beats,
                active.BeatType,
                occurrence.MeasureNumber,
                occurrence.OccurrenceIndex));

            foreach (var change in ordered.Where(change =>
                         change.SourceBeat > sourceStart + BeatEpsilon &&
                         change.SourceBeat < sourceEnd + BeatEpsilon))
            {
                result.Add(new ScoreMeterChange(
                    occurrence.PerformanceStartBeat + change.SourceBeat - sourceStart,
                    change.Beats,
                    change.BeatType,
                    occurrence.MeasureNumber,
                    occurrence.OccurrenceIndex));
            }
        }

        return result
            .OrderBy(change => change.PerformanceBeat)
            .GroupBy(change => Math.Round(change.PerformanceBeat, 6))
            .Select(group => group.Last())
            .ToArray();
    }

    private static void ValidateNavigationDirectives(
        XElement root,
        ICollection<ScoreValidationWarning> warnings)
    {
        var navigationAttributes = new[] { "dacapo", "dalsegno", "segno", "coda", "tocoda", "fine" };
        var unsupported = Descendants(root, "sound")
            .SelectMany(sound => navigationAttributes.Where(attribute => sound.Attribute(attribute) is not null))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unsupported.Length == 0)
            return;

        var measureCount = Children(Children(root, "part").FirstOrDefault(), "measure").Count();
        warnings.Add(new ScoreValidationWarning(
            "navigation-directive",
            $"Unsupported score navigation was found ({string.Join(", ", unsupported)}). Playback and assessment are disabled until reviewed.",
            1,
            Math.Max(1, measureCount),
            true));
    }

    private static void ValidateSlurs(
        XElement part,
        ICollection<ScoreValidationWarning> warnings,
        ref int slurPairCount)
    {
        var active = new Dictionary<string, int>();
        foreach (var note in Descendants(part, "note"))
        {
            var measureNumber = MeasureNumberOf((string?)note.Parent?.Attribute("number") ?? "1", 1);
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
                        $"An unmatched slur stop was found at measure {measureNumber}.",
                        measureNumber,
                        measureNumber,
                        true));
            }
        }

        foreach (var measureNumber in active.Values)
        {
            warnings.Add(new ScoreValidationWarning(
                "unmatched-slur-start",
                $"An unmatched slur start was found at measure {measureNumber}.",
                measureNumber,
                measureNumber,
                true));
        }
    }

    private static ParsedPitch? ParsePitch(XElement note)
    {
        var pitch = Descendant(note, "pitch");
        var step = Value(Descendant(pitch, "step"));
        var octave = ParseInt(Value(Descendant(pitch, "octave")));
        if (step is null || octave is null)
            return null;

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
        if (pitchClass < 0)
            return null;

        var alter = double.TryParse(
            Value(Descendant(pitch, "alter")),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsedAlter)
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
        if (beats >= 4)
            return "whole";
        if (beats >= 2)
            return "half";
        if (beats >= 1)
            return "quarter";
        if (beats >= 0.5)
            return "eighth";
        if (beats >= 0.25)
            return "16th";
        return "32nd";
    }

    private static string FormatKeySignature(int fifths, string? mode)
    {
        var majorKeys = new[] { "Cb", "Gb", "Db", "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#" };
        var index = Math.Clamp(fifths + 7, 0, majorKeys.Length - 1);
        var majorKey = majorKeys[index];
        return string.Equals(mode, "minor", StringComparison.OrdinalIgnoreCase)
            ? $"{majorKey} minor"
            : $"{majorKey} major";
    }

    private static int MeasureNumberOf(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : fallback;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static string? Value(XElement? element) => element?.Value.Trim();

    private static XElement? Descendant(XElement? root, string localName) =>
        root?.Descendants().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<XElement> Descendants(XElement? root, string localName) =>
        root is null
            ? []
            : root.Descendants().Where(element =>
                string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<XElement> Children(XElement? root, string localName) =>
        root is null
            ? []
            : root.Elements().Where(element =>
                string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private sealed record ParsedPart(
        string Id,
        string Name,
        IReadOnlyList<ParsedMeasure> Measures,
        IReadOnlyList<ScoreNote> Notes,
        IReadOnlyList<ScoreRest> Rests,
        IReadOnlyList<ScoreMark> Marks,
        IReadOnlyList<ParsedTempoChange> TempoChanges,
        IReadOnlyList<ParsedMeterChange> MeterChanges,
        double TotalBeats);

    private sealed record ParsedMeasure(
        MeasureSummary Summary,
        IReadOnlyList<ScoreNote> Notes,
        IReadOnlyList<ScoreRest> Rests,
        IReadOnlyList<ScoreMark> Marks,
        IReadOnlyList<ParsedTempoChange> TempoChanges,
        IReadOnlyList<ParsedMeterChange> MeterChanges,
        int Divisions,
        double DurationBeats,
        bool RepeatForward,
        bool RepeatBackward,
        int RepeatTimes,
        IReadOnlyList<ParsedEnding> Endings,
        IReadOnlyList<int> InitialEndingPasses,
        (int Beats, int BeatType) EndingMeter)
    {
        public IReadOnlyList<int> EndingPasses { get; set; } = InitialEndingPasses;
    }

    private sealed record ParsedPitch(int MidiNoteNumber, string Step, int Octave, int Alter);
    private sealed record ParsedEnding(string Number, string Type);
    private sealed record ParsedTempoChange(double SourceBeat, double Bpm, string MeasureNumber, int SourceMeasureIndex);
    private sealed record ParsedMeterChange(double SourceBeat, int Beats, int BeatType, string MeasureNumber, int SourceMeasureIndex);
    private sealed record RepeatSection(int Id, int StartIndex, int EndIndex, int ExitIndex, int TotalPasses);
}
