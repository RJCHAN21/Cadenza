using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop.Services;

public sealed partial class MusicXmlImporter
{
    private static void ResolveEndingPasses(
        IReadOnlyList<ParsedMeasure> measures,
        ICollection<ScoreValidationWarning> warnings)
    {
        var directivesByBoundary = new Dictionary<int, List<ParsedEndingDirective>>();
        for (var index = 0; index < measures.Count; index++)
        {
            foreach (var directive in measures[index].EndingDirectives)
            {
                var boundary = directive.Location == BarlineLocation.Left ? index : index + 1;
                if (directive.Location == BarlineLocation.Middle)
                {
                    warnings.Add(new ScoreValidationWarning(
                        "middle-ending-barline",
                        $"Measure {measures[index].Number} uses a middle-barline ending directive. Assessment is disabled for this measure.",
                        MeasureNumberOf(measures[index].Number, index + 1),
                        MeasureNumberOf(measures[index].Number, index + 1),
                        true));
                    boundary = index + 1;
                }

                directivesByBoundary.GetOrAdd(boundary).Add(directive);
            }
        }

        var active = new HashSet<int>();
        for (var boundary = 0; boundary <= measures.Count; boundary++)
        {
            if (directivesByBoundary.TryGetValue(boundary, out var directives))
            {
                foreach (var directive in directives.Where(item => IsEndingStop(item.Type)))
                    active.Clear();

                foreach (var directive in directives.Where(item => IsEndingStart(item.Type)))
                {
                    var parsed = ParseEndingPasses(directive.Number);
                    if (parsed.Count == 0)
                    {
                        var measureIndex = Math.Clamp(boundary, 0, Math.Max(0, measures.Count - 1));
                        warnings.Add(new ScoreValidationWarning(
                            "volta-ending",
                            $"Ending near measure {measures[measureIndex].Number} has unsupported number \"{directive.Number}\".",
                            MeasureNumberOf(measures[measureIndex].Number, measureIndex + 1),
                            MeasureNumberOf(measures[measureIndex].Number, measureIndex + 1),
                            true));
                    }
                    else
                    {
                        active = parsed;
                    }
                }
            }

            if (boundary < measures.Count)
                measures[boundary].EndingPasses = active.Order().ToArray();
        }

        if (active.Count > 0)
        {
            var last = measures[^1];
            warnings.Add(new ScoreValidationWarning(
                "volta-ending",
                "An alternate ending was not closed before the end of the score.",
                MeasureNumberOf(last.Number, measures.Count),
                MeasureNumberOf(last.Number, measures.Count),
                true));
        }
    }

    private static bool IsEndingStart(string value) =>
        string.Equals(value, "start", StringComparison.OrdinalIgnoreCase);

    private static bool IsEndingStop(string value) =>
        string.Equals(value, "stop", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "discontinue", StringComparison.OrdinalIgnoreCase);

    private static HashSet<int> ParseEndingPasses(string value)
    {
        var result = new HashSet<int>();
        foreach (var token in value.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var range = token.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (range.Length == 1 &&
                int.TryParse(range[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pass) &&
                pass is > 0 and <= MaxRepeatTimes)
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
        foreach (var part in parts.Where(part => !ReferenceEquals(part, canonical)))
        {
            if (part.Measures.Count != canonical.Measures.Count)
            {
                warnings.Add(new ScoreValidationWarning(
                    "part-measure-count",
                    $"Part {part.Name} contains {part.Measures.Count} measures while navigation part {canonical.Name} contains {canonical.Measures.Count}.",
                    1,
                    Math.Max(part.Measures.Count, canonical.Measures.Count),
                    true));
            }

            var shared = Math.Min(part.Measures.Count, canonical.Measures.Count);
            for (var index = 0; index < shared; index++)
            {
                var expected = canonical.Measures[index];
                var actual = part.Measures[index];
                if (Math.Abs(expected.DurationBeats - actual.DurationBeats) > 0.01)
                {
                    warnings.Add(new ScoreValidationWarning(
                        "part-duration",
                        $"Part {part.Name} disagrees on the duration of measure {actual.Number}.",
                        MeasureNumberOf(actual.Number, index + 1),
                        MeasureNumberOf(actual.Number, index + 1),
                        true));
                }

                if (!expected.NavigationSignature.SequenceEqual(actual.NavigationSignature))
                {
                    warnings.Add(new ScoreValidationWarning(
                        "part-navigation",
                        $"Part {part.Name} disagrees with navigation part {canonical.Name} at measure {actual.Number}.",
                        MeasureNumberOf(actual.Number, index + 1),
                        MeasureNumberOf(actual.Number, index + 1),
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
        var sectionsByStart = sections
            .GroupBy(section => section.StartIndex)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(section => section.ExitIndex - section.StartIndex).ToArray());
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

            if (endingOwner[sourceIndex] is { } ownerId)
            {
                if (!activePasses.TryGetValue(ownerId, out var pass) ||
                    !measures[sourceIndex].EndingPasses.Contains(pass))
                    return;
            }

            var activeSection = activePasses.Keys
                .Select(id => sectionById[id])
                .Where(section => sourceIndex >= section.StartIndex && sourceIndex <= section.ExitIndex)
                .OrderBy(section => section.ExitIndex - section.StartIndex)
                .FirstOrDefault();
            var measure = measures[sourceIndex];
            occurrences.Add(new ScoreMeasureOccurrence(
                occurrences.Count,
                sourceIndex,
                measure.Number,
                measure.Summary.StartBeat,
                performanceBeat,
                measure.DurationBeats,
                activeSection is null ? 1 : activePasses[activeSection.Id],
                activeSection?.Id ?? -1));
            performanceBeat += measure.DurationBeats;
        }

        void ExpandRange(int startIndex, int endIndex, int suppressedSectionId = -1)
        {
            var index = startIndex;
            while (index <= endIndex && index < measures.Count)
            {
                var section = sectionsByStart.GetValueOrDefault(index, [])
                    .FirstOrDefault(candidate =>
                        candidate.Id != suppressedSectionId && candidate.ExitIndex <= endIndex);
                if (section is not null)
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

        if (occurrences.Count == 0)
            throw new InvalidDataException("The score produced no playable measure occurrences.");
        return occurrences;
    }

    private static IReadOnlyList<RepeatSection> BuildRepeatSections(
        IReadOnlyList<ParsedMeasure> measures,
        ICollection<ScoreValidationWarning> warnings)
    {
        var startsByBoundary = new Dictionary<int, int>();
        var endsByBoundary = new Dictionary<int, List<(int Times, int SourceMeasureIndex)>>();

        for (var index = 0; index < measures.Count; index++)
        {
            foreach (var directive in measures[index].RepeatDirectives)
            {
                var boundary = directive.Location == BarlineLocation.Left ? index : index + 1;
                if (directive.Location == BarlineLocation.Middle)
                {
                    warnings.Add(new ScoreValidationWarning(
                        "middle-repeat-barline",
                        $"Measure {measures[index].Number} uses a middle repeat barline. Assessment is disabled for this measure.",
                        MeasureNumberOf(measures[index].Number, index + 1),
                        MeasureNumberOf(measures[index].Number, index + 1),
                        true));
                    boundary = index + 1;
                }

                if (string.Equals(directive.Direction, "forward", StringComparison.OrdinalIgnoreCase))
                    startsByBoundary[boundary] = index;
                else if (string.Equals(directive.Direction, "backward", StringComparison.OrdinalIgnoreCase))
                    endsByBoundary.GetOrAdd(boundary).Add((directive.Times, index));
            }
        }

        var sections = new List<RepeatSection>();
        var startStack = new Stack<int>();
        var id = 0;
        for (var boundary = 0; boundary <= measures.Count; boundary++)
        {
            if (endsByBoundary.TryGetValue(boundary, out var endings))
            {
                foreach (var ending in endings)
                {
                    var startBoundary = startStack.Count > 0 ? startStack.Pop() : 0;
                    var endBoundary = boundary;
                    if (endBoundary <= startBoundary)
                    {
                        warnings.Add(new ScoreValidationWarning(
                            "invalid-repeat-range",
                            $"Repeat near measure {measures[ending.SourceMeasureIndex].Number} has an empty or reversed range.",
                            MeasureNumberOf(measures[ending.SourceMeasureIndex].Number, ending.SourceMeasureIndex + 1),
                            MeasureNumberOf(measures[ending.SourceMeasureIndex].Number, ending.SourceMeasureIndex + 1),
                            true));
                        continue;
                    }

                    var totalPasses = ending.Times is >= 1 and <= MaxRepeatTimes ? ending.Times : 2;
                    if (ending.Times is < 1 or > MaxRepeatTimes)
                    {
                        warnings.Add(new ScoreValidationWarning(
                            "repeat-times",
                            $"Repeat at measure {measures[ending.SourceMeasureIndex].Number} uses unsupported times=\"{ending.Times}\"; two passes are used.",
                            MeasureNumberOf(measures[startBoundary].Number, startBoundary + 1),
                            MeasureNumberOf(measures[ending.SourceMeasureIndex].Number, ending.SourceMeasureIndex + 1),
                            true));
                    }

                    var repeatEndIndex = endBoundary - 1;
                    var exitIndex = repeatEndIndex;
                    while (exitIndex + 1 < measures.Count && measures[exitIndex + 1].EndingPasses.Count > 0)
                        exitIndex++;
                    sections.Add(new RepeatSection(
                        id++,
                        startBoundary,
                        repeatEndIndex,
                        exitIndex,
                        totalPasses));
                }
            }

            if (startsByBoundary.ContainsKey(boundary))
                startStack.Push(boundary);
        }

        foreach (var unmatchedBoundary in startStack)
        {
            var index = Math.Clamp(unmatchedBoundary, 0, measures.Count - 1);
            warnings.Add(new ScoreValidationWarning(
                "unmatched-repeat-start",
                $"A forward repeat near measure {measures[index].Number} has no backward repeat.",
                MeasureNumberOf(measures[index].Number, index + 1),
                MeasureNumberOf(measures[index].Number, index + 1),
                true));
        }

        return sections
            .OrderBy(section => section.StartIndex)
            .ThenBy(section => section.ExitIndex)
            .ToArray();
    }
}
