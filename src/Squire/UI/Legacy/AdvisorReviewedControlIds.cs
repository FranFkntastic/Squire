using System.Collections.Generic;

namespace MarketMafioso.Windows.Squire;

internal static class AdvisorReviewedControlIds
{
    public const string Refresh = "squire.outfitter.advisor.refresh";
    public const string Cancel = "squire.outfitter.advisor.cancel";
    public const string ContextPrefix = "squire.outfitter.advisor.context.";
    public const string FrontierList = "squire.outfitter.advisor.frontier-view.solutions";
    public const string FrontierPlot = "squire.outfitter.advisor.frontier-view.plot";
    public const string SolutionPrevious = "squire.outfitter.advisor.solution.previous";
    public const string SolutionNext = "squire.outfitter.advisor.solution.next";
    public const string SolutionPreviousPage = "squire.outfitter.advisor.solution.previous-page";
    public const string SolutionNextPage = "squire.outfitter.advisor.solution.next-page";
    public const string SolutionNomination = "squire.outfitter.advisor.solution.nomination";
    public const string SolutionPrefix = "squire.outfitter.advisor.solution.";
    public const string PlotPrefix = "squire.outfitter.advisor.plot.";
    public const string CopyArtisanList = "squire.outfitter.advisor.copy-artisan-list";
    public const string StageWorkbench = "squire.outfitter.advisor.stage-workbench";
    public const string StageMaterialsWorkbench = "squire.outfitter.advisor.stage-materials-workbench";
#if DEBUG
    public const string SyntheticReview = "squire.outfitter.advisor.synthetic-review";
#endif

    public static IReadOnlyList<string> Fixed { get; } =
    [
        Refresh,
        Cancel,
        FrontierList,
        FrontierPlot,
        SolutionPrevious,
        SolutionNext,
        SolutionPreviousPage,
        SolutionNextPage,
        SolutionNomination,
        CopyArtisanList,
        StageWorkbench,
        StageMaterialsWorkbench,
    ];
}
