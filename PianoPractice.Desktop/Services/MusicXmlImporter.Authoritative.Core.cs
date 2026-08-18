using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop.Services;

/// <summary>
/// Imports MusicXML into one occurrence-aware performance plan. Written score
/// coordinates remain distinct from performed coordinates; every downstream
/// consumer receives the same expanded notes, rests, marks, tempo map, meter
/// map, and measure occurrences.
/// </summary>
public sealed partial class MusicXmlImporter
{
    private const long MaxSourceFileBytes = 64L * 1024 * 1024;
    private const long MaxScoreXmlBytes = 32L * 1024 * 1024;
    private const long MaxArchiveExpandedBytes = 96L * 1024 * 1024;
    private const int MaxArchiveEntries = 256;
    private const double MaxCompressionRatio = 250d;
    private const int MaxRepeatTimes = 16;
    private const int MaxPerformanceOccurrences = 100_000;
    private const int MaxParts = 64;
    private const int MaxMeasuresPerPart = 20_000;
    private const int MaxScoreEvents = 500_000;
    private const int MaxVoicesPerPart = 128;
    private const int MaxVoiceIdentifierLength = 128;
    private const int MaxStavesPerPart = 32;
    private const int MaxDivisions = 1_000_000;
    private const int MaxMeasureBeats = 1_024;
    private const double MaxWrittenBeats = 250_000;
    private const double MaxPerformedBeats = 1_000_000;
    private const int MaxExpandedEvents = 1_000_000;
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
            throw new InvalidDataException(
                $"The MusicXML source exceeds the {MaxSourceFileBytes / (1024 * 1024)} MB import limit.");

        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);

        byte[] validatedMusicXml;
        var sourceContainer = "MusicXML XML file";
        if (IsZipArchive(stream))
        {
            sourceContainer = "MXL archive";
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            ValidateArchive(archive);
            var scoreEntry = ResolveScoreEntry(archive);
            ValidateScoreEntry(scoreEntry);
            using var scoreStream = scoreEntry.Open();
            validatedMusicXml = ReadBoundedBytes(
                scoreStream,
                Math.Min(MaxScoreXmlBytes, Math.Max(1, scoreEntry.Length)));
        }
        else
        {
            stream.Position = 0;
            validatedMusicXml = ReadBoundedBytes(stream, MaxScoreXmlBytes);
        }

        using var validatedStream = new MemoryStream(validatedMusicXml, writable: false);
        var document = LoadXml(validatedStream, validatedMusicXml.LongLength);
        return ParseDocument(document, fullPath, sourceContainer, validatedMusicXml);
    }

    private static byte[] ReadBoundedBytes(Stream stream, long maximumBytes)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0)
                break;
            total = checked(total + read);
            if (total > maximumBytes)
                throw new InvalidDataException(
                    $"The MusicXML score exceeds the {maximumBytes / (1024 * 1024)} MB document limit.");
            buffer.Write(chunk, 0, read);
        }

        if (total == 0)
            throw new InvalidDataException("The MusicXML score document is empty.");
        return buffer.ToArray();
    }

    private static XDocument LoadXml(Stream stream, long maxCharacters)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = Math.Max(1, maxCharacters),
            CloseInput = false
        };
        try
        {
            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("The MusicXML document is malformed or contains a prohibited XML construct.", exception);
        }
    }

    private static bool IsZipArchive(Stream stream)
    {
        Span<byte> signature = stackalloc byte[4];
        var bytesRead = stream.Read(signature);
        stream.Position = 0;
        return bytesRead >= 2 && signature[0] == (byte)'P' && signature[1] == (byte)'K';
    }

    private static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count == 0)
            throw new InvalidDataException("The MXL archive is empty.");
        if (archive.Entries.Count > MaxArchiveEntries)
            throw new InvalidDataException($"The MXL archive contains more than {MaxArchiveEntries} entries.");

        long totalExpanded = 0;
        var normalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var normalizedPath = NormalizeArchivePath(entry.FullName);
            if (!IsSafeArchivePath(normalizedPath))
                throw new InvalidDataException(
                    $"The MXL archive contains an unsafe entry path: {entry.FullName}.");
            if (!normalizedPaths.Add(normalizedPath))
                throw new InvalidDataException(
                    $"The MXL archive contains duplicate entry paths under Windows path semantics: {normalizedPath}.");

            if (entry.Length < 0 || entry.CompressedLength < 0)
                throw new InvalidDataException("The MXL archive contains invalid size metadata.");

            totalExpanded = checked(totalExpanded + entry.Length);
            if (totalExpanded > MaxArchiveExpandedBytes)
                throw new InvalidDataException(
                    $"The MXL archive expands beyond the {MaxArchiveExpandedBytes / (1024 * 1024)} MB limit.");

            if (entry.Length > 1_048_576 && entry.CompressedLength > 0 &&
                entry.Length / (double)entry.CompressedLength > MaxCompressionRatio)
                throw new InvalidDataException("The MXL archive has a suspicious compression ratio.");
        }
    }

    private static ZipArchiveEntry ResolveScoreEntry(ZipArchive archive)
    {
        var container = archive.Entries.FirstOrDefault(entry =>
            string.Equals(
                NormalizeArchivePath(entry.FullName),
                "META-INF/container.xml",
                StringComparison.OrdinalIgnoreCase));

        if (container is not null)
        {
            if (container.Length > 1_048_576)
                throw new InvalidDataException("The MXL container manifest is unexpectedly large.");

            using var containerStream = container.Open();
            var containerDocument = LoadXml(containerStream, 1_048_576);
            var rootFiles = Descendants(containerDocument.Root, "rootfile").ToArray();
            if (rootFiles.Length != 1)
                throw new InvalidDataException(
                    "The MXL container must identify exactly one score rootfile.");

            var requestedPath = rootFiles[0].Attribute("full-path")?.Value;
            if (string.IsNullOrWhiteSpace(requestedPath))
                throw new InvalidDataException("The MXL container rootfile has no full-path.");
            var normalizedPath = NormalizeArchivePath(requestedPath);
            if (!IsSafeArchivePath(normalizedPath))
                throw new InvalidDataException("The MXL container references an unsafe score path.");

            var resolved = archive.Entries.Where(entry =>
                    string.Equals(
                        NormalizeArchivePath(entry.FullName),
                        normalizedPath,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (resolved.Length != 1)
                throw new InvalidDataException(
                    "The MXL container rootfile does not resolve to exactly one score document.");
            return resolved[0];
        }

        var candidates = archive.Entries.Where(entry =>
        {
            var normalized = NormalizeArchivePath(entry.FullName);
            return IsSafeArchivePath(normalized) &&
                   !normalized.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) &&
                   (normalized.EndsWith(".musicxml", StringComparison.OrdinalIgnoreCase) ||
                    normalized.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
        }).ToArray();
        return candidates.Length switch
        {
            0 => throw new InvalidDataException("The MXL archive does not contain a score XML file."),
            1 => candidates[0],
            _ => throw new InvalidDataException(
                "The MXL archive contains multiple score documents but no unambiguous container manifest.")
        };
    }

    private static void ValidateScoreEntry(ZipArchiveEntry entry)
    {
        var normalized = NormalizeArchivePath(entry.FullName);
        if (!IsSafeArchivePath(normalized))
            throw new InvalidDataException("The MXL score entry uses an unsafe path.");
        if (entry.Length <= 0)
            throw new InvalidDataException("The MXL score entry is empty.");
        if (entry.Length > MaxScoreXmlBytes)
            throw new InvalidDataException(
                $"The MXL score XML exceeds the {MaxScoreXmlBytes / (1024 * 1024)} MB limit.");
        if (entry.CompressedLength > 0 && entry.Length > 1_048_576 &&
            entry.Length / (double)entry.CompressedLength > MaxCompressionRatio)
            throw new InvalidDataException("The MXL score entry has a suspicious compression ratio.");
    }

    private static string NormalizeArchivePath(string value)
    {
        var normalized = value.Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        return normalized;
    }

    private static bool IsSafeArchivePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
            return false;
        return !value.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");
    }

    private static ScoreDocument ParseDocument(
        XDocument document,
        string sourcePath,
        string sourceContainer,
        byte[] validatedMusicXml)
    {
        var root = document.Root ?? throw new InvalidDataException("The MusicXML document has no root element.");
        if (!string.Equals(root.Name.LocalName, "score-partwise", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                $"Cadenza currently imports score-partwise MusicXML, not {root.Name.LocalName}.");

        ValidateSemanticComplexity(root);

        var warnings = new List<ScoreValidationWarning>();
        ValidateNavigationDirectives(root, warnings);
        ValidateNotationCapabilities(root, warnings);

        var title = Value(Descendant(root, "work-title"))
                    ?? Value(Descendant(root, "movement-title"))
                    ?? Path.GetFileNameWithoutExtension(sourcePath);
        var creators = Descendants(root, "creator")
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var partDefinitions = Descendants(root, "score-part")
            .GroupBy(element => (string?)element.Attribute("id") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => Value(Descendant(group.First(), "part-name")) ?? "Unnamed part",
                StringComparer.OrdinalIgnoreCase);

        var parsedParts = new List<ParsedPart>();
        var slurPairCount = 0;
        foreach (var partElement in Children(root, "part"))
        {
            var partId = (string?)partElement.Attribute("id") ?? $"P{parsedParts.Count + 1}";
            var partName = partDefinitions.GetValueOrDefault(partId, "Unnamed part");
            var part = ParsePart(partElement, partId, partName, warnings);
            ResolveEndingPasses(part.Measures, warnings);
            ValidateSlurs(partElement, warnings, ref slurPairCount);
            parsedParts.Add(part);
        }

        if (parsedParts.Count == 0)
            throw new InvalidDataException("The MusicXML score does not contain any parts.");

        var canonicalPart = parsedParts
            .OrderByDescending(part => part.NavigationDirectiveCount)
            .ThenByDescending(part => part.Measures.Count)
            .First();
        if (canonicalPart.Measures.Count == 0)
            throw new InvalidDataException("The MusicXML score does not contain any measures.");

        ValidatePartAlignment(parsedParts, canonicalPart, warnings);
        var performanceMeasures = BuildPerformanceOccurrences(
            canonicalPart.Measures,
            warnings,
            out var repeatPairCount);
        var totalPerformedBeats = performanceMeasures.Sum(occurrence => occurrence.DurationBeats);
        if (!double.IsFinite(totalPerformedBeats) || totalPerformedBeats > MaxPerformedBeats)
            throw new InvalidDataException(
                $"The expanded performance exceeds the {MaxPerformedBeats:0} beat safety limit.");

        var expandedNotes = new List<ScoreNote>();
        var expandedRests = new List<ScoreRest>();
        var expandedMarks = new List<ScoreMark>();
        var tiePairCount = 0;
        foreach (var part in parsedParts)
        {
            var partNotes = ExpandNotes(part.Notes, performanceMeasures, warnings);
            expandedNotes.AddRange(MergeTiedNotes(partNotes, warnings, ref tiePairCount));
            expandedRests.AddRange(ExpandRests(part.Rests, performanceMeasures, warnings));
            expandedMarks.AddRange(ExpandMarks(part.Marks, performanceMeasures));
        }

        var notes = expandedNotes
            .OrderBy(note => note.OnsetBeats)
            .ThenBy(note => note.PartId, StringComparer.Ordinal)
            .ThenBy(note => note.StaffNumber)
            .ThenBy(note => note.MidiNoteNumber)
            .ToArray();
        var rests = expandedRests
            .OrderBy(rest => rest.OnsetBeats)
            .ThenBy(rest => rest.PartId, StringComparer.Ordinal)
            .ThenBy(rest => rest.StaffNumber)
            .ToArray();
        var marks = expandedMarks
            .OrderBy(mark => mark.OnsetBeats)
            .ThenBy(mark => mark.PartId, StringComparer.Ordinal)
            .ToArray();
        if ((long)notes.Length + rests.Length + marks.Length > MaxExpandedEvents)
            throw new InvalidDataException(
                $"The expanded performance exceeds the {MaxExpandedEvents:N0} event safety limit.");

        var tempoChanges = ExpandTempoChanges(
            canonicalPart.Measures,
            canonicalPart.TempoChanges,
            performanceMeasures);
        var meterChanges = ExpandMeterChanges(
            canonicalPart.Measures,
            canonicalPart.MeterChanges,
            performanceMeasures);

        ValidateExpandedPlan(performanceMeasures, notes, warnings);

        var firstPartElement = Children(root, "part").FirstOrDefault(element =>
            string.Equals((string?)element.Attribute("id"), canonicalPart.Id, StringComparison.OrdinalIgnoreCase))
            ?? Children(root, "part").FirstOrDefault();
        var firstMeasureElement = Children(firstPartElement, "measure").FirstOrDefault();
        var firstAttributes = Descendant(firstMeasureElement, "attributes");
        var keyElement = Descendant(firstAttributes, "key");
        var keyFifths = ParseInt(Value(Descendant(keyElement, "fifths"))) ?? 0;
        var keySignature = FormatKeySignature(keyFifths, Value(Descendant(keyElement, "mode")));

        var initialMeter = meterChanges.FirstOrDefault()
                           ?? new ScoreMeterChange(0, 4, 4, canonicalPart.Measures[0].Number, 0);
        var initialTempo = tempoChanges.FirstOrDefault()
                           ?? new ScoreTempoChange(0, 120, canonicalPart.Measures[0].Number, 0);

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
            ValidatedMusicXml = validatedMusicXml,
            ContentSha256 = Convert.ToHexString(SHA256.HashData(validatedMusicXml)).ToLowerInvariant(),
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
            Notes = notes,
            Rests = rests,
            Marks = marks,
            PerformanceMeasures = performanceMeasures,
            TempoChanges = tempoChanges,
            MeterChanges = meterChanges,
            ValidationWarnings = warnings,
            RepeatPairCount = repeatPairCount,
            TiePairCount = tiePairCount,
            SlurPairCount = slurPairCount
        };
    }

    private static void ValidateSemanticComplexity(XElement root)
    {
        var parts = Children(root, "part").ToArray();
        if (parts.Length > MaxParts)
            throw new InvalidDataException($"The MusicXML score exceeds the {MaxParts} part safety limit.");

        long totalEvents = 0;
        foreach (var part in parts)
        {
            var measures = Children(part, "measure").ToArray();
            if (measures.Length > MaxMeasuresPerPart)
                throw new InvalidDataException(
                    $"A MusicXML part exceeds the {MaxMeasuresPerPart:N0} measure safety limit.");

            totalEvents = checked(totalEvents + measures.Sum(measure =>
                measure.Elements().Count(element => element.Name.LocalName is
                    "note" or "backup" or "forward" or "direction" or "barline")));
            if (totalEvents > MaxScoreEvents)
                throw new InvalidDataException(
                    $"The MusicXML score exceeds the {MaxScoreEvents:N0} source event safety limit.");

            var voices = Descendants(part, "voice")
                .Select(Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .Take(MaxVoicesPerPart + 1)
                .Count();
            if (voices > MaxVoicesPerPart)
                throw new InvalidDataException(
                    $"A MusicXML part exceeds the {MaxVoicesPerPart} voice safety limit.");

            var staves = Descendants(part, "staff")
                .Select(Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .Take(MaxStavesPerPart + 1)
                .Count();
            if (staves > MaxStavesPerPart)
                throw new InvalidDataException(
                    $"A MusicXML part exceeds the {MaxStavesPerPart} staff safety limit.");
        }
    }
}
