using System.Reflection;
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

namespace Fums.Customs.Server;

// Keys that the 1.3 Customs scenes ask for and that live bots carry. WTT-ContentBackport
// defines all three items. It gives none of them bot loot.
//   68e95f4fa4a577e907015787  Reshala's bunkhouse key (Custom_Expansion)
//   6aa2f6565d82e97ee9076820  ZB-1013 key: the ZB-1013 extract door (Custom_Construction_Factory)
//   6aa2f73005eae7bedb0ad83e  Security post key (custom_factoryStorageZone)
// WTT added the two door keys in its 11-8-26 content update (WTT-ContentBackport 2.0.7).
// With an older WTT the items do not exist and those two doors cannot be opened.
//
// Chances come from SPT 5.0's bot data (database/bots/types), which counts what live
// bots carried. The generation weights of a loot section add up to the number of bots
// sampled, and an item's weight is how many of them carried it. The key is added just
// before the bot's loot is generated (see the bunkhouse key notes in BunkhouseKey.cs).
// Live also spawns the two door keys in jackets and drawers; db/staticLoot.json already
// has those at live rates. Live sells neither door key on the flea
// (CanSellOnRagfair false), so they stay off it unless customsKeysFleaSale is on.
// followertagilla also carries the Security post key on live (2 of 828), but SPT 4.1 has
// no followertagilla bot type, so it is left out.
// Config: bunkhouseKeyOnBots, customsKeysOnBots, customsKeysFleaSale.
internal static class KeysOnBots
{
    internal sealed record Rule(double Chance, HashSet<EquipmentSlots> Slots);

    internal sealed record Key(MongoId Tpl, string Name, Dictionary<string, Rule> Rules);

    private static readonly HashSet<EquipmentSlots> Pockets = [EquipmentSlots.Pockets, EquipmentSlots.TacticalVest, EquipmentSlots.Backpack];
    private static readonly HashSet<EquipmentSlots> Backpack = [EquipmentSlots.Backpack, EquipmentSlots.Pockets, EquipmentSlots.TacticalVest];

    internal static readonly Key Bunkhouse = new(BunkhouseKey.Tpl, "Reshala's bunkhouse key", new()
    {
        ["bossbully"] = new(44.0 / 828, Pockets),
        ["followerbirdeye"] = new(1.0 / 3099, Backpack),
        ["assault"] = new(2.0 / 58157, Backpack),
    });

    internal static readonly Key Zb1013 = new(new MongoId("6aa2f6565d82e97ee9076820"), "ZB-1013 key", new()
    {
        ["assault"] = new(73.0 / 58157, Backpack),
        ["bossboar"] = new(2.0 / 736, Pockets),
        ["bossgluhar"] = new(4.0 / 1133, Pockets),
        ["bosskilla"] = new(1.0 / 1400, Pockets),
        ["bosskillaagro"] = new(1.0 / 1120, Pockets),
        ["bossknight"] = new(18.0 / 4804, Pockets),
        ["bosspartisan"] = new(2.0 / 3099, Pockets),
        ["bosstagilla"] = new(1.0 / 2754, Pockets),
        ["followerbigpipe"] = new(3.0 / 3099, Pockets),
        ["followerbirdeye"] = new(4.0 / 3099, Backpack),
        ["infectedtagilla"] = new(8.0 / 2773, Pockets),
        ["sectantpriest"] = new(27.0 / 3761, Pockets),
        ["sectantwarrior"] = new(7.0 / 7323, Backpack),
    });

    internal static readonly Key SecurityPost = new(new MongoId("6aa2f73005eae7bedb0ad83e"), "Security post key", new()
    {
        ["assault"] = new(115.0 / 58157, Backpack),
        ["bossboar"] = new(1.0 / 736, Pockets),
        ["bossgluhar"] = new(5.0 / 1133, Pockets),
        ["bosskilla"] = new(5.0 / 1400, Pockets),
        ["bossknight"] = new(16.0 / 4804, Pockets),
        ["bosspartisan"] = new(2.0 / 3099, Pockets),
        ["bosstagilla"] = new(10.0 / 2754, Pockets),
        ["bosstagillaagro"] = new(2.0 / 830, Pockets),
        ["followerbigpipe"] = new(7.0 / 3099, Pockets),
        ["followerbirdeye"] = new(11.0 / 3099, Backpack),
        ["infectedtagilla"] = new(7.0 / 2773, Pockets),
        ["sectantpriest"] = new(31.0 / 3761, Pockets),
        ["sectantwarrior"] = new(17.0 / 7323, Backpack),
    });

    internal static string Percent(Key key, string role) =>
        key.Rules.TryGetValue(role, out var r) ? $"{r.Chance * 100:0.####}%" : "0%";
}

[Injectable(TypePriority = OnLoadOrder.PostLoad + 102), UsedImplicitly]
public class KeysOnBotsLoad(
    BotGeneratorHelper botGeneratorHelper,
    ItemHelper itemHelper,
    ISptLogger<KeysOnBotsLoad> logger)
    : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (ClassicCoexist.FumsOff) return Task.CompletedTask; // CustomsClassic installed
        var cfg = BunkhouseKey.Load();
        var keys = new List<KeysOnBots.Key>();

        bool bunkhouse = itemHelper.GetItem(KeysOnBots.Bunkhouse.Tpl).Key;
        if (!bunkhouse)
            logger.Warning("[FumsCustoms] Reshala's bunkhouse key is not in the item database (is WTT-ContentBackport installed?); bots will not carry it");
        else if (cfg.bunkhouseKeyOnBots)
            keys.Add(KeysOnBots.Bunkhouse);

        var missingDoorKeys = new[] { KeysOnBots.Zb1013, KeysOnBots.SecurityPost }
            .Where(k => !itemHelper.GetItem(k.Tpl).Key).ToList();
        if (missingDoorKeys.Count > 0)
            logger.Warning("[FumsCustoms] " + string.Join(" and ", missingDoorKeys.Select(k => $"the {k.Name} ({k.Tpl})")) +
                           " are not in the item database. Without them the ZB-1013 extract door and the security post " +
                           "cannot be opened. Update WTT-ContentBackport to 2.0.7 or later: it adds both keys.");
        else if (cfg.customsKeysOnBots)
            keys.AddRange([KeysOnBots.Zb1013, KeysOnBots.SecurityPost]);

        if (keys.Count == 0) return Task.CompletedTask;
        try
        {
            KeysOnBotsLootPatch.Init(keys, botGeneratorHelper, itemHelper, logger);
            new KeysOnBotsLootPatch().Enable();
            if (AccessTools.TypeByName("ProgressiveBotSystem.Generators.CustomBotLootGenerator") != null)
                logger.Info("[FumsCustoms] Acid's Progressive Bot System found: bot types it generates use its own loot and will not " +
                            "carry these keys; bot types disabled in APBS still do");
            foreach (var k in keys)
                logger.Debug(k == KeysOnBots.Bunkhouse
                    ? $"[FumsCustoms] {k.Name} on bots at live rates: Reshala {KeysOnBots.Percent(k, "bossbully")}, " +
                      $"Birdeye {KeysOnBots.Percent(k, "followerbirdeye")}, scavs {KeysOnBots.Percent(k, "assault")}"
                    : $"[FumsCustoms] {k.Name} on bots at live rates: scavs {KeysOnBots.Percent(k, "assault")}, " +
                      $"cultist priests {KeysOnBots.Percent(k, "sectantpriest")}, Knight {KeysOnBots.Percent(k, "bossknight")}, " +
                      $"{k.Rules.Count - 3} other boss/follower/cultist types");
        }
        catch (Exception e)
        {
            logger.Error($"[FumsCustoms] keys on bots not enabled: {e.Message}");
        }
        return Task.CompletedTask;
    }
}

// Acid's Progressive Bot System (2.4.1) replaces BotInventoryGenerator.GenerateInventory for
// the bots it handles (prefix returns false) and fills their loot with its own
// CustomBotLootGenerator.GenerateLoot, so SPT's BotLootGenerator.GenerateLoot never runs
// for them and they get none of these keys. FumsCustoms does not patch APBS (user
// decision, 2026-11-27); that is APBS's to support.
// Gravity changes loot pools and gear but keeps SPT's GenerateLoot, so it works as is.
// Optional, off by default (customsKeysFleaSale): puts the ZB-1013 and Security post keys on
// the flea. Live does not allow it (CanSellOnRagfair false), and WTT-ContentBackport also
// adds both to ItemConfig.Blacklist at Preload + 2. Like BunkhouseKeyFlea, this lifts the
// blacklist (config set and ItemFilterService's cached copy) and sets CanSellOnRagfair,
// before the flea is generated (RagfairCallbacks). WTT gives the keys no flea price, so the
// flea uses their handbook price (ZB-1013 63,455, Security post 13,455 roubles).
[Injectable(TypePriority = OnLoadOrder.Preload + 51), UsedImplicitly]
public class CustomsKeysFlea(
    ItemConfig itemConfig,
    ItemFilterService itemFilterService,
    ItemHelper itemHelper,
    ISptLogger<CustomsKeysFlea> logger)
    : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (ClassicCoexist.FumsOff) return Task.CompletedTask; // CustomsClassic installed
        if (!BunkhouseKey.Load().customsKeysFleaSale) return Task.CompletedTask;
        HashSet<MongoId>? cache = null;
        try
        {
            cache = AccessTools.Field(typeof(ItemFilterService), "ItemBlacklistCache")?.GetValue(itemFilterService) as HashSet<MongoId>;
        }
        catch (Exception e)
        {
            logger.Warning($"[FumsCustoms] door keys flea: item blacklist cache not reachable ({e.Message})");
        }
        var done = new List<string>();
        foreach (var key in new[] { KeysOnBots.Zb1013, KeysOnBots.SecurityPost })
        {
            var (found, template) = itemHelper.GetItem(key.Tpl);
            if (!found || template?.Properties == null) continue; // KeysOnBotsLoad warns about missing keys
            itemConfig.Blacklist.Remove(key.Tpl);
            cache?.Remove(key.Tpl);
            template.Properties.CanSellOnRagfair = true;
            done.Add(key.Name);
        }
        if (done.Count > 0)
            logger.Debug($"[FumsCustoms] {string.Join(" and ", done)} can be bought and sold on the flea (customsKeysFleaSale; not allowed on live)");
        return Task.CompletedTask;
    }
}

// Enabled once, by hand, from KeysOnBotsLoad.
[IgnoreAutoPatch]
public class KeysOnBotsLootPatch : AbstractPatch
{
    private static List<KeysOnBots.Key> _keys = [];
    private static BotGeneratorHelper? _helper;
    private static ItemHelper? _items;
    private static ISptLogger<KeysOnBotsLoad>? _log;

    internal static void Init(List<KeysOnBots.Key> keys, BotGeneratorHelper helper, ItemHelper items, ISptLogger<KeysOnBotsLoad> log)
    {
        _keys = keys;
        _helper = helper;
        _items = items;
        _log = log;
    }

    protected override MethodBase? GetTargetMethod() =>
        AccessTools.Method(typeof(BotLootGenerator), nameof(BotLootGenerator.GenerateLoot));

    [PatchPrefix, HarmonyPriority(Priority.First)]
    public static void Prefix(MongoId botId, BotGenerationDetails botGenerationDetails, BotBaseInventory botInventory)
    {
        if (_helper == null || _items == null || botInventory?.Items == null) return;
        var role = botGenerationDetails?.RoleLowercase ?? botGenerationDetails?.Role?.ToLowerInvariant();
        if (role == null) return;
        foreach (var key in _keys)
        {
            try
            {
                if (!key.Rules.TryGetValue(role, out var rule)) continue;
                if (Random.Shared.NextDouble() >= rule.Chance) continue;
                if (botInventory.Items.Any(i => i.Template == key.Tpl)) continue;
                var (found, template) = _items.GetItem(key.Tpl);
                if (!found || template == null) continue;
                var id = new MongoId();
                var item = new Item
                {
                    Id = id,
                    Template = key.Tpl,
                    Upd = _helper.GenerateExtraPropertiesForItem(template, role),
                };
                var result = _helper.AddItemWithChildrenToEquipmentSlot(botId, rule.Slots, id, key.Tpl, [item], botInventory);
                if (result == ItemAddedResult.SUCCESS) _log?.Debug($"[FumsCustoms] {key.Name} given to a {role} ({rule.Slots.First()})");
                else _log?.Debug($"[FumsCustoms] {key.Name} rolled for a {role} but not placed: {result}");
            }
            catch (Exception e)
            {
                _log?.Warning($"[FumsCustoms] {key.Name} not added to a bot: {e.Message}");
            }
        }
    }
}
