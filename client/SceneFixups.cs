using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Fums.Customs.Client
{
    // Per-scene fixes for the backport scenes, run after the shader rebind.
    //  - Decals: optional debug switch to hide decal meshes. Off by default:
    //    vanilla draws its decals as ordinary mesh renderers too.
    //  - Bridge abutments (bridge_opora03_l and its copy, both ends of the bridge):
    //    their far LOD (opora_lod) draws nothing, so a concrete piece vanished at
    //    normal zoom and came back in a scope (the scope switches to LOD0). They
    //    keep LOD0 down to the far LOD's switch height; LOD0 is 387 vertices.
    // Seasonal and event content is not handled here: the scenes carry the
    // game's own EventEnvironment markers, registered in each scene's
    // LocationScene, so the game shows or hides the New Year groups itself.
    // Shootable padlocks are not handled here either: the scenes carry the
    // game's own trigger components (TriggerBallistic -> HandlerInteractable /
    // HandlerGameObjectState), so shooting a padlock opens what it locks.
    internal static class SceneFixups
    {
        // LODGroup name prefixes whose far LODs do not render.
        private static readonly string[] KeepNearLod = { "bridge_opora03" };

        internal static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!BundleSceneLoader.Owns(scene.path)) return;
            try
            {
                int groups = 0;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var group in root.GetComponentsInChildren<LODGroup>(true))
                        if (KeepNearLod.Any(prefix => group.name.StartsWith(prefix, StringComparison.Ordinal)) && KeepLod0(group))
                            groups++;
                if (groups > 0) Plugin.Verbose($"Fixups: {scene.name} kept LOD0 on {groups} LOD group(s)");
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("LOD fixup failed for " + scene.name + ": " + error);
            }
            if (!Plugin.HideDecals.Value) return;
            try
            {
                int decals = 0;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                        if (renderer.enabled && UsesDecalShader(renderer))
                        {
                            renderer.enabled = false;
                            decals++;
                        }
                Plugin.Verbose($"Fixups: {scene.name} hid {decals} decal renderer(s)");
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("Scene fixups failed for " + scene.name + ": " + error);
            }
        }

        // LOD0 takes over the last LOD's range; renderers only in the far LODs are switched off
        // (a renderer taken out of a LODGroup would otherwise draw at every distance).
        private static bool KeepLod0(LODGroup group)
        {
            var lods = group.GetLODs();
            if (lods.Length < 2) return false;
            var near = new HashSet<Renderer>(lods[0].renderers.Where(r => r != null));
            foreach (var lod in lods.Skip(1))
                foreach (var renderer in lod.renderers)
                {
                    if (renderer == null || near.Contains(renderer)) continue;
                    renderer.enabled = false;
                    renderer.forceRenderingOff = true;
                }
            var keep = lods[0];
            keep.screenRelativeTransitionHeight = lods[lods.Length - 1].screenRelativeTransitionHeight;
            group.SetLODs(new[] { keep });
            group.RecalculateBounds();
            return true;
        }

        private static bool UsesDecalShader(Renderer renderer)
        {
            foreach (var material in renderer.sharedMaterials)
                if (material != null && material.shader != null && material.shader.name.StartsWith("Decal/", StringComparison.Ordinal))
                    return true;
            return false;
        }
    }
}
