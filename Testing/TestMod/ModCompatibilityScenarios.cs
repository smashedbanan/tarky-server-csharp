using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Services.Locales;
using SPTarkov.Server.Core.Utils;

namespace TestMod;

/// <summary>
/// Scenario classes for the server's ModCompatibilityTests. Each one exercises a distinct
/// mod-pipeline guarantee; none of them are used by the TestMod's own runtime behavior.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Watermark + 1)]
public class TestModWatermarkOverride(
    ISptLogger<Watermark> logger,
    ServerLocalisationService serverLocalisationService,
    WatermarkLocale watermarkLocale,
    CoreConfig coreConfig
) : Watermark(logger, serverLocalisationService, watermarkLocale, coreConfig);
