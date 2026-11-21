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
// Config: bunkhouseKeyOnBots, bunkhouseKeyFleaSale.
internal static class BunkhouseKey
{
    internal static readonly MongoId Tpl = new("68e95f4fa4a577e907015787");

    internal sealed class Config
    {
        public bool bunkhouseKeyOnBots { get; set; } = true;
        public bool bunkhouseKeyFleaSale { get; set; } = true;
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
            logger.Info($"[FumsCustoms] Reshala's bunkhouse key can be sold on the flea, as on live (removed from the item blacklist: config {fromConfig}, cache {fromCache})");
        return Task.CompletedTask;
    }
}

[Injectable(TypePriority = OnLoadOrder.PostLoad + 102), UsedImplicitly]
public class BunkhouseKeyBots(
    BotGeneratorHelper botGeneratorHelper,
    ItemHelper itemHelper,
    ISptLogger<BunkhouseKeyBots> logger)
    : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (ClassicCoexist.FumsOff) return Task.CompletedTask; // CustomsClassic installed
        if (!BunkhouseKey.Load().bunkhouseKeyOnBots) return Task.CompletedTask;
        if (!itemHelper.GetItem(BunkhouseKey.Tpl).Key)
        {
            logger.Warning("[FumsCustoms] Reshala's bunkhouse key is not in the item database (is WTT-ContentBackport installed?); bots will not carry it");
            return Task.CompletedTask;
        }
        try
        {
            BunkhouseKeyLootPatch.Init(botGeneratorHelper, itemHelper, logger);
            new BunkhouseKeyLootPatch().Enable();
            logger.Info("[FumsCustoms] Reshala's bunkhouse key on bots at live rates: Reshala 5.31%, Birdeye 0.032%, scavs 0.0034%");
        }
        catch (Exception e)
        {
            logger.Error($"[FumsCustoms] bunkhouse key bot loot not enabled: {e.Message}");
        }
        return Task.CompletedTask;
    }
}

// Enabled once, by hand, from BunkhouseKeyBots.
[IgnoreAutoPatch]
public class BunkhouseKeyLootPatch : AbstractPatch
{
    private sealed record Rule(double Chance, HashSet<EquipmentSlots> Slots);

    // role (lowercase) -> live chance per bot, live container first, then fallbacks
    private static readonly Dictionary<string, Rule> Rules = new()
    {
        ["bossbully"] = new(44.0 / 828, [EquipmentSlots.Pockets, EquipmentSlots.TacticalVest, EquipmentSlots.Backpack]),
        ["followerbirdeye"] = new(1.0 / 3099, [EquipmentSlots.Backpack, EquipmentSlots.Pockets, EquipmentSlots.TacticalVest]),
        ["assault"] = new(2.0 / 58157, [EquipmentSlots.Backpack, EquipmentSlots.Pockets, EquipmentSlots.TacticalVest]),
    };

    private static BotGeneratorHelper? _helper;
    private static ItemHelper? _items;
    private static ISptLogger<BunkhouseKeyBots>? _log;

    internal static void Init(BotGeneratorHelper helper, ItemHelper items, ISptLogger<BunkhouseKeyBots> log)
    {
        _helper = helper;
        _items = items;
        _log = log;
    }

    protected override MethodBase? GetTargetMethod() =>
        AccessTools.Method(typeof(BotLootGenerator), nameof(BotLootGenerator.GenerateLoot));

    [PatchPrefix, HarmonyPriority(Priority.First)]
    public static void Prefix(MongoId botId, BotGenerationDetails botGenerationDetails, BotBaseInventory botInventory)
    {
        try
        {
            if (_helper == null || _items == null || botInventory?.Items == null) return;
            var role = botGenerationDetails?.RoleLowercase ?? botGenerationDetails?.Role?.ToLowerInvariant();
            if (role == null || !Rules.TryGetValue(role, out var rule)) return;
            if (Random.Shared.NextDouble() >= rule.Chance) return;
            if (botInventory.Items.Any(i => i.Template == BunkhouseKey.Tpl)) return;
            var (found, template) = _items.GetItem(BunkhouseKey.Tpl);
            if (!found || template == null) return;
            var id = new MongoId();
            var key = new Item
            {
                Id = id,
                Template = BunkhouseKey.Tpl,
                Upd = _helper.GenerateExtraPropertiesForItem(template, role),
            };
            var result = _helper.AddItemWithChildrenToEquipmentSlot(botId, rule.Slots, id, BunkhouseKey.Tpl, [key], botInventory);
            if (result == ItemAddedResult.SUCCESS) _log?.Info($"[FumsCustoms] bunkhouse key given to a {role} ({rule.Slots.First()})");
            else _log?.Warning($"[FumsCustoms] bunkhouse key rolled for a {role} but not placed: {result}");
        }
        catch (Exception e)
        {
            _log?.Warning($"[FumsCustoms] bunkhouse key not added to a bot: {e.Message}");
        }
    }
}
