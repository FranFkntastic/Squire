using System;

namespace MarketMafioso.Automation.Diagnostics;

public interface IAutomationDiagnosticProbe
{
    string Name { get; }
    AutomationDiagnosticProbeResult Run();
}

public sealed class AutomationDiagnosticProbe : IAutomationDiagnosticProbe
{
    private readonly Func<AutomationDiagnosticProbeResult> run;

    public AutomationDiagnosticProbe(string name, Func<AutomationDiagnosticProbeResult> run)
    {
        Name = name;
        this.run = run;
    }

    public string Name { get; }

    public AutomationDiagnosticProbeResult Run() => run();
}
