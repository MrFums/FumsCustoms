using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Fums.Customs.Client
{
    // 1.3 horizon mountains (SkyboxMountains_Customs, copied into custom_background_BP
    // by BPRebake). Live's SeasonsMeshRenderer, a 1.3-only class, swaps each mountain's
    // material by season: slots Summer, Winter, Spring, SpringEarly, Autumn, AutumnLate
    // (same material on every mountain). This does the same from the game's season
    // controller. Summer is the material the scene already uses; the other five come
    // from customs_bp_seasons.bundle (loaded once, kept for the session).
    // MountainViewAngleController (also 1.3-only: a Transform ref and 76, 179, 8.5) is
    // not reproduced; the mountains keep live's saved placement. Without it they leave a
    // flat grey band on the horizon from high places, and 0.16.9 has no ring, so the
    // HorizonMountains config (default off) hides the whole ring at scene load.
    internal static class SeasonalMountains
    {
        private const string Root = "SkyboxMountains_Customs";
        private const string BundleFile = "customs_bp_seasons.bundle";
        private static AssetBundle _bundle;
        private static Dictionary<ESeason, Material> _materials;
        private static readonly List<MeshRenderer> Renderers = new List<MeshRenderer>();
        private static Material _summer;
        private static bool _subscribed;

        internal static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!BundleSceneLoader.Owns(scene.path)) return;
            try
            {
                GameObject root = null;
                foreach (var g in scene.GetRootGameObjects())
                    if (g.name == Root) { root = g; break; }
                if (root == null) return;
                if (!Plugin.HorizonMountains.Value)
                {
                    root.SetActive(false);
                    Plugin.Verbose("Horizon mountains hidden (HorizonMountains off)");
                    return;
                }
                Renderers.Clear();
                Renderers.AddRange(root.GetComponentsInChildren<MeshRenderer>(true));
                _summer = Renderers.Count > 0 ? Renderers[0].sharedMaterial : null;
                LoadMaterials();
                Subscribe();
                Apply("scene load");
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("Seasonal mountains failed: " + error);
            }
        }

        private static void LoadMaterials()
        {
            if (_materials != null) return;
            _materials = new Dictionary<ESeason, Material>();
            var path = Path.Combine(Plugin.Root, "bundles", BundleFile);
            if (!File.Exists(path)) { Plugin.Log.LogWarning("Seasonal mountains: bundle not found " + path); return; }
            _bundle = AssetBundle.LoadFromFile(path);
            if (_bundle == null) return;
            var names = new Dictionary<ESeason, string>
            {
                { ESeason.Autumn, "SkyboxMountainAutumn" }, { ESeason.AutumnLate, "SkyboxMountainAutumnLate" },
                { ESeason.Winter, "SkyboxMountainWinter" }, { ESeason.Spring, "SkyboxMountainSpring" },
                { ESeason.SpringEarly, "SkyboxMountainSpringEarly" },
            };
            foreach (var kv in names)
            {
                var m = _bundle.LoadAsset<Material>(kv.Value);
                if (m == null) { Plugin.Log.LogWarning("Seasonal mountains: material missing " + kv.Value); continue; }
                ShaderRebinder.RebindMaterials(new[] { m });
                _materials[kv.Key] = m;
            }
        }

        private static void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;
            Seasons.OnInitialized += controller =>
            {
                controller.StatusChangedEvent += _ => Apply("season change");
                Apply("seasons initialized");
            };
            if (Seasons.Controller != null) Seasons.Controller.StatusChangedEvent += _ => Apply("season change");
        }

        private static void Apply(string why)
        {
            Renderers.RemoveAll(r => r == null);
            if (Renderers.Count == 0) return;
            var controller = Seasons.Controller;
            if (controller == null) return;
            var season = controller.Season;
            var material = season == ESeason.Summer ? _summer
                : (_materials != null && _materials.TryGetValue(season, out var m) ? m : null);
            if (material == null) return;
            foreach (var r in Renderers) r.sharedMaterial = material;
            Plugin.Verbose($"Seasonal mountains: {season} ({material.name}) on {Renderers.Count} renderers ({why})");
        }
    }
}
