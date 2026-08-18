using System.Globalization;
using System.IO;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop.Services;

public sealed partial class MusicXmlImporter
{
    private static void ResolveEndingPasses(
        IReadOnlyList<ParsedMeasure> measures,
        ICollection<ScoreValidationWarning> warnings)
    {
        var analysis = AnalyzeNavigation(measures, warnings, emitWarnings: true);
        for (var index = 0; index < measures.Count; index++)
        {
            measures[index].EndingPasses = analysis.EndingByMeasure[index] is { } region
                ? region.Passes.Order().ToArray()
                : [];
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
                    true,
                    true,
                    ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
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
                        true,
                        true,
                        ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
                }

                if (!expected.NavigationSignature.SequenceEqual(actual.NavigationSignature))
                {
                    warnings.Add(new ScoreValidationWarning(
                        "part-navigation",
                        $"Part {part.Name} disagrees with navigation part {canonical.Name} at measure {actual.Number}.",
                        MeasureNumberOf(actual.Number, index + 1),
                        MeasureNumberOf(actual.Number, index + 1),
                        true,
                        true,
                        ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
                }
            }
        }
    }

    private static IReadOnlyList<ScoreMeasureOccurrence> BuildPerformanceOccurrences(
        IReadOnlyList<ParsedMeasure> measures,
        ICollection<ScoreValidationWarning> warnings,
        out int repeatPairCount)
    {
        var analysis = AnalyzeNavigation(measures, warnings, emitWarnings: false);
        repeatPairCount = analysis.Sections.Count;

        var sectionsByStart = analysis.Sections
            .GroupBy(section => section.StartBoundary)
            .ToDictionary(group => group.Key, group => group.OrderBy(section => section.EndBoundary).ToArray());
        var sectionsByRepeatEnd = analysis.Sections
            .GroupBy(section => section.EndBoundary - 1)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(section => section.StartBoundary).ToArray());
        var sectionById = analysis.Sections.ToDictionary(section => section.Id);

        var active = new Dictionary<int, RepeatRuntimeV2>();
        var completed = new HashSet<int>();
        var reportedMissingOwners = new HashSet<int>();
        var occurrences = new List<ScoreMeasureOccurrence>();
        var performanceBeat = 0d;
        var sourceIndex = 0;
        var transitionCount = 0;
        var transitionLimit = Math.Max(MaxPerformanceOccurrences * 8, Math.Max(1, measures.Count) * 64);

        while (sourceIndex < measures.Count)
        {
            transitionCount++;
            if (transitionCount > transitionLimit)
                throw new InvalidDataException("Repeat navigation exceeded the safe transition limit.");

            foreach (var pair in active.ToArray())
            {
                var section = sectionById[pair.Key];
                if (pair.Value.BodyComplete && sourceIndex >= section.ExitBoundary)
                {
                    active.Remove(pair.Key);
                    completed.Add(pair.Key);
                }
            }

            if (sectionsByStart.TryGetValue(sourceIndex, out var startingSections))
            {
                foreach (var section in startingSections)
                {
                    if (!active.ContainsKey(section.Id) && !completed.Contains(section.Id))
                        active.Add(section.Id, new RepeatRuntimeV2());
                }
            }

            var ending = analysis.EndingByMeasure[sourceIndex];
            if (ending is not null && active.TryGetValue(ending.OwnerSectionId, out var ownerRuntime))
            {
                if (!ending.Passes.Contains(ownerRuntime.Pass))
                {
                    var ownerSection = sectionById[ending.OwnerSectionId];
                    if (ownerRuntime.Pass >= ownerSection.TotalPasses &&
                        ending.EndBoundary >= ownerSection.EndBoundary)
                    {
                        ownerRuntime.BodyComplete = true;
                    }

                    sourceIndex = ending.EndBoundary;
                    continue;
                }
            }
            else if (ending is not null && !completed.Contains(ending.OwnerSectionId))
            {
                if (reportedMissingOwners.Add(ending.OwnerSectionId))
                {
                    var measureNumber = MeasureNumberOf(measures[sourceIndex].Number, sourceIndex + 1);
                    warnings.Add(new ScoreValidationWarning(
                        "volta-owner-state",
                        $"The ending beginning near measure {measures[sourceIndex].Number} could not be matched to an active repeat pass. Playback and assessment are disabled for this ending.",
                        measureNumber,
                        measureNumber,
                        true,
                        true,
                        ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
                }

                if (!ending.Passes.Contains(1))
                {
                    sourceIndex = ending.EndBoundary;
                    continue;
                }
            }

            if (occurrences.Count >= MaxPerformanceOccurrences)
                throw new InvalidDataException("Repeat expansion exceeded the safe performance-occurrence limit.");

            NavigationRepeatSectionV2? labelSection = null;
            if (ending is not null && active.ContainsKey(ending.OwnerSectionId))
            {
                labelSection = sectionById[ending.OwnerSectionId];
            }
            else
            {
                labelSection = active.Keys
                    .Select(id => sectionById[id])
                    .Where(section =>
                        sourceIndex >= section.StartBoundary &&
                        sourceIndex < section.ExitBoundary)
                    .OrderByDescending(section => section.StartBoundary)
                    .ThenBy(section => section.ExitBoundary)
                    .FirstOrDefault();
            }

            var measure = measures[sourceIndex];
            var repeatPass = labelSection is null ? 1 : active[labelSection.Id].Pass;
            occurrences.Add(new ScoreMeasureOccurrence(
                occurrences.Count,
                sourceIndex,
                measure.Number,
                measure.Summary.StartBeat,
                performanceBeat,
                measure.DurationBeats,
                repeatPass,
                labelSection?.Id ?? -1));
            performanceBeat += measure.DurationBeats;

            var jumped = false;
            if (sectionsByRepeatEnd.TryGetValue(sourceIndex, out var endingSections))
            {
                foreach (var section in endingSections)
                {
                    if (!active.TryGetValue(section.Id, out var runtime) || runtime.BodyComplete)
                        continue;

                    if (runtime.Pass < section.TotalPasses)
                    {
                        runtime.Pass++;
                        sourceIndex = section.StartBoundary;
                        jumped = true;
                        break;
                    }

                    runtime.BodyComplete = true;
                }
            }

            if (!jumped)
                sourceIndex++;
        }

        if (occurrences.Count == 0)
            throw new InvalidDataException("The score produced no playable measure occurrences.");

        return occurrences;
    }

    private static NavigationAnalysisV2 AnalyzeNavigation(
        IReadOnlyList<ParsedMeasure> measures,
        ICollection<ScoreValidationWarning> warnings,
        bool emitWarnings)
    {
        var sections = BuildRepeatSectionsV2(measures, warnings, emitWarnings);
        var endingRegions = BuildEndingRegionsV2(measures, sections, warnings, emitWarnings);
        var endingByMeasure = new EndingRegionV2?[measures.Count];

        foreach (var region in endingRegions)
        {
            for (var index = region.StartBoundary; index < region.EndBoundary && index < measures.Count; index++)
            {
                if (endingByMeasure[index] is not null)
                {
                    if (emitWarnings)
                    {
                        var measureNumber = MeasureNumberOf(measures[index].Number, index + 1);
                        warnings.Add(new ScoreValidationWarning(
                            "overlapping-endings",
                            $"Measure {measures[index].Number} belongs to overlapping alternate endings. Playback and assessment are disabled for this measure.",
                            measureNumber,
                            measureNumber,
                            true,
                            true,
                            ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
                    }
                    continue;
                }

                endingByMeasure[index] = region;
            }
        }

        return new NavigationAnalysisV2(sections, endingRegions, endingByMeasure);
    }

    private static IReadOnlyList<NavigationRepeatSectionV2> BuildRepeatSectionsV2(
        IReadOnlyList<ParsedMeasure> measures,
        ICollection<ScoreValidationWarning> warnings,
        bool emitWarnings)
    {
        var startsByBoundary = new Dictionary<int, List<int>>();
        var endsByBoundary = new Dictionary<int, List<(int Times, int SourceMeasureIndex)>>();

        for (var index = 0; index < measures.Count; index++)
        {
            foreach (var directive in measures[index].RepeatDirectives)
            {
                var boundary = BoundaryForV2(
                    directive.Location,
                    index,
                    measures,
                    warnings,
                    emitWarnings,
                    "repeat");

                if (string.Equals(directive.Direction, "forward", StringComparison.OrdinalIgnoreCase))
                    startsByBoundary.GetOrAdd(boundary).Add(index);
                else if (string.Equals(directive.Direction, "backward", StringComparison.OrdinalIgnoreCase))
                    endsByBoundary.GetOrAdd(boundary).Add((directive.Times, index));
            }
        }

        var sections = new List<NavigationRepeatSectionV2>();
        var startStack = new Stack<int>();
        for (var boundary = 0; boundary <= measures.Count; boundary++)
        {
            if (endsByBoundary.TryGetValue(boundary, out var endings))
            {
                foreach (var ending in endings)
                {
                    var startBoundary = startStack.Count > 0 ? startStack.Pop() : 0;
                    if (boundary <= startBoundary)
                    {
                        if (emitWarnings)
                        {
                            var measureNumber = MeasureNumberOf(
                                measures[ending.SourceMeasureIndex].Number,
                                ending.SourceMeasureIndex + 1);
                            warnings.Add(new ScoreValidationWarning(
                                "invalid-repeat-range",
                                $"Repeat near measure {measures[ending.SourceMeasureIndex].Number} has an empty or reversed range.",
                                measureNumber,
                                measureNumber,
                                true,
                                true,
                                ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
                        }
                        continue;
                    }

                    var totalPasses = ending.Times is >= 1 and <= MaxRepeatTimes ? ending.Times : 2;
                    if (emitWarnings && ending.Times is < 1 or > MaxRepeatTimes)
                    {
                        warnings.Add(new ScoreValidationWarning(
                            "repeat-times",
                            $"Repeat at measure {measures[ending.SourceMeasureIndex].Number} uses unsupported times=\"{ending.Times}\". The two-pass visual fallback is not available for playback or assessment.",
                            MeasureNumberOf(measures[startBoundary].Number, startBoundary + 1),
                            MeasureNumberOf(measures[ending.SourceMeasureIndex].Number, ending.SourceMeasureIndex + 1),
                            true,
                            true,
                            ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
                    }

                    sections.Add(new NavigationRepeatSectionV2(
                        sections.Count,
                        startBoundary,
                        boundary,
                        totalPasses));
                }
            }

            if (startsByBoundary.TryGetValue(boundary, out var starts))
            {
                foreach (var _ in starts)
                    startStack.Push(boundary);
            }
        }

        if (emitWarnings)
        {
            foreach (var unmatchedBoundary in startStack)
            {
                var index = Math.Clamp(unmatchedBoundary, 0, measures.Count - 1);
                warnings.Add(new ScoreValidationWarning(
                    "unmatched-repeat-start",
                    $"A forward repeat near measure {measures[index].Number} has no backward repeat.",
                    MeasureNumberOf(measures[index].Number, index + 1),
                    MeasureNumberOf(measures[index].Number, index + 1),
                    true,
                    true,
                    ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
            }
        }

        return sections
            .OrderBy(section => section.StartBoundary)
            .ThenBy(section => section.EndBoundary)
            .ToArray();
    }

    private static IReadOnlyList<EndingRegionV2> BuildEndingRegionsV2(
        IReadOnlyList<ParsedMeasure> measures,
        IReadOnlyList<NavigationRepeatSectionV2> sections,
        ICollection<ScoreValidationWarning> warnings,
        bool emitWarnings)
    {
        var directivesByBoundary = new Dictionary<int, List<EndingBoundaryDirectiveV2>>();
        for (var index = 0; index < measures.Count; index++)
        {
            foreach (var directive in measures[index].EndingDirectives)
            {
                var boundary = BoundaryForV2(
                    directive.Location,
                    index,
                    measures,
                    warnings,
                    emitWarnings,
                    "ending");
                directivesByBoundary.GetOrAdd(boundary).Add(
                    new EndingBoundaryDirectiveV2(index, directive));
            }
        }

        var regions = new List<EndingRegionV2>();
        PendingEndingV2? active = null;
        for (var boundary = 0; boundary <= measures.Count; boundary++)
        {
            if (!directivesByBoundary.TryGetValue(boundary, out var directives))
                continue;

            foreach (var item in directives.Where(item => IsEndingTerminalV2(item.Directive.Type)))
            {
                if (active is null)
                {
                    if (emitWarnings)
                    {
                        var measureNumber = MeasureNumberOf(
                            measures[item.SourceMeasureIndex].Number,
                            item.SourceMeasureIndex + 1);
                        warnings.Add(new ScoreValidationWarning(
                            "unmatched-ending-stop",
                            $"An ending closes near measure {measures[item.SourceMeasureIndex].Number} without a matching start.",
                            measureNumber,
                            measureNumber,
                            true,
                            true,
                            ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
                    }
                    continue;
                }

                var owner = FindEndingOwnerV2(sections, active.StartBoundary);
                var semanticEnd = Math.Max(active.StartBoundary + 1, boundary);
                if (owner is not null &&
                    string.Equals(item.Directive.Type, "discontinue", StringComparison.OrdinalIgnoreCase) &&
                    active.Passes.Contains(1) &&
                    active.StartBoundary < owner.EndBoundary &&
                    semanticEnd < owner.EndBoundary &&
                    !HasEndingStartBetweenV2(directivesByBoundary, semanticEnd, owner.EndBoundary))
                {
                    semanticEnd = owner.EndBoundary;
                }

                if (owner is null)
                {
                    if (emitWarnings)
                    {
                        var startIndex = Math.Clamp(active.StartBoundary, 0, measures.Count - 1);
                        var measureNumber = MeasureNumberOf(measures[startIndex].Number, startIndex + 1);
                        warnings.Add(new ScoreValidationWarning(
                            "unowned-ending",
                            $"Ending {active.Number} near measure {measures[startIndex].Number} is not associated with a repeat section.",
                            measureNumber,
                            measureNumber,
                            true,
                            true,
                            ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
                    }
                }
                else
                {
                    var region = new EndingRegionV2(
                        regions.Count,
                        owner.Id,
                        active.StartBoundary,
                        Math.Min(semanticEnd, measures.Count),
                        active.Passes,
                        active.Number);
                    regions.Add(region);
                    owner.ExitBoundary = Math.Max(owner.ExitBoundary, region.EndBoundary);
                }

                active = null;
            }

            foreach (var item in directives.Where(item =>
                         string.Equals(item.Directive.Type, "start", StringComparison.OrdinalIgnoreCase)))
            {
                if (active is not null && emitWarnings)
                {
                    var previousIndex = Math.Clamp(active.StartBoundary, 0, measures.Count - 1);
                    warnings.Add(new ScoreValidationWarning(
                        "overlapping-ending-start",
                        $"Ending {active.Number} near measure {measures[previousIndex].Number} was not closed before another ending started.",
                        MeasureNumberOf(measures[previousIndex].Number, previousIndex + 1),
                        MeasureNumberOf(measures[item.SourceMeasureIndex].Number, item.SourceMeasureIndex + 1),
                        true,
                        true,
                        ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
                }

                var passes = ParseEndingPasses(item.Directive.Number);
                if (passes.Count == 0)
                {
                    if (emitWarnings)
                    {
                        var measureNumber = MeasureNumberOf(
                            measures[item.SourceMeasureIndex].Number,
                            item.SourceMeasureIndex + 1);
                        warnings.Add(new ScoreValidationWarning(
                            "volta-ending",
                            $"Ending near measure {measures[item.SourceMeasureIndex].Number} has unsupported number \"{item.Directive.Number}\".",
                            measureNumber,
                            measureNumber,
                            true,
                            true,
                            ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
                    }
                    active = null;
                    continue;
                }

                active = new PendingEndingV2(boundary, passes, item.Directive.Number);
            }
        }

        if (active is not null)
        {
            var owner = FindEndingOwnerV2(sections, active.StartBoundary);
            if (owner is not null)
            {
                var endBoundary = active.StartBoundary < owner.EndBoundary
                    ? owner.EndBoundary
                    : measures.Count;
                var region = new EndingRegionV2(
                    regions.Count,
                    owner.Id,
                    active.StartBoundary,
                    endBoundary,
                    active.Passes,
                    active.Number);
                regions.Add(region);
                owner.ExitBoundary = Math.Max(owner.ExitBoundary, region.EndBoundary);
            }

            if (emitWarnings)
            {
                var startIndex = Math.Clamp(active.StartBoundary, 0, measures.Count - 1);
                warnings.Add(new ScoreValidationWarning(
                    "unclosed-ending",
                    $"Ending {active.Number} near measure {measures[startIndex].Number} was not explicitly closed.",
                    MeasureNumberOf(measures[startIndex].Number, startIndex + 1),
                    MeasureNumberOf(measures[^1].Number, measures.Count),
                    true,
                    true,
                    ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
            }
        }

        return regions.OrderBy(region => region.StartBoundary).ToArray();
    }

    private static NavigationRepeatSectionV2? FindEndingOwnerV2(
        IReadOnlyList<NavigationRepeatSectionV2> sections,
        int endingStartBoundary)
    {
        var preceding = sections
            .Where(section =>
                section.StartBoundary < endingStartBoundary &&
                endingStartBoundary <= section.EndBoundary)
            .OrderByDescending(section => section.StartBoundary)
            .ThenBy(section => section.EndBoundary - section.StartBoundary)
            .FirstOrDefault();
        if (preceding is not null)
            return preceding;

        return sections
            .Where(section => section.StartBoundary == endingStartBoundary)
            .OrderBy(section => section.EndBoundary - section.StartBoundary)
            .FirstOrDefault();
    }

    private static bool HasEndingStartBetweenV2(
        IReadOnlyDictionary<int, List<EndingBoundaryDirectiveV2>> directivesByBoundary,
        int startBoundary,
        int endBoundary)
    {
        for (var boundary = startBoundary + 1; boundary < endBoundary; boundary++)
        {
            if (directivesByBoundary.TryGetValue(boundary, out var directives) &&
                directives.Any(item =>
                    string.Equals(item.Directive.Type, "start", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    private static bool IsEndingTerminalV2(string value) =>
        string.Equals(value, "stop", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "discontinue", StringComparison.OrdinalIgnoreCase);

    private static int BoundaryForV2(
        BarlineLocation location,
        int sourceMeasureIndex,
        IReadOnlyList<ParsedMeasure> measures,
        ICollection<ScoreValidationWarning> warnings,
        bool emitWarnings,
        string directiveKind)
    {
        if (location == BarlineLocation.Left)
            return sourceMeasureIndex;
        if (location != BarlineLocation.Middle)
            return sourceMeasureIndex + 1;

        if (emitWarnings)
        {
            var measureNumber = MeasureNumberOf(
                measures[sourceMeasureIndex].Number,
                sourceMeasureIndex + 1);
            warnings.Add(new ScoreValidationWarning(
                $"middle-{directiveKind}-barline",
                $"Measure {measures[sourceMeasureIndex].Number} uses a middle-barline {directiveKind} directive. Playback and assessment are disabled for this measure.",
                measureNumber,
                measureNumber,
                true,
                true,
                ScoreCapabilityDisposition.BlocksPlaybackAndAssessment));
        }

        return sourceMeasureIndex + 1;
    }

    private sealed class NavigationRepeatSectionV2
    {
        public NavigationRepeatSectionV2(
            int id,
            int startBoundary,
            int endBoundary,
            int totalPasses)
        {
            Id = id;
            StartBoundary = startBoundary;
            EndBoundary = endBoundary;
            ExitBoundary = endBoundary;
            TotalPasses = totalPasses;
        }

        public int Id { get; }
        public int StartBoundary { get; }
        public int EndBoundary { get; }
        public int ExitBoundary { get; set; }
        public int TotalPasses { get; }
    }

    private sealed record EndingRegionV2(
        int Id,
        int OwnerSectionId,
        int StartBoundary,
        int EndBoundary,
        HashSet<int> Passes,
        string Number);

    private sealed record PendingEndingV2(
        int StartBoundary,
        HashSet<int> Passes,
        string Number);

    private sealed record EndingBoundaryDirectiveV2(
        int SourceMeasureIndex,
        ParsedEndingDirective Directive);

    private sealed record NavigationAnalysisV2(
        IReadOnlyList<NavigationRepeatSectionV2> Sections,
        IReadOnlyList<EndingRegionV2> Endings,
        IReadOnlyList<EndingRegionV2?> EndingByMeasure);

    private sealed class RepeatRuntimeV2
    {
        public int Pass { get; set; } = 1;
        public bool BodyComplete { get; set; }
    }
}
