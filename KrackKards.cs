using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Modding.Custom;
using Path = System.IO.Path;

namespace KrackKards;

// 4.0 ran this at OnLoadOrder.PostDBModLoader + 1, which sat between GameCallbacks and
// TraderRegistration. 4.1 dropped the Database/PostDBModLoader stages (the database is
// imported before the DI container is built now), so the equivalent slot is
// GameCallbacks + 1: after PostDbLoadService, still ahead of TraderRegistration,
// HandbookCallbacks, TraderCallbacks and RagfairCallbacks.
[Injectable(TypePriority = OnLoadOrder.GameCallbacks + 1)]
public class KrackKardsMod(
    ISptLogger<KrackKardsMod> logger,
    ModHelper modHelper,
    TemplateTable templateTable,
    TradersTable tradersTable,
    RagfairConfig ragfairConfig,
    InventoryConfig inventoryConfig,
    CustomItemService customItemService
) : IOnLoad
{
    private const string RagmanId  = "5ac3b934156ae10c4430e83c";
    private const string RoublesId = "5449016a4bdc2d6f028b456f";

    private int _itemsRegistered;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true
    };

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var ragman    = tradersTable[new MongoId(RagmanId)];

        // Collect all card IDs so the card case can accept them all
        var allCardIds = new List<string>();

        // ── Anime cards ──────────────────────────────────────────────────────
        foreach (var file in EnumJson(pathToMod, "config", "anime", "collectables", "cards"))
            ProcessCard(file, ragman, allCardIds);

        foreach (var file in EnumJson(pathToMod, "config", "anime", "collectables", "packs"))
            ProcessPack(file, ragman);

        foreach (var file in EnumJson(pathToMod, "config", "anime", "cases"))
            ProcessBinder(file, ragman);

        // ── Pokemon cards ────────────────────────────────────────────────────
        foreach (var file in EnumJson(pathToMod, "config", "pokemon", "cards"))
            ProcessCard(file, ragman, allCardIds);

        foreach (var file in EnumJson(pathToMod, "config", "pokemon", "packs"))
            ProcessPack(file, ragman);

        foreach (var file in EnumJson(pathToMod, "config", "pokemon", "binders"))
            ProcessBinder(file, ragman);

        // ── Card case ────────────────────────────────────────────────────────
        var casePath = Path.Combine(pathToMod, "config", "cases", "card_case.json");
        if (File.Exists(casePath))
            ProcessCardCase(casePath, ragman, allCardIds);

        logger.Info($"[KrackKards] Registered {_itemsRegistered} items ({allCardIds.Count} cards)");
        logger.Success("[KrackKards] Gotta find 'em all!");
        return Task.CompletedTask;
    }

    // ── Per-type processing ──────────────────────────────────────────────────

    private void ProcessCard(string file, Trader ragman, List<string> allCardIds)
    {
        var cfg = Load(file);
        if (cfg == null) return;
        if (!RegisterItem(cfg, ragman)) return;
        allCardIds.Add(cfg.Id);
    }

    private void ProcessPack(string file, Trader ragman)
    {
        var cfg = Load(file);
        if (cfg == null) return;
        if (!RegisterItem(cfg, ragman)) return;
        RegisterLootBox(cfg);
    }

    private void ProcessBinder(string file, Trader ragman)
    {
        var cfg = Load(file);
        if (cfg == null) return;
        if (!RegisterItem(cfg, ragman)) return;
        ApplySlotStructure(cfg);
    }

    private void ProcessCardCase(string file, Trader ragman, List<string> allCardIds)
    {
        var cfg = Load(file);
        if (cfg == null) return;
        if (!RegisterItem(cfg, ragman)) return;
        BuildAllCardsSlot(cfg.Id, allCardIds);
    }

    // ── Core registration ────────────────────────────────────────────────────

    private bool RegisterItem(KrackItemConfig cfg, Trader ragman)
    {
        // 4.0 generated an id when "id" was absent; 4.1 takes NewId verbatim, and MongoId's
        // constructor maps an empty string onto the all-zero id rather than rejecting it. Left
        // alone that registers a template under the zero id.
        if (!MongoId.IsValidMongoId(cfg.Id))
        {
            logger.Warning($"[KrackKards] Skipping {cfg.ItemName}: 'id' must be 24 hex characters");
            return false;
        }

        var result = customItemService.CreateItemFromClone(new NewItemFromCloneDetails
        {
            ItemTplToClone       = new MongoId(cfg.CloneItem),
            NewId                = new MongoId(cfg.Id),
            ParentId             = new MongoId(cfg.ItemParent),
            // 4.1 made the internal template _name a required field on the details record.
            NewItemName          = BuildInternalName(cfg),
            HandbookParentId     = cfg.CategoryId,
            HandbookPriceRoubles = cfg.Price,
            FleaPriceRoubles     = cfg.Price,
            Locales = new Dictionary<string, LocaleDetails>
            {
                {
                    "en", new LocaleDetails
                    {
                        Name        = cfg.ItemName,
                        ShortName   = cfg.ItemShortName,
                        Description = cfg.ItemDescription
                    }
                }
            },
            OverrideProperties = new TemplateItemProperties
            {
                BackgroundColor   = cfg.Color,
                Weight            = cfg.Weight,
                Width             = cfg.ExternalSize.Width,
                Height            = cfg.ExternalSize.Height,
                StackMaxSize      = cfg.StackMaxSize,
                ExaminedByDefault = cfg.ExaminedByDefault
            }
        }, Assembly.GetExecutingAssembly());

        if (!result.Success)
        {
            logger.Warning($"[KrackKards] Could not create {cfg.Id} ({cfg.ItemName}): {string.Join("; ", result.Errors)}");
            return false;
        }

        _itemsRegistered++;

        // Apply properties not covered by OverrideProperties
        if (templateTable.Items.TryGetValue(new MongoId(cfg.Id), out var item) && item.Properties != null)
        {
            if (!string.IsNullOrEmpty(cfg.ItemPrefabPath))
            {
                item.Properties.Prefab ??= new Prefab();
                item.Properties.Prefab.Path = cfg.ItemPrefabPath;
            }
            if (!string.IsNullOrEmpty(cfg.ItemSound))
                item.Properties.ItemSound = cfg.ItemSound;
            if (cfg.DiscardLimit >= 0)
                item.Properties.DiscardLimit = cfg.DiscardLimit;
        }

        // Trader stock
        if (cfg.Sold)
            AddToRagman(cfg, ragman);

        // Flea blacklist (all KrackKards items are blacklisted as in the original)
        ragfairConfig.Dynamic.Blacklist.Custom.Add(new MongoId(cfg.Id));
        return true;
    }

    // ── Slot structures ──────────────────────────────────────────────────────

    /// <summary>Parse slotStructure JSON elements and assign to item in DB.</summary>
    private void ApplySlotStructure(KrackItemConfig cfg)
    {
        if (cfg.SlotStructure is not { Count: > 0 }) return;
        if (!templateTable.Items.TryGetValue(new MongoId(cfg.Id), out var item) || item.Properties == null) return;

        var slots = new List<Slot>();
        foreach (var el in cfg.SlotStructure)
        {
            var name     = TryStr(el, "_name");
            var id       = TryStr(el, "_id");
            var parent   = TryStr(el, "_parent");
            var required = TryBool(el, "_required");
            var merge    = TryBool(el, "_mergeSlotWithChildren");
            var proto    = TryStr(el, "_proto") ?? "55d30c4c4bdc2db4468b457e";

            var filters = new List<SlotFilter>();
            if (el.TryGetProperty("_props", out var props) &&
                props.TryGetProperty("filters", out var filtersArr))
            {
                foreach (var f in filtersArr.EnumerateArray())
                {
                    var ids = new HashSet<MongoId>();
                    if (f.TryGetProperty("Filter", out var filterList))
                        foreach (var fid in filterList.EnumerateArray())
                            ids.Add(new MongoId(fid.GetString() ?? ""));
                    filters.Add(new SlotFilter { Filter = ids });
                }
            }

            slots.Add(new Slot
            {
                Name                  = name ?? "",
                Id                    = AsMongoId(id ?? ""),
                Parent                = AsMongoId(parent ?? cfg.Id),
                Properties            = new SlotProperties { Filters = filters },
                Required              = required,
                MergeSlotWithChildren = merge,
                Prototype             = proto
            });
        }

        item.Properties.Slots = slots;
    }

    /// <summary>Build a single catch-all slot that accepts every card ID.</summary>
    private void BuildAllCardsSlot(string itemId, List<string> cardIds)
    {
        if (cardIds.Count == 0) return;
        if (!templateTable.Items.TryGetValue(new MongoId(itemId), out var item) || item.Properties == null) return;

        item.Properties.Slots =
        [
            new Slot
            {
                Name   = "mod_mount_1",
                Id     = AsMongoId($"{itemId}_card_slot"),
                Parent = new MongoId(itemId),
                Properties = new SlotProperties
                {
                    Filters =
                    [
                        new SlotFilter { Filter = new HashSet<MongoId>(cardIds.Select(id => new MongoId(id))) }
                    ]
                },
                Required              = false,
                MergeSlotWithChildren = false,
                Prototype             = "55d30c4c4bdc2db4468b457e"
            }
        ];
    }

    // ── Loot box ─────────────────────────────────────────────────────────────

    private void RegisterLootBox(KrackItemConfig cfg)
    {
        if (!cfg.IsLootBox || cfg.LootContent == null) return;

        inventoryConfig.RandomLootContainers[new MongoId(cfg.Id)] = new RewardDetails
        {
            RewardCount   = cfg.LootContent.RewardCount,
            FoundInRaid   = cfg.LootContent.FoundInRaid,
            RewardTplPool = cfg.LootContent.RewardTplPool
                .ToDictionary(kvp => new MongoId(kvp.Key), kvp => kvp.Value)
        };
    }

    // ── Trader ───────────────────────────────────────────────────────────────

    private static void AddToRagman(KrackItemConfig cfg, Trader ragman)
    {
        var id = new MongoId(cfg.Id);
        ragman.Assort.Items.Add(new Item
        {
            Id       = id,
            Template = id,
            ParentId = "hideout",
            SlotId   = "hideout",
            Upd      = new Upd
            {
                UnlimitedCount    = cfg.UnlimitedStock,
                StackObjectsCount = cfg.StockAmount
            }
        });
        ragman.Assort.BarterScheme[id]    = [[new BarterScheme { Count = cfg.Price, Template = new MongoId(RoublesId) }]];
        ragman.Assort.LoyalLevelItems[id] = cfg.TraderLoyaltyLevel;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Slot._id and Slot._parent became MongoId in 4.1, and MongoId's constructor rejects
    /// anything that isn't 24 hex characters. This mod's configs use suffixed ids such as
    /// "6699b7fd0d1d25cf00072c43_slot_1", so map those onto a stable synthetic MongoId
    /// derived from the original string. Same input always yields the same id, so slot
    /// identities survive server restarts and the config schema stays untouched.
    /// </summary>
    private static MongoId AsMongoId(string raw)
    {
        if (MongoId.IsValidMongoId(raw))
            return new MongoId(raw);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return new MongoId(Convert.ToHexStringLower(hash.AsSpan(0, 12)));
    }

    /// <summary>Internal template _name, required by NewItemFromCloneDetails in 4.1.</summary>
    private static string BuildInternalName(KrackItemConfig cfg)
    {
        var source = string.IsNullOrWhiteSpace(cfg.ItemName) ? cfg.Id : cfg.ItemName;
        var slug   = new string(source.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());
        return string.IsNullOrWhiteSpace(slug) ? cfg.Id : slug;
    }

    private KrackItemConfig? Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<KrackItemConfig>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex)
        {
            logger.Warning($"[KrackKards] Failed to load {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private static IEnumerable<string> EnumJson(params string[] parts)
    {
        var dir = Path.Combine(parts);
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.json") : [];
    }

    private static string? TryStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) ? v.GetString() : null;

    private static bool TryBool(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.GetBoolean();
}
