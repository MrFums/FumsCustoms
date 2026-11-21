using System;
using System.Linq;
using System.IO;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.AI;

namespace Fums.Customs.Client
{
    // Replaces vanilla custom_AI's navmesh with the backport's (it covers the new
    // and changed areas). Vanilla's bot zones, spawn points, patrols and cover
    // data stay as they are.
    // The navmesh is baked in the editor (WTT-SDK BPNavBake.Full) from the 1.3
    // geometry with the live NavMeshSurface 'Main_Customs' settings and modifiers:
    // live's own asset has holes (dorm floors, Reshala's bunkhouse). It sits at the
    // origin with identity rotation, as the live surface does.
    //
    // When it is applied: in a last-priority BotsController.Init prefix, the
    // same hook Waypoints and other navmesh mods use, so nothing replaces it
    // after us. Waypoints' own bigmap navmesh is the 0.16.9 layout, so its
    // injection is skipped on our raids; its door-link and path patches stay on.
    internal static class LiveNavMesh
    {
        private const string AssetsBundleFile = "customs_bp_assets.bundle";
        private const string WaypointPatchType = "DrakiaXYZ.Waypoints.Patches.WaypointPatch";
        private static AssetBundle _bundle;
        private static NavMeshData _data;
        private static NavMeshDataInstance _instance;
        private static bool _waypointsChecked;

        internal static string BundlePath => Path.Combine(Plugin.Root, "bundles", AssetsBundleFile);
        internal static bool Wanted => Plugin.UseLiveNavMesh.Value && BundleSceneLoader.Active;

        internal static void Apply()
        {
            if (!Wanted) return;
            try
            {
                if (_data == null && !Load()) return;
                if (_instance.valid) _instance.Remove();
                NavMesh.RemoveAllNavMeshData();
                _instance = NavMesh.AddNavMeshData(_data, Vector3.zero, Quaternion.identity);
                Plugin.Verbose($"Live navmesh active: {_data.name}, bounds {_data.sourceBounds.size}, valid {_instance.valid}");
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("Live navmesh swap failed: " + error);
            }
        }

        private static bool Load()
        {
            if (!File.Exists(BundlePath))
            {
                Plugin.Log.LogWarning("Live navmesh bundle not found, keeping vanilla navmesh: " + BundlePath);
                return false;
            }
            _bundle = AssetBundle.LoadFromFile(BundlePath);
            _data = _bundle != null ? _bundle.LoadAllAssets<NavMeshData>().FirstOrDefault() : null;
            if (_data != null) return true;
            Plugin.Log.LogError("Live navmesh bundle holds no NavMeshData, keeping vanilla navmesh");
            Release();
            return false;
        }

        internal static void Release()
        {
            if (_instance.valid) _instance.Remove();
            _instance = default;
            _data = null;
            if (_bundle != null)
            {
                _bundle.Unload(true);
                _bundle = null;
            }
        }

        // Waypoints loads after or before us depending on plugin order, so look
        // for it on the first raid rather than at Awake.
        internal static void HookWaypointsOnce()
        {
            if (_waypointsChecked) return;
            _waypointsChecked = true;
            try
            {
                Type type = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.IsDynamic) continue;
                    try { type = assembly.GetType(WaypointPatchType, false); }
                    catch (Exception) { }
                    if (type != null) break;
                }
                if (type == null) return;
                var inject = type.GetMethod("InjectNavmesh", BindingFlags.NonPublic | BindingFlags.Static, null,
                    new[] { typeof(GameWorld) }, null);
                if (inject == null)
                {
                    Plugin.Log.LogWarning("Waypoints found but its InjectNavmesh method was not; its 0.16.9 navmesh may replace the live one.");
                    return;
                }
                new WaypointsInjectPatch(inject).Enable();
                Plugin.Verbose("Waypoints found: its Customs navmesh is skipped on backport raids, its door and path patches stay on.");
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("Waypoints compatibility patch failed: " + error);
            }
        }

        // Counts bot spawn and patrol points that have no navmesh within 2 m.
        // Many misses mean vanilla custom_AI does not fit the 1.3 layout there.
        internal static void AuditBotPoints(BotZone[] zones)
        {
            if (zones == null) return;
            try
            {
                var tri = NavMesh.CalculateTriangulation();
                int spawns = 0, spawnsOff = 0, patrols = 0, patrolsOff = 0;
                foreach (var zone in zones)
                {
                    if (zone == null) continue;
                    foreach (var point in zone.SpawnPoints ?? Array.Empty<EFT.Game.Spawning.ISpawnPoint>())
                    {
                        spawns++;
                        if (!OnMesh(point.Position))
                        {
                            spawnsOff++;
                            Plugin.Log.LogWarning($"Bot spawn off navmesh: zone {zone.name}, {point.Position}");
                        }
                    }
                    foreach (var way in zone.PatrolWays ?? Array.Empty<PatrolWay>())
                    {
                        if (way == null) continue;
                        foreach (var point in way.Points)
                        {
                            if (point == null) continue;
                            patrols++;
                            if (!OnMesh(point.Position))
                            {
                                patrolsOff++;
                                Plugin.Log.LogWarning($"Patrol point off navmesh: zone {zone.name}, way {way.name}, {point.Position}");
                            }
                        }
                    }
                }
                Plugin.Verbose($"Navmesh at bot init: {tri.vertices.Length} vertices, {tri.indices.Length / 3} triangles, " +
                                   $"live {(_instance.valid ? "active" : "NOT active")}. Bot points off mesh: spawns {spawnsOff}/{spawns}, " +
                                   $"patrol {patrolsOff}/{patrols}, zones {zones.Length}");
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("Bot point audit failed: " + error);
            }
        }

        private static bool OnMesh(Vector3 position) =>
            NavMesh.SamplePosition(position, out _, 2f, NavMesh.AllAreas);

        private sealed class WaypointsInjectPatch : ModulePatch
        {
            private readonly MethodBase _target;
            internal WaypointsInjectPatch(MethodBase target) { _target = target; }
            protected override MethodBase GetTargetMethod() => _target;

            [PatchPrefix]
            private static bool Prefix()
            {
                if (!Wanted) return true;
                Plugin.Verbose("Skipped Waypoints' 0.16.9 Customs navmesh; the backport's 1.3 navmesh is used.");
                return false;
            }
        }
    }

    // Runs last among BotsController.Init prefixes, so no other navmesh mod
    // replaces the live navmesh after us.
    internal sealed class LiveNavMeshInitPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotsController), nameof(BotsController.Init));

        [PatchPrefix]
        [HarmonyPriority(Priority.Last)]
        private static void Prefix()
        {
            if (LiveNavMesh.Wanted) LiveNavMesh.Apply();
        }

        [PatchPostfix]
        private static void Postfix(BotZone[] botZones)
        {
            if (BundleSceneLoader.Active) LiveNavMesh.AuditBotPoints(botZones);
        }
    }
}
