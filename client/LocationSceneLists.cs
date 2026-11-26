using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Fums.Customs.Client
{
    // Completes each backport scene's LocationScene lists with subclass objects.
    //
    // The bake (BPRebake.WireLocationScene) fills every LocationScene list with the
    // components of that list's type. In the editor, the identity stubs of the
    // subclasses (ScavExfiltrationPoint, SharedExfiltrationPoint,
    // SecretExfiltrationPoint, Minefield, SniperFiringZone, ...) derive from
    // MonoBehaviour, not from their game base class, so the bake never saw them.
    // In the shipped custom_DesignMain_BP, ExfiltrationPoints held only the 10 PMC
    // exits (no scav, secret or co-op exits), and BorderZones held neither the 2
    // minefields nor the 3 sniper zones.
    //
    // A scav raid then had no scav exits. ExfiltrationController.ScavExfiltrationClaim,
    // called by SPT's ScavExfilPatch, indexed an empty list and threw inside the
    // game's start step. The raid never started: no timer, no extracts, bots never
    // activated.
    //
    // Runs on scene load, after LocationScene.Awake has registered the baked lists
    // and before the game reads them at raid start. It appends the subclass objects
    // the bake missed, using the same rules as the bake: active objects only for
    // the lists the game fills from active objects.
    internal static class LocationSceneLists
    {
        // Same as BPRebake.WireLocationScene and the game's LocationScene.FillArrays.
        private static readonly HashSet<string> ActiveOnly = new HashSet<string> { "BorderZones", "Windows", "FlameDamages" };

        // Filled by the game itself in Awake, or a hand-picked list rather than
        // "every object of this type".
        private static readonly HashSet<string> Skip = new HashSet<string> { "treeWinds", "EventSoundContentChangers" };

        private static readonly FieldInfo Containers = AccessTools.Field(typeof(LocationScene), "_typeToContainer");

        internal static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!BundleSceneLoader.Owns(scene.path)) return;
            try
            {
                var roots = scene.GetRootGameObjects();
                foreach (var root in roots)
                    foreach (var locationScene in root.GetComponentsInChildren<LocationScene>(true))
                        Complete(locationScene, roots, scene.name);
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("LocationScene lists could not be completed for " + scene.name + ": " + error);
            }
        }

        private static void Complete(LocationScene locationScene, GameObject[] roots, string sceneName)
        {
            var registered = Containers?.GetValue(locationScene) as IDictionary;
            foreach (var field in typeof(LocationScene).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var elem = field.FieldType.IsArray ? field.FieldType.GetElementType() : null;
                if (elem == null || Skip.Contains(field.Name) || !typeof(Component).IsAssignableFrom(elem)) continue;

                var current = field.GetValue(locationScene) as Array;
                var have = new HashSet<Component>();
                if (current != null)
                    foreach (var item in current)
                        if (item is Component component && component != null) have.Add(component);

                // Only objects of a subclass type: those are the ones the bake could not see.
                bool activeOnly = ActiveOnly.Contains(field.Name);
                var missing = roots
                    .SelectMany(root => root.GetComponentsInChildren(elem, true))
                    .Where(c => c != null && c.GetType() != elem && !have.Contains(c))
                    .Where(c => !activeOnly || c.gameObject.activeInHierarchy)
                    .ToList();
                if (missing.Count == 0) continue;

                int count = current?.Length ?? 0;
                var completed = Array.CreateInstance(elem, count + missing.Count);
                if (current != null) Array.Copy(current, completed, count);
                for (int i = 0; i < missing.Count; i++) completed.SetValue(missing[i], count + i);
                field.SetValue(locationScene, completed);
                // LocationScene.Awake registered the baked array; GetAllObjects reads this registration.
                if (registered != null && registered.Contains(elem)) registered[elem] = completed;

                var kinds = missing.GroupBy(c => c.GetType().Name).Select(g => g.Count() + " " + g.Key);
                Plugin.Log.LogInfo($"{sceneName}: {field.Name} completed with {missing.Count} object(s) the bake missed ({string.Join(", ", kinds)})");
            }
        }
    }
}
