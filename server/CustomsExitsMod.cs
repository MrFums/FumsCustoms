using System.Text.Json;
using System.Text.Json.Serialization;
using JetBrains.Annotations;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;
using SysPath = System.IO.Path;

namespace Fums.Customs.Server;

// Customs (bigmap) exits for the live 1.3 custom_DesignMain (client ReplacedScenes).
// The game matches each scene exfil to the server's exit list by name and switches
// off the rest. 1.3 renamed them (ex_customs_pmc_*, ex_customs_scav_*, ...), so
// db/exits_1x.json (analysis/gen_customs_exits_1x.py) carries BSG's 1.x exits,
// allExtracts and secretExits from SPT 5.0's data, plus:
//  - rename: 0.16.9 name -> 1.3 name. Every 1.3 exit with a 0.16.9 twin is given
//    its old name back here, and the client does the same to the scene exfils
//    (client ExitNames.cs). Quests, SPT's car/co-op extract rules and other mods
//    keep matching, in Fums and Vanilla raids alike; no quest is rewritten;
//  - localeFrom / newText: display text for the exits new in 1.3.
// Only for a client that loads the 1.3 DesignMain (the client default): config use13Exits.
// Turn it off only if the client's ReplacedScenes leaves custom_DesignMain vanilla.
[Injectable(TypePriority = OnLoadOrder.PostLoad + 101), UsedImplicitly]
public class CustomsExitsMod(
    LocationTable locationTable,
    LocaleTable localeTable,
    JsonUtil jsonUtil,
    ISptLogger<CustomsExitsMod> logger)
    : IOnLoad
{
    private sealed class Config
    {
        public bool use13Exits { get; set; } = true;
        public bool use1xRaidSettings { get; set; } = true;
        public bool respectBotSpawnMods { get; set; } = true;
    }

    private sealed class ExitData
    {
        public List<Exit> exits { get; set; } = [];
        public List<AllExtractsExit> allExtracts { get; set; } = [];
        public List<Exit> secretExits { get; set; } = [];
        public Dictionary<string, string> rename { get; set; } = [];
        public Dictionary<string, string> localeFrom { get; set; } = [];
        public Dictionary<string, string> newText { get; set; } = [];
    }

    // db/raid_1x.json (analysis/gen_customs_raid_1x.py), base.json key names
    private sealed class RaidData
    {
        [JsonPropertyName("BotMax")] public int? BotMax { get; set; }
        [JsonPropertyName("BossLocationSpawn")] public List<BossLocationSpawn>? BossLocationSpawn { get; set; }
        [JsonPropertyName("waves")] public List<Wave>? Waves { get; set; }
        [JsonPropertyName("AirdropParameters")] public List<AirdropParameter>? AirdropParameters { get; set; }
        [JsonPropertyName("EscapeTimeLimit")] public double? EscapeTimeLimit { get; set; }
        [JsonPropertyName("EscapeTimeLimitPVE")] public int? EscapeTimeLimitPVE { get; set; }
        [JsonPropertyName("maxItemCountInLocation")] public List<MaxItemCountInLocation>? MaxItemCountInLocation { get; set; }
    }

    // Server mods that own bot spawning. With one loaded, Customs keeps SPT's bot
    // tables so that mod decides spawns, as it does on every other map.
    private static readonly string[] BotSpawnMods =
        ["acidphantasm-botplacementsystem", "MoreBotsServer", "SWAG", "MOAR", "Donuts", "DewardianDev.MOAR"];

    // Set when the 1.x boss table or waves replaced SPT's. Otherwise Customs keeps
    // SPT's (a bot spawn mod owns them), and Fums and Vanilla Customs share one
    // spawn table (SpawnModSync).
    private bool ReplacedSpawnTable;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        // CustomsClassic installed: FumsCustoms stands down (see ClassicCoexist)
        if (ClassicCoexist.FumsOff) return Task.CompletedTask;
        var dir = SysPath.GetDirectoryName(typeof(CustomsExitsMod).Assembly.Location)!;
        Config cfg;
        try
        {
            var cfgPath = SysPath.Combine(dir, "config.json");
            cfg = File.Exists(cfgPath) ? JsonSerializer.Deserialize<Config>(File.ReadAllText(cfgPath)) ?? new Config() : new Config();
        }
        catch (Exception e)
        {
            logger.Error($"[FumsCustoms] config.json unreadable, Customs keeps SPT's exits and raid settings: {e.Message}");
            return Task.CompletedTask;
        }
        if (!cfg.use13Exits && !cfg.use1xRaidSettings)
        {
            logger.Info("[FumsCustoms] use13Exits and use1xRaidSettings are off: Customs keeps SPT's 0.16.9 base");
            return Task.CompletedTask;
        }

        // a copy of Base, not SPT's own: MapVariants swaps SPT's objects back in for a
        // Vanilla Customs raid, so they must keep the 0.16.9 values
        var customs = locationTable.Bigmap;
        LocationBase copy;
        try
        {
            copy = jsonUtil.Deserialize<LocationBase>(jsonUtil.Serialize(customs.Base))
                   ?? throw new InvalidDataException("Customs base could not be copied");
        }
        catch (Exception e)
        {
            logger.Error($"[FumsCustoms] Customs base could not be copied, SPT's own kept: {e.Message}");
            return Task.CompletedTask;
        }

        if (cfg.use13Exits) ApplyExits(dir, customs, copy);
        else logger.Info("[FumsCustoms] use13Exits is off: Customs keeps SPT's 0.16.9 exits");
        if (cfg.use1xRaidSettings) ApplyRaid(dir, copy, cfg.respectBotSpawnMods);
        SpawnModSync.Track(customs.Base, copy, !ReplacedSpawnTable);
        customs.Base = copy;
        return Task.CompletedTask;
    }

    private void ApplyExits(string dir, Location customs, LocationBase copy)
    {
        try
        {
            var data = jsonUtil.Deserialize<ExitData>(File.ReadAllText(SysPath.Combine(dir, "db", "exits_1x.json")))
                       ?? throw new InvalidDataException("exits_1x.json is empty");
            if (data.exits.Count == 0 || data.allExtracts.Count == 0) throw new InvalidDataException("exits_1x.json has no exits");
            var toOld = data.rename.ToDictionary(kv => kv.Value, kv => kv.Key);
            int renamed = 0;
            foreach (var exit in data.exits.Concat(data.secretExits))
                if (exit.Name != null && toOld.TryGetValue(exit.Name, out var old)) { exit.Name = old; renamed++; }
            foreach (var exit in data.allExtracts)
                if (exit.Name != null && toOld.TryGetValue(exit.Name, out var old)) { exit.Name = old; renamed++; }
            copy.Exits = data.exits;
            copy.SecretExits = data.secretExits;
            customs.AllExtracts = data.allExtracts;

            int locales = AddLocales(data, toOld);
            var fresh = data.exits.Select(e => e.Name).Concat(data.allExtracts.Select(e => e.Name))
                .Where(n => n != null && n.StartsWith("ex_customs_")).Distinct();
            logger.Info($"[FumsCustoms] 1.3 Customs exits: {data.exits.Count} exits, {data.allExtracts.Count} extracts, " +
                        $"{data.secretExits.Count} secret; {renamed} given their 0.16.9 names; new in 1.3: {string.Join(", ", fresh)}; " +
                        $"locale keys added in {locales} languages");
        }
        catch (Exception e)
        {
            // keep SPT's own exits; the raid still starts
            logger.Error($"[FumsCustoms] 1.3 exits not applied, SPT's own Customs exits kept: {e.Message}");
        }
    }

    private void ApplyRaid(string dir, LocationBase copy, bool respectSpawnMods)
    {
        try
        {
            var data = jsonUtil.Deserialize<RaidData>(File.ReadAllText(SysPath.Combine(dir, "db", "raid_1x.json")))
                       ?? throw new InvalidDataException("raid_1x.json is empty");
            var loaded = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
            var spawnMods = BotSpawnMods.Where(loaded.Contains).ToList();
            var applied = new List<string>();
            if (respectSpawnMods && spawnMods.Count > 0)
            {
                logger.Info($"[FumsCustoms] bot spawn mod(s) loaded ({string.Join(", ", spawnMods)}): Customs keeps SPT's bot count, " +
                            "waves and boss table so they decide spawns");
            }
            else
            {
                if (data.BotMax is int botMax) { applied.Add($"bot max {copy.BotMax} -> {botMax}"); copy.BotMax = botMax; }
                if (data.BossLocationSpawn is { Count: > 0 } bosses) { copy.BossLocationSpawn = bosses; ReplacedSpawnTable = true; applied.Add($"{bosses.Count} boss/PMC spawns"); }
                if (data.Waves is { Count: > 0 } waves) { copy.Waves = waves; ReplacedSpawnTable = true; applied.Add($"{waves.Count} waves"); }
            }
            if (data.AirdropParameters is { Count: > 0 } air) { copy.AirdropParameters = air; applied.Add("airdrops"); }
            if (data.EscapeTimeLimit is double minutes) { applied.Add($"raid time {copy.EscapeTimeLimit} -> {minutes} min"); copy.EscapeTimeLimit = minutes; }
            if (data.EscapeTimeLimitPVE is int pve) copy.EscapeTimeLimitPVE = pve;
            if (data.MaxItemCountInLocation is { Count: > 0 } caps) { copy.MaxItemCountInLocation = caps; applied.Add($"{caps.Count} per-raid item caps"); }
            logger.Info("[FumsCustoms] 1.x Customs raid settings: " + string.Join(", ", applied));
        }
        catch (Exception e)
        {
            logger.Error($"[FumsCustoms] 1.x raid settings not applied, SPT's own kept: {e.Message}");
        }
    }

    private int AddLocales(ExitData data, Dictionary<string, string> toOld)
    {
        int n = 0;
        foreach (var lazy in localeTable.Global.Values)
        {
            lazy.AddTransformer(dict =>
            {
                if (dict == null) return dict;
                foreach (var (key, from) in data.localeFrom)
                {
                    // renamed back: the 0.16.9 key already has its text
                    if (toOld.ContainsKey(key) || dict.ContainsKey(key)) continue;
                    if (dict.TryGetValue(from, out var text)) dict[key] = text;
                    if (dict.TryGetValue(from + " Description", out var desc)) dict[key + " Description"] = desc;
                }
                foreach (var (key, text) in data.newText)
                    dict.TryAdd(key, text);
                return dict;
            });
            n++;
        }
        return n;
    }
}
