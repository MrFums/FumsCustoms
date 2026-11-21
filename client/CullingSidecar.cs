using System.IO;
using System.Reflection;
using HarmonyLib;
using Koenigz.PerfectCulling.EFT;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Fums.Customs.Client
{
    // Occlusion culling for the backport scenes uses the live 1.3 bake. Live and
    // vanilla Customs share the grid hash but not the bake, so the live
    // '<hash>_packed_cull.bytes' ships in the plugin's Culling_Data folder and the
    // game's lookup is redirected there for grids in our scenes only.
    internal sealed class CullingSidecarPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(PackedCullingGridData), nameof(PackedCullingGridData.GetPackedFilePathForGrid));

        [PatchPostfix]
        private static void Postfix(PerfectCullingAdaptiveGrid grid, ref string __result)
        {
            if (grid == null || !BundleSceneLoader.Owns(grid.gameObject.scene.path) || !CullingPack.Ready) return;
            var path = Path.Combine(Plugin.Root, "Culling_Data", grid.GridHash + "_packed_cull.bytes");
            if (File.Exists(path)) __result = path;
            else Plugin.Log.LogWarning("Live culling bake not found, grid uses the game's file: " + path);
        }
    }

    // Kill switch: with OcclusionCulling off, or when the 1.3 bake could not be
    // built (CullingPack), disable the backport's culling grid and volumes so
    // everything renders (lower FPS, never missing geometry). SPT's own bake is
    // never used for our scenes: it was baked for the 0.16.9 geometry.
    internal static class CullingSwitch
    {
        internal static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if ((Plugin.OcclusionCulling.Value && CullingPack.Ready) || !BundleSceneLoader.Owns(scene.path)) return;
            int n = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var grid in root.GetComponentsInChildren<PerfectCullingAdaptiveGrid>(true)) { grid.enabled = false; n++; }
                foreach (var vol in root.GetComponentsInChildren<PerfectCullingCrossSceneVolume>(true)) { vol.enabled = false; n++; }
                // the group's Start reads the volumes' bake groups and throws once they
                // are off, so it must not start either
                foreach (var group in root.GetComponentsInChildren<PerfectCullingCrossSceneGroup>(true)) { group.enabled = false; n++; }
            }
            if (n > 0) Plugin.Verbose($"Occlusion culling off: disabled {n} culling component(s) in {scene.name}");
        }
    }
}
