using System.Text.Json;
using Microsoft.Extensions.Logging;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.InRaid;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Native;
using SPTarkov.Server.Core.Native.Loot;
using SPTarkov.Server.Core.Services.Items;
using SPTarkov.Server.Core.Services.Locales;
using SPTarkov.Server.Core.Services.Server;
using SPTarkov.Server.Core.Utils.Cloners;

namespace SPTarkov.Server.Core.Generators.Loot;

/// <summary>
/// Location loot generation lives in <c>rust/spt-native/src/loot/location_loot_generator.rs</c>; this
/// class projects the live database and config into the native payload, hands it over and replays the
/// log lines the native side would have written itself.
/// </summary>
[Injectable]
public class LocationLootGenerator(
    ISptLogger<LocationLootGenerator> logger,
    LocationTable locationTable,
    TemplateTable templateTable,
    ItemHelper itemHelper,
    PresetHelper presetHelper,
    ServerLocalisationService serverLocalisationService,
    SeasonalEventService seasonalEventService,
    ItemFilterService itemFilterService,
    LocationConfig locationConfig,
    SeasonalEventConfig seasonalEventConfig,
    CounterTrackerHelper counterTrackerHelper,
    ICloner cloner
)
{
    /// <summary>
    /// Generate Loot for provided location ()
    /// </summary>
    /// <param name="locationId">Id of location (e.g. bigmap/factory4_day)</param>
    /// <returns>Collection of spawn points with loot</returns>
    public List<SpawnpointTemplate> GenerateLocationLoot(string locationId)
    {
        var result = new List<SpawnpointTemplate>();

        // Get generation details for location from db
        var locationDetails = locationTable.GetLocation(locationId);
        if (locationDetails is null)
        {
            logger.Error($"Location: {locationId} not found in database, generated 0 loot items");
            return result;
        }

        // Clone ammo data to ensure any changes don't affect the db values
        var staticAmmoDistClone = cloner.Clone(locationDetails.StaticAmmo);

        // Pull location-specific spawn limits from db
        var itemsWithSpawnCountLimitsClone = cloner.Clone(
            locationConfig.LootMaxSpawnLimits.GetValueOrDefault(locationId.ToLowerInvariant())
        );

        // Store items with spawn count limits inside so they can be accessed later inside static/dynamic loot spawn methods
        // The clone is load-bearing: the tracker holds onto this dictionary by reference and empties it on Clear()
        if (itemsWithSpawnCountLimitsClone is not null)
        {
            counterTrackerHelper.AddDataToTrack(itemsWithSpawnCountLimitsClone);
        }

        // Create containers with loot
        result.AddRange(GenerateStaticContainers(locationId.ToLowerInvariant(), staticAmmoDistClone));

        // Nothing has asked to transform the loose loot, so skip materialising it: a null argument
        // sends the location's file over as raw JSON instead of parsing 42 MB of loot into objects
        // only to encode it straight back out. A registered transformer takes the typed path.
        var looseLoot = locationDetails.LooseLoot;
        var untransformed = looseLoot is { HasTransformers: false, HasRawJson: true };

        // Add dynamic loot to output loot
        var dynamicSpawnPoints = GenerateDynamicLoot(
            untransformed ? null : looseLoot?.Value,
            staticAmmoDistClone,
            locationId.ToLowerInvariant()
        );

        // Merge dynamic spawns into result
        result.AddRange(dynamicSpawnPoints);

        logger.Success(serverLocalisationService.GetText("location-dynamic_items_spawned_success", dynamicSpawnPoints.Count));
        logger.Success(serverLocalisationService.GetText("location-generated_success", locationId));

        // Clean up tracker
        counterTrackerHelper.Clear();

        return result;
    }

    /// Create a list of container objects with randomised loot
    /// <param name="locationId">Location to generate for</param>
    /// <param name="staticAmmoDist">Static ammo distribution</param>
    /// <returns>List of container objects</returns>
    public List<SpawnpointTemplate> GenerateStaticContainers(
        string locationId,
        Dictionary<string, IEnumerable<StaticAmmoDetails>> staticAmmoDist
    )
    {
        var mapData = locationTable.GetLocation(locationId);

        // Every `.Value` re-runs the lazy load, so each file is read exactly once per call
        var staticContainerDetails = mapData!.StaticContainers!.Value!;
        var common = BuildCommonPayload(locationId, staticAmmoDist);

        var result = SptNative.GenerateStaticContainers(
            new StaticContainersRequest
            {
                LocationId = common.LocationId,
                ItemsView = common.ItemsView,
                DefaultPresets = common.DefaultPresets,
                MoneyTpls = common.MoneyTpls,
                StaticAmmoDist = common.StaticAmmoDist,
                Config = common.Config,
                Seasonal = common.Seasonal,
                LootableItemBlacklist = common.LootableItemBlacklist,
                Counter = common.Counter,
                // Nulls are passed through, not replaced with empty lists - the native side logs a
                // map-specific error for each missing list
                StaticWeapons = staticContainerDetails.StaticWeapons,
                StaticContainers = staticContainerDetails.StaticContainers,
                StaticForced = staticContainerDetails.StaticForced,
                StaticLootDist = mapData.StaticLoot!.Value!,
                Statics = mapData.Statics,
            }
        );

        ReplayDiagnostics(result.Diagnostics);

        // Carry the counts the native side reached into the dynamic phase
        counterTrackerHelper.SetTrackedCounts(result.TrackedCounts);

        return result.Spawnpoints;
    }

    /// <summary>
    ///     Create array of loose + forced loot using probability system
    /// </summary>
    /// <param name="dynamicLootDist">
    ///     Loot data to generate from, or null to use the location's own <c>looseLoot.json</c> as the
    ///     raw JSON it sits on disk as - only valid when nothing transforms that file
    /// </param>
    /// <param name="staticAmmoDist"></param>
    /// <param name="locationName">Location to generate loot for</param>
    /// <returns>Array of spawn points with loot in them</returns>
    public List<SpawnpointTemplate> GenerateDynamicLoot(
        LooseLoot? dynamicLootDist,
        Dictionary<string, IEnumerable<StaticAmmoDetails>> staticAmmoDist,
        string locationName
    )
    {
        var common = BuildCommonPayload(locationName, staticAmmoDist);

        var result = SptNative.GenerateDynamicLoot(
            new DynamicLootRequest
            {
                LocationId = common.LocationId,
                ItemsView = common.ItemsView,
                DefaultPresets = common.DefaultPresets,
                MoneyTpls = common.MoneyTpls,
                StaticAmmoDist = common.StaticAmmoDist,
                Config = common.Config,
                Seasonal = common.Seasonal,
                LootableItemBlacklist = common.LootableItemBlacklist,
                Counter = common.Counter,
                // The caller's loot data, so any transformer or patch applied to it is honoured
                LooseLoot = dynamicLootDist is null ? RawLooseLootJson(locationName) : dynamicLootDist,
            }
        );

        ReplayDiagnostics(result.Diagnostics);

        // Keep the tracker in step with what the native side counted
        counterTrackerHelper.SetTrackedCounts(result.TrackedCounts);

        return result.Spawnpoints;
    }

    /// <summary>
    /// A location's loose loot as the raw JSON it sits on disk as. Only equivalent to
    /// <c>LazyLoad.Value</c> while no transformer is registered, which is what makes it safe to
    /// splice: with none registered the file is if anything the more faithful of the two, since
    /// explicit nulls and members the C# models do not declare survive it. Throws rather than
    /// quietly generating from nothing when the raw JSON is not usable.
    /// </summary>
    private LooseLootPayload RawLooseLootJson(string locationId)
    {
        var looseLoot = locationTable.GetLocation(locationId)?.LooseLoot;
        var rawJson = looseLoot is { HasTransformers: false } ? looseLoot.ReadRawJson() : null;

        if (rawJson is null)
        {
            throw new InvalidOperationException(
                $"Location: {locationId} has no raw loose loot JSON to generate from - it is missing, or a transformer is "
                    + "registered on it. Pass the LooseLoot to generate from instead of null."
            );
        }

        return LooseLootPayload.FromRawJson(rawJson.Value);
    }

    /// <summary>
    /// Everything both generation calls need from the live database, services and config, resolved
    /// for one location. Stateless: nothing here is cached between calls, so a mod that swaps an
    /// item, a config value or a seasonal state is picked up on the next raid.
    /// </summary>
    private LootCommon BuildCommonPayload(string locationId, Dictionary<string, IEnumerable<StaticAmmoDetails>> staticAmmoDist)
    {
        return new LootCommon
        {
            LocationId = locationId,
            ItemsView = BuildItemsView(),
            DefaultPresets = presetHelper
                .GetDefaultPresetByTpl()
                .ToDictionary(preset => preset.Key, preset => new PresetView { Items = preset.Value.Items }),
            MoneyTpls = itemHelper.GetMoneyTpls(),
            StaticAmmoDist = staticAmmoDist.ToDictionary(caliber => caliber.Key, caliber => caliber.Value.ToList()),
            Config = BuildConfigView(locationId),
            Seasonal = new SeasonalView
            {
                SeasonalEventActive = seasonalEventService.SeasonalEventEnabled(),
                ChristmasEventEnabled = seasonalEventService.ChristmasEventEnabled(),
                InactiveSeasonalItems = seasonalEventService.GetInactiveSeasonalEventItems(),
                ChristmasContainerIds = seasonalEventConfig.ChristmasContainerIds,
            },
            // The cache, not the config list: it also holds anything a mod blacklisted at runtime
            LootableItemBlacklist = itemFilterService.GetLootableItemBlacklistCache(),
            Counter = new CounterState
            {
                MaxCounts = counterTrackerHelper.GetMaxCounts(),
                TrackedCounts = counterTrackerHelper.GetTrackedCounts(),
            },
        };
    }

    /// <summary>
    /// Every projection the native generator reads off a <c>TemplateItem</c>, in one pass over the
    /// live items table. Templates without props are dropped - their absence is how the native side
    /// says "lacks _props".
    /// </summary>
    private Dictionary<MongoId, ItemView> BuildItemsView()
    {
        var itemsView = new Dictionary<MongoId, ItemView>(templateTable.Items.Count);

        foreach (var (tpl, template) in templateTable.Items)
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
                // null arm into a default MongoId instead of leaving the member absent
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
                // Deliberate divergence: an empty filter set is sent as null rather than as the
                // empty MongoId `Filter?.FirstOrDefault()` would have produced. Never fires on
                // vanilla data - a stack slot with an empty filter has nothing to stack
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

    private LootConfigView BuildConfigView(string locationId)
    {
        var randomisation = locationConfig.ContainerRandomisationSettings;

        return new LootConfigView
        {
            ContainerRandomisationEnabled = randomisation.Enabled,
            LocationInRandomisationMaps = randomisation.Maps.ContainsKey(locationId),
            ContainerTypesToNotRandomise = randomisation.ContainerTypesToNotRandomise,
            ContainerGroupMinSizeMultiplier = randomisation.ContainerGroupMinSizeMultiplier,
            ContainerGroupMaxSizeMultiplier = randomisation.ContainerGroupMaxSizeMultiplier,
            AllowDuplicateItemsInStaticContainers = locationConfig.AllowDuplicateItemsInStaticContainers,
            TplsToStripChildItemsFrom = locationConfig.TplsToStripChildItemsFrom,
            FitLootIntoContainerAttempts = locationConfig.FitLootIntoContainerAttempts,
            MagazineLootHasAmmoChancePercent = locationConfig.MagazineLootHasAmmoChancePercent,
            StaticMagazineLootHasAmmoChancePercent = locationConfig.StaticMagazineLootHasAmmoChancePercent,
            MinFillLooseMagazinePercent = locationConfig.MinFillLooseMagazinePercent,
            MinFillStaticMagazinePercent = locationConfig.MinFillStaticMagazinePercent,
            StaticLootMultiplier = MultiplierForLocation(locationConfig.StaticLootMultiplier, locationId),
            LooseLootMultiplier = MultiplierForLocation(locationConfig.LooseLootMultiplier, locationId),
            ModSpawnChancePercent = locationConfig.EquipmentLootSettings.ModSpawnChancePercent,
            LooseLootBlacklist = locationConfig.LooseLootBlacklist.GetValueOrDefault(locationId) ?? [],
        };
    }

    /// <summary>
    /// Get the multiplier for this location, or the default if the location has no entry. A config
    /// missing its "default" entry throws, as it always has
    /// </summary>
    private static double MultiplierForLocation(Dictionary<string, double> multipliers, string locationId)
    {
        return multipliers.TryGetValue(locationId, out var multiplier) ? multiplier : multipliers["default"];
    }

    /// <summary>
    /// Write out the log lines the native generator collected instead of logging itself, so the
    /// server log reads as it did before the cutover
    /// </summary>
    private void ReplayDiagnostics(List<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Level == "debug" && !logger.IsLogEnabled(LogLevel.Debug))
            {
                continue;
            }

            var message = LocaliseDiagnostic(diagnostic);
            switch (diagnostic.Level)
            {
                case "debug":
                    logger.Debug(message);
                    break;
                case "warning":
                    logger.Warning(message);
                    break;
                case "error":
                    logger.Error(message);
                    break;
                case "success":
                    logger.Success(message);
                    break;
                default:
                    // Never drop a line a future native version tags with a level we don't know
                    logger.Warning($"[{diagnostic.Level}] {message}");
                    break;
            }
        }
    }

    private string LocaliseDiagnostic(Diagnostic diagnostic)
    {
        if (diagnostic.LocaleKey is null)
        {
            return diagnostic.Message ?? string.Empty;
        }

        if (diagnostic.Args is not { } args)
        {
            return serverLocalisationService.GetText(diagnostic.LocaleKey);
        }

        // A scalar argument is the `%s` overload
        if (args.ValueKind != JsonValueKind.Object)
        {
            return serverLocalisationService.GetText(diagnostic.LocaleKey, args.ToString());
        }

        // Named arguments are substituted here rather than by ServerLocalisationService: it reads its
        // args object's *properties* by reflection, which only works for the anonymous types the C#
        // call sites passed - a dictionary would leave every `{{placeholder}}` in place
        var text = serverLocalisationService.GetText(diagnostic.LocaleKey);
        foreach (var argument in args.EnumerateObject())
        {
            text = text.Replace($"{{{{{argument.Name}}}}}", argument.Value.ToString());
        }

        return text;
    }
}
