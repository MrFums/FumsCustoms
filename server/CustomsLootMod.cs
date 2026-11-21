using System.Reflection;
using System.Text.Json;
using JetBrains.Annotations;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Json;
using SysPath = System.IO.Path;

namespace Fums.Customs.Server;

// Customs (bigmap) loot for the live 1.3 backport, from SPT 5.0's 1.x raid dumps
// (game build 48558). Built by analysis/gen_customs_loot_1x.py into db/:
//  - looseLoot.json, staticContainers.json, staticLoot.json, staticAmmo.json,
//    statics.json: the 1.x dump. They replace SPT 0.16.9's Customs loot outright.
//  - old_spots.json: 0.16.9 spots the live scene still has but the dump never saw.
//  - unseen_spots.json: new 1.3 spots the dump never saw, with the pool of the
//    nearest 1.x spot (config fillUnseenNewSpots, off by default).
//  - extra_items.json: 0.16.9 items kept at 1.x spots (Dorm room 314 marked key).
//  - container_templates.json: the 5 new 1.x container types (locked crates, USEC
//    corpse, rich gun safe), built on the 0.16.9 type each one replaced.
// The 1.x dump assumes a Customs loot multiplier of 1 (0.16.9 used 2.5), so the
// multipliers are set from config.json.
// Every item is checked against the item database when loot is generated, so an
// item no installed mod defines is dropped instead of breaking the raid. If
// anything throws, SPT's own Customs loot is used.
[Injectable(TypePriority = OnLoadOrder.PostLoad + 100), UsedImplicitly]
public class CustomsLootMod(
    LocationTable locationTable,
    TemplateTable templateTable,
    LocationConfig locationConfig,
    JsonUtil jsonUtil,
    ISptLogger<CustomsLootMod> logger)
    : IOnLoad
{
    private const string Map = "bigmap";
    private const string BrokenCustomizationTpl = "673c7b00cbf4b984b5099181";

    private sealed class Config
    {
        public bool useLiveLoot { get; set; } = true;
        public double looseLootMultiplier { get; set; } = 1.0;
        public double staticLootMultiplier { get; set; } = 1.0;
        public bool keepOldSpotsMissingFromDump { get; set; } = true;
        public bool fillUnseenNewSpots { get; set; } = false;
    }

    private Config _cfg = new();
    private string _db = "";

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = SysPath.GetDirectoryName(typeof(CustomsLootMod).Assembly.Location)!;
        _db = SysPath.Combine(dir, "db");
        var configPath = SysPath.Combine(dir, "config.json");
        try
        {
            if (File.Exists(configPath))
                _cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath)) ?? new Config();
        }
        catch (Exception e)
        {
            logger.Error($"[FumsCustoms] config.json is invalid, using defaults: {e.Message}");
        }
        if (ClassicCoexist.FumsOff) return Task.CompletedTask;
        if (!_cfg.useLiveLoot)
        {
            logger.Info("[FumsCustoms] useLiveLoot is off: Customs keeps SPT's own loot");
            return Task.CompletedTask;
        }

        var customs = locationTable.Bigmap;

        // Parse every file once now, so a bad file shows in the startup log and is
        // skipped instead of failing every Customs raid start.
        var looseR = Read<LooseLoot>("looseLoot.json");
        var containersR = Read<StaticContainerDetails>("staticContainers.json");
        var staticLootR = Read<Dictionary<MongoId, StaticLootDetails>>("staticLoot.json");
        var ammoR = Read<Dictionary<string, IEnumerable<StaticAmmoDetails>>>("staticAmmo.json");
        var staticsR = Read<StaticContainer>("statics.json");
        if (looseR is not { } loose || containersR is not { } containers || staticLootR is not { } staticLoot
            || ammoR is not { } ammo || staticsR is not { } statics)
        {
            logger.Error("[FumsCustoms] 1.x loot data incomplete: Customs keeps SPT's own loot");
            return Task.CompletedTask;
        }

        _known1x = new HashSet<MongoId>(
            (loose.Spawnpoints ?? []).Concat(loose.SpawnpointsForced ?? [])
                .SelectMany(s => s.Template?.Items ?? []).Select(i => i.Template)
            .Concat(staticLoot.Values.SelectMany(v => v.ItemDistribution).Select(d => d.Tpl)));
        try
        {
            // every item the 1.x game has, on any map: a 1.x item another mod adds
            // with a guessed chance is left out where the 1.x Customs data has none
            foreach (var id in JsonSerializer.Deserialize<List<string>>(File.ReadAllText(SysPath.Combine(_db, "items_1x_ids.json"))) ?? [])
                _known1x.Add(new MongoId(id));
        }
        catch (Exception e)
        {
            logger.Warning($"[FumsCustoms] items_1x_ids.json not read; only Customs' own 1.x items count as 1.x: {e.Message}");
        }
        StartupReport(loose, containers, staticLoot);

        // MapVariants (Customs: Fums or Vanilla) keeps the location's original objects and
        // swaps them back in for a Vanilla raid. So the 1.x loot goes into NEW LazyLoad
        // objects built on top of SPT's (whose value, with every earlier mod's
        // transformers, is the input): SPT's own objects stay vanilla. Not transformers on
        // SPT's objects: MapVariants mirrors the backport's transformers into the stock set.
        // SPT reads each value before the map's loot multiplier, so each value sets the
        // multiplier its own data assumes: 1.x below, SPT's own (0.16.9: loose 2.5) on stock.
        // The builders read the 1.x files from disk each time: the lambdas below must not
        // capture the values parsed above, or those stay in memory for the whole session.
        var stockLoose = customs.LooseLoot;
        var stockContainers = customs.StaticContainers;
        var stockStaticLoot = customs.StaticLoot;
        // the input is SPT's data as it stands now (every earlier mod's transformers),
        // not stock.Value at raid time: a Vanilla raid has MapVariants copy later mods'
        // transformers onto the stock objects, and reading those here would apply them twice
        var looseIn = PreState(stockLoose);
        var containersIn = PreState(stockContainers);
        var staticLootIn = PreState(stockStaticLoot);
        // location.json has a key per map and no "default" one, so a missing map counts as 1
        double stockLooseMult = locationConfig.LooseLootMultiplier.TryGetValue(Map, out var looseMult) ? looseMult : 1;
        double stockStaticMult = locationConfig.StaticLootMultiplier.TryGetValue(Map, out var staticMult) ? staticMult : 1;
        if (stockLoose is not null)
        {
            stockLoose.AddTransformer(ll => { locationConfig.LooseLootMultiplier[Map] = stockLooseMult; return ll; });
            customs.LooseLoot = new LazyLoad<LooseLoot>(() =>
            {
                var input = looseIn();
                var built = Safe("loose loot", input, () => BuildLooseLoot(ReadDb("looseLoot.json"), input));
                if (!ReferenceEquals(built, input)) locationConfig.LooseLootMultiplier[Map] = _cfg.looseLootMultiplier;
                return built!;
            }, cacheValue: false);
        }
        if (stockContainers is not null)
            customs.StaticContainers = new LazyLoad<StaticContainerDetails>(() =>
            {
                var input = containersIn();
                return Safe("containers", input, () => BuildContainers(ReadDb("staticContainers.json"), input))!;
            }, cacheValue: false);
        if (stockStaticLoot is not null)
        {
            stockStaticLoot.AddTransformer(sl => { locationConfig.StaticLootMultiplier[Map] = stockStaticMult; return sl; });
            customs.StaticLoot = new LazyLoad<Dictionary<MongoId, StaticLootDetails>>(() =>
            {
                var input = staticLootIn();
                var built = Safe("container loot", input, () => BuildStaticLoot(ReadDb("staticLoot.json"), input));
                if (!ReferenceEquals(built, input)) locationConfig.StaticLootMultiplier[Map] = _cfg.staticLootMultiplier;
                return built!;
            }, cacheValue: false);
        }
        // With CustomsClassic beside it, a Classic raid's loot has no transformer to set the
        // multipliers, so a Fums raid's would carry over: ClassicLootMultipliers puts SPT's back.
        if (ClassicCoexist.Together) ClassicLootMultipliers.Track(customs.LooseLoot, stockLooseMult, stockStaticMult);
        // Read SPT's raw Customs files for their keys now, at start: the parsed files are
        // freed before anything (a raid, Tushonkapedia's loot index) builds Customs' loot,
        // so that build never holds them on top of SPT's own copy and the 1.x data.
        Base<LooseLoot, RawLoose>(ref _baseLoose, "looseLoot.json", Summarise);
        Base<StaticContainerDetails, RawContainers>(ref _baseContainers, "staticContainers.json", Summarise);
        Base<Dictionary<MongoId, StaticLootDetails>, Dictionary<MongoId, HashSet<MongoId>>>(ref _baseStaticLoot, "staticLoot.json", Summarise);
        try
        {
            customs.StaticAmmo = BuildAmmo(ammo);
            customs.Statics = statics;
        }
        catch (Exception e)
        {
            logger.Error($"[FumsCustoms] 1.x ammo/container groups not applied: {e.Message}");
        }

        logger.Success($"[FumsCustoms] Customs uses live 1.x loot (loose x{_cfg.looseLootMultiplier}, containers x{_cfg.staticLootMultiplier}, " +
                       $"old spots {(_cfg.keepOldSpotsMissingFromDump ? "kept" : "dropped")}, unseen new spots {(_cfg.fillUnseenNewSpots ? "filled" : "empty")})");
        return Task.CompletedTask;
    }

    // SPT's LazyLoad value with only the transformers it has now: its loader and a copy of
    // its transformer list (private; MapVariants reads the same list). Falls back to Value.
    private Func<T?> PreState<T>(LazyLoad<T>? lazy) where T : class
    {
        if (lazy is null) return () => null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var load = typeof(LazyLoad<T>).GetFields(flags).FirstOrDefault(f => f.FieldType == typeof(Func<T>))?.GetValue(lazy) as Func<T>;
        var list = typeof(LazyLoad<T>).GetField("_lazyLoadTransformers", flags)?.GetValue(lazy) as List<Func<T?, T?>>;
        if (load is null || list is null)
        {
            logger.Warning($"[FumsCustoms] SPT's LazyLoad<{typeof(T).Name}> layout changed: reading its live value");
            return () => lazy.Value;
        }
        var snapshot = list.ToArray();
        return () =>
        {
            T? v = load();
            foreach (var t in snapshot) v = t(v);
            return v;
        };
    }

    private T? Read<T>(string file) where T : class
    {
        try
        {
            return jsonUtil.Deserialize<T>(ReadDb(file)) ?? throw new InvalidDataException("empty");
        }
        catch (Exception e)
        {
            logger.Error($"[FumsCustoms] {file} could not be read: {e.Message}");
            return null;
        }
    }

    private string ReadDb(string file) => File.ReadAllText(SysPath.Combine(_db, file));

    private T? Safe<T>(string what, T? fallback, Func<T> build)
    {
        try { return build(); }
        catch (Exception e)
        {
            // never break raid start: fall back to SPT's own Customs data
            logger.Error($"[FumsCustoms] Customs {what} failed, using SPT's: {e}");
            return fallback;
        }
    }

    private bool Known(MongoId tpl) => templateTable.Items.ContainsKey(tpl);

    // ---- other mods' Customs loot ---------------------------------------------------
    // The 1.x data replaces SPT's Customs loot, so anything another mod added on top
    // of SPT's own files (quest items, new spots, extra items at a spot, container
    // items) is found by comparing the incoming value with SPT's raw files, and
    // carried over.

    private readonly Lock _baseLock = new();
    private HashSet<MongoId> _known1x = [];   // every item the 1.x Customs data has (loose or in containers)
    // Only the keys of SPT's raw Customs files are kept, not the files themselves: the raw
    // loose loot alone (42 MB of JSON) is hundreds of MB once parsed.
    private RawLoose? _baseLoose;
    private RawContainers? _baseContainers;
    private Dictionary<MongoId, HashSet<MongoId>>? _baseStaticLoot;

    private sealed class RawLoose
    {
        // spot key -> its distribution keys (the first spot with that key)
        public readonly Dictionary<string, HashSet<string>> Spots = new();
        public readonly HashSet<string> Forced = new();
    }

    private sealed class RawContainers
    {
        public readonly HashSet<string> Containers = new();
        public readonly HashSet<string> Weapons = new();
        public readonly HashSet<string> Forced = new();
    }

    private static RawLoose Summarise(LooseLoot raw)
    {
        var summary = new RawLoose();
        foreach (var sp in raw.Spawnpoints ?? [])
            summary.Spots.TryAdd(SpotKey(sp), DistKeys(sp));
        foreach (var sp in raw.SpawnpointsForced ?? [])
            summary.Forced.Add(SpotKey(sp));
        return summary;
    }

    private static RawContainers Summarise(StaticContainerDetails raw)
    {
        var summary = new RawContainers();
        summary.Containers.UnionWith(raw.StaticContainers.Select(c => c.Template?.Id).OfType<string>());
        summary.Weapons.UnionWith(raw.StaticWeapons.Select(w => w.Id).OfType<string>());
        summary.Forced.UnionWith(raw.StaticForced.Select(f => f.ContainerId + "|" + f.ItemTpl));
        return summary;
    }

    private static Dictionary<MongoId, HashSet<MongoId>> Summarise(Dictionary<MongoId, StaticLootDetails> raw) =>
        raw.ToDictionary(kv => kv.Key, kv => new HashSet<MongoId>(kv.Value.ItemDistribution.Select(d => d.Tpl)));

    private TSummary? Base<T, TSummary>(ref TSummary? cache, string file, Func<T, TSummary> summarise)
        where T : class where TSummary : class
    {
        using (_baseLock.EnterScope())
        {
            if (cache is not null) return cache;
            // SPT's server is one file, so its assemblies have no location: SPT_Data is next to the server itself
            var location = typeof(LocationTable).Assembly.Location;
            var root = string.IsNullOrEmpty(location) ? AppContext.BaseDirectory : SysPath.GetDirectoryName(location) ?? AppContext.BaseDirectory;
            var path = SysPath.Combine(root, "SPT_Data", "database", "locations", Map, file);
            var raw = File.Exists(path) ? jsonUtil.Deserialize<T>(File.ReadAllText(path)) : null;
            cache = raw is null ? null : summarise(raw);
            if (cache is null) logger.Warning($"[FumsCustoms] SPT's {file} not found: other mods' Customs loot is not carried over");
            return cache;
        }
    }

    private static string SpotKey(Spawnpoint sp) => sp.Template?.Id ?? sp.LocationId ?? "";

    private static HashSet<string> DistKeys(Spawnpoint sp) =>
        new((sp.ItemDistribution ?? []).Select(d => d.ComposedKey?.Key).OfType<string>());

    // The 1.x data is the authority for every item it has: another mod's addition is
    // carried over only when its item never appears in the 1.x Customs loot (an item
    // only that mod adds). Items backport mods add with guessed chances (WTT puts its
    // 1.x items in every map's containers) are left to the real 1.x pools.
    private int CarryLooseLoot(LooseLoot? input, List<Spawnpoint> spawnpoints, List<Spawnpoint> forced)
    {
        var raw = Base<LooseLoot, RawLoose>(ref _baseLoose, "looseLoot.json", Summarise);
        if (input is null || raw is null) return 0;
        var known1x = _known1x;
        int carried = 0;
        var samples = new List<string>();
        var rawForced = raw.Forced;
        var mine = spawnpoints.GroupBy(SpotKey).ToDictionary(g => g.Key, g => g.First());
        foreach (var sp in input.Spawnpoints ?? [])
        {
            var key = SpotKey(sp);
            var rawKeys = raw.Spots.TryGetValue(key, out var keys) ? keys : [];
            var items = sp.Template?.Items?.ToList() ?? [];
            var kids = items.Where(i => i.ParentId is not null).GroupBy(i => i.ParentId!).ToDictionary(g => g.Key, g => g.ToList());
            // entries a mod added (a whole new spot, or new items at an SPT spot) whose
            // item the 1.x data never has
            var added = new List<(LooseLootItemDistribution d, List<SptLootItem> tree)>();
            foreach (var d in sp.ItemDistribution ?? [])
            {
                if (d.ComposedKey?.Key is not { } k || rawKeys.Contains(k)) continue;
                var root = items.FirstOrDefault(i => i.ParentId is null && i.ComposedKey == k);
                if (root is null || known1x.Contains(root.Template)) continue;
                added.Add((d, Tree(root, kids)));
            }
            if (added.Count == 0) continue;
            if (!mine.TryGetValue(key, out var target))
            {
                // a spot the 1.x data does not have: keep it with only the mod's own items
                sp.Template!.Items = added.SelectMany(a => a.tree).ToList();
                sp.ItemDistribution = added.Select(a => a.d).ToList();
                sp.Template.Root = added[0].tree[0].Id.ToString();
                spawnpoints.Add(sp);
                mine[key] = sp;
                carried += added.Count;
                if (samples.Count < 5) samples.Add($"{key} ({added[0].tree[0].Template})");
                continue;
            }
            if (target.Template?.Items is null) continue;
            var have = DistKeys(target);
            foreach (var (d, tree) in added)
            {
                if (!have.Add(d.ComposedKey!.Key!)) continue;
                target.Template.Items = [.. target.Template.Items, .. tree];
                target.ItemDistribution = [.. target.ItemDistribution ?? [], d];
                carried++;
                if (samples.Count < 5) samples.Add($"{key} ({tree[0].Template})");
            }
        }
        // forced spots are quest items: keep every one another mod adds
        var forcedHave = new HashSet<string>(forced.Select(SpotKey));
        foreach (var sp in input.SpawnpointsForced ?? [])
            if (!rawForced.Contains(SpotKey(sp)) && forcedHave.Add(SpotKey(sp)))
            {
                forced.Add(sp);
                carried++;
                if (samples.Count < 5) samples.Add($"forced {SpotKey(sp)}");
            }
        if (carried > 0) logger.Info($"[FumsCustoms] kept {carried} loose-loot addition(s) from other mods, e.g. {string.Join("; ", samples)}");
        return carried;
    }


    // ---- loose loot ----------------------------------------------------------------

    private LooseLoot BuildLooseLoot(string json, LooseLoot? input)
    {
        var ll = jsonUtil.Deserialize<LooseLoot>(json)!;
        var spawnpoints = ll.Spawnpoints!.ToList();
        var have = new HashSet<string>(spawnpoints.Select(s => s.Template?.Id).OfType<string>());
        int old = 0, unseen = 0, extra = 0;

        void AddFrom(string file, ref int count)
        {
            var more = jsonUtil.Deserialize<LooseLoot>(File.ReadAllText(SysPath.Combine(_db, file)))?.Spawnpoints ?? [];
            foreach (var sp in more)
                if (sp.Template?.Id is not null && have.Add(sp.Template.Id)) { spawnpoints.Add(sp); count++; }
        }
        if (_cfg.keepOldSpotsMissingFromDump) AddFrom("old_spots.json", ref old);
        if (_cfg.fillUnseenNewSpots) AddFrom("unseen_spots.json", ref unseen);

        var extraPath = SysPath.Combine(_db, "extra_items.json");
        if (File.Exists(extraPath))
        {
            var byId = spawnpoints.Where(s => s.Template?.Id is not null)
                .GroupBy(s => s.Template!.Id!).ToDictionary(g => g.Key, g => g.First());
            foreach (var entry in JsonSerializer.Deserialize<List<AddEntry>>(File.ReadAllText(extraPath)) ?? [])
            {
                if (!byId.TryGetValue(entry.spawnpointId, out var sp) || sp.Template?.Items is null) continue;
                foreach (var add in entry.add)
                {
                    var tree = jsonUtil.Deserialize<List<SptLootItem>>(add.items.GetRawText());
                    if (tree is null || tree.Count == 0) continue;
                    if (sp.Template.Items.Any(i => i.Id == tree[0].Id)) continue;
                    // SPT matches distribution keys against each root item's ComposedKey
                    tree[0].ComposedKey ??= tree[0].Id.ToString();
                    sp.Template.Items = [.. sp.Template.Items, .. tree];
                    sp.ItemDistribution = [.. sp.ItemDistribution ?? [], new LooseLootItemDistribution
                    {
                        ComposedKey = new ComposedKey { Key = tree[0].ComposedKey },
                        RelativeProbability = add.relativeProbability
                    }];
                    extra++;
                }
            }
        }

        var forcedIn = (ll.SpawnpointsForced ?? []).ToList();
        int carried = CarryLooseLoot(input, spawnpoints, forcedIn);

        int droppedSpots = 0, droppedForced = 0;
        var kept = new List<Spawnpoint>();
        foreach (var sp in spawnpoints)
        {
            if (sp.Template?.Items is null) { droppedSpots++; continue; }
            RemoveTemplate(sp, BrokenCustomizationTpl);
            if (KeepKnown(sp)) kept.Add(sp); else droppedSpots++;
        }
        var forced = new List<Spawnpoint>();
        foreach (var sp in forcedIn)
        {
            // forced spots carry one fixed item tree; keep it only if every item exists
            if (sp.Template?.Items is not null && sp.Template.Items.All(i => Known(i.Template))) forced.Add(sp);
            else droppedForced++;
        }
        ll.Spawnpoints = kept;
        ll.SpawnpointsForced = forced;
        logger.Info($"[FumsCustoms] Customs loose loot: {kept.Count} spots ({old} old, {unseen} unseen-new, {extra} extra item(s), {carried} from other mods), " +
                    $"{forced.Count} forced; dropped {droppedSpots} spot(s) and {droppedForced} forced spot(s) with no known items");
        return ll;
    }

    // Drops distribution entries whose item tree has an unknown template; false if none remain.
    private bool KeepKnown(Spawnpoint sp)
    {
        var items = sp.Template!.Items!.ToList();
        var kids = items.Where(i => i.ParentId is not null).GroupBy(i => i.ParentId!).ToDictionary(g => g.Key, g => g.ToList());
        var keptItems = new List<SptLootItem>();
        var keptDist = new List<LooseLootItemDistribution>();
        foreach (var dist in sp.ItemDistribution ?? [])
        {
            var root = items.FirstOrDefault(i => i.ParentId is null && (i.ComposedKey ?? i.Id.ToString()) == dist.ComposedKey?.Key)
                       ?? items.FirstOrDefault(i => i.ParentId is null && i.Id.ToString() == dist.ComposedKey?.Key);
            if (root is null) continue;
            var tree = Tree(root, kids);
            if (tree.Any(i => !Known(i.Template))) continue;
            keptItems.AddRange(tree);
            keptDist.Add(dist);
        }
        if (keptDist.Count == 0) return false;
        sp.Template.Items = keptItems;
        sp.ItemDistribution = keptDist;
        if (sp.Template.Root is null || keptItems.All(i => i.Id.ToString() != sp.Template.Root))
            sp.Template.Root = keptItems[0].Id.ToString();
        return true;
    }

    private static void RemoveTemplate(Spawnpoint sp, string tpl)
    {
        if (sp.Template?.Items is null || sp.ItemDistribution is null) return;
        var items = sp.Template.Items.ToList();
        var bad = items.Where(i => i.ParentId is null && i.Template.ToString() == tpl).ToList();
        if (bad.Count == 0) return;
        var badKeys = new HashSet<string>(bad.Select(i => i.ComposedKey ?? i.Id.ToString()).Concat(bad.Select(i => i.Id.ToString())));
        sp.ItemDistribution = sp.ItemDistribution.Where(d => d.ComposedKey?.Key is null || !badKeys.Contains(d.ComposedKey.Key)).ToList();
        var badIds = new HashSet<string>(bad.Select(i => i.Id.ToString()));
        sp.Template.Items = items.Where(i => !badIds.Contains(i.Id.ToString())).ToList();
    }

    private static List<SptLootItem> Tree(SptLootItem root, Dictionary<string, List<SptLootItem>> kids)
    {
        var list = new List<SptLootItem> { root };
        if (kids.TryGetValue(root.Id.ToString(), out var children))
            foreach (var c in children) list.AddRange(Tree(c, kids));
        return list;
    }

    // ---- static loot ---------------------------------------------------------------

    private StaticContainerDetails BuildContainers(string json, StaticContainerDetails? input)
    {
        var sc = jsonUtil.Deserialize<StaticContainerDetails>(json)!;
        var raw = Base<StaticContainerDetails, RawContainers>(ref _baseContainers, "staticContainers.json", Summarise);
        if (input is not null && raw is not null)
        {
            // containers, static weapons and forced container items other mods added
            var rawC = raw.Containers;
            var haveC = new HashSet<string>(sc.StaticContainers.Select(c => c.Template?.Id).OfType<string>());
            var addC = input.StaticContainers.Where(c => c.Template?.Id is { } id && !rawC.Contains(id) && haveC.Add(id)).ToList();
            var rawW = raw.Weapons;
            var haveW = new HashSet<string>(sc.StaticWeapons.Select(w => w.Id).OfType<string>());
            var addW = input.StaticWeapons.Where(w => w.Id is { } id && !rawW.Contains(id) && haveW.Add(id)).ToList();
            var rawF = raw.Forced;
            var haveF = new HashSet<string>(sc.StaticForced.Select(f => f.ContainerId + "|" + f.ItemTpl));
            var addF = input.StaticForced.Where(f => !rawF.Contains(f.ContainerId + "|" + f.ItemTpl) && haveF.Add(f.ContainerId + "|" + f.ItemTpl)).ToList();
            sc.StaticContainers = [.. sc.StaticContainers, .. addC];
            sc.StaticWeapons = [.. sc.StaticWeapons, .. addW];
            sc.StaticForced = [.. sc.StaticForced, .. addF];
            if (addC.Count + addW.Count + addF.Count > 0)
                logger.Info($"[FumsCustoms] kept other mods' Customs containers: {addC.Count} container(s), {addW.Count} static weapon(s), {addF.Count} forced item(s)");
        }
        int before = sc.StaticContainers.Count() + sc.StaticWeapons.Count();
        sc.StaticContainers = sc.StaticContainers
            .Where(c => c.Template?.Items is not null && c.Template.Items.All(i => Known(i.Template))).ToList();
        sc.StaticWeapons = sc.StaticWeapons
            .Where(w => w.Items is not null && w.Items.All(i => Known(i.Template))).ToList();
        sc.StaticForced = sc.StaticForced.Where(f => Known(f.ItemTpl)).ToList();
        int dropped = before - sc.StaticContainers.Count() - sc.StaticWeapons.Count();
        if (dropped > 0) logger.Warning($"[FumsCustoms] {dropped} Customs container(s)/static weapon(s) skipped: unknown item type");
        return sc;
    }

    private Dictionary<MongoId, StaticLootDetails> BuildStaticLoot(string json, Dictionary<MongoId, StaticLootDetails>? input)
    {
        var sl = jsonUtil.Deserialize<Dictionary<MongoId, StaticLootDetails>>(json)!;
        var raw = Base<Dictionary<MongoId, StaticLootDetails>, Dictionary<MongoId, HashSet<MongoId>>>(ref _baseStaticLoot, "staticLoot.json", Summarise);
        if (input is not null && raw is not null)
        {
            // items other mods added to a container type's pool, when the 1.x data
            // never has that item (see CarryLooseLoot)
            int carried = 0, skipped = 0;
            foreach (var (tpl, details) in input)
            {
                var rawTpls = raw.TryGetValue(tpl, out var r) ? r : [];
                var added = details.ItemDistribution.Where(d => !rawTpls.Contains(d.Tpl)).ToList();
                skipped += added.RemoveAll(d => _known1x.Contains(d.Tpl));
                if (added.Count == 0) continue;
                if (!sl.TryGetValue(tpl, out var target))
                {
                    sl[tpl] = new StaticLootDetails { ItemCountDistribution = details.ItemCountDistribution, ItemDistribution = added };
                    carried += added.Count;
                    continue;
                }
                var have = new HashSet<MongoId>(target.ItemDistribution.Select(d => d.Tpl));
                var add = added.Where(d => have.Add(d.Tpl)).ToList();
                target.ItemDistribution = [.. target.ItemDistribution, .. add];
                carried += add.Count;
            }
            if (carried + skipped > 0)
                logger.Info($"[FumsCustoms] container pools: kept {carried} mod-only item(s) other mods added; left {skipped} 1.x item(s) to the 1.x pools");
        }
        foreach (var details in sl.Values)
            details.ItemDistribution = details.ItemDistribution.Where(d => Known(d.Tpl)).ToList();
        return sl;
    }

    private Dictionary<string, IEnumerable<StaticAmmoDetails>> BuildAmmo(Dictionary<string, IEnumerable<StaticAmmoDetails>> ammo)
    {
        return ammo.ToDictionary(kv => kv.Key,
            kv => (IEnumerable<StaticAmmoDetails>)kv.Value.Where(a => a.Tpl is { } t && Known(t)).ToList());
    }

    // ---- startup report --------------------------------------------------------------

    // 1.x Customs loot items that exist only in the 1.x game: no SPT 4.1 item and no
    // WTT-ContentBackport item defines them (WTT 2.0.7 ships models for some, no items).
    // Missing these is expected, so the startup report says so instead of warning.
    private static readonly HashSet<string> OnlyIn1x = new(StringComparer.Ordinal)
    {
        "6a3181f178450ec91c0ea1aa", // Item_barter_info_project
        "6526909b539ee574cb624991", // item_recorder
        "68dbc04cd700686fbf0d77bd", // item_note_quest_bogatir_patch
        "68d2fd018c12620073059934", // item_info_note_letter_from_kozlov_02
        "68d2f4fc17b59ead010894b6", // item_info_note_letter_from_kozlov_03
        "68d2f4aff8817df4690db3d1", // item_info_note_envelope_opened
        "689b52ff147eeab4410ecd16", // item_info_note_sectant_corpse
        "68d2f302be7c1493b90cd3f5", // item_info_note_toReshala_warning
        "68ea9b43b8469178710c158e", // item_info_note_sectantPriest_eye
        "68d2f1318c12620073059930", // item_info_note_kozlov_rembox
        "68d2f09fb350a6169f04bea9", // item_info_note_kozlov_debt
        "68d2f9bd2ca1a737d107b84d", // item_info_note_bogatir_mention_general
        "68d2f3375f98276b7503c32e", // item_info_note_kozlov_captured
    };

    private void StartupReport(LooseLoot ll, StaticContainerDetails sc, Dictionary<MongoId, StaticLootDetails> sl)
    {
        try
        {
            var unknown = new HashSet<string>();
            foreach (var sp in (ll.Spawnpoints ?? []).Concat(ll.SpawnpointsForced ?? []))
                foreach (var i in sp.Template?.Items ?? [])
                    if (!Known(i.Template)) unknown.Add(i.Template.ToString());
            foreach (var c in sc.StaticContainers)
                foreach (var i in c.Template?.Items ?? [])
                    if (!Known(i.Template)) unknown.Add(i.Template.ToString());
            foreach (var d in sl.Values.SelectMany(v => v.ItemDistribution))
                if (!Known(d.Tpl)) unknown.Add(d.Tpl.ToString());
            var expected = unknown.Where(OnlyIn1x.Contains).ToList();
            var other = unknown.Where(id => !OnlyIn1x.Contains(id)).ToList();
            if (unknown.Count == 0)
                logger.Success("[FumsCustoms] every 1.x Customs loot item exists in the item database");
            if (expected.Count > 0)
                logger.Info($"[FumsCustoms] {expected.Count} 1.x Customs loot item type(s) will not spawn: " + string.Join(", ", expected) +
                            ". This is not an error and is expected: these items (lore notes, a voice recorder and a barter item) " +
                            "exist only in the 1.x game, and neither SPT 4.1 nor WTT-ContentBackport has them. All other loot spawns normally.");
            if (other.Count > 0)
                logger.Warning($"[FumsCustoms] {other.Count} 1.x Customs loot item type(s) are not in the item database and will not spawn: " +
                               string.Join(", ", other.Take(20)) + (other.Count > 20 ? ", ..." : "") +
                               ". Check that WTT-ContentBackport is installed and up to date.");
        }
        catch (Exception e)
        {
            logger.Warning($"[FumsCustoms] startup item check failed: {e.Message}");
        }
    }

    private sealed class AddEntry
    {
        public string spawnpointId { get; set; } = "";
        public List<AddItem> add { get; set; } = [];
    }

    private sealed class AddItem
    {
        public JsonElement items { get; set; }
        public double relativeProbability { get; set; }
    }
}
