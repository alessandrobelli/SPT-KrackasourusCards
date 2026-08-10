using System.Text.Json.Serialization;

namespace KrackKards;

/// <summary>
/// Schema of config/static_loot.json. Controls whether KrackKards items appear in the
/// static loot containers their own item config already names under "loot_locations".
/// </summary>
public class StaticLootConfig
{
    /// <summary>Master switch. When false nothing is added to any container.</summary>
    [JsonPropertyName("enable_container_spawns")]
    public bool EnableContainerSpawns { get; set; } = true;

    /// <summary>
    /// Scales how often cards turn up, across every container and map.
    /// 0 disables spawns, 1 is roughly twice the shipped default, 0 to 25 is the useful range.
    /// </summary>
    [JsonPropertyName("card_weight_multiplier")]
    public double CardWeightMultiplier { get; set; } = 0.5;

    /// <summary>
    /// Per-container-type multipliers keyed by container template id. Missing entries are 1.0,
    /// 0 switches that container type off. Only scales containers a card already lists in its
    /// own "loot_locations" - it cannot add new ones.
    /// </summary>
    [JsonPropertyName("container_multipliers")]
    public Dictionary<string, double> ContainerMultipliers { get; set; } = new();

    /// <summary>
    /// Relative weights per card rarity, splitting each container's card budget. A rarity that
    /// is absent here weighs 1.0, so an empty object gives every card the same odds. 0 drops a
    /// rarity from static loot entirely.
    /// </summary>
    [JsonPropertyName("rarity_weights")]
    public Dictionary<string, double> RarityWeights { get; set; } = new();
}
