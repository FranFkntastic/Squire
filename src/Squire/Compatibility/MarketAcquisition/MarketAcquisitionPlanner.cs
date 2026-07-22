using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionPlanner
{
    public static MarketAcquisitionPlan BuildPlan(
        MarketAcquisitionRequestView request,
        IEnumerable<MarketAcquisitionListing> listings,
        DateTimeOffset preparedAtUtc,
        string? currentWorld = null,
        IEnumerable<MarketAcquisitionSweepWorldExclusion>? sweepWorldExclusions = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(listings);
        ValidateRequest(request);

        var requestLines = BuildRequestLines(request);
        var sourceListings = listings.ToList();
        var sweepExclusions = sweepWorldExclusions?.ToArray() ?? [];
        var selectedSubtasks = new List<MarketAcquisitionWorldItemSubtask>();
        var planLines = new List<MarketAcquisitionPlanLine>();
        var isAllWorldSweep = request.WorldMode.Equals("AllWorldSweep", StringComparison.OrdinalIgnoreCase);
        var sweepWorlds = isAllWorldSweep
            ? ResolveSweepWorlds(request, currentWorld)
            : [];

        foreach (var line in requestLines)
        {
            var matchingListings = sourceListings
                .Where(listing => ListingMatchesLine(request, line, listing))
                .OrderBy(listing => listing.UnitPrice)
                .ThenByDescending(listing => listing.Quantity)
                .ThenBy(listing => listing.LastReviewTimeUtc)
                .ThenBy(listing => listing.WorldName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(listing => listing.RetainerName, StringComparer.OrdinalIgnoreCase)
                .GroupBy(listing => listing.WorldName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.AsEnumerable(),
                    StringComparer.OrdinalIgnoreCase);

            var lineSweepWorlds = isAllWorldSweep
                ? sweepWorlds
                    .Where(world => !IsSweepWorldExcluded(line, world, sweepExclusions))
                    .ToArray()
                : [];

            var candidates = isAllWorldSweep
                ? lineSweepWorlds
                    .Select(world =>
                    {
                        var hasListings = matchingListings.TryGetValue(world, out var worldListings);
                        return BuildWorldSubtask(
                            line,
                            world,
                            hasListings ? worldListings! : [],
                            hasListings ? "Planned" : "SweepProbe");
                    })
                    .ToList()
                : matchingListings
                    .Select(group => BuildWorldSubtask(line, group.Key, group.Value))
                    .Where(subtask => subtask.Listings.Count > 0)
                .OrderByDescending(subtask => LineSatisfiesQuantity(line, subtask.PlannedQuantity))
                .ThenBy(subtask => subtask.ExceedsRequestedQuantity)
                .ThenByDescending(subtask => subtask.PlannedQuantity)
                .ThenBy(subtask => subtask.PlannedGil)
                .ThenBy(subtask => subtask.Listings.Count == 0 ? uint.MaxValue : subtask.Listings[0].UnitPrice)
                .ThenBy(subtask => subtask.WorldName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var lineSubtasks = BuildExecutableLinePlan(line, candidates, isAllWorldSweep);
            selectedSubtasks.AddRange(lineSubtasks);
            planLines.Add(new MarketAcquisitionPlanLine
            {
                LineId = line.LineId,
                Ordinal = line.Ordinal,
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                QuantityMode = line.QuantityMode,
                RequestedQuantity = line.Quantity,
                HqPolicy = line.HqPolicy,
                MaxUnitPrice = line.MaxUnitPrice,
                GilCap = line.MaxTotalGil,
                Status = lineSubtasks.Count == 0 ? "NoSupportedListings" : "Ready",
                PlannedQuantity = (uint)lineSubtasks.Sum(subtask => subtask.PlannedQuantity),
                PlannedGil = (uint)lineSubtasks.Sum(subtask => subtask.PlannedGil),
            });
        }

        var batches = RouteSortBatches(
            selectedSubtasks
                .GroupBy(subtask => subtask.WorldName, StringComparer.OrdinalIgnoreCase)
                .Select(group => BuildWorldBatch(group.Key, group))
                .ToList(),
            currentWorld);

        var primaryLine = requestLines[0];
        var totalQuantity = (uint)batches.Sum(batch => batch.PlannedQuantity);
        var totalGil = (uint)batches.Sum(batch => batch.PlannedGil);
        var diagnostics = BuildDiagnostics(request, requestLines, sourceListings, sweepWorlds, sweepExclusions, selectedSubtasks);

        return new MarketAcquisitionPlan
        {
            RequestId = request.Id,
            Status = batches.Count == 0 ? "NoSupportedListings" : "Ready",
            WorldMode = request.WorldMode,
            ItemId = primaryLine.ItemId,
            RequestedQuantity = (uint)requestLines.Sum(line => line.Quantity),
            PlannedQuantity = totalQuantity,
            PlannedGil = totalGil,
            PreparedAtUtc = preparedAtUtc,
            Diagnostics = diagnostics with
            {
                PlannedListingCount = batches.Sum(batch => batch.Listings.Count),
            },
            Lines = planLines,
            WorldBatches = batches,
        };
    }

    private static IReadOnlyList<PlannerLine> BuildRequestLines(MarketAcquisitionRequestView request)
    {
        var lines = request.Lines.Count == 0
            ? new[]
            {
                new MarketAcquisitionBatchLineView
                {
                    LineId = request.Id,
                    Ordinal = 0,
                    ItemId = request.ItemId,
                    ItemName = request.ItemName,
                    QuantityMode = request.QuantityMode,
                    TargetQuantity = request.Quantity,
                    MaxQuantity = request.Quantity,
                    HqPolicy = request.HqPolicy,
                    MaxUnitPrice = request.MaxUnitPrice,
                    GilCap = request.MaxTotalGil,
                },
            }.ToList()
            : request.Lines
                .OrderBy(line => line.Ordinal)
                .ToList();

        if (lines.Count == 0)
            throw new InvalidOperationException("At least one acquisition line is required before planning.");

        return lines
            .Select(line => new PlannerLine
            {
                LineId = string.IsNullOrWhiteSpace(line.LineId) ? request.Id : line.LineId,
                Ordinal = line.Ordinal,
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                QuantityMode = NormalizeQuantityMode(line.QuantityMode),
                Quantity = ResolveLineQuantity(line),
                HqPolicy = MarketAcquisitionPolicy.NormalizeHqPolicy(line.HqPolicy),
                MaxUnitPrice = line.MaxUnitPrice,
                MaxTotalGil = line.GilCap,
            })
            .ToList();
    }

    private static uint ResolveLineQuantity(MarketAcquisitionBatchLineView line) =>
        line.QuantityMode.Equals("AllBelowThreshold", StringComparison.OrdinalIgnoreCase)
            ? line.MaxQuantity
            : line.TargetQuantity;

    private static MarketAcquisitionPlanDiagnostics BuildDiagnostics(
        MarketAcquisitionRequestView request,
        IReadOnlyList<PlannerLine> requestLines,
        IReadOnlyList<MarketAcquisitionListing> listings,
        IReadOnlyList<string> sweepWorlds,
        IReadOnlyCollection<MarketAcquisitionSweepWorldExclusion> sweepWorldExclusions,
        IReadOnlyList<MarketAcquisitionWorldItemSubtask> selectedSubtasks)
    {
        var lineItemIds = requestLines.Select(line => line.ItemId).ToHashSet();
        var relevantListings = listings
            .Where(listing => lineItemIds.Contains(listing.ItemId))
            .ToList();
        var nonZero = relevantListings
            .Where(listing => listing.Quantity != 0 && listing.UnitPrice != 0)
            .ToList();
        var priceSupported = nonZero
            .Where(listing => requestLines.Any(line => line.ItemId == listing.ItemId && listing.UnitPrice <= line.MaxUnitPrice))
            .ToList();
        var hqSupported = priceSupported
            .Where(listing => requestLines.Any(line => line.ItemId == listing.ItemId && MarketAcquisitionPolicy.HqMatches(line.HqPolicy, listing.IsHq)))
            .ToList();
        var worldSupported = hqSupported
            .Where(listing =>
                !request.WorldMode.Equals("CurrentWorldOnly", StringComparison.OrdinalIgnoreCase) ||
                listing.WorldName.Equals(request.TargetWorld, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new MarketAcquisitionPlanDiagnostics
        {
            SourceListingCount = listings.Count,
            NonZeroListingCount = nonZero.Count,
            PriceSupportedListingCount = priceSupported.Count,
            HqSupportedListingCount = hqSupported.Count,
            WorldSupportedListingCount = worldSupported.Count,
            ListingDecisions = BuildListingDecisions(request, requestLines, listings, worldSupported, sweepWorlds, sweepWorldExclusions, selectedSubtasks),
        };
    }

    private static IReadOnlyList<MarketAcquisitionListingDecision> BuildListingDecisions(
        MarketAcquisitionRequestView request,
        IReadOnlyList<PlannerLine> requestLines,
        IReadOnlyList<MarketAcquisitionListing> listings,
        IReadOnlyCollection<MarketAcquisitionListing> worldSupportedListings,
        IReadOnlyList<string> sweepWorlds,
        IReadOnlyCollection<MarketAcquisitionSweepWorldExclusion> sweepWorldExclusions,
        IReadOnlyList<MarketAcquisitionWorldItemSubtask> selectedSubtasks)
    {
        var decisions = new List<MarketAcquisitionListingDecision>();
        var worldSupportedLookup = worldSupportedListings
            .Select(listing => listing.ListingId)
            .Where(listingId => !string.IsNullOrWhiteSpace(listingId))
            .ToHashSet(StringComparer.Ordinal);
        var plannedListingIdsByLine = selectedSubtasks
            .SelectMany(subtask => subtask.Listings)
            .GroupBy(listing => listing.LineId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(listing => listing.ListingId)
                    .Where(listingId => !string.IsNullOrWhiteSpace(listingId))
                    .ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
        var plannedQuantityByLine = selectedSubtasks
            .GroupBy(subtask => subtask.LineId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (uint)group.Sum(subtask => subtask.PlannedQuantity),
                StringComparer.Ordinal);
        var plannedGilByLine = selectedSubtasks
            .GroupBy(subtask => subtask.LineId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (uint)group.Sum(subtask => subtask.PlannedGil),
                StringComparer.Ordinal);

        foreach (var listing in listings)
        {
            var matchingLines = requestLines
                .Where(line => line.ItemId == listing.ItemId)
                .ToList();
            if (matchingLines.Count == 0)
            {
                decisions.Add(CreateListingDecision(
                    new PlannerLine { ItemId = listing.ItemId, ItemName = listing.ItemName },
                    listing,
                    "RejectedWrongItem",
                    "Listing item id is not part of this acquisition batch."));
                continue;
            }

            foreach (var line in matchingLines)
            {
                decisions.Add(EvaluateListingDecision(
                    request,
                    line,
                    listing,
                    worldSupportedLookup,
                    plannedListingIdsByLine,
                    plannedQuantityByLine,
                    plannedGilByLine));
            }
        }

        if (request.WorldMode.Equals("AllWorldSweep", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in requestLines)
            {
                var plannedWorlds = listings
                    .Where(listing => listing.ItemId == line.ItemId)
                    .Where(listing => worldSupportedLookup.Contains(listing.ListingId))
                    .Select(listing => listing.WorldName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                decisions.AddRange(sweepWorlds
                    .Where(world => !IsSweepWorldExcluded(line, world, sweepWorldExclusions))
                    .Where(world => !plannedWorlds.Contains(world))
                    .Select(world => new MarketAcquisitionListingDecision
                    {
                        LineId = line.LineId,
                        ItemId = line.ItemId,
                        ItemName = line.ItemName,
                        WorldName = world,
                        Decision = "SweepProbeNoRemoteListing",
                        Reason = "Explicit all-world sweep will probe this world even though remote data has no supported listing.",
                    }));
            }
        }

        return decisions;
    }

    private static MarketAcquisitionListingDecision EvaluateListingDecision(
        MarketAcquisitionRequestView request,
        PlannerLine line,
        MarketAcquisitionListing listing,
        IReadOnlySet<string> worldSupportedListingIds,
        IReadOnlyDictionary<string, HashSet<string>> plannedListingIdsByLine,
        IReadOnlyDictionary<string, uint> plannedQuantityByLine,
        IReadOnlyDictionary<string, uint> plannedGilByLine)
    {
        if (listing.Quantity == 0 || listing.UnitPrice == 0)
        {
            return CreateListingDecision(
                line,
                listing,
                "RejectedZeroQuantityOrPrice",
                "Listing has zero quantity or zero unit price.");
        }

        if (listing.UnitPrice > line.MaxUnitPrice)
        {
            return CreateListingDecision(
                line,
                listing,
                "RejectedAboveMaxUnit",
                $"Listing unit price {listing.UnitPrice:N0} is above max unit {line.MaxUnitPrice:N0}.");
        }

        if (!MarketAcquisitionPolicy.HqMatches(line.HqPolicy, listing.IsHq))
        {
            return CreateListingDecision(
                line,
                listing,
                "RejectedHqPolicy",
                $"Listing quality does not satisfy {line.HqPolicy}.");
        }

        if (request.WorldMode.Equals("CurrentWorldOnly", StringComparison.OrdinalIgnoreCase) &&
            !listing.WorldName.Equals(request.TargetWorld, StringComparison.OrdinalIgnoreCase))
        {
            return CreateListingDecision(
                line,
                listing,
                "RejectedWrongWorldScope",
                $"Listing world {listing.WorldName} is outside current-world target {request.TargetWorld}.");
        }

        var isWorldSupported = string.IsNullOrWhiteSpace(listing.ListingId) ||
            worldSupportedListingIds.Contains(listing.ListingId);
        if (isWorldSupported &&
            plannedListingIdsByLine.TryGetValue(line.LineId, out var plannedListingIds) &&
            plannedListingIds.Contains(listing.ListingId))
        {
            return CreateListingDecision(
                line,
                listing,
                "AcceptedRemoteCandidate",
                "Remote listing satisfies item, price, quality, and world scope filters.");
        }

        if (isWorldSupported &&
            line.MaxTotalGil > 0 &&
            plannedGilByLine.TryGetValue(line.LineId, out var plannedGil) &&
            plannedGil + listing.TotalGil > line.MaxTotalGil)
        {
            return CreateListingDecision(
                line,
                listing,
                "RejectedGilCap",
                $"Listing would exceed the line gil cap of {line.MaxTotalGil:N0}.");
        }

        if (isWorldSupported &&
            !IsUnboundedAllBelowThreshold(line) &&
            plannedQuantityByLine.TryGetValue(line.LineId, out var plannedQuantity) &&
            plannedQuantity >= line.Quantity)
        {
            return CreateListingDecision(
                line,
                listing,
                "RejectedMaxQuantity",
                $"Line quantity cap of {line.Quantity:N0} was already satisfied by cheaper planned listings.");
        }

        if (isWorldSupported)
        {
            return CreateListingDecision(
                line,
                listing,
                "AcceptedRemoteCandidateNotPlanned",
                "Remote listing satisfies hard filters but was not selected for the current route plan.");
        }

        return CreateListingDecision(
            line,
            listing,
            "RejectedWrongWorldScope",
            "Listing is outside the selected route scope.");
    }

    private static MarketAcquisitionListingDecision CreateListingDecision(
        PlannerLine line,
        MarketAcquisitionListing listing,
        string decision,
        string reason) =>
        new()
        {
            LineId = line.LineId,
            ItemId = line.ItemId == 0 ? listing.ItemId : line.ItemId,
            ItemName = string.IsNullOrWhiteSpace(line.ItemName) ? listing.ItemName : line.ItemName,
            WorldName = listing.WorldName,
            ListingId = listing.ListingId,
            Decision = decision,
            Reason = reason,
            Quantity = listing.Quantity,
            UnitPrice = listing.UnitPrice,
            IsHq = listing.IsHq,
        };

    private static bool ListingMatchesLine(
        MarketAcquisitionRequestView request,
        PlannerLine line,
        MarketAcquisitionListing listing)
    {
        if (listing.Quantity == 0 || listing.UnitPrice == 0)
            return false;

        if (listing.ItemId != line.ItemId)
            return false;

        if (listing.UnitPrice > line.MaxUnitPrice)
            return false;

        if (!MarketAcquisitionPolicy.HqMatches(line.HqPolicy, listing.IsHq))
            return false;

        if (request.WorldMode.Equals("CurrentWorldOnly", StringComparison.OrdinalIgnoreCase) &&
            !listing.WorldName.Equals(request.TargetWorld, StringComparison.OrdinalIgnoreCase))
            return false;

        if (request.WorldMode.Equals("Selected", StringComparison.OrdinalIgnoreCase) &&
            !request.SelectedWorlds.Contains(listing.WorldName, StringComparer.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static bool IsSweepWorldExcluded(
        PlannerLine line,
        string worldName,
        IReadOnlyCollection<MarketAcquisitionSweepWorldExclusion> sweepWorldExclusions) =>
        sweepWorldExclusions.Any(exclusion =>
            exclusion.ItemId == line.ItemId &&
            exclusion.MaxUnitPrice == line.MaxUnitPrice &&
            exclusion.LineId.Equals(line.LineId, StringComparison.Ordinal) &&
            exclusion.WorldName.Equals(worldName, StringComparison.OrdinalIgnoreCase) &&
            MarketAcquisitionPolicy.NormalizeHqPolicy(exclusion.HqPolicy).Equals(line.HqPolicy, StringComparison.OrdinalIgnoreCase));

    private static void ValidateRequest(MarketAcquisitionRequestView request)
    {
        if (!string.IsNullOrWhiteSpace(request.HqPolicy))
            _ = MarketAcquisitionPolicy.NormalizeHqPolicy(request.HqPolicy);

        if (request.WorldMode is not ("Recommended" or "CurrentWorldOnly" or "Selected" or "AllWorldSweep"))
            throw new InvalidOperationException($"Unknown world mode {request.WorldMode}.");

        if (request.WorldMode == "Selected" && request.SelectedWorlds.Count == 0)
            throw new InvalidOperationException("Selected world mode requires selected worlds in the request payload.");
    }

    private static IReadOnlyList<MarketAcquisitionWorldItemSubtask> BuildExecutableLinePlan(
        PlannerLine line,
        IReadOnlyList<MarketAcquisitionWorldItemSubtask> candidates,
        bool keepProbeSubtasksAfterCaps = false)
    {
        var subtasks = new List<MarketAcquisitionWorldItemSubtask>();
        uint plannedQuantity = 0;
        uint plannedGil = 0;
        var hasGilCap = line.MaxTotalGil > 0;

        foreach (var subtask in candidates)
        {
            if (HasReachedQuantityCap(line, plannedQuantity))
            {
                if (keepProbeSubtasksAfterCaps)
                    subtasks.Add(ToProbeSubtask(subtask));
                continue;
            }

            if (hasGilCap && plannedGil + subtask.PlannedGil > line.MaxTotalGil)
            {
                if (keepProbeSubtasksAfterCaps)
                    subtasks.Add(ToProbeSubtask(subtask));
                continue;
            }

            subtasks.Add(subtask);
            plannedQuantity += subtask.PlannedQuantity;
            plannedGil += subtask.PlannedGil;
        }

        return subtasks;
    }

    public static string ResolveNorthAmericaDataCenter(string worldName)
        => MarketAcquisitionWorldCatalog.ResolveDataCenter(worldName);

    public static IReadOnlyList<string> ResolveNorthAmericaWorldsForDataCenters(IEnumerable<string> dataCenters)
        => MarketAcquisitionWorldCatalog.ResolveWorldsForDataCenters("North America", dataCenters);

    private static IReadOnlyList<string> ResolveSweepWorlds(
        MarketAcquisitionRequestView request,
        string? currentWorld)
    {
        var regionDataCenters = MarketAcquisitionWorldCatalog.ResolveDataCenters(request.Region);
        var scope = string.IsNullOrWhiteSpace(request.SweepScope)
            ? "Region"
            : request.SweepScope.Trim();

        return scope switch
        {
            "Region" => regionDataCenters.Values.SelectMany(worlds => worlds).ToArray(),
            "CurrentDataCenter" => MarketAcquisitionWorldCatalog.ResolveWorldsForDataCenters(
                request.Region,
                [MarketAcquisitionWorldCatalog.ResolveDataCenter(string.IsNullOrWhiteSpace(currentWorld) ? request.TargetWorld : currentWorld)]),
            "DataCenters" => MarketAcquisitionWorldCatalog.ResolveWorldsForDataCenters(request.Region, request.SweepDataCenters),
            _ => throw new InvalidOperationException($"Unknown all-world sweep scope {request.SweepScope}."),
        };
    }

    private static MarketAcquisitionWorldItemSubtask BuildWorldSubtask(
        PlannerLine line,
        string worldName,
        IEnumerable<MarketAcquisitionListing> listings,
        string source = "Planned")
    {
        var plannedListings = new List<MarketAcquisitionPlannedListing>();
        uint plannedQuantity = 0;
        uint plannedGil = 0;
        var hasGilCap = line.MaxTotalGil > 0;

        foreach (var listing in listings)
        {
            if (HasReachedQuantityCap(line, plannedQuantity))
                break;

            if (hasGilCap && plannedGil + listing.TotalGil > line.MaxTotalGil)
                continue;

            plannedListings.Add(new MarketAcquisitionPlannedListing
            {
                LineId = line.LineId,
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                ListingId = listing.ListingId,
                RetainerName = listing.RetainerName,
                RetainerId = listing.RetainerId,
                Quantity = listing.Quantity,
                UnitPrice = listing.UnitPrice,
                TotalGil = listing.TotalGil,
                IsHq = listing.IsHq,
                LastReviewTimeUtc = listing.LastReviewTimeUtc,
            });
            plannedQuantity += listing.Quantity;
            plannedGil += listing.TotalGil;
        }

        return new MarketAcquisitionWorldItemSubtask
        {
            LineId = line.LineId,
            LineOrdinal = line.Ordinal,
            Source = source,
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            WorldName = worldName,
            DataCenter = MarketAcquisitionWorldCatalog.ResolveDataCenter(worldName),
            QuantityMode = line.QuantityMode,
            RequestedQuantity = line.Quantity,
            HqPolicy = line.HqPolicy,
            MaxUnitPrice = line.MaxUnitPrice,
            GilCap = line.MaxTotalGil,
            PlannedQuantity = plannedQuantity,
            PlannedGil = plannedGil,
            ExceedsRequestedQuantity = line.Quantity > 0 && plannedQuantity > line.Quantity,
            Listings = plannedListings,
        };
    }

    private static MarketAcquisitionWorldItemSubtask ToProbeSubtask(MarketAcquisitionWorldItemSubtask subtask) =>
        subtask with
        {
            Source = "SweepProbe",
            PlannedQuantity = 0,
            PlannedGil = 0,
            ExceedsRequestedQuantity = false,
            Listings = [],
        };

    private static MarketAcquisitionWorldBatch BuildWorldBatch(
        string worldName,
        IEnumerable<MarketAcquisitionWorldItemSubtask> subtasks)
    {
        var orderedSubtasks = subtasks
            .OrderBy(subtask => subtask.LineOrdinal)
            .ToList();
        var listings = orderedSubtasks
            .SelectMany(subtask => subtask.Listings)
            .ToList();

        return new MarketAcquisitionWorldBatch
        {
            WorldName = worldName,
            DataCenter = MarketAcquisitionWorldCatalog.ResolveDataCenter(worldName),
            PlannedQuantity = (uint)orderedSubtasks.Sum(subtask => subtask.PlannedQuantity),
            PlannedGil = (uint)orderedSubtasks.Sum(subtask => subtask.PlannedGil),
            ExceedsRequestedQuantity = orderedSubtasks.Any(subtask => subtask.ExceedsRequestedQuantity),
            ItemSubtasks = orderedSubtasks,
            Listings = listings,
        };
    }

    private static bool HasReachedQuantityCap(PlannerLine line, uint plannedQuantity) =>
        !IsUnboundedAllBelowThreshold(line) && plannedQuantity >= line.Quantity;

    private static bool IsUnboundedAllBelowThreshold(PlannerLine line) =>
        line.Quantity == 0 &&
        line.QuantityMode.Equals("AllBelowThreshold", StringComparison.OrdinalIgnoreCase);

    private static bool LineSatisfiesQuantity(PlannerLine line, uint plannedQuantity) =>
        IsUnboundedAllBelowThreshold(line) || plannedQuantity >= line.Quantity;

    private static IReadOnlyList<MarketAcquisitionWorldBatch> RouteSortBatches(
        IReadOnlyList<MarketAcquisitionWorldBatch> batches,
        string? currentWorld)
    {
        if (string.IsNullOrWhiteSpace(currentWorld))
            return batches;

        var currentDataCenter = MarketAcquisitionWorldCatalog.ResolveDataCenter(currentWorld);
        var indexedBatches = batches
            .Select((batch, index) => new
            {
                Batch = batch,
                Index = index,
                DataCenter = string.IsNullOrWhiteSpace(batch.DataCenter)
                    ? MarketAcquisitionWorldCatalog.ResolveDataCenter(batch.WorldName)
                    : batch.DataCenter,
            })
            .ToList();

        if (indexedBatches.Count <= 1)
            return batches;

        return indexedBatches
            .OrderBy(entry => !entry.Batch.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
            .ThenBy(entry => !entry.DataCenter.Equals(currentDataCenter, StringComparison.OrdinalIgnoreCase))
            .ThenBy(entry => entry.DataCenter.Equals(currentDataCenter, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : entry.DataCenter,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Batch)
            .ToList();
    }

    private static string NormalizeQuantityMode(string quantityMode) =>
        quantityMode switch
        {
            "TargetQuantity" => "TargetQuantity",
            "AllBelowThreshold" => "AllBelowThreshold",
            _ => throw new InvalidOperationException($"Unknown quantity mode {quantityMode}."),
        };

    private sealed record PlannerLine
    {
        public string LineId { get; init; } = string.Empty;
        public int Ordinal { get; init; }
        public uint ItemId { get; init; }
        public string? ItemName { get; init; }
        public string QuantityMode { get; init; } = string.Empty;
        public uint Quantity { get; init; }
        public string HqPolicy { get; init; } = string.Empty;
        public uint MaxUnitPrice { get; init; }
        public uint MaxTotalGil { get; init; }
    }
}
