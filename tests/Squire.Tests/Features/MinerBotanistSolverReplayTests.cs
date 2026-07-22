using System.IO.Compression;
using System.Diagnostics;
using System.Text.Json;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;
using Xunit.Abstractions;

namespace MarketMafioso.Tests.Squire;

public sealed class MinerBotanistSolverReplayTests(ITestOutputHelper output)
{
    [Fact]
    public void Capture_IsDeterministicSanitizedAndRoundTripsTheExactRequestShape()
    {
        var request = Request();

        var first = Capture(request);
        var reordered = Capture(request with { Offers = request.Offers.Reverse().ToArray() });
        var json = JsonSerializer.Serialize(first);

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(reordered));
        Assert.DoesNotContain("Siren", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-observation", json, StringComparison.Ordinal);
        Assert.DoesNotContain("market:live", json, StringComparison.Ordinal);
        var recaptured = MinerBotanistSolverReplay.Capture(
            first.ToRequest(),
            first.Profile.Context,
            first.Profile.ClassJobId,
            first.Profile.CharacterLevel,
            first.Profile.OfferBaseline,
            first.Profile.FixedStats);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(recaptured));
    }

    [Fact]
    public void NeutralReplayContract_ReconstructsLegacyMinBtnPayloadWithoutChangingItsJson()
    {
        var replay = Capture(Request());
        IAdvisorSolverReplay neutralReplay = replay;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var expected = JsonSerializer.Serialize(replay, options);
        var directory = Path.Combine(Path.GetTempPath(), "MarketMafiosoAdvisorReplayTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "replay.json");
        try
        {
            AdvisorSolverReplayFileStore.Write(path, neutralReplay);

            Assert.Equal(expected, File.ReadAllText(path));
            Assert.Equal(
                JsonSerializer.Serialize(replay.ToRequest()),
                JsonSerializer.Serialize(neutralReplay.ToRequest()));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Replay_PreservesFrontierMetricsAndCanonicalRetainedPathCounts()
    {
        var request = Request();
        var replay = Capture(request);

        var expected = new EquipmentExactFrontierSolver().Solve(request);
        var actual = new EquipmentExactFrontierSolver().Solve(replay.ToRequest());

        Assert.Equal(Metrics(expected), Metrics(actual));
        Assert.Equal(expected.Diagnostics.RetainedCompletePathCount, actual.Diagnostics.RetainedCompletePathCount);
        Assert.Equal(expected.RetainedEquivalenceSummaries.Select(value => value.RetainedPathCount).Order().ToArray(),
            actual.RetainedEquivalenceSummaries.Select(value => value.RetainedPathCount).Order().ToArray());
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void SanitizedBtn72Replay_MeetsWorstSupportedExactSolverBudget()
    {
        var request = ReadBtn72Fixture().ToRequest();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var managedMemory = new ManagedHeapPeakSampler();
        var collectionsBefore = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
        managedMemory.Start();
        var beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        EquipmentExactFrontierProgress? progress = null;
        var checkpoints = new List<(
            EquipmentExactFrontierProgress Progress,
            long AllocatedBytes,
            long LiveBytes,
            long OccupancyBytes)>();

        EquipmentExactFrontierResult result;
        try
        {
            result = new EquipmentExactFrontierSolver().Solve(request, cancellation.Token, value =>
            {
                progress = value;
                if (value.Phase != "Pruning" ||
                    value.ProcessedCandidateCount == 0 ||
                    value.ProcessedCandidateCount == value.CandidateStateCount)
                    checkpoints.Add((
                        value,
                        GC.GetAllocatedBytesForCurrentThread() - beforeAllocated,
                        managedMemory.IncrementalPeakLiveBytes,
                        managedMemory.IncrementalPeakOccupancyBytes));
            });
        }
        catch (OperationCanceledException)
        {
            var allocatedAtCancellation = GC.GetAllocatedBytesForCurrentThread() - beforeAllocated;
            Assert.Fail($"BTN 72 replay exceeded the 15-second guard; allocated={allocatedAtCancellation:N0}; lastProgress={progress}; checkpoints={string.Join("; ", checkpoints.Select(CheckpointText))}.");
            throw;
        }
        finally
        {
            managedMemory.Stop();
        }

        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAllocated;
        var incrementalPeakLiveManaged = managedMemory.IncrementalPeakLiveBytes;
        var incrementalPeakManagedOccupancy = managedMemory.IncrementalPeakOccupancyBytes;
        var collections = Enumerable.Range(0, GC.MaxGeneration + 1)
            .Select(generation => GC.CollectionCount(generation) - collectionsBefore[generation])
            .ToArray();
        var gcInfo = GC.GetGCMemoryInfo();
        var lohBytesAfterLastGc = gcInfo.GenerationInfo.Length > 3 ? gcInfo.GenerationInfo[3].SizeAfterBytes : 0;
        var checkpointText = string.Join("; ", checkpoints.Select(CheckpointText));
        var witnessCount = result.Pareto.Dominated.Sum(value => value.DominatingSolutionIds.Count);
        var memoryText = $"peakLiveManagedIncrement={incrementalPeakLiveManaged:N0} bytes ({incrementalPeakLiveManaged / 1024d / 1024d:F2} MiB); peakManagedOccupancyIncrement={incrementalPeakManagedOccupancy:N0} bytes ({incrementalPeakManagedOccupancy / 1024d / 1024d:F2} MiB); allocated={allocated:N0}; collections=[{string.Join(',', collections)}]; lohAfterLastGc={lohBytesAfterLastGc:N0}; fragmentedAfterLastGc={gcInfo.FragmentedBytes:N0}";
        var resultShape = $"frontier={result.Pareto.Frontier.Count}; dominated={result.Pareto.Dominated.Count}; witnesses={witnessCount:N0}; retainedClasses={result.RetainedEquivalenceSummaries.Count}";
        output.WriteLine($"elapsed={stopwatch.Elapsed}; {memoryText}; peakStates={result.Diagnostics.PeakRetainedStateCount}; {resultShape}");
        Assert.True(stopwatch.Elapsed <= TimeSpan.FromSeconds(10),
            $"BTN 72 replay took {stopwatch.Elapsed}; {memoryText}; peakStates={result.Diagnostics.PeakRetainedStateCount}; {resultShape}; checkpoints={checkpointText}.");
        Assert.True(incrementalPeakLiveManaged <= 512L * 1024 * 1024,
            $"BTN 72 replay peak live managed-memory increment was {incrementalPeakLiveManaged:N0} bytes ({incrementalPeakLiveManaged / 1024d / 1024d:F2} MiB); elapsed={stopwatch.Elapsed}; {memoryText}; peakStates={result.Diagnostics.PeakRetainedStateCount}; {resultShape}; checkpoints={checkpointText}.");

        static string CheckpointText((EquipmentExactFrontierProgress Progress, long AllocatedBytes, long LiveBytes, long OccupancyBytes) value) =>
            $"{value.Progress} allocated={value.AllocatedBytes:N0} live={value.LiveBytes:N0} occupancy={value.OccupancyBytes:N0}";
    }

    private static MinerBotanistSolverReplay Capture(EquipmentExactFrontierRequest request) =>
        MinerBotanistSolverReplay.Capture(
            request,
            MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark,
            MinerBotanistUtilityProfile.BotanistClassJobId,
            72,
            new(900, 900, 600),
            new(50, 50, 50));

    internal static MinerBotanistSolverReplay ReadBtn72Fixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Squire", "btn72-solver-replay-v1.json.gz");
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<MinerBotanistSolverReplay>(gzip, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("BTN 72 solver replay fixture was empty.");
    }

    private static EquipmentExactFrontierRequest Request()
    {
        var profile = new MinerBotanistUtilityProfile(
            MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark,
            new(900, 900, 600),
            MinerBotanistUtilityProfile.BotanistClassJobId,
            72,
            new(50, 50, 50));
        var head = Offer(EquipmentLoadoutPosition.Head, 100, EquipmentAcquisitionSourceKind.Owned, null, null, 10, 10, 0, 0);
        var body = Offer(EquipmentLoadoutPosition.Body, 200, EquipmentAcquisitionSourceKind.Owned, null, null, 10, 10, 0, 0);
        var upgradeA = Offer(EquipmentLoadoutPosition.Head, 101, EquipmentAcquisitionSourceKind.MarketBoard,
            "private-observation-a", "Siren", 30, 20, 0, 1_000);
        var upgradeB = Offer(EquipmentLoadoutPosition.Head, 101, EquipmentAcquisitionSourceKind.MarketBoard,
            "private-observation-b", "Siren", 30, 20, 0, 1_000);
        var vendor = Offer(EquipmentLoadoutPosition.Body, 201, EquipmentAcquisitionSourceKind.GilVendor,
            null, null, 20, 30, 0, 500, vendor: "private-vendor");
        return new(
            [head, body, upgradeA, upgradeB, vendor],
            new HashSet<EquipmentLoadoutPosition> { EquipmentLoadoutPosition.Head, EquipmentLoadoutPosition.Body },
            new Dictionary<EquipmentLoadoutPosition, EquipmentOfferAllocationKey?>
            {
                [EquipmentLoadoutPosition.Head] = head.AllocationKey,
                [EquipmentLoadoutPosition.Body] = body.AllocationKey,
            },
            profile);
    }

    private static EquipmentExactSolverOffer Offer(
        EquipmentLoadoutPosition position,
        uint itemId,
        EquipmentAcquisitionSourceKind source,
        string? observation,
        string? world,
        int gathering,
        int perception,
        int gp,
        ulong cost,
        string? vendor = null)
    {
        var definition = new EquipmentItemDefinition(
            itemId,
            $"Sensitive item {itemId}",
            1,
            1,
            position == EquipmentLoadoutPosition.Head ? EquipmentSlot.Head : EquipmentSlot.Body,
            new HashSet<uint> { MinerBotanistUtilityProfile.BotanistClassJobId },
            1,
            true,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            false);
        var offer = new EquipmentLoadoutOffer(
            definition,
            source,
            "Sensitive source label",
            cost > uint.MaxValue ? uint.MaxValue : (uint)cost,
            Quality: EquipmentQuality.Normal,
            SourceCatalogKey: $"market:live:{source}:{itemId}");
        return new(
            offer,
            observation,
            new HashSet<EquipmentLoadoutPosition> { position },
            1,
            new([
                new("gathering", gathering),
                new("perception", perception),
                new("gathering-points", gp),
            ]),
            cost,
            world,
            vendor,
            source == EquipmentAcquisitionSourceKind.Owned ? 0 : 1,
            new(0, 0, 0),
            [source.ToString()]);
    }

    private static string[] Metrics(EquipmentExactFrontierResult result) => result.Pareto.Frontier
        .Select(value => string.Join('|',
            value.AcquisitionCostGil,
            value.Utility.UtilityScore,
            value.Burden.WorldVisits,
            value.Burden.VendorStops,
            value.Burden.PurchaseTransactions,
            value.EvidenceRisk.FreshnessBucket,
            value.EvidenceRisk.IncompleteCoverageCount,
            value.EvidenceRisk.ConfidencePenalty))
        .Order(StringComparer.Ordinal)
        .ToArray();

    private sealed class ManagedHeapPeakSampler : IDisposable
    {
        private readonly ManualResetEventSlim stop = new(false);
        private readonly Thread thread;
        private long peakOccupancyBytes;
        private long peakLiveBytes;
        private bool started;

        public ManagedHeapPeakSampler() => thread = new(Sample)
        {
            IsBackground = true,
            Name = "BTN solver managed-memory sampler",
        };

        public long BaselineOccupancyBytes { get; private set; }
        public long BaselineLiveBytes { get; private set; }
        public long IncrementalPeakOccupancyBytes => Math.Max(0, Volatile.Read(ref peakOccupancyBytes) - BaselineOccupancyBytes);
        public long IncrementalPeakLiveBytes => Math.Max(0, Volatile.Read(ref peakLiveBytes) - BaselineLiveBytes);

        public void Start()
        {
            if (started)
                throw new InvalidOperationException("Managed-memory sampler was already started.");
            started = true;
            BaselineOccupancyBytes = GC.GetTotalMemory(forceFullCollection: false);
            var gcInfo = GC.GetGCMemoryInfo();
            BaselineLiveBytes = Math.Max(0, gcInfo.HeapSizeBytes - gcInfo.FragmentedBytes);
            peakOccupancyBytes = BaselineOccupancyBytes;
            peakLiveBytes = BaselineLiveBytes;
            thread.Start();
        }

        public void Stop()
        {
            if (!started || stop.IsSet)
                return;
            stop.Set();
            thread.Join();
        }

        public void Dispose()
        {
            Stop();
            stop.Dispose();
        }

        private void Sample()
        {
            while (!stop.IsSet)
            {
                Observe();
                Thread.Sleep(5);
            }
            Observe();
        }

        private void Observe()
        {
            ObserveMaximum(ref peakOccupancyBytes, GC.GetTotalMemory(forceFullCollection: false));
            var gcInfo = GC.GetGCMemoryInfo();
            ObserveMaximum(ref peakLiveBytes, Math.Max(0, gcInfo.HeapSizeBytes - gcInfo.FragmentedBytes));
        }

        private static void ObserveMaximum(ref long maximum, long current)
        {
            var observed = Volatile.Read(ref maximum);
            while (current > observed)
            {
                var previous = Interlocked.CompareExchange(ref maximum, current, observed);
                if (previous == observed)
                    return;
                observed = previous;
            }
        }
    }
}
