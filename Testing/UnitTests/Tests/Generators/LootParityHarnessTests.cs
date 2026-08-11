using System.Diagnostics;
using System.Text;
using NUnit.Framework;
using SPTarkov.Server.Core.Generators.Loot;
using SPTarkov.Server.Core.Helpers.InRaid;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Native;
using SPTarkov.Server.Core.Native.Loot;
using SPTarkov.Server.Core.Services.Items;
using SPTarkov.Server.Core.Services.Server;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;

namespace UnitTests.Tests.Generators;

/// <summary>
/// Dev-only empirical proof that the native loot generator is semantically equivalent to
/// <see cref="LocationLootGenerator"/>. Runs both implementations N times over the real
/// <c>bigmap</c> data and compares distributions - never sequences, and never spawn point ordering
/// (Rust iterates HashMaps where C# iterates insertion-ordered Dictionaries).
///
/// The native payload is assembled inline from the same live data the C# generator reads, which is
/// the shape the cutover's payload builder has to take.
///
/// Deleted once the cutover's integration fixture and perf gate land.
/// </summary>
[TestFixture]
[Explicit("dev-only parity harness, deleted before merge")]
public class LootParityHarnessTests
{
    private const string LocationId = "bigmap";
    private const int RunCount = 200;

    /// <summary>
    /// |mean delta| / C# mean must stay under this for every counted metric.
    /// </summary>
    private const double CountTolerance = 0.05;

    /// <summary>
    /// L1 distance between the two per-tpl frequency distributions, over the tpls either side drew
    /// at least <see cref="FrequencyOccurrenceFloor"/> times.
    /// </summary>
    private const double FrequencyL1Tolerance = 0.10;

    private const int FrequencyOccurrenceFloor = 50;

    private LocationLootGenerator _locationLootGenerator = default!;
    private LocationTable _locationTable = default!;
    private TemplateTable _templateTable = default!;
    private ItemHelper _itemHelper = default!;
    private PresetHelper _presetHelper = default!;
    private SeasonalEventService _seasonalEventService = default!;
    private ItemFilterService _itemFilterService = default!;
    private CounterTrackerHelper _counterTrackerHelper = default!;
    private LocationConfig _locationConfig = default!;
    private SeasonalEventConfig _seasonalEventConfig = default!;
    private ICloner _cloner = default!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var di = DI.GetInstance();

        // Publishes the static JsonSerializerOptions SptNative serialises payloads with.
        _ = di.GetService<JsonUtil>();

        _locationLootGenerator = di.GetService<LocationLootGenerator>();
        _locationTable = di.GetService<LocationTable>();
        _templateTable = di.GetService<TemplateTable>();
        _itemHelper = di.GetService<ItemHelper>();
        _presetHelper = di.GetService<PresetHelper>();
        _seasonalEventService = di.GetService<SeasonalEventService>();
        _itemFilterService = di.GetService<ItemFilterService>();
        _counterTrackerHelper = di.GetService<CounterTrackerHelper>();
        _locationConfig = di.GetService<LocationConfig>();
        _seasonalEventConfig = di.GetService<SeasonalEventConfig>();
        _cloner = di.GetService<ICloner>();
    }

    [Test]
    public void NativeLootGenerationMatchesTheCSharpGeneratorStatistically()
    {
        var diagnosticCounts = new Dictionary<string, int>();

        // Warm-up runs (JIT, native library load, first lazy-load deserialise) are excluded from
        // the statistics so the timings are steady-state.
        RunCSharp();
        RunNative(diagnosticCounts);
        diagnosticCounts.Clear();

        var csharpRuns = new List<RunStats>(RunCount);
        var nativeRuns = new List<RunStats>(RunCount);

        for (var run = 0; run < RunCount; run++)
        {
            csharpRuns.Add(RunCSharp());
            nativeRuns.Add(RunNative(diagnosticCounts));

            if ((run + 1) % 25 == 0)
            {
                TestContext.Progress.WriteLine($"  ... {run + 1}/{RunCount} runs of each implementation done");
            }
        }

        var summary = new StringBuilder();
        summary.AppendLine($"=== Loot parity harness: location={LocationId}, N={RunCount} runs per implementation ===");
        summary.AppendLine();

        var metrics = new (string Name, Func<RunStats, double> Selector)[]
        {
            ("total spawnpoints", stats => stats.TotalSpawnpoints),
            ("container spawnpoints", stats => stats.ContainerSpawnpoints),
            ("root items", stats => stats.RootItems),
            ("mean items per container", stats => stats.MeanItemsPerContainer),
        };

        var countDeltas = new List<(string Name, double Delta)>();
        summary.AppendLine("-- counts (mean over runs) --");
        foreach (var (name, selector) in metrics)
        {
            var csharpMean = csharpRuns.Average(selector);
            var nativeMean = nativeRuns.Average(selector);
            var delta = csharpMean == 0 ? 0 : Math.Abs(nativeMean - csharpMean) / csharpMean;
            countDeltas.Add((name, delta));

            summary.AppendLine(
                $"{name, -26} C#={csharpMean, 11:F3}  Rust={nativeMean, 11:F3}  |delta|={delta * 100, 7:F3}%  (tolerance {CountTolerance * 100:F0}%)"
            );
        }

        var csharpTotals = MergeCounts(csharpRuns);
        var nativeTotals = MergeCounts(nativeRuns);
        var csharpGrandTotal = (double)csharpTotals.Values.Sum();
        var nativeGrandTotal = (double)nativeTotals.Values.Sum();

        // A tpl qualifies on either side, so a tpl Rust draws often and C# never draws still counts
        // against the distance.
        var comparedTpls = csharpTotals
            .Keys.Union(nativeTotals.Keys)
            .Where(tpl =>
                csharpTotals.GetValueOrDefault(tpl) >= FrequencyOccurrenceFloor
                || nativeTotals.GetValueOrDefault(tpl) >= FrequencyOccurrenceFloor
            )
            .ToList();

        var perTplDeltas = comparedTpls
            .Select(tpl =>
            {
                var csharpFrequency = csharpTotals.GetValueOrDefault(tpl) / csharpGrandTotal;
                var nativeFrequency = nativeTotals.GetValueOrDefault(tpl) / nativeGrandTotal;

                return (
                    Tpl: tpl,
                    Delta: Math.Abs(csharpFrequency - nativeFrequency),
                    CsharpFrequency: csharpFrequency,
                    NativeFrequency: nativeFrequency
                );
            })
            .OrderByDescending(entry => entry.Delta)
            .ToList();

        var frequencyL1 = perTplDeltas.Sum(entry => entry.Delta);

        summary.AppendLine();
        summary.AppendLine("-- per-tpl root item frequency --");
        summary.AppendLine($"distinct tpls              C#={csharpTotals.Count}  Rust={nativeTotals.Count}");
        summary.AppendLine($"root items drawn (total)   C#={csharpGrandTotal:F0}  Rust={nativeGrandTotal:F0}");
        summary.AppendLine($"tpls compared (>= {FrequencyOccurrenceFloor} draws either side): {comparedTpls.Count}");
        summary.AppendLine($"L1 distance                {frequencyL1:F5}  (tolerance {FrequencyL1Tolerance:F2})");
        summary.AppendLine("worst 10 per-tpl frequency deltas:");
        foreach (var entry in perTplDeltas.Take(10))
        {
            summary.AppendLine(
                $"  {entry.Tpl}  C#={entry.CsharpFrequency:F6} ({csharpTotals.GetValueOrDefault(entry.Tpl)})"
                    + $"  Rust={entry.NativeFrequency:F6} ({nativeTotals.GetValueOrDefault(entry.Tpl)})  delta={entry.Delta:F6}"
            );
        }

        var onlyCsharp = comparedTpls.Where(tpl => !nativeTotals.ContainsKey(tpl)).ToList();
        var onlyNative = comparedTpls.Where(tpl => !csharpTotals.ContainsKey(tpl)).ToList();
        summary.AppendLine($"tpls above the floor drawn only by C#: {onlyCsharp.Count}{FormatTplList(onlyCsharp)}");
        summary.AppendLine($"tpls above the floor drawn only by Rust: {onlyNative.Count}{FormatTplList(onlyNative)}");

        summary.AppendLine();
        summary.AppendLine("-- wall clock per run (ms) --");
        AppendTiming(summary, "C# GenerateLocationLoot", csharpRuns.Select(stats => stats.TotalMs).ToList());
        AppendTiming(summary, "native total", nativeRuns.Select(stats => stats.TotalMs).ToList());
        AppendTiming(summary, "  payload build", nativeRuns.Select(stats => stats.PayloadMs).ToList());
        AppendTiming(summary, "  of which items view", nativeRuns.Select(stats => stats.ItemsViewMs).ToList());
        AppendTiming(summary, "  FFI (json + generate)", nativeRuns.Select(stats => stats.NativeMs).ToList());
        summary.AppendLine(
            $"speedup (mean C# / mean native total): {csharpRuns.Average(stats => stats.TotalMs) / nativeRuns.Average(stats => stats.TotalMs):F2}x"
        );

        summary.AppendLine();
        summary.AppendLine($"-- native diagnostics over {RunCount} runs (level | key or message) --");
        foreach (var (diagnostic, count) in diagnosticCounts.OrderByDescending(entry => entry.Value))
        {
            summary.AppendLine($"  {count, 8}x  {diagnostic}");
        }

        TestContext.Progress.WriteLine(summary.ToString());
        TestContext.Out.WriteLine(summary.ToString());

        Assert.Multiple(() =>
        {
            foreach (var (name, delta) in countDeltas)
            {
                Assert.That(delta, Is.LessThan(CountTolerance), $"mean delta for '{name}'");
            }

            Assert.That(frequencyL1, Is.LessThan(FrequencyL1Tolerance), "per-tpl frequency L1 distance");
        });
    }

    private RunStats RunCSharp()
    {
        var stopwatch = Stopwatch.StartNew();
        var spawnpoints = _locationLootGenerator.GenerateLocationLoot(LocationId);
        stopwatch.Stop();

        return Summarise(spawnpoints, stopwatch.Elapsed.TotalMilliseconds, 0, 0, 0);
    }

    /// <summary>
    /// The native path, assembled from the same live data the C# generator reads. Mirrors
    /// <see cref="LocationLootGenerator.GenerateLocationLoot"/>'s counter tracker lifecycle:
    /// seed the limits, carry the counts from the static phase into the dynamic one, clear at the end.
    /// </summary>
    private RunStats RunNative(Dictionary<string, int> diagnosticCounts)
    {
        var total = Stopwatch.StartNew();
        var payload = Stopwatch.StartNew();

        var mapData = _locationTable.GetLocation(LocationId)!;

        var itemsViewStopwatch = Stopwatch.StartNew();
        var itemsView = BuildItemsView();
        itemsViewStopwatch.Stop();

        var staticAmmoDist = mapData.StaticAmmo.ToDictionary(entry => entry.Key, entry => entry.Value.ToList());
        var defaultPresets = _presetHelper
            .GetDefaultPresetsByTplKey()
            .ToDictionary(entry => entry.Key, entry => new PresetView { Items = entry.Value.Items });
        var moneyTpls = _itemHelper.GetMoneyTpls();
        var config = BuildConfigView();
        var seasonal = BuildSeasonalView();
        var lootableItemBlacklist = _itemFilterService.GetBlacklistedLootableItems();

        var spawnLimits = _cloner.Clone(_locationConfig.LootMaxSpawnLimits.GetValueOrDefault(LocationId));
        if (spawnLimits is not null)
        {
            _counterTrackerHelper.AddDataToTrack(spawnLimits);
        }

        // Three `.Value` reads in the C# generator become one here - LazyLoad deserialises on every
        // access, so the C# baseline pays for staticContainers.json three times per run.
        var staticContainerDetails = mapData.StaticContainers!.Value!;

        var staticRequest = new StaticContainersRequest
        {
            LocationId = LocationId,
            ItemsView = itemsView,
            DefaultPresets = defaultPresets,
            MoneyTpls = moneyTpls,
            StaticAmmoDist = staticAmmoDist,
            Config = config,
            Seasonal = seasonal,
            LootableItemBlacklist = lootableItemBlacklist,
            Counter = new CounterState
            {
                MaxCounts = _counterTrackerHelper.GetMaxCounts(),
                TrackedCounts = _counterTrackerHelper.GetTrackedCounts(),
            },
            StaticWeapons = staticContainerDetails.StaticWeapons,
            StaticContainers = staticContainerDetails.StaticContainers,
            StaticForced = staticContainerDetails.StaticForced,
            StaticLootDist = mapData.StaticLoot!.Value!,
            Statics = mapData.Statics,
        };
        payload.Stop();

        var native = Stopwatch.StartNew();
        var staticResult = SptNative.GenerateStaticContainers(staticRequest);
        native.Stop();

        _counterTrackerHelper.SetTrackedCounts(staticResult.TrackedCounts);

        payload.Start();
        var dynamicRequest = new DynamicLootRequest
        {
            LocationId = LocationId,
            ItemsView = itemsView,
            DefaultPresets = defaultPresets,
            MoneyTpls = moneyTpls,
            StaticAmmoDist = staticAmmoDist,
            Config = config,
            Seasonal = seasonal,
            LootableItemBlacklist = lootableItemBlacklist,
            Counter = new CounterState
            {
                MaxCounts = _counterTrackerHelper.GetMaxCounts(),
                TrackedCounts = _counterTrackerHelper.GetTrackedCounts(),
            },
            LooseLoot = mapData.LooseLoot!.Value!,
        };
        payload.Stop();

        native.Start();
        var dynamicResult = SptNative.GenerateDynamicLoot(dynamicRequest);
        native.Stop();

        _counterTrackerHelper.Clear();
        total.Stop();

        foreach (var diagnostic in staticResult.Diagnostics.Concat(dynamicResult.Diagnostics))
        {
            var key = $"{diagnostic.Level} | {diagnostic.LocaleKey ?? diagnostic.Message}";
            diagnosticCounts[key] = diagnosticCounts.GetValueOrDefault(key) + 1;
        }

        var spawnpoints = staticResult.Spawnpoints.Concat(dynamicResult.Spawnpoints).ToList();

        return Summarise(
            spawnpoints,
            total.Elapsed.TotalMilliseconds,
            payload.Elapsed.TotalMilliseconds,
            itemsViewStopwatch.Elapsed.TotalMilliseconds,
            native.Elapsed.TotalMilliseconds
        );
    }

    /// <summary>
    /// Every projection the native generator reads off a <c>TemplateItem</c>. Templates without
    /// props are dropped - their absence is how the native side says "lacks _props".
    /// </summary>
    private Dictionary<MongoId, ItemView> BuildItemsView()
    {
        var itemsView = new Dictionary<MongoId, ItemView>(_templateTable.Items.Count);

        foreach (var (tpl, template) in _templateTable.Items)
        {
            var props = template.Properties;
            if (props is null)
            {
                continue;
            }

            var firstGrid = props.Grids?.FirstOrDefault();
            var firstStackSlot = props.StackSlots?.FirstOrDefault();
            var firstCartridgeSlot = props.Cartridges?.FirstOrDefault();
            var firstChamber = props.Chambers?.FirstOrDefault();
            var stackSlotFilter = firstStackSlot?.Properties?.Filters?.FirstOrDefault()?.Filter;

            itemsView[tpl] = new ItemView
            {
                // Cast needed on both arms: MongoId's implicit string conversion otherwise turns the
                // null arm into a default MongoId instead of leaving the member absent.
                Parent = template.Parent.IsEmpty ? null : (MongoId?)template.Parent,
                Width = props.Width,
                Height = props.Height,
                StackMaxSize = props.StackMaxSize,
                StackMinRandom = props.StackMinRandom,
                StackMaxRandom = props.StackMaxRandom,
                ExtraSizeUp = props.ExtraSizeUp,
                ExtraSizeDown = props.ExtraSizeDown,
                ExtraSizeLeft = props.ExtraSizeLeft,
                ExtraSizeRight = props.ExtraSizeRight,
                ExtraSizeForceAdd = props.ExtraSizeForceAdd,
                GridCellsH = firstGrid?.Properties?.CellsH,
                GridCellsV = firstGrid?.Properties?.CellsV,
                StackSlotMaxCount = firstStackSlot?.MaxCount,
                StackSlotFirstFilterFirst = stackSlotFilter is { Count: > 0 } ? (MongoId?)stackSlotFilter.First() : null,
                CartridgesMaxCount = firstCartridgeSlot?.MaxCount,
                CartridgesFirstFilter = firstCartridgeSlot?.Properties?.Filters?.FirstOrDefault()?.Filter,
                ChambersFirstFilter = firstChamber?.Properties?.Filters?.FirstOrDefault()?.Filter,
                Slots = props
                    .Slots?.Select(slot => new SlotView
                    {
                        Name = slot.Name,
                        Required = slot.Required,
                        Filter = slot.Properties?.Filters?.FirstOrDefault()?.Filter,
                    })
                    .ToList(),
                ConflictingItems = props.ConflictingItems,
                Caliber = props.Caliber,
                AmmoCaliber = props.AmmoCaliber,
                DefAmmo = props.DefAmmo,
            };
        }

        return itemsView;
    }

    private LootConfigView BuildConfigView()
    {
        var randomisation = _locationConfig.ContainerRandomisationSettings;

        return new LootConfigView
        {
            ContainerRandomisationEnabled = randomisation.Enabled,
            LocationInRandomisationMaps = randomisation.Maps.ContainsKey(LocationId),
            ContainerTypesToNotRandomise = randomisation.ContainerTypesToNotRandomise,
            ContainerGroupMinSizeMultiplier = randomisation.ContainerGroupMinSizeMultiplier,
            ContainerGroupMaxSizeMultiplier = randomisation.ContainerGroupMaxSizeMultiplier,
            AllowDuplicateItemsInStaticContainers = _locationConfig.AllowDuplicateItemsInStaticContainers,
            TplsToStripChildItemsFrom = _locationConfig.TplsToStripChildItemsFrom,
            FitLootIntoContainerAttempts = _locationConfig.FitLootIntoContainerAttempts,
            MagazineLootHasAmmoChancePercent = _locationConfig.MagazineLootHasAmmoChancePercent,
            StaticMagazineLootHasAmmoChancePercent = _locationConfig.StaticMagazineLootHasAmmoChancePercent,
            MinFillLooseMagazinePercent = _locationConfig.MinFillLooseMagazinePercent,
            MinFillStaticMagazinePercent = _locationConfig.MinFillStaticMagazinePercent,
            StaticLootMultiplier = MultiplierForLocation(_locationConfig.StaticLootMultiplier),
            LooseLootMultiplier = MultiplierForLocation(_locationConfig.LooseLootMultiplier),
            ModSpawnChancePercent = _locationConfig.EquipmentLootSettings.ModSpawnChancePercent,
            LooseLootBlacklist = _locationConfig.LooseLootBlacklist.GetValueOrDefault(LocationId) ?? [],
        };
    }

    private SeasonalView BuildSeasonalView()
    {
        return new SeasonalView
        {
            SeasonalEventActive = _seasonalEventService.SeasonalEventEnabled(),
            ChristmasEventEnabled = _seasonalEventService.ChristmasEventEnabled(),
            InactiveSeasonalItems = _seasonalEventService.GetInactiveSeasonalEventItems(),
            ChristmasContainerIds = _seasonalEventConfig.ChristmasContainerIds,
        };
    }

    private static double MultiplierForLocation(Dictionary<string, double> multipliers)
    {
        return multipliers.TryGetValue(LocationId, out var multiplier) ? multiplier : multipliers["default"];
    }

    /// <summary>
    /// Order-independent statistics for one run. Spawn point ordering is deliberately not captured.
    /// </summary>
    private static RunStats Summarise(
        List<SpawnpointTemplate> spawnpoints,
        double totalMs,
        double payloadMs,
        double itemsViewMs,
        double nativeMs
    )
    {
        var rootTplCounts = new Dictionary<MongoId, int>();
        var rootItems = 0;
        var containerSpawnpoints = 0;
        var containerItems = 0;

        foreach (var spawnpoint in spawnpoints)
        {
            var items = spawnpoint.Items?.ToList() ?? [];

            if (spawnpoint.IsContainer ?? false)
            {
                containerSpawnpoints++;
                containerItems += items.Count;
            }

            foreach (var item in items)
            {
                // Top-of-tree items only: a loose loot root or a container's own item has no parent,
                // and loot placed into a container sits in slot "main". Mods and cartridges sit in
                // named slots under one of those, so they are excluded.
                if (item.ParentId is not null && item.SlotId != "main")
                {
                    continue;
                }

                rootItems++;
                rootTplCounts[item.Template] = rootTplCounts.GetValueOrDefault(item.Template) + 1;
            }
        }

        return new RunStats(
            spawnpoints.Count,
            containerSpawnpoints,
            rootItems,
            containerSpawnpoints == 0 ? 0 : (double)containerItems / containerSpawnpoints,
            rootTplCounts,
            totalMs,
            payloadMs,
            itemsViewMs,
            nativeMs
        );
    }

    private static Dictionary<MongoId, int> MergeCounts(List<RunStats> runs)
    {
        var totals = new Dictionary<MongoId, int>();

        foreach (var (tpl, count) in runs.SelectMany(run => run.RootTplCounts))
        {
            totals[tpl] = totals.GetValueOrDefault(tpl) + count;
        }

        return totals;
    }

    private static string FormatTplList(List<MongoId> tpls)
    {
        return tpls.Count == 0 ? string.Empty : $" [{string.Join(", ", tpls.Take(10))}]";
    }

    private static void AppendTiming(StringBuilder summary, string name, List<double> samples)
    {
        var sorted = samples.Order().ToList();

        summary.AppendLine(
            $"{name, -26} mean={samples.Average(), 9:F2}  median={sorted[sorted.Count / 2], 9:F2}"
                + $"  min={sorted[0], 9:F2}  max={sorted[^1], 9:F2}"
        );
    }

    private sealed record RunStats(
        int TotalSpawnpoints,
        int ContainerSpawnpoints,
        int RootItems,
        double MeanItemsPerContainer,
        Dictionary<MongoId, int> RootTplCounts,
        double TotalMs,
        double PayloadMs,
        double ItemsViewMs,
        double NativeMs
    );
}
