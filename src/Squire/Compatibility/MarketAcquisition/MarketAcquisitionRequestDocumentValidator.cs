using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionRequestDocumentValidator
{
    public static MarketAcquisitionRequestValidationResult Validate(
        MarketAcquisitionRequestDocument document,
        string? clientApiKey,
        string? characterName,
        string? world)
    {
        ArgumentNullException.ThrowIfNull(document);

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(clientApiKey))
            errors.Add("Client API key is required.");
        if (string.IsNullOrWhiteSpace(characterName))
            errors.Add("Current character name is required.");
        if (string.IsNullOrWhiteSpace(world))
            errors.Add("Current world is required.");

        errors.AddRange(ValidateDraft(document).Errors);

        return new MarketAcquisitionRequestValidationResult(errors);
    }

    public static MarketAcquisitionRequestValidationResult ValidateDraft(
        MarketAcquisitionRequestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<string>();
        ValidateRoute(document, errors);

        if (document.Lines.Count == 0)
        {
            errors.Add("At least one acquisition request line is required.");
        }
        else
        {
            for (var index = 0; index < document.Lines.Count; index++)
                ValidateLine(document.Lines[index], index + 1, errors);
        }

        return new MarketAcquisitionRequestValidationResult(errors);
    }

    private static void ValidateRoute(MarketAcquisitionRequestDocument document, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(document.Region))
        {
            errors.Add("Region is required.");
        }
        else
        {
            try
            {
                _ = MarketAcquisitionWorldCatalog.NormalizeRegion(document.Region);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(ex.Message);
            }
        }

        if (document.WorldMode is not ("Recommended" or "AllWorldSweep"))
            errors.Add("World mode must be Recommended or AllWorldSweep.");

        if (document.WorldMode == "AllWorldSweep")
        {
            var sweepScope = string.IsNullOrWhiteSpace(document.SweepScope)
                ? "Region"
                : document.SweepScope.Trim();
            if (sweepScope is not ("Region" or "CurrentDataCenter" or "DataCenters"))
            {
                errors.Add("Sweep scope must be Region, CurrentDataCenter, or DataCenters.");
            }
            else if (sweepScope == "DataCenters")
            {
                if (document.SweepDataCenters.Count == 0 ||
                    document.SweepDataCenters.All(string.IsNullOrWhiteSpace))
                {
                    errors.Add("At least one data center is required for a data-center sweep.");
                }
                else if (!string.IsNullOrWhiteSpace(document.Region))
                {
                    foreach (var dataCenter in document.SweepDataCenters.Where(dc => !string.IsNullOrWhiteSpace(dc)))
                    {
                        try
                        {
                            _ = MarketAcquisitionWorldCatalog.NormalizeDataCenterName(document.Region, dataCenter);
                        }
                        catch (InvalidOperationException ex)
                        {
                            errors.Add(ex.Message);
                        }
                    }
                }
            }
        }
    }

    private static void ValidateLine(
        MarketAcquisitionRequestLineDocument line,
        int lineNumber,
        List<string> errors)
    {
        if (line.ItemId == 0)
            errors.Add($"Line {lineNumber}: item id is required.");
        if (line.MaxUnitPrice == 0)
            errors.Add($"Line {lineNumber}: max unit price is required before request sync.");

        if (line.QuantityMode is not ("TargetQuantity" or "AllBelowThreshold"))
        {
            errors.Add($"Line {lineNumber}: quantity mode must be TargetQuantity or AllBelowThreshold.");
        }
        else if (line.QuantityMode == "TargetQuantity" && line.TargetQuantity == 0)
        {
            errors.Add($"Line {lineNumber}: target quantity is required.");
        }

        if (!IsSupportedHqPolicy(line.HqPolicy))
            errors.Add($"Line {lineNumber}: HQ policy must be Either, HQOnly, or NQOnly.");
    }

    private static bool IsSupportedHqPolicy(string hqPolicy)
    {
        try
        {
            _ = MarketAcquisitionPolicy.NormalizeHqPolicy(hqPolicy);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

public sealed record MarketAcquisitionRequestValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}
