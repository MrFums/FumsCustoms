using System.Reflection;
using EFT;
using HarmonyLib;
using JsonType;
using SPT.Reflection.Patching;

namespace Fums.Customs.Client
{
    // SPT's bigmap looseLoot.json lists the abstract "Customization" base
    // template (673c7b00cbf4b984b5099181) as a possible loose item at two spawn
    // points ("Loot 19 (7)" and "Loot 19 (8)"). It has no world model, so when it
    // rolls, GameWorld.SpawnLoot throws and the raid fails to start. Skip it.
    internal sealed class BrokenLootGuard : ModulePatch
    {
        private const string CustomizationNode = "673c7b00cbf4b984b5099181";

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GameWorld), nameof(GameWorld.SpawnLootItem));

        [PatchPrefix]
        private static bool Prefix(JsonLootItem lootItem)
        {
            var item = lootItem?.Item;
            if (item == null || item.TemplateId.ToString() != CustomizationNode) return true;
            Plugin.Log.LogWarning($"Skipped loose loot {lootItem.Id}: abstract Customization template has no world model");
            return false;
        }
    }
}
