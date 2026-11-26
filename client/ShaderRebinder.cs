using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Fums.Customs.Client
{
    // The bundle carries SDK-compiled copies of the ripped shaders. Materials in
    // our scenes point at those copies, which are not the game's programs. Rebind
    // every material to the game's shader of the same name where one exists.
    internal static class ShaderRebinder
    {
        private static readonly Dictionary<string, Shader> Native = new Dictionary<string, Shader>(StringComparer.Ordinal);
        private static readonly HashSet<Material> Done = new HashSet<Material>();
        private static readonly HashSet<string> Missing = new HashSet<string>(StringComparer.Ordinal);

        // Live shaders the 0.16.9 game does not ship: use the closest game
        // shader (same _MainTex/_BumpMap/_Color property names) instead of the
        // ripped stand-in, which renders flat white.
        private static readonly Dictionary<string, string> Fallbacks = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Cloth/ClothShader", "p0/Cutout/Bumped Diffuse" },
            { "Cloth/ClothShader_backface", "p0/Cutout/Bumped Diffuse" },
            { "ANGRYMESH/PBR Rocks/SpecGloss_BlendTopDetail_Triplanar", "p0/Reflective/Bumped Specular" },
            { "Custom/MountainDeferred", "p0/Reflective/Bumped Specular" },
        };

        // Call before our bundle loads: everything loaded now is the game's own.
        internal static void CaptureNative()
        {
            Native.Clear();
            foreach (var shader in Resources.FindObjectsOfTypeAll<Shader>())
            {
                // Skip our own stand-ins left in memory by an earlier raid.
                if (shader != null && shader.isSupported && !Ours.Contains(shader.GetInstanceID()) && !Native.ContainsKey(shader.name))
                    Native.Add(shader.name, shader);
            }
            int added = AddVanillaShaders();
            Plugin.Verbose($"Shader snapshot: {Native.Count} game shaders ({added} from {ShaderBundleFile})");
        }

        // Shaders vanilla stores only in the shared files of the Customs levels we
        // replace (ANGRYMESH rocks, Cloth, Bumped Diffuse Emissive, fog sheets).
        // customs_bp_shaders.bundle carries the real vanilla copies; it stays loaded
        // for the session because rebound materials keep using its shaders.
        private const string ShaderBundleFile = "customs_bp_shaders.bundle";
        private static AssetBundle _shaderBundle;
        private static Shader[] _vanillaShaders;
        private static readonly HashSet<int> Ours = new HashSet<int>();

        private static int AddVanillaShaders()
        {
            if (_vanillaShaders == null)
            {
                _vanillaShaders = new Shader[0];
                var path = System.IO.Path.Combine(Plugin.Root, "bundles", ShaderBundleFile);
                try
                {
                    _shaderBundle = AssetBundle.LoadFromFile(path);
                    if (_shaderBundle != null) _vanillaShaders = _shaderBundle.LoadAllAssets<Shader>();
                    else Plugin.Log.LogWarning("Shader bundle not loaded: " + path);
                }
                catch (Exception error)
                {
                    Plugin.Log.LogWarning("Shader bundle failed: " + error.Message);
                }
            }
            int added = 0;
            foreach (var shader in _vanillaShaders)
            {
                if (shader == null || Native.ContainsKey(shader.name)) continue;
                if (!shader.isSupported)
                {
                    Plugin.Log.LogWarning("Shader bundle: not supported on this GPU: " + shader.name);
                    continue;
                }
                Native.Add(shader.name, shader);
                added++;
            }
            return added;
        }

        // The SDK's stand-in shaders declare no keywords, so the build stores every
        // material keyword as invalid. Unity does not turn them back on when the
        // shader is swapped, so the variant (for example ALPHA_HEIGHT on the
        // vertex-paint road shader) would be wrong. material_keywords.json holds
        // each material's keywords, keyed "material name|original shader name".
        private static Dictionary<string, string[]> _keywords;
        internal static int KeywordsEnabled;

        private static void EnableKeywords(Material material, string shaderName)
        {
            if (_keywords == null) _keywords = LoadKeywords();
            if (!_keywords.TryGetValue(material.name + "|" + shaderName, out var keywords)) return;
            foreach (var keyword in keywords)
            {
                if (material.IsKeywordEnabled(keyword)) continue;
                material.EnableKeyword(keyword);
                KeywordsEnabled++;
            }
        }

        // For the census: a shader that is one of our stand-ins (left unbound)
        // or one the game has no copy of.
        internal static bool IsStandIn(Shader shader)
        {
            if (NativeIds.Count != Native.Count)
            {
                NativeIds.Clear();
                foreach (var s in Native.Values) NativeIds.Add(s.GetInstanceID());
            }
            int id = shader.GetInstanceID();
            return Ours.Contains(id) || Missing.Contains(shader.name) || !NativeIds.Contains(id);
        }

        private static readonly HashSet<int> NativeIds = new HashSet<int>();

        private static Dictionary<string, string[]> _keywordsByMaterial;

        // For the census: keywords this material should have, by material name.
        internal static string[] ExpectedKeywords(Material material)
        {
            if (_keywords == null) _keywords = LoadKeywords();
            if (_keywordsByMaterial == null)
            {
                _keywordsByMaterial = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var pair in _keywords)
                {
                    var name = pair.Key.Substring(0, pair.Key.LastIndexOf('|'));
                    _keywordsByMaterial[name] = pair.Value;
                }
            }
            return _keywordsByMaterial.TryGetValue(material.name, out var keywords) ? keywords : Array.Empty<string>();
        }

        private static Dictionary<string, string[]> LoadKeywords()
        {
            var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var path = System.IO.Path.Combine(Plugin.Root, "material_keywords.json");
            try
            {
                var json = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string[]>>(System.IO.File.ReadAllText(path));
                if (json != null) result = new Dictionary<string, string[]>(json, StringComparer.Ordinal);
                Plugin.Verbose($"Material keywords: {result.Count} material(s) listed");
            }
            catch (Exception error)
            {
                Plugin.Log.LogWarning("Material keywords not loaded (" + path + "): " + error.Message);
            }
            return result;
        }

        private static Dictionary<string, int> _queues;

        private static Dictionary<string, int> Queues
        {
            get
            {
                if (_queues != null) return _queues;
                _queues = new Dictionary<string, int>(StringComparer.Ordinal);
                var path = System.IO.Path.Combine(Plugin.Root, "material_queues.json");
                try
                {
                    var json = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, int>>(System.IO.File.ReadAllText(path));
                    if (json != null) _queues = new Dictionary<string, int>(json, StringComparer.Ordinal);
                    Plugin.Verbose($"Material queues: {_queues.Count} material(s) listed");
                }
                catch (Exception error)
                {
                    Plugin.Log.LogWarning("Material queues not loaded (" + path + "): " + error.Message);
                }
                return _queues;
            }
        }

        internal static void Clear()
        {
            Native.Clear();
            Done.Clear();
            Missing.Clear();
        }

        internal static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!Plugin.RebindShaders.Value || !BundleSceneLoader.Owns(scene.path)) return;
            try
            {
                int rebound = 0, missingBefore = Missing.Count;
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                        rebound += Rebind(renderer.sharedMaterials);
                    foreach (var terrain in root.GetComponentsInChildren<Terrain>(true))
                        rebound += RebindTerrain(terrain);
                    rebound += RebindHeldMaterials(root);
                }
                Plugin.Verbose($"Shaders: {scene.name} rebound {rebound} material(s), decals {DecalShaderRebind.Rebound} so far, keywords enabled so far {KeywordsEnabled}");
                var report = Missing.Where(n => n != "Hidden/InternalErrorShader").ToList();
                if (Missing.Count > missingBefore && report.Count > 0)
                    Plugin.Log.LogWarning("No game shader for: " + string.Join(", ", report));
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("Shader rebind failed for " + scene.name + ": " + error);
            }
        }

        // Materials held by script fields and drawn later by reference: broken
        // window pieces (WindowBreaker.Material, when it is not the window
        // renderer's own material) and the stationary weapons' muzzle jet and
        // smoke. Static decals copy their material on enable, so DecalShaderRebind
        // handles them in Awake instead.
        private static int RebindHeldMaterials(GameObject root)
        {
            int count = 0;
            foreach (var window in root.GetComponentsInChildren<EFT.Interactive.WindowBreaker>(true))
                if (window.Material != null) count += Rebind(new[] { window.Material });
            foreach (var muzzle in root.GetComponentsInChildren<MuzzleManager>(true))
                if (muzzle.JetMaterial != null) count += Rebind(new[] { muzzle.JetMaterial });
            foreach (var smoke in root.GetComponentsInChildren<MuzzleSmoke>(true))
                if (smoke.Material != null) count += Rebind(new[] { smoke.Material });
            return count;
        }

        private static int RebindTerrain(Terrain terrain)
        {
            int count = 0;
            if (terrain.materialTemplate != null) count += Rebind(new[] { terrain.materialTemplate });
            var data = terrain.terrainData;
            if (data == null) return count;
            foreach (var tree in data.treePrototypes)
                if (tree.prefab != null)
                    foreach (var r in tree.prefab.GetComponentsInChildren<Renderer>(true))
                        count += Rebind(r.sharedMaterials);
            foreach (var detail in data.detailPrototypes)
                if (detail.prototype != null)
                    foreach (var r in detail.prototype.GetComponentsInChildren<Renderer>(true))
                        count += Rebind(r.sharedMaterials);
            return count;
        }

        // materials loaded outside a bundle scene (SeasonalMountains)
        internal static int RebindMaterials(Material[] materials) => Plugin.RebindShaders.Value ? Rebind(materials) : 0;

        private static int Rebind(Material[] materials)
        {
            int count = 0;
            foreach (var material in materials)
            {
                if (material == null || material.shader == null || !Done.Add(material)) continue;
                var name = material.shader.name;
                if (!Native.TryGetValue(name, out var native) && Fallbacks.TryGetValue(name, out var fallback))
                    Native.TryGetValue(fallback, out native);
                if (native != null)
                {
                    if (native != material.shader)
                    {
                        Ours.Add(material.shader.GetInstanceID());
                        var queue = material.renderQueue;
                        material.shader = native;
                        // Draw order: vanilla's queue for this material where it has a
                        // vanilla twin, else live's saved value (material_queues.json).
                        // Reading renderQueue before the swap is wrong for materials
                        // without a custom queue: it returns the stand-in's default.
                        material.renderQueue = Queues.TryGetValue(material.name + "|" + name, out var q) ? q : queue;
                        count++;
                        EnableKeywords(material, name);
                    }
                }
                else
                {
                    Missing.Add(name);
                }
            }
            return count;
        }
    }
}
