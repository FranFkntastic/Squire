using System.Collections.Generic;

namespace MarketMafioso.Automation.Diagnostics;

public sealed record AutomationDiagnosticProbeResult(
    string ProbeName,
    bool IsSuccess,
    string Message,
    IReadOnlyDictionary<string, string?> Details);
