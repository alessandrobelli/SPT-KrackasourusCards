# KrackKards on SPT 4.1.2

This mod targeted SPT 4.0.13. This document records what 4.1 broke, what changed to fix it,
and what has and has not been tested.

**Status:** loads on SPT 4.1.2. All 249 items register at startup with no warnings, and 1434
static loot entries are queued across 12 maps.

---

## Proof it loads

Server log after a build and restart:

```
ModLoader: loading: 4 server mods...
Mod: KrackKards version: 1.0.0 (GUID: com.vonbraunz.krackkards | targets SPT: ~4.1.2) by: Vonbraunz loaded
Mod: WTT-ServerCommonLib version: 3.0.3 (GUID: com.wtt.commonlib | targets SPT: ~4.1.0) by: GrooveypenguinX loaded
Mod: server version: 2.4.0 (GUID: Fika | targets SPT: >=4.1.0) by: Fika loaded
Mod: SVM version: 2.2.1 (GUID: fika.ghostfenixx.svm | targets SPT: ~4.1.0) by: GhostFenixx loaded
Importing database...
Database import finished
Server: executing startup callbacks...
[KrackKards] Registered 249 items (231 cards)
[KrackKards] Static loot: 1434 entries queued into 219 containers (34 types) across 12 maps, applied when a map loads
[KrackKards] Gotta find 'em all!
Started webserver at https://0.0.0.0:6969
Server has started, happy playing
```

The mod count depends on what else is installed. What matters is that KrackKards appears in
the `Mod: ... loaded` list, that no `Could not load type` or `does not have an implementation`
error names it, and that the other server mods present still load alongside it. Verified here
against Fika 2.4.0, SVM 2.2.1 and WTT-ServerCommonLib 3.0.3.

The 249 registrations match the config file counts exactly:

| Directory | Files |
| --- | --- |
| `config/anime/collectables/cards` | 75 |
| `config/anime/collectables/packs` | 3 |
| `config/anime/cases` | 8 |
| `config/pokemon/cards` | 156 |
| `config/pokemon/packs` | 3 |
| `config/pokemon/binders` | 3 |
| `config/cases` | 1 |
| **Total** | **249** (231 of them cards) |

---

## API changes this port had to absorb

| 4.0.13 | 4.1.2 |
| --- | --- |
| `AbstractModMetadata` base record | `IModMetadata` interface |
| `IModMetadata.IsBundleMod` | removed |
| — | `IModMetadata.HasPrepatcher` added (required) |
| `IOnLoad.OnLoad()` | `IOnLoad.OnLoadAsync(CancellationToken)` |
| `DatabaseService.GetTables()` → `DatabaseTables` | `DatabaseTables` moved into the non-published `SPT.Server` entry assembly; inject individual tables |
| `db.Templates.Items` | inject `TemplateTable`, use `.Items` |
| `db.Traders` | inject `TradersTable` (is a `Dictionary<MongoId, Trader>`) |
| `db.Locations` | inject `LocationTable` |
| `configServer.GetConfig<RagfairConfig>()` | inject `RagfairConfig` directly |
| `configServer.GetConfig<InventoryConfig>()` | inject `InventoryConfig` directly |
| `ISptLogger` in `SPTarkov.Server.Core.Models.Utils` | `SPTarkov.Common.Models.Logging` |
| `ModHelper` in `SPTarkov.Server.Core.Helpers` | `SPTarkov.Server.Core.Helpers.Server` |
| `CustomItemService` in `...Services.Mod` | `...Services.Modding.Custom` |
| `Slot.Id`, `Slot.Parent` were `string?` | now `MongoId?` |
| `RagfairBlacklist.Custom` was a string collection | now `HashSet<MongoId>` |
| `NewItemFromCloneDetails` had no name field | `NewItemName` is now **required** |
| `CreateItemFromClone` generated an id when `NewId` was empty | takes `NewId` verbatim |
| `TargetFramework net9.0` | `net10.0` |

`SPTarkov.{Common,DI,Server.Core}` package references all moved `4.0.13` → `4.1.2`;
`SptVersion` moved `~4.0.13` → `~4.1.2`.

---

## Decisions worth reviewing

### Load order: `GameCallbacks + 1`

4.1 deleted the `Database` and `PostDBModLoader` stages from `OnLoadOrder` — the database is
imported *before* the DI container is built (`Program.StartServerAfterModLoading`), so there is
no DB stage left to hook.

| 4.0.13 | | 4.1.2 | |
| --- | --- | --- | --- |
| `Watermark` | 0 | `Watermark` | 0 |
| `PreSptModLoader` | 100000 | `Preload` | 100000 |
| `Database` | 200000 | *(gone)* | |
| `GameCallbacks` | 300000 | `GameCallbacks` | 200000 |
| **`PostDBModLoader`** | **400000** | *(gone)* | |
| `TraderRegistration` | 500000 | `TraderRegistration` | 300000 |
| | | `Routers` | 400000 |
| `HandbookCallbacks` | 600000 | `HandbookCallbacks` | 500000 |
| `SaveCallbacks` | 700000 | `SaveCallbacks` | 600000 |
| `TraderCallbacks` | 800000 | `TraderCallbacks` | 700000 |
| `PresetCallbacks` | 900000 | `PresetCallbacks` | 800000 |
| `RagfairCallbacks` | 1000000 | `RagfairCallbacks` | 900000 |
| `PostSptModLoader` | 1100000 | `PostLoad` | 1000000 |

The mod ran at `PostDBModLoader + 1`, i.e. after `GameCallbacks` and before
`TraderRegistration`. `GameCallbacks + 1` (200001) is the same position in 4.1: it still lands
ahead of `TraderRegistration`, `HandbookCallbacks`, `TraderCallbacks` and `RagfairCallbacks`,
which is what the Ragman assort, handbook entries and flea blacklist depend on.

The threshold matters, not just the ordering: `SPTStartupHostedService` runs only `IOnLoad`
components with `TypePriority >= 200000`. Anything below that runs in a separate earlier phase
(`ProgramExtensions.RunPreSptLoadCallbacks`).

### Slot IDs mapped onto synthetic MongoIds

`Slot._id` and `Slot._parent` became `MongoId`, whose constructor throws on anything that is
not exactly 24 hex characters. This mod's configs use suffixed ids:

```json
"_id": "6699b7fd0d1d25cf00072c43_slot_1"
```

Editing the config schema is off-limits, so `AsMongoId()` passes valid MongoIds through
unchanged and maps invalid ones to the first 12 bytes of their SHA-256 digest. The same input
always yields the same id, so slot identities survive server restarts.

This is a 1:1 mapping of whatever the config already said, including its quirks: the eight
anime binders were cloned from one source and share 75 slot `_id` values between them. 4.0 used
those duplicate strings verbatim, and the synthetic ids are duplicated in exactly the same
places. Nothing was silently deduplicated.

The mapping is deliberately **not** applied to slot filter ids, `rewardTplPool` keys, or an
item's own `id`/`clone_item`/`item_parent`. Those must resolve to a template that really
exists, so hashing a bad value would turn a loud failure into a silent wrong reference. They
are left to fail as they did on 4.0.

### An item with no `id` is now rejected

4.0's `CreateItemFromClone` generated an id when `NewId` was empty. 4.1 takes `NewId` verbatim,
and `MongoId`'s constructor maps the empty string onto the all-zero id instead of rejecting it —
so a config missing `id` would register a template under the zero id, report success, and carry
that id into the trader assort and static loot. `RegisterItem` now checks `id` is a valid
MongoId first and skips the item with a warning. All 249 shipped configs pass.

### `IsBundleMod` removal is not a functional loss

4.1 dropped the property, but `SPTStartupHostedService` now decides a mod ships bundles by
checking for `bundles.json` in the mod folder — which this mod has.

### `NewItemName`

Newly required. Derived from the configured display name, lowercased with non-alphanumerics
replaced by `_`, falling back to the item id. This sets the internal template `_name`; the
user-visible name still comes from the locale entry, so nothing changes in game.

### Registration failures surface

`CreateItemFromClone` returns a `CreateItemResult` the original code discarded. A failed clone
would silently leave a card id in the card case filter and apply a slot structure to a
nonexistent template. It now logs the errors, skips the dependent work, and reports totals at
the end of load.

### `obj/` untracked

`obj/` was tracked, and its stale net9.0 artifacts break a net10.0 rebuild. It is now ignored
and removed from the index, along with `bin/`.

---

## Static loot spawns

Cards used to come from Ragman only. They now also spawn in static containers.

Every item config already carried a `loot_locations` map (map name → container template ids)
and a `lootable` flag that the 4.0 TypeScript mod used for this. Both were being ignored.
Nothing decides *where* cards belong except that existing data — the new config only decides
*whether* it happens and *how often*.

### What ships

`config/static_loot.json` — the only new config. JSON with `//` comments, read through the same
`JsonSerializerOptions` the item configs use (`ReadCommentHandling = Skip`).

| Key | Default | Meaning |
| --- | --- | --- |
| `enable_container_spawns` | `true` | Master switch; `false` leaves every container untouched |
| `card_weight_multiplier` | `0.5` | Global frequency, deliberately conservative — see below |
| `container_multipliers` | 34 entries, all `1.0` | Per-container-type scaling; `0` disables a type |
| `rarity_weights` | 5 entries | Splits each container's card budget across rarities |

The 34 container ids are exactly the ones the card configs reference — no more, no fewer — each
annotated with its in-game name. `container_multipliers` only *scales* containers a card
already lists; it cannot add one.

A **missing** file falls back to the defaults above, which are equivalent to the shipped file
apart from rarity weighting flattening to equal odds. A file that **exists but cannot be
parsed** disables static loot for that run instead of falling back, because defaults would
re-enable spawns for an author whose unreadable file may have said
`enable_container_spawns: false`. Both cases are logged.

Writing `null` rather than `{}` for `container_multipliers` or `rarity_weights` is accepted:
System.Text.Json assigns a JSON null straight over a property initialiser, so those are
normalised back to empty on load.

### Frequency

`BaseGroupChance` is 0.02, so at `card_weight_multiplier: 1` a container roll lands on a
KrackKards item 2% of the time. The shipped `0.5` halves that to **1%** — a card is a pleasant
surprise, not something in every other jacket. The share is hard-capped at 50% per container
however high the multipliers go.

Container weights are relative, so the value of one unit of weight depends on what is already
in the container. For a container with existing weight mass `M` and a target group chance `p`,
the injector adds total weight

```
W = p·M / (1 − p)
```

which makes `W / (M + W)` come out at exactly `p`. That total is then split across the eligible
cards in proportion to their rarity weight. `RelativeProbability` is `float?` in 4.1, so
fractional weights are used directly — no rounding up to a minimum of 1, which would have
inflated the rare cards in small containers.

Rarity weighting degrades to flat on its own: a card whose rarity is missing from
`rarity_weights` weighs 1.0, so emptying that object gives every card identical odds.

### Scope

`lootable: false` is already set on all 6 card packs, all 3 pokemon binders and the card case,
so those never spawn — only the 231 cards and the 8 anime cases do. That is the item author's
own switch and it was left alone.

| | Count |
| --- | --- |
| Items registered | 249 |
| Eligible for static loot (`lootable` + non-empty `loot_locations`) | 239 |
| — of which cards | 231 |
| — of which anime cases | 8 |
| Excluded by `lootable: false` | 10 |

### Database tables modified

Only one, and only through the lazy-load hook:

| Table | Path | Entries added |
| --- | --- | --- |
| `LocationTable` | `Location.StaticLoot` → `Dictionary<MongoId, StaticLootDetails>` → `StaticLootDetails.ItemDistribution` | **1434** `ItemDistribution` entries |

Spread over **219** container distributions (a map plus a container type — each map holds its
own distribution for a given container) covering **34** container types across **12** maps:
`bigmap`, `factory4_day`, `factory4_night`, `interchange`, `laboratory`, `lighthouse`,
`rezervbase`, `sandbox`, `sandbox_high`, `shoreline`, `tarkovstreets`, `woods`.

Explicitly **not** touched by this feature: the Ragman assort (`TradersTable`), the flea
blacklist (`RagfairConfig.Dynamic.Blacklist.Custom`), `TemplateTable`,
`InventoryConfig.RandomLootContainers`, and the schema of any existing card config.

### 4.1 API this needed

| Concern | 4.1.2 shape |
| --- | --- |
| Reaching maps | inject `LocationTable`; `GetLocation("rezervbase")` resolves the `RezervBase` property |
| Static loot | `Location.StaticLoot` is `LazyLoad<Dictionary<MongoId, StaticLootDetails>>?` |
| Mutating it | `LazyLoad.AddTransformer(Func<T?,T?>)` |
| Weights | `ItemDistribution.RelativeProbability` is `float?`, and `StaticLootDetails.ItemDistribution` is `IEnumerable<>` |

`LazyLoad<T>.Value` re-runs its deserialiser on **every** access and then replays every
registered transformer, with no caching — its own XML doc comment claims the result is cached,
but the code does not cache it. So the work is registered as one transformer per map and runs
when a raid loads that map, never at startup. Each run starts from freshly deserialised data,
so appending cannot compound across raids. Container mass is therefore computed inside the
transformer against live data, which avoids the hardcoded per-map probability tables the
reference mod carries.

`LocationLootGenerator.GetPossibleLootItemsForContainer` dereferences
`item.RelativeProbability.Value` directly, so the field is always set, never left null. The same
method drops anything in `ItemFilterService.IsLootableItemBlacklisted`, which reads
`ItemConfig.LootableItemBlacklist` — a different list from the `RagfairConfig` blacklist this
mod writes to, so blacklisting cards from the flea does not stop them spawning.

### Differences from the reference

[Chazut/TarkovTradingCards](https://github.com/Chazut/TarkovTradingCards) (MIT) was read for
approach and config shape. It targets 4.0, so none of its API calls carried over: it resolves
maps through `DatabaseService.GetTables().Locations`, which no longer exists for mods. Beyond
that:

- **Container mass** — TTC ships hardcoded per-map probability totals with a live-data
  fallback. This computes from live data always, so there is no table to go stale.
- **Integer weights** — TTC rounds `RelativeProbability` to an int and clamps to a minimum of
  1. 4.1 types it as `float?`, so fractional weights are used as-is.
- **Rarity split** — TTC splits the budget by rarity then equally within a rarity. This weights
  each card directly, which collapses to flat when no rarity is configured.
- **Counting** — TTC increments its totals inside the transformer, so they re-count on every
  map load. Totals here are counted once, when the transformers are registered.

### Measured result

1434 / 219 / 34 / 12 were counted independently from the config files before the code ran, and
match the startup log exactly.

Because the injection is lazy, nothing is written to a map until a raid loads it. Forcing
`StaticLoot.Value` on two maps during development confirmed the transformer runs and produced a
measured card share of 1.000% of container entries on both `bigmap` (26 containers) and
`sandbox` (25 containers) — exactly `BaseGroupChance × card_weight_multiplier` = `0.02 × 0.5`,
confirming the weight solve lands where intended against real container data.

---

## Not verified

The mod loads and registers items server-side. **In-game behaviour was not tested** — that
needs a client connection. Specifically unconfirmed:

- binder slots accepting cards
- card pack loot boxes opening and rolling rewards
- card case catch-all slot accepting every card
- Ragman assort showing the items at the right loyalty levels
- flea blacklist actually hiding them
- bundles resolving client-side
- cards actually being **found** in a container in a live raid. The transformer was proven to
  run and produce the intended 1% share, but the loot generator drawing a card from that
  distribution in-raid was not observed.

---

## Finding real 4.1 API shapes

Package versions were confirmed against
`https://api.nuget.org/v3-flatcontainer/sptarkov.server.core/index.json`.

Assemblies were inspected rather than guessed:

```sh
dotnet tool install -g ilspycmd
export DOTNET_ROOT=/path/to/dotnet     # ilspycmd cannot always find dotnet on its own
ilspycmd -p -o /tmp/core-decomp \
  ~/.nuget/packages/sptarkov.server.core/4.1.2/lib/net10.0/SPTarkov.Server.Core.dll
```

Decompiling as a project (`-p`) produces one directory per namespace, which is how the moved
types were located. `ilspycmd -l` lists type names without namespaces and is much less useful
here.

Two things are **not** in the NuGet packages and can only be read from a server install:

- `SPTarkov.Server.Helpers.DatabaseTables` and `DatabaseImporter` live in `SPT.Server.dll`, the
  entry assembly, which is why mods can no longer reference `DatabaseTables` at all.
- `SPTStartupHostedService`'s 200000 priority threshold is in `SPTarkov.Server.Core`, but the
  pre-200000 phase is in `SPT.Server.dll`.

ILSpy cannot decode `[Injectable(...)]` attribute arguments, so `TypePriority` values were read
from the raw IL blob (`ilspycmd -il`) — the named argument is the little-endian int32 following
the hex for the string `TypePriority`.

An installed copy of SVM 2.2.1 was the most useful working reference for the 4.1 metadata and
DI patterns.
