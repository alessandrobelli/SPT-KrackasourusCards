using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace KrackKards;

/// <summary>
/// Totals for one injection pass, reported by the caller. Containers counts one entry per
/// map-and-container pair, since each map holds its own distribution for a container type;
/// ContainerTypes counts the distinct types behind them.
/// </summary>
internal readonly record struct StaticLootReport(int Maps, int Containers, int ContainerTypes, int Entries);

/// <summary>
/// Adds KrackKards items to the static loot distributions of the containers their own item
/// config already lists under "loot_locations", so nothing here decides where cards belong.
///
/// Location.StaticLoot is a LazyLoad in 4.1: reading .Value re-deserialises the map's loot from
/// disk and then replays every registered transformer. So the work is registered as one
/// transformer per map and runs whenever a raid actually loads that map, never at startup.
/// </summary>
internal sealed class StaticLootInjector
{
    /// <summary>
    /// Share of a container's item rolls that land on a KrackKards item before the global and
    /// per-container multipliers apply. With the shipped multiplier of 0.5 that works out at 1%.
    /// </summary>
    private const double BaseGroupChance = 0.02;

    /// <summary>Cards never take more than this share of a container, whatever the config asks for.</summary>
    private const double MaxGroupChance = 0.5;

    private readonly ISptLogger<KrackKardsMod> _logger;
    private readonly LocationTable _locationTable;
    private readonly StaticLootConfig _config;
    private readonly Dictionary<MongoId, double> _containerMultipliers;

    public StaticLootInjector(ISptLogger<KrackKardsMod> logger, LocationTable locationTable, StaticLootConfig config)
    {
        _logger = logger;
        _locationTable = locationTable;
        _config = config;

        // System.Text.Json assigns a JSON null straight over a property initialiser, so an author
        // who writes "container_multipliers": null gets a null dictionary rather than an empty one.
        // That deserialises without error, so the caller's fallback cannot catch it.
        config.ContainerMultipliers ??= [];
        config.RarityWeights ??= [];

        // Resolve the config's container keys to MongoIds once, so the hot path compares ids
        // rather than re-parsing strings for every container on every map load.
        _containerMultipliers = new Dictionary<MongoId, double>();
        foreach (var (key, multiplier) in config.ContainerMultipliers)
        {
            if (MongoId.IsValidMongoId(key))
                _containerMultipliers[new MongoId(key)] = multiplier;
            else
                _logger.Warning($"[KrackKards] static_loot.json: '{key}' is not a container template id, ignoring");
        }
    }

    /// <summary>Schedule every eligible item into the containers its config names.</summary>
    public StaticLootReport Inject(IReadOnlyList<KrackItemConfig> items)
    {
        if (!_config.EnableContainerSpawns)
        {
            _logger.Info("[KrackKards] Static loot spawns are off (enable_container_spawns = false)");
            return default;
        }

        var plan = BuildPlan(items);

        int maps = 0, containers = 0, entries = 0;
        var containerTypes = new HashSet<MongoId>();
        var skippedMaps = new List<string>();

        foreach (var (mapName, containerPlan) in plan)
        {
            // GetLocation maps "rezervbase"/"sandbox_high" onto the table's property names.
            var location = _locationTable.GetLocation(mapName);
            if (location?.StaticLoot == null)
            {
                skippedMaps.Add(mapName);
                continue;
            }

            location.StaticLoot.AddTransformer(staticLoot => ApplyToMap(staticLoot, containerPlan));

            maps++;
            containers += containerPlan.Count;
            containerTypes.UnionWith(containerPlan.Keys);
            entries += containerPlan.Values.Sum(byItem => byItem.Count);
        }

        if (skippedMaps.Count > 0)
            _logger.Warning($"[KrackKards] No static loot on {string.Join(", ", skippedMaps)}, skipped");

        return new StaticLootReport(maps, containers, containerTypes.Count, entries);
    }

    /// <summary>
    /// map name -> container template -> item template -> relative weight inside the card pool.
    /// Keying by template dedupes an item that lists the same container twice for one map.
    /// </summary>
    private Dictionary<string, Dictionary<MongoId, Dictionary<MongoId, double>>> BuildPlan(
        IReadOnlyList<KrackItemConfig> items)
    {
        var plan = new Dictionary<string, Dictionary<MongoId, Dictionary<MongoId, double>>>(StringComparer.OrdinalIgnoreCase);

        foreach (var cfg in items)
        {
            // "lootable" is the item author's own switch: the packs, binders and card case all
            // set it false, so only the cards and the anime cases reach static loot.
            if (!cfg.Lootable || cfg.LootLocations is not { Count: > 0 })
                continue;

            var weight = RarityWeight(cfg);
            if (weight <= 0)
                continue;

            var itemTpl = new MongoId(cfg.Id);

            foreach (var (mapName, containerTpls) in cfg.LootLocations)
            {
                if (containerTpls is not { Count: > 0 })
                    continue;

                foreach (var rawContainer in containerTpls)
                {
                    if (!MongoId.IsValidMongoId(rawContainer))
                    {
                        _logger.Warning($"[KrackKards] {cfg.Id} lists '{rawContainer}' on {mapName}, which is not a container template id");
                        continue;
                    }

                    var containerTpl = new MongoId(rawContainer);
                    if (ContainerMultiplier(containerTpl) <= 0)
                        continue;

                    if (!plan.TryGetValue(mapName, out var byContainer))
                        plan[mapName] = byContainer = new Dictionary<MongoId, Dictionary<MongoId, double>>();

                    if (!byContainer.TryGetValue(containerTpl, out var byItem))
                        byContainer[containerTpl] = byItem = new Dictionary<MongoId, double>();

                    byItem[itemTpl] = weight;
                }
            }
        }

        return plan;
    }

    /// <summary>
    /// Runs on every map load, against a freshly deserialised copy of that map's static loot,
    /// so it appends to pristine data each time rather than compounding earlier runs.
    /// </summary>
    private Dictionary<MongoId, StaticLootDetails>? ApplyToMap(
        Dictionary<MongoId, StaticLootDetails>? staticLoot,
        Dictionary<MongoId, Dictionary<MongoId, double>> containerPlan)
    {
        if (staticLoot == null)
            return staticLoot;

        foreach (var (containerTpl, cardWeights) in containerPlan)
        {
            if (!staticLoot.TryGetValue(containerTpl, out var details) || details == null)
                continue;

            // LocationLootGenerator.GetPossibleLootItemsForContainer reads
            // ItemDistribution.RelativeProbability.Value with no null check, so an entry that
            // has no weight throws the moment the container is rolled. Keep them out of any
            // list this transformer emits.
            var distribution = new List<ItemDistribution>();
            var unweighted = new List<MongoId>();
            foreach (var entry in details.ItemDistribution ?? [])
            {
                if (entry is null)
                    continue;

                if (entry.RelativeProbability is null)
                {
                    unweighted.Add(entry.Tpl);
                    continue;
                }

                distribution.Add(entry);
            }

            // A container's weights are relative, so the existing total is what fixes the value
            // of one unit of weight. Without it there is nothing to size the cards against.
            var mass = distribution.Sum(entry => (double)(entry.RelativeProbability ?? 0f));
            if (mass <= 0)
                continue;

            var groupChance = Math.Clamp(
                BaseGroupChance * _config.CardWeightMultiplier * ContainerMultiplier(containerTpl),
                0,
                MaxGroupChance);
            if (groupChance <= 0)
                continue;

            var weightSum = cardWeights.Values.Sum();
            if (weightSum <= 0)
                continue;

            // Solve added / (mass + added) == groupChance, so the chance a roll from this
            // container yields any KrackKards item is exactly groupChance.
            var addedMass = groupChance * mass / (1 - groupChance);

            var present = distribution.Select(entry => entry.Tpl).ToHashSet();
            foreach (var (itemTpl, weight) in cardWeights)
            {
                if (!present.Add(itemTpl))
                    continue;

                // Set the weight explicitly and only queue the entry once it is known good, so
                // this never contributes an item the loot generator would fall over reading.
                var relativeProbability = (float)(addedMass * weight / weightSum);
                if (!float.IsFinite(relativeProbability) || relativeProbability <= 0)
                {
                    _logger.Warning(
                        $"[KrackKards] Not adding {itemTpl} to container {containerTpl}: " +
                        $"computed relativeProbability {relativeProbability} is not usable");
                    continue;
                }

                distribution.Add(new ItemDistribution
                {
                    Tpl                 = itemTpl,
                    RelativeProbability = relativeProbability
                });
            }

            foreach (var tpl in unweighted)
                _logger.Warning(
                    $"[KrackKards] Dropped {tpl} from container {containerTpl}: it has no " +
                    "relativeProbability, which the loot generator cannot read");

            staticLoot[containerTpl] = details with { ItemDistribution = distribution };
        }

        return staticLoot;
    }

    /// <summary>Rarity weight for an item, falling back to flat when the rarity is unknown.</summary>
    private double RarityWeight(KrackItemConfig cfg)
        => cfg.Rarity != null && _config.RarityWeights.TryGetValue(cfg.Rarity, out var weight) ? weight : 1.0;

    private double ContainerMultiplier(MongoId containerTpl)
        => _containerMultipliers.TryGetValue(containerTpl, out var multiplier) ? multiplier : 1.0;
}
