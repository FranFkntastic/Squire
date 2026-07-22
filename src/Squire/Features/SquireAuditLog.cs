using System;
using System.IO;
using Newtonsoft.Json;

namespace MarketMafioso.Squire;

public sealed class SquireAuditLog
{
    private readonly string directory;

    public SquireAuditLog(string directory)
    {
        this.directory = directory;
    }

    public string Write(SquireActionPlan plan, SquireRunResult result, string pluginVersion)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"squire-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json");
        var payload = new { pluginVersion, plan, result };
        File.WriteAllText(path, JsonConvert.SerializeObject(payload, Formatting.Indented));
        return path;
    }
}
