using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Characters;
using Lumina.Excel.Sheets;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Observation;

public sealed class DalamudAdvisorCharacterSubjectSource
{
    private readonly IPlayerState playerState;
    private readonly IDataManager dataManager;

    public DalamudAdvisorCharacterSubjectSource(IPlayerState playerState, IDataManager dataManager)
    {
        this.playerState = playerState;
        this.dataManager = dataManager;
    }

    public AdvisorCharacterSubject Capture()
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0 || playerState.ClassJob.RowId == 0)
            return AdvisorCharacterSubject.Unavailable;

        var classJobId = playerState.ClassJob.RowId;
        var row = dataManager.GetExcelSheet<ClassJob>()?.GetRowOrDefault(classJobId);
        var abbreviation = row?.Abbreviation.ToString();
        var label = string.IsNullOrWhiteSpace(abbreviation) ? $"Class/job {classJobId}" : abbreviation;
        return new(true, classJobId, label, playerState.Level);
    }
}
