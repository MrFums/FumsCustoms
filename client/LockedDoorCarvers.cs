using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Fums.Customs.Client
{
    // Bots cannot open a locked door: they carry no keys and never shoot a padlock.
    // The navmesh runs through every doorway, so without a blocker the pathfinder
    // (the game's, SAIN's, any AI mod's) routes bots into locked doors, where they stall.
    //
    // The game's NavMeshDoorLink only carves open and breached doors. Waypoints adds
    // door links with a closed carver, but only for locked doors that are breachable or
    // keyed, so the 1.3 padlocked doors (keyless, not breachable) get none, and without
    // Waypoints no locked door is carved at all.
    //
    // After BotsController.Init (after Waypoints), every locked door in our scenes gets
    // a NavMeshDoorLink if it has none (built as Waypoints builds them), and its closed
    // carver cuts the doorway while the door is Locked. A key or a shot padlock unlocks
    // it and the carver stops. Being real door links, AI mods that route through locked
    // doors (ORBIT's DoorNavMesh opens a link's closed carver) can still use them; the
    // link is also handed to ORBIT's DoorNavMesh when ORBIT is loaded.
    //
    // Padlocked doors are the exception: they get the link but no carving, so bots path
    // through them as normal doors; PadlockWatcher unlocks one when a bot reaches it.
    internal sealed class LockedDoorCarvers : ModulePatch
    {
        private static readonly HashSet<NavMeshDoorLink> Watched = new HashSet<NavMeshDoorLink>();

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotsController), nameof(BotsController.Init));

        [PatchPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(BotsController __instance)
        {
            if (!BundleSceneLoader.Active) return;
            // padlock list first: padlocked doors are linked but not carved
            var padlocked = new HashSet<WorldInteractiveObject>();
            try { foreach (var e in Padlocks.Entries) padlocked.Add(e.Target); }
            catch (Exception error) { Plugin.Log.LogError("Padlocks: " + error); }
            try { Apply(__instance, padlocked); }
            catch (Exception error) { Plugin.Log.LogError("Locked door carvers failed: " + error); }
        }

        private static void Apply(BotsController bc, HashSet<WorldInteractiveObject> padlocked)
        {
            Watched.RemoveWhere(l => l == null);
            var controller = BotDoorsController.CreateOrFind(false);
            if (controller == null) return;
            if (controller._navMeshDoorLinks == null) controller._navMeshDoorLinks = new List<NavMeshDoorLink>();
            var byDoor = new Dictionary<Door, NavMeshDoorLink>();
            foreach (var link in controller._navMeshDoorLinks)
                if (link != null && link.Door != null) byDoor[link.Door] = link;

            int locked = 0, created = 0, carving = 0, open = 0;
            var names = new List<string>();
            var holder = UnityEngine.Object.FindObjectOfType<AIDoorsHolder>();
            foreach (var door in UnityEngine.Object.FindObjectsOfType<Door>())
            {
                if (door == null || door.DoorState != EDoorState.Locked) continue;
                if (!BundleSceneLoader.Owns(door.gameObject.scene.path)) continue;
                locked++;
                if (!byDoor.TryGetValue(door, out var link))
                {
                    link = Create(door, bc, holder);
                    if (link == null) continue;
                    controller._navMeshDoorLinks.Add(link);
                    OrbitRegister(link);
                    created++;
                    names.Add(door.Id);
                }
                if (link.Carver_Closed == null) continue;
                // padlocked: bots path through it; PadlockWatcher unlocks it when one arrives
                if (padlocked.Contains(door))
                {
                    link.Carver_Closed.carving = false;
                    link.Carver_Closed.enabled = false;
                    var pe = Padlocks.Entries.FirstOrDefault(x => x.Target == door);
                    if (pe != null) pe.Link = link;
                    open++;
                    continue;
                }
                link.Carver_Closed.enabled = true;
                link.Carver_Closed.carving = door.DoorState == EDoorState.Locked;
                carving++;
                if (Watched.Add(link))
                {
                    var l = link;
                    door.OnDoorStateChanged += (obj, prev, next) =>
                    {
                        if (l == null || l.Carver_Closed == null) return;
                        if (next == EDoorState.Locked) l.Carver_Closed.carving = true;
                        else if (prev == EDoorState.Locked) l.Carver_Closed.carving = false;
                    };
                }
            }
            Plugin.Verbose($"Locked door carvers: {locked} locked doors, {carving} carved while locked, {open} padlocked left open for bots, " +
                           $"{created} new door link(s): {string.Join(", ", names)}");
        }

        // As Waypoints' DoorLinkPatch: hinge, shut/open/breach leaf ends, carvers.
        private static NavMeshDoorLink Create(Door door, BotsController bc, AIDoorsHolder holder)
        {
            var t = door.transform;
            var axis = WorldInteractiveObject.GetRotationAxis(door.DoorForward, t);
            var hinge = t.position;
            var go = new GameObject("FumsDoorLink_" + door.Id);
            if (holder != null) go.transform.SetParent(holder.transform);
            else SceneManager.MoveGameObjectToScene(go, door.gameObject.scene);
            go.transform.position = hinge;
            var link = go.AddComponent<NavMeshDoorLink>();
            link.Close1 = hinge;
            link.Open1 = hinge;
            link.Close2_Normal = hinge + door.GetDoorRotation(door.GetAngle(EDoorState.Open)) * axis;
            link.Close2_Breach = hinge + door.GetDoorRotation(door.GetAngle(EDoorState.Breaching)) * axis;
            link.Open2 = hinge + door.GetDoorRotation(door.GetAngle(EDoorState.Shut)) * axis;
            link.MidOpen = (link.Open1 + link.Open2) / 2f;
            link.MidClose = (link.Close1 + link.Close2_Normal) / 2f;
            link.TryCreateCrave();
            link.ShallTryInteract = true;
            link.Init(bc);
            link.SetDoor(door, true);
            AddToCells(bc.CoversData, door, link);
            return link;
        }

        private static void AddToCells(AICoversData covers, Door door, NavMeshDoorLink link)
        {
            if (covers == null) return;
            var p = door.transform.position;
            var min = covers.MinVoxelesValues;
            int cx = (int)((int)(p.x - min.x) / 10f), cy = (int)((int)(p.y - min.y) / 5f), cz = (int)((int)(p.z - min.z) / 10f);
            if (cx >= covers.MaxX || cy >= covers.MaxY || cz >= covers.MaxZ) return;
            for (int i = cx - 1; i <= cx + 1; i++)
                for (int j = cy - 1; j <= cy + 1; j++)
                    for (int k = cz - 1; k <= cz + 1; k++)
                    {
                        if (i < 0 || k < 0) continue;
                        var voxel = covers.GetVoxelSafeByIndexes(i, j, k);
                        if (voxel == null) continue;
                        if (voxel.DoorLinks == null) voxel.DoorLinks = new List<NavMeshDoorLink>();
                        if (voxel.DoorLinks.Contains(link)) continue;
                        voxel.DoorLinks.Add(link);
                        voxel.DoorLinksIds.Add(link.Id);
                    }
        }

        // ORBIT keeps its own door-link table (filled at GameWorld.OnGameStarted); hand
        // it our links too, in case that ran first. Soft dependency, by reflection.
        private static MethodInfo _orbitRegister;
        private static bool _orbitLooked;

        private static void OrbitRegister(NavMeshDoorLink link)
        {
            if (!_orbitLooked)
            {
                _orbitLooked = true;
                _orbitRegister = AccessTools.TypeByName("Orbit.Helpers.DoorNavMesh")?.GetMethod("RegisterLink", BindingFlags.Public | BindingFlags.Static);
            }
            try { _orbitRegister?.Invoke(null, new object[] { link }); }
            catch (Exception error) { Plugin.Log.LogWarning("ORBIT door link registration failed: " + error.Message); }
        }
    }
}
