using System;
using System.Collections.Generic;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionRouteDiagnosticEvent
{
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }

    public required long Sequence { get; init; }

    public required long ElapsedMilliseconds { get; init; }

    public required DateTimeOffset RecordedAtUtc { get; init; }

    public required string EventName { get; init; }

    public required string Message { get; init; }

    public required IReadOnlyDictionary<string, string> Details { get; init; }
}

public sealed record MarketAcquisitionRouteDiagnosticManifest
{
    public required int SchemaVersion { get; init; }

    public required string RunId { get; init; }

    public required string PackageKind { get; init; }

    public required string CaptureStatus { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public required string? AssemblyName { get; init; }

    public required string? AssemblyVersion { get; init; }

    public required string? InformationalVersion { get; init; }

    public required IReadOnlyDictionary<string, string> Artifacts { get; init; }

    public required IReadOnlyList<string> CaptureCapabilities { get; init; }
}
