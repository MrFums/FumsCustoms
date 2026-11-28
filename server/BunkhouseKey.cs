using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using JetBrains.Annotations;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Generators.Loot;
using SPTarkov.Server.Core.Helpers.Bot;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Bots;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services.Items;
using SysPath = System.IO.Path;

namespace Fums.Customs.Server;

// Reshala's bunkhouse key (68e95f4fa4a577e907015787, item_key_quest_reshala_vremyanka).
// WTT-ContentBackport defines the item. It gives it no bot loot, and its
// addtoItemBlacklist flag bans it from the flea. On live it can be sold on the flea
// (SPT 5.0: CanSellOnRagfair true, not on any blacklist).
//
// Bot loot for this key and the two 1.x Customs door keys is in KeysOnBots.cs.
// Bot chances come from SPT 5.0's bot data (database/bots/types), which counts what
// live bots carried. The generation weights of a section add up to the number of bots
// sampled, and an item's weight is how many of them carried it:
//   bossbully       Pockets   44 of 828 Reshalas    = 5.31%
//   followerbirdeye Backpack   1 of 3,099 Birdeyes  = 0.032%
//   assault         Backpack   2 of 58,157 scavs    = 0.0034%
// SPT's own generator turns those counts into one weighted roll per pocket item
// (0.79% for Reshala), so the key is added here at the live rate instead, just before
// the bot's loot is generated: equipment and weapons are in place, and the pockets
// still have room (Reshala has no vest or backpack; his loot fills his pockets). It
// does not depend on loadout or loot mods' tables (APBS, Gravity), and their loot
// filters only edit the pools, not items already placed.
// Config: bunkhouseKeyOnBots, bunkhouseKeyFleaSale, customsKeysOnBots, customsKeysFleaSale (KeysOnBots.cs).
internal static class BunkhouseKey
{
    internal static readonly MongoId Tpl = new("68e95f4fa4a577e907015787");

    internal sealed class Config
    {
        public bool bunkhouseKeyOnBots { get; set; } = true;
        public bool bunkhouseKeyFleaSale { get; set; } = true;
        public bool customsKeysOnBots { get; set; } = true; // ZB-1013 and Security post keys, see KeysOnBots.cs
        public bool customsKeysFleaSale { get; set; } = false; // same keys on the flea; live does not allow it
    }

    internal static Config Load()
    {
        try
        {
            var dir = SysPath.GetDirectoryName(typeof(BunkhouseKey).Assembly.Location)!;
            var path = SysPath.Combine(dir, "config.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) ?? new Config() : new Config();
        }
        catch
        {
            return new Config();
        }
    }
}

// Lifts WTT's flea ban. WTT adds the key to ItemConfig.Blacklist at Preload + 2. The
// flea assort reads that set, and ItemHelper.IsValidItem reads ItemFilterService's
// copy of it, made when that service is built. Both are cleared here, before the flea
// is generated (RagfairCallbacks).
[Injectable(TypePriority = OnLoadOrder.Preload + 50), UsedImplicitly]
public class BunkhouseKeyFlea(
    ItemConfig itemConfig,
    ItemFilterService itemFilterService,
    ISptLogger<BunkhouseKeyFlea> logger)
    : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (ClassicCoexist.FumsOff) return Task.CompletedTask; // CustomsClassic installed
        if (!BunkhouseKey.Load().bunkhouseKeyFleaSale) return Task.CompletedTask;
        bool fromConfig = itemConfig.Blacklist.Remove(BunkhouseKey.Tpl);
        bool fromCache = false;
        try
        {
            if (AccessTools.Field(typeof(ItemFilterService), "ItemBlacklistCache")?.GetValue(itemFilterService) is HashSet<MongoId> cache)
                fromCache = cache.Remove(BunkhouseKey.Tpl);
        }
        catch (Exception e)
        {
            logger.Warning($"[FumsCustoms] bunkhouse key: item blacklist cache not reachable ({e.Message}); the flea list is still fixed");
        }
        if (fromConfig || fromCache)
            logger.Debug($"[FumsCustoms] Reshala's bunkhouse key can be sold on the flea, as on live (removed from the item blacklist: config {fromConfig}, cache {fromCache})");
        return Task.CompletedTask;
    }
}
