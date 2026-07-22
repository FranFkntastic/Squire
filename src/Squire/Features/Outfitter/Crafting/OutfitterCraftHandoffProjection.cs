using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Outfitter.Crafting;

internal sealed record OutfitterCraftHandoffRecipe(
    uint RecipeId,
    uint CraftCount,
    string ItemName,
    int Depth);

internal sealed record OutfitterCraftHandoffMaterial(
    OutfitterCraftPlanIdentity PlanIdentity,
    uint ItemId,
    string ItemName,
    EquipmentQuality Quality,
    uint ConsumedQuantity,
    uint PurchasedQuantity,
    uint SurplusQuantity,
    OutfitterMaterialSourceIdentity Source);

internal sealed record OutfitterCraftHandoffProjection(
    string SelectedSolutionId,
    IReadOnlyList<OutfitterCraftPlanIdentity> PlanIdentities,
    IReadOnlyList<OutfitterCraftHandoffRecipe> Recipes,
    IReadOnlyList<OutfitterCraftHandoffMaterial> Materials)
{
    public IReadOnlyList<OutfitterCraftHandoffMaterial> MarketMaterials =>
        Materials.Where(material => material.Source is OutfitterMarketMaterialSourceIdentity).ToArray();

    public IReadOnlyList<OutfitterCraftHandoffMaterial> VendorMaterials =>
        Materials.Where(material => material.Source is OutfitterGilVendorMaterialSourceIdentity).ToArray();

    public static OutfitterCraftHandoffProjection Build(
        MinerBotanistReadOnlyAdvice advice,
        string selectedSolutionId,
        PlayerAdvisorBaseline currentBaseline,
        OutfitterMarketEvidenceBook currentEvidence,
        DateTimeOffset asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(advice);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedSolutionId);
        ArgumentNullException.ThrowIfNull(currentBaseline);
        ArgumentNullException.ThrowIfNull(currentEvidence);
        if (asOfUtc == default)
            throw new ArgumentException("Craft handoff requires an explicit review time.", nameof(asOfUtc));
        if (advice is not { Status: MinerBotanistAdvisorStatus.Complete, Frontier: { } frontier } ||
            !currentEvidence.IsPublishable)
        {
            throw new InvalidOperationException("Craft handoff requires complete current Advisor and market evidence.");
        }

        var selected = frontier.Pareto.Frontier.SingleOrDefault(solution =>
            string.Equals(solution.Candidate.SolutionId, selectedSolutionId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The selected solution is absent from the current Advisor frontier.");
        var allocations = selected.Candidate.Selections
            .Select(selection => selection.AllocationKey)
            .Distinct()
            .Where(key => advice.OffersByAllocation.TryGetValue(key, out var offer) &&
                offer.Offer.SourceKind == EquipmentAcquisitionSourceKind.Craft)
            .ToArray();
        if (allocations.Length == 0)
            throw new InvalidOperationException("The selected solution contains no craft acquisition.");

        var craftOffers = allocations.Select(key =>
        {
            if (!advice.CraftOffersByAllocation.TryGetValue(key, out var craft) ||
                craft.SolverOffer.AllocationKey != key ||
                !craft.Matches(currentBaseline, currentEvidence, asOfUtc))
            {
                throw new InvalidOperationException("A selected craft plan is stale or no longer matches current player and market evidence.");
            }
            return craft;
        }).ToArray();
        var plans = craftOffers
            .DistinctBy(craft => craft.Source.PlanIdentity)
            .Select(craft => (craft.Source.Plan, craft.Source.PlanIdentity))
            .ToArray();
        if (plans.Any(value => value.Plan.ComputeStructuralIdentity() != value.PlanIdentity ||
            !value.Plan.Validate(requireEconomyReady: true).IsValid))
        {
            throw new InvalidOperationException("A selected craft plan failed structural revalidation.");
        }

        var duplicateListing = plans
            .SelectMany(value => value.Plan.TerminalMaterials)
            .Select(line => line.Source)
            .OfType<OutfitterMarketMaterialSourceIdentity>()
            .GroupBy(source => source.PhysicalSourceKey)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicateListing is not null)
            throw new InvalidOperationException("Selected craft plans reuse one indivisible market listing.");

        var recipes = BuildRecipes(plans.Select(value => value.Plan));
        var materials = plans
            .SelectMany(value => BuildMaterials(value.Plan, value.PlanIdentity))
            .OrderBy(material => material.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(material => material.Quality)
            .ThenBy(material => material.Source.Kind)
            .ToArray();
        return new(
            selected.Candidate.SolutionId,
            plans.Select(value => value.PlanIdentity).OrderBy(value => value.Sha256, StringComparer.Ordinal).ToArray(),
            recipes,
            materials);
    }

    private static IReadOnlyList<OutfitterCraftHandoffRecipe> BuildRecipes(IEnumerable<OutfitterCraftPlan> plans)
    {
        var inputs = plans.SelectMany(plan =>
        {
            var nodes = plan.ExpandedNodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
            return plan.ExpandedNodes
                .Where(node => node.Kind == OutfitterCraftNodeKind.Craft)
                .Select(node =>
                {
                    var resolution = node.ResolvedRecipe
                        ?? throw new InvalidOperationException("A selected craft node lacks frozen recipe resolution.");
                    if (string.IsNullOrWhiteSpace(resolution.OutputItemName))
                        throw new InvalidOperationException("A selected craft node lacks a player-facing item name.");
                    var craftCount = checked((uint)(((ulong)node.RequiredQuantity + node.RecipeOutputQuantity - 1) /
                        node.RecipeOutputQuantity));
                    return new RecipeInput(node, resolution, craftCount, Depth(node, nodes));
                });
        }).ToArray();

        return inputs
            .GroupBy(input => input.Node.RecipeId)
            .Select(group =>
            {
                var first = group.First();
                if (group.Any(input =>
                        input.Resolution.OutputItemId != first.Resolution.OutputItemId ||
                        input.Resolution.OutputQuantity != first.Resolution.OutputQuantity ||
                        !string.Equals(input.Resolution.DefinitionFingerprint, first.Resolution.DefinitionFingerprint, StringComparison.Ordinal) ||
                        !string.Equals(input.Resolution.OutputItemName, first.Resolution.OutputItemName, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("One recipe ID has conflicting frozen craft definitions.");
                }
                var count = group.Aggregate(0u, (sum, input) => checked(sum + input.CraftCount));
                return new OutfitterCraftHandoffRecipe(
                    group.Key,
                    count,
                    first.Resolution.OutputItemName,
                    group.Max(input => input.Depth));
            })
            .OrderByDescending(recipe => recipe.Depth)
            .ThenBy(recipe => recipe.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(recipe => recipe.RecipeId)
            .ToArray();
    }

    private static IReadOnlyList<OutfitterCraftHandoffMaterial> BuildMaterials(
        OutfitterCraftPlan plan,
        OutfitterCraftPlanIdentity planIdentity)
    {
        var names = new Dictionary<uint, string>();
        foreach (var node in plan.ExpandedNodes.Where(node => node.Kind == OutfitterCraftNodeKind.Material))
        {
            var parent = plan.ExpandedNodes.Single(candidate => candidate.NodeId == node.ParentNodeId);
            var name = parent.ResolvedRecipe?.Ingredients.SingleOrDefault(ingredient =>
                string.Equals(ingredient.ChildNodeId, node.NodeId, StringComparison.Ordinal))?.ItemName;
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("A selected terminal material lacks a player-facing item name.");
            if (names.TryGetValue(node.ItemId, out var existing) && !string.Equals(existing, name, StringComparison.Ordinal))
                throw new InvalidOperationException("One terminal material has conflicting player-facing names.");
            names[node.ItemId] = name;
        }

        return plan.TerminalMaterials.Select(line => new OutfitterCraftHandoffMaterial(
            planIdentity,
            line.ItemId,
            names.GetValueOrDefault(line.ItemId)
                ?? throw new InvalidOperationException("A selected terminal material is disconnected from the frozen recipe tree."),
            line.Quality,
            line.ConsumedQuantity,
            line.PurchasedQuantity,
            line.SurplusQuantity,
            line.Source)).ToArray();
    }

    private static int Depth(OutfitterCraftNode node, IReadOnlyDictionary<string, OutfitterCraftNode> nodes)
    {
        var depth = 0;
        var cursor = node;
        while (cursor.ParentNodeId is { } parentId)
        {
            cursor = nodes[parentId];
            depth = checked(depth + 1);
        }
        return depth;
    }

    private sealed record RecipeInput(
        OutfitterCraftNode Node,
        OutfitterResolvedRecipeSnapshot Resolution,
        uint CraftCount,
        int Depth);
}
