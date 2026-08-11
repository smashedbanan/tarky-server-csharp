using NUnit.Framework;
using SPTarkov.Server.Core.Generators.Loot;
using SPTarkov.Server.Core.Utils;

namespace UnitTests.Tests.Generators;

/// <summary>
/// Smoke test for the whole native loot path: the payload builder reading the live database, the two
/// generation calls, and the diagnostic replay through <c>ServerLocalisationService</c>.
/// <see cref="LocationLootGeneratorNativeTests"/> pins the wire contract on synthetic data; this
/// fixture is what catches a projection that is wrong only for real game data.
/// </summary>
[TestFixture]
public class LocationLootGeneratorTests
{
    private const string LocationId = "bigmap";

    private LocationLootGenerator _locationLootGenerator = default!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var di = DI.GetInstance();

        // Publishes the static JsonSerializerOptions SptNative serialises payloads with
        _ = di.GetService<JsonUtil>();

        _locationLootGenerator = di.GetService<LocationLootGenerator>();
    }

    [Test]
    public void GenerateLocationLootFillsAMapWithStaticAndLooseLoot()
    {
        var spawnpoints = _locationLootGenerator.GenerateLocationLoot(LocationId);

        var containers = spawnpoints.Where(spawnpoint => spawnpoint.IsContainer ?? false).ToList();
        var loosePoints = spawnpoints.Where(spawnpoint => !(spawnpoint.IsContainer ?? false)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(containers, Is.Not.Empty, "no container spawn points were generated");
            Assert.That(loosePoints, Is.Not.Empty, "no loose loot spawn points were generated");
            Assert.That(
                containers.Sum(container => container.Items!.Count()),
                Is.GreaterThan(containers.Count),
                "no loot went into any container"
            );
            Assert.That(spawnpoints.All(spawnpoint => spawnpoint.Items?.Any() ?? false), "a spawn point came back with no items at all");
        });
    }

    /// <summary>
    /// The generator is stateless per call, so a second raid on the same map generates just as much
    /// loot - a leaked spawn-limit tracker or a mutated database would show up as an empty result.
    /// </summary>
    [Test]
    public void GenerateLocationLootIsRepeatable()
    {
        var first = _locationLootGenerator.GenerateLocationLoot(LocationId);
        var second = _locationLootGenerator.GenerateLocationLoot(LocationId);

        Assert.That(second, Has.Count.GreaterThan(first.Count / 2));
    }
}
