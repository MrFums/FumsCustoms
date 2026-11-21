using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;

namespace Fums.Customs.Client
{
    // Live audio components route sounds through named groups of the game's own
    // AudioMixer (ambience, radio, environment...). A bundle cannot reference the
    // game's mixer, so those fields arrive null. mixer_refs.json (written by the
    // pipeline) lists every such field with its live mixer and group name; after a
    // backport scene loads, assign the game's group of that name.
    internal static class MixerBinder
    {
        private const string FileName = "mixer_refs.json";
        private static Dictionary<string, List<JObject>> _byScene;
        private static Dictionary<string, AudioMixerGroup> _groups;
        private static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>();

        internal static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!BundleSceneLoader.Owns(scene.path)) return;
            try { Bind(scene); }
            catch (Exception error) { Plugin.Log.LogError("Mixer binding failed in " + scene.name + ": " + error); }
        }

        private static void Bind(Scene scene)
        {
            if (_byScene == null) Load();
            var key = scene.name.EndsWith("_BP") ? scene.name.Substring(0, scene.name.Length - 3) : scene.name;
            if (!_byScene.TryGetValue(key, out var rows) || rows.Count == 0) return;
            if (_groups == null || _groups.Count == 0) _groups = CollectGroups();
            var roots = scene.GetRootGameObjects();
            int set = 0, noObject = 0, noGroup = 0, failed = 0;
            var missingGroups = new HashSet<string>();
            foreach (var row in rows)
            {
                var name = (string)row["mixer"] + "/" + (string)row["group"];
                if (!_groups.TryGetValue(name, out var group) && !_groups.TryGetValue("*/" + (string)row["group"], out group))
                {
                    noGroup++;
                    missingGroups.Add(name);
                    continue;
                }
                var t = FindPath(roots, (string)row["go"]);
                var type = TypeOf((string)row["class"]);
                Component comp = null;
                if (t != null && type != null)
                {
                    // "index": which of several same-type components on the object (AudioSource rows)
                    var all = t.GetComponents(type);
                    int index = row["index"] != null ? (int)row["index"] : 0;
                    comp = index < all.Length ? all[index] : null;
                }
                if (comp == null) { noObject++; continue; }
                try
                {
                    if (SetPath(comp, ((JArray)row["path"]).Select(x => x.Type == JTokenType.Integer ? (object)(int)x : (string)x).ToList(), group)) set++;
                    else failed++;
                }
                catch (Exception) { failed++; }
            }
            Plugin.Verbose($"Mixer groups {scene.name}: set {set}, object missing {noObject}, group missing {noGroup}, failed {failed}" +
                               (missingGroups.Count > 0 ? " (missing: " + string.Join(", ", missingGroups.Take(8)) + ")" : ""));
        }

        private static void Load()
        {
            _byScene = new Dictionary<string, List<JObject>>(StringComparer.OrdinalIgnoreCase);
            var path = Path.Combine(Plugin.Root, FileName);
            if (!File.Exists(path)) { Plugin.Log.LogWarning("Mixer refs not found: " + path); return; }
            foreach (JObject row in JArray.Parse(File.ReadAllText(path)))
            {
                var scene = (string)row["scene"];
                if (!_byScene.TryGetValue(scene, out var list)) _byScene[scene] = list = new List<JObject>();
                list.Add(row);
            }
        }

        // "<mixer>/<group>", plus "*/<group>" for the first group of each name
        internal static Dictionary<string, AudioMixerGroup> Groups()
        {
            if (_groups == null || _groups.Count == 0) _groups = CollectGroups();
            return _groups;
        }

        private static Dictionary<string, AudioMixerGroup> CollectGroups()
        {
            var map = new Dictionary<string, AudioMixerGroup>(StringComparer.Ordinal);
            foreach (var g in Resources.FindObjectsOfTypeAll<AudioMixerGroup>())
            {
                if (g == null) continue;
                var mixer = g.audioMixer != null ? g.audioMixer.name : "";
                map[mixer + "/" + g.name] = g;
                if (!map.ContainsKey("*/" + g.name)) map["*/" + g.name] = g;
            }
            return map;
        }

        private static Type TypeOf(string fullName)
        {
            if (!Types.TryGetValue(fullName, out var t)) Types[fullName] = t = AccessTools.TypeByName(fullName);
            return t;
        }

        // Pipeline paths: "/"-joined names; "name~k" is the k-th earlier same-named sibling.
        internal static Transform FindPath(GameObject[] roots, string path)
        {
            var parts = path.Split('/');
            Transform cur = null;
            for (int i = 0; i < parts.Length; i++)
            {
                var name = parts[i];
                int k = 0, tilde = name.LastIndexOf('~');
                if (tilde > 0 && int.TryParse(name.Substring(tilde + 1), out var n)) { k = n; name = name.Substring(0, tilde); }
                Transform next = null;
                int seen = 0;
                if (cur == null)
                {
                    foreach (var r in roots)
                        if (r.name == name && seen++ == k) { next = r.transform; break; }
                }
                else
                {
                    for (int c = 0; c < cur.childCount; c++)
                    {
                        var ch = cur.GetChild(c);
                        if (ch.name == name && seen++ == k) { next = ch; break; }
                    }
                }
                if (next == null) return null;
                cur = next;
            }
            return cur;
        }

        // Walk field names / array indices; struct values are written back on the way out.
        private static bool SetPath(object target, List<object> path, object value)
        {
            if (path.Count == 0 || target == null) return false;
            var step = path[0];
            var rest = path.Skip(1).ToList();
            if (step is int index)
            {
                if (!(target is IList list) || index >= list.Count) return false;
                if (rest.Count == 0) { list[index] = value; return true; }
                var child = list[index];
                if (!SetPath(child, rest, value)) return false;
                if (child != null && child.GetType().IsValueType) list[index] = child;
                return true;
            }
            var field = AccessTools.Field(target.GetType(), (string)step);
            if (field == null)
            {
                // built-in components (AudioSource.outputAudioMixerGroup) expose properties
                var prop = rest.Count == 0 ? AccessTools.Property(target.GetType(), (string)step) : null;
                if (prop == null || !prop.CanWrite) return false;
                prop.SetValue(target, value, null);
                return true;
            }
            if (rest.Count == 0) { field.SetValue(target, value); return true; }
            var inner = field.GetValue(target);
            if (!SetPath(inner, rest, value)) return false;
            if (inner != null && inner.GetType().IsValueType) field.SetValue(target, inner);
            return true;
        }
    }
}
