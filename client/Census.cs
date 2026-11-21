using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Fums.Customs.Client
{
    // Load-time audit of every backport scene, so one raid checks the whole map
    // instead of someone walking it. Logged per scene after the shader rebind:
    //  - renderers whose material still uses one of our stand-in shaders, or a
    //    shader the game does not have
    //  - null material slots
    //  - material keywords from material_keywords.json that are not enabled
    //  - how many objects each LocationScene list registered
    // A few seconds into the raid it also logs event content that is still
    // active (the game should have switched it off unless an event is running).
    internal static class Census
    {
        private const int ShowFirst = 20;

        internal static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!Plugin.VerboseLogs.Value || !BundleSceneLoader.Owns(scene.path)) return;
            try
            {
                int renderers = 0, standIn = 0, nullSlots = 0, keywordsOff = 0;
                var offenders = new List<string>();
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                    {
                        renderers++;
                        foreach (var material in renderer.sharedMaterials)
                        {
                            if (material == null) { nullSlots++; continue; }
                            var shader = material.shader;
                            if (shader == null || ShaderRebinder.IsStandIn(shader))
                            {
                                standIn++;
                                if (offenders.Count < ShowFirst)
                                    offenders.Add($"stand-in shader '{(shader != null ? shader.name : "null")}' on '{material.name}' at {Path(renderer.transform)}");
                            }
                            foreach (var keyword in ShaderRebinder.ExpectedKeywords(material))
                                if (!material.IsKeywordEnabled(keyword))
                                {
                                    keywordsOff++;
                                    if (offenders.Count < ShowFirst)
                                        offenders.Add($"keyword {keyword} off on '{material.name}' ({material.shader.name}) at {Path(renderer.transform)}");
                                }
                        }
                    }
                }
                var lists = new StringBuilder();
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var ls in root.GetComponentsInChildren<LocationScene>(true))
                        foreach (var field in typeof(LocationScene).GetFields())
                            if (field.FieldType.IsArray && field.GetValue(ls) is Array array && array.Length > 0)
                                lists.Append($"{field.Name} {array.Length}, ");
                Plugin.Log.LogInfo($"Census {scene.name}: {renderers} renderers, {standIn} on stand-in/missing shaders, " +
                                   $"{nullSlots} null material slots, {keywordsOff} keywords off; LocationScene: {lists}");
                foreach (var line in offenders) Plugin.Log.LogWarning("  " + line);
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("Census failed for " + scene.name + ": " + error);
            }
        }

        // Called from CensusTicker.Update every frame; runs once per raid, 10 s in.
        private static float _raidStart = -1f;
        private static bool _eventChecked;

        internal static void Tick()
        {
            if (!BundleSceneLoader.Active || !Singleton<GameWorld>.Instantiated || Singleton<GameWorld>.Instance.MainPlayer == null)
            {
                _raidStart = -1f;
                _eventChecked = false;
                return;
            }
            if (_raidStart < 0f) _raidStart = Time.time;
            if (_eventChecked || Time.time - _raidStart < 10f) return;
            _eventChecked = true;
            if (!Plugin.VerboseLogs.Value) return;
            try
            {
                var events = UnityEngine.Object.FindObjectsOfType<EventEnvironment>(true);
                var active = events.Where(e => e.gameObject.activeInHierarchy).ToArray();
                Plugin.Log.LogInfo($"Census event content: {events.Length} EventEnvironment group(s), {active.Length} active 10 s into the raid");
                foreach (var e in active.Take(ShowFirst)) Plugin.Log.LogWarning("  active event group: " + Path(e.transform));
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("Event census failed: " + error);
            }
        }

        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (; t != null && parts.Count < 10; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }

    // Drives Census.Tick (the in-raid event-content check).
    internal sealed class CensusTicker : MonoBehaviour
    {
        private void Update() => Census.Tick();
    }
}
