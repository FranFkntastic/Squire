using MarketMafioso.Squire.Observation;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class RenderedRetainerUiPreparationCoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 18, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Begin_completes_without_commands_when_rendered_retainer_list_is_visible()
    {
        var commands = new List<string>();
        var result = new RenderedRetainerUiPreparationCoordinator().Begin(Start, true, true, "Siren", command => { commands.Add(command); return true; });

        Assert.Equal(RenderedRetainerUiPreparationStatus.Complete, result.Status);
        Assert.Empty(commands);
    }

    [Fact]
    public void Workflow_travels_interacts_and_completes_only_from_rendered_ui()
    {
        var commands = new List<string>();
        bool Process(string command) { commands.Add(command); return true; }
        var coordinator = new RenderedRetainerUiPreparationCoordinator();

        Assert.Equal(RenderedRetainerUiPreparationStatus.Traveling, coordinator.Begin(Start, false, true, "Siren", Process).Status);
        Assert.Equal(RenderedRetainerUiPreparationStatus.Traveling, coordinator.Advance(Start.AddSeconds(1), false, true, false, false, Process).Status);
        Assert.Equal(RenderedRetainerUiPreparationStatus.ClearingMarketBoardUi, coordinator.Advance(Start.AddSeconds(3), false, true, true, true, Process).Status);
        Assert.Equal(RenderedRetainerUiPreparationStatus.OpeningRetainerList, coordinator.Advance(Start.AddSeconds(4), false, true, false, false, Process).Status);
        Assert.Equal(RenderedRetainerUiPreparationStatus.OpeningRetainerList, coordinator.Advance(Start.AddSeconds(5), false, true, true, false, Process).Status);
        Assert.Equal(RenderedRetainerUiPreparationStatus.Complete, coordinator.Advance(Start.AddSeconds(6), true, true, false, false, Process).Status);
        Assert.Equal(1, coordinator.Snapshot().InteractionAttempts);
        Assert.Equal(["/li Siren mb", "lifestream:interact-object:2000401"], commands);
    }

    [Fact]
    public void Workflow_fails_closed_when_lifestream_state_is_unavailable()
    {
        var coordinator = new RenderedRetainerUiPreparationCoordinator();
        coordinator.Begin(Start, false, true, "Siren", _ => true);

        var result = coordinator.Advance(Start.AddSeconds(3), false, false, false, false, _ => true);

        Assert.Equal(RenderedRetainerUiPreparationStatus.Failed, result.Status);
    }

    [Fact]
    public void Workflow_fails_when_interaction_finishes_without_rendered_list()
    {
        var coordinator = new RenderedRetainerUiPreparationCoordinator();
        coordinator.Begin(Start, false, true, "Siren", _ => true);
        coordinator.Advance(Start.AddSeconds(3), false, true, false, false, _ => true);

        var result = coordinator.Advance(Start.AddSeconds(7), false, true, false, false, _ => true);

        Assert.Equal(RenderedRetainerUiPreparationStatus.OpeningRetainerList, result.Status);
        Assert.Equal(2, result.InteractionAttempts);

        result = coordinator.Advance(Start.AddSeconds(53), false, true, false, false, _ => true);

        Assert.Equal(RenderedRetainerUiPreparationStatus.Failed, result.Status);
        Assert.Contains("within forty-five seconds", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rendered_market_board_arrival_waits_for_close_before_interaction()
    {
        var commands = new List<string>();
        var coordinator = new RenderedRetainerUiPreparationCoordinator();
        coordinator.Begin(Start, false, true, "Siren", command => { commands.Add(command); return true; });

        var result = coordinator.Advance(Start.AddSeconds(3), false, true, true, true,
            command => { commands.Add(command); return true; });

        Assert.Equal(RenderedRetainerUiPreparationStatus.ClearingMarketBoardUi, result.Status);
        Assert.Equal(["/li Siren mb"], commands);

        result = coordinator.Advance(Start.AddSeconds(4), false, true, false, false,
            command => { commands.Add(command); return true; });

        Assert.Equal(RenderedRetainerUiPreparationStatus.OpeningRetainerList, result.Status);
        Assert.Equal(["/li Siren mb", "lifestream:interact-object:2000401"], commands);
    }

    [Fact]
    public void Workflow_fails_when_semantic_interaction_is_rejected()
    {
        var coordinator = new RenderedRetainerUiPreparationCoordinator();
        coordinator.Begin(Start, false, true, "Siren", _ => true);

        var result = coordinator.Advance(Start.AddSeconds(3), false, true, false, false,
            command => command != "lifestream:interact-object:2000401");

        Assert.Equal(RenderedRetainerUiPreparationStatus.Failed, result.Status);
        Assert.Contains("did not accept", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Workflow_uses_rendered_bell_activation_once_when_lifestream_does_not_open_list()
    {
        var commands = new List<string>();
        bool Process(string command) { commands.Add(command); return true; }
        var coordinator = new RenderedRetainerUiPreparationCoordinator();
        coordinator.Begin(Start, false, true, "Siren", Process);
        coordinator.Advance(Start.AddSeconds(3), false, true, false, false, Process);

        var fallback = coordinator.Advance(Start.AddSeconds(7), false, true, false, false, Process);
        var complete = coordinator.Advance(Start.AddSeconds(8), true, true, false, false, Process);

        Assert.Equal(RenderedRetainerUiPreparationStatus.OpeningRetainerList, fallback.Status);
        Assert.Equal(RenderedRetainerUiPreparationStatus.Complete, complete.Status);
        Assert.Equal([
            "/li Siren mb",
            "lifestream:interact-object:2000401",
            "rendered-ui:activate-summoning-bell",
        ], commands);
    }

    [Fact]
    public void Workflow_waits_for_external_retainer_automation_after_bell_activation()
    {
        var coordinator = new RenderedRetainerUiPreparationCoordinator();
        coordinator.Begin(Start, false, true, "Siren", _ => true);
        coordinator.Advance(Start.AddSeconds(3), false, true, false, false, _ => true);
        coordinator.Advance(Start.AddSeconds(7), false, true, false, false, _ => true);

        var waiting = coordinator.Advance(Start.AddSeconds(20), false, true, false, false, _ => true);
        var complete = coordinator.Advance(Start.AddSeconds(30), true, true, false, false, _ => true);

        Assert.Equal(RenderedRetainerUiPreparationStatus.OpeningRetainerList, waiting.Status);
        Assert.Contains("external retainer automation", waiting.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RenderedRetainerUiPreparationStatus.Complete, complete.Status);
    }

    [Fact]
    public void Begin_rejects_missing_or_unsafe_home_world_without_commands()
    {
        var commands = new List<string>();
        var coordinator = new RenderedRetainerUiPreparationCoordinator();

        var missing = coordinator.Begin(Start, false, true, string.Empty, command => { commands.Add(command); return true; });
        var unsafeName = coordinator.Begin(Start, false, true, "Siren; /shutdown", command => { commands.Add(command); return true; });

        Assert.Equal(RenderedRetainerUiPreparationStatus.Failed, missing.Status);
        Assert.Equal(RenderedRetainerUiPreparationStatus.Failed, unsafeName.Status);
        Assert.Empty(commands);
    }
}
