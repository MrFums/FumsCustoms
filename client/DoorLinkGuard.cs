using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Fums.Customs.Client
{
    // Vanilla custom_AI carries NavMeshDoorLinks for doors that lived in the
    // vanilla geometry scenes. Those scenes are replaced, and the backport's
    // doors are not restored yet. BotDoorsController.RefreshData falls back to
    // Aggregate() over all doors when a link's door id is missing, which throws
    // "Sequence contains no elements" when there are no doors, and the raid
    // fails to start. While the backport is loaded: keep links whose door
    // exists (by id, else the nearest door within MaxSnap), and drop the rest
    // with their carvers off so bots treat those doorways as open.
    internal sealed class DoorLinkGuard : ModulePatch
    {
        private const float MaxSnap = 2f;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotDoorsController), nameof(BotDoorsController.RefreshData));

        [PatchPrefix]
        private static bool Prefix(BotDoorsController __instance, BotsController bc)
        {
            if (!BundleSceneLoader.Active) return true;
            var doors = Object.FindObjectsOfType<Door>();
            var byId = new Dictionary<string, Door>();
            foreach (var door in doors)
                if (door != null && !string.IsNullOrEmpty(door.Id)) byId[door.Id] = door;

            var kept = new List<NavMeshDoorLink>();
            int dropped = 0;
            foreach (var link in Object.FindObjectsOfType<NavMeshDoorLink>())
            {
                if (link == null) continue;
                if (!byId.TryGetValue(link.DoorId ?? "", out var door)) door = Nearest(doors, link.transform.position);
                if (door == null)
                {
                    link.SetAllCarvers(false);
                    dropped++;
                    continue;
                }
                link.Init(bc);
                link.SetDoor(door, withSubscribe: true);
                kept.Add(link);
            }
            __instance._navMeshDoorLinks = kept;
            __instance.InitCarveStates();
            Plugin.Verbose($"Door links: kept {kept.Count}, dropped {dropped} with no door ({doors.Length} doors in scene)");
            return false;
        }

        private static Door Nearest(Door[] doors, Vector3 pos)
        {
            Door best = null;
            float bestSq = MaxSnap * MaxSnap;
            foreach (var door in doors)
            {
                if (door == null) continue;
                var sq = (door.transform.position - pos).sqrMagnitude;
                if (sq <= bestSq) { bestSq = sq; best = door; }
            }
            return best;
        }
    }
}
