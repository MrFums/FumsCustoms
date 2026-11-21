using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EFT;
using EFT.AssetsManager;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Fums.Customs.Client
{
    // Loads the backported Customs scenes from our own bundle.
    // Flow: the game asks for the bigmap preset, we load our bundle, clone the
    // preset with the configured scene keys pointed at our _BP scenes, and run
    // the game's own preset loader on the clone. Scene keys we own are served
    // by SceneManager from our bundle; every other key loads vanilla.
    internal static class BundleSceneLoader
    {
        private const string BundleFile = "customs_bp_scenes.bundle";
        private const string ServerName = "bigmap";
        private const string Suffix = "_BP";

        private static readonly HashSet<string> Owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly FieldInfo DisableServerScenesField =
            AccessTools.Field(typeof(ScenesPreset), "_disableServerScenes");
        private static AssetBundle _bundle;
        private static ScenesPreset _clone;
        private static bool _busy;

        internal static string BundlePath => Path.Combine(Plugin.Root, "bundles", BundleFile);
        internal static bool Owns(string scenePath) => scenePath != null && Owned.Contains(scenePath);
        internal static bool Active => Owned.Count > 0;

        internal static bool BeforeLoadPreset(LoadScenesFromPresetOperation operation, ScenesPreset preset, ref Task result)
        {
            if (!Plugin.Enabled.Value || preset == null || _busy) return true;
            if (!string.Equals(preset.ServerName, ServerName, StringComparison.OrdinalIgnoreCase)) return true;
            if (!File.Exists(BundlePath))
            {
                Plugin.Log.LogWarning("Backport bundle not found, loading vanilla Customs: " + BundlePath);
                return true;
            }
            result = LoadAsync(operation, preset);
            return false;
        }

        private static async Task LoadAsync(LoadScenesFromPresetOperation operation, ScenesPreset native)
        {
            _busy = true;
            ScenesPreset toLoad = native;
            try
            {
                Release();
                if (!CullingPack.WhenReady.IsCompleted)
                    Plugin.Log.LogInfo("Waiting for the culling pack to finish building before loading Customs");
                await CullingPack.WhenReady;
                var dataPath = Application.dataPath;
                var problem = await Task.Run(() => NativeFiles.Check(dataPath, BundlePath));
                if (problem != null) throw new InvalidDataException(problem);
                // Snapshot the game's shaders before our bundle adds same-named copies.
                ShaderRebinder.CaptureNative();
                var request = AssetBundle.LoadFromFileAsync(BundlePath);
                while (!request.isDone) await Task.Yield();
                _bundle = request.assetBundle;
                if (_bundle == null) throw new IOException("Unity could not load " + BundlePath);
                toLoad = ClonePreset(native);
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("Backport preparation failed, loading vanilla Customs: " + error);
                Release();
                toLoad = native;
            }
            finally
            {
                _busy = false;
            }
            await LoadPresetReversePatch.LoadOriginal(operation, toLoad);
            if (operation.Failed) Plugin.Log.LogError("Customs preset load failed: " + operation.Error);
            else if (Active)
            {
                LiveNavMesh.HookWaypointsOnce();
                LiveNavMesh.Apply();
            }
        }

        private static ScenesPreset ClonePreset(ScenesPreset native)
        {
            var bundled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in _bundle.GetAllScenePaths())
                bundled[Path.GetFileNameWithoutExtension(path)] = path;
            var wanted = new HashSet<string>(
                Plugin.ReplacedScenes.Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            _clone = Object.Instantiate(native);
            _clone.name = native.name;
            // Instantiate does not copy non-serialized fields.
            _clone.DisableServerScenes((bool)DisableServerScenesField.GetValue(native));
            _clone._scenesResourceKeys = new List<SceneResourceKey>(native._scenesResourceKeys.Count);
            var replaced = new List<string>();
            foreach (var key in native._scenesResourceKeys)
            {
                var stem = Path.GetFileNameWithoutExtension(key.path);
                if (wanted.Remove(stem) && bundled.TryGetValue(stem + Suffix, out var path))
                {
                    _clone._scenesResourceKeys.Add(new SceneResourceKey { path = path, rcid = path, onlyOffline = key.onlyOffline });
                    Owned.Add(path);
                    replaced.Add(stem);
                }
                else
                {
                    _clone._scenesResourceKeys.Add(key);
                }
            }
            foreach (var missing in wanted)
                Plugin.Log.LogWarning("Configured scene not in the bigmap preset or bundle: " + missing);
            Plugin.Verbose($"Fums Customs: {replaced.Count} of {native._scenesResourceKeys.Count} scenes from the bundle: " +
                               string.Join(", ", replaced));
            return _clone;
        }

        internal static bool BeforeLoadScene(ResourceKey key, LoadSceneMode mode, bool activate, Action<float> progress,
            ref LoadSceneOperation result)
        {
            if (key == null || !Owns(key.path)) return true;
            var owned = new OwnedSceneOperation();
            result = owned;
            owned.Begin(key.path, mode, activate, progress);
            return false;
        }

        internal static void OnSceneUnloaded(Scene scene)
        {
            if (!Owns(scene.path) || _busy) return;
            foreach (var path in Owned)
            {
                var remaining = SceneManager.GetSceneByPath(path);
                if (remaining.IsValid() && remaining.isLoaded) return;
            }
            Release();
        }

        // MapVariants standDown hook: release the bundle when no backport scene is loaded.
        public static void ReleaseIfUnused()
        {
            foreach (var path in Owned)
            {
                var scene = SceneManager.GetSceneByPath(path);
                if (scene.IsValid() && scene.isLoaded) return;
            }
            Release();
        }

        private static void Release()
        {
            Owned.Clear();
            ShaderRebinder.Clear();
            LiveNavMesh.Release();
            if (_bundle != null)
            {
                _bundle.Unload(true);
                _bundle = null;
                Plugin.Verbose("Fums Customs bundle released");
            }
            if (_clone != null)
            {
                Object.Destroy(_clone);
                _clone = null;
            }
        }

        private sealed class OwnedSceneOperation : LoadSceneOperation
        {
            internal async void Begin(string path, LoadSceneMode mode, bool activate, Action<float> progress)
            {
                Info = "FumsCustoms: " + path;
                try
                {
                    AsyncOperation = SceneManager.LoadSceneAsync(path, mode);
                    if (AsyncOperation == null) throw new IOException("Unity rejected scene " + path);
                    // The preset loader activates scenes itself once all reach 90%.
                    AsyncOperation.allowSceneActivation = activate;
                    while (!AsyncOperation.isDone && (activate || AsyncOperation.progress < 0.9f))
                    {
                        progress?.Invoke(AsyncOperation.progress);
                        await Task.Yield();
                    }
                    Succeed = true;
                }
                catch (Exception error)
                {
                    Plugin.Log.LogError(Info + ": " + error);
                    Error = error.Message;
                }
            }
        }
    }
}
