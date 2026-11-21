using System;
using System.Collections.Generic;
using System.IO;
using Comfort.Common;
using EFT;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;

namespace Fums.Customs.Client
{
    // 1.3's HandlerEnvironmentAudioSource (no 0.16.9 class): bonfire and barrel-fire
    // loops whose AudioSource has no mixer group of its own. 1.3 routes each through
    // an indoor group (CommonAmbInEffects) or an outdoor one (CommonAmbOutEffects) as
    // the listener goes in and out. env_audio.json (analysis/mixer_refs.py) lists them;
    // this follows the main player's Environment, as 1.3 does.
    internal sealed class EnvironmentAudio : MonoBehaviour
    {
        private const string FileName = "env_audio.json";
        private static List<JObject> _rows;
        private sealed class Entry { public AudioSource Source; public AudioMixerGroup Indoor, Outdoor; }
        private static readonly List<Entry> Entries = new List<Entry>();
        private static EnvironmentAudio _runner;
        private EnvironmentType? _applied;
        private float _next;

        // A/B test (Fums-Debug's FireAudioKey, via reflection): 0 = 1.3 routing (indoor/outdoor
        // group by the player's Environment), 1 = always the outdoor group,
        // 2 = fire loops muted.
        internal static int TestMode;
        private static int _appliedMode = -1;
        internal static readonly string[] ModeNames = { "1.3 routing", "always outdoor group", "muted" };

        internal static void CycleTestMode()
        {
            TestMode = (TestMode + 1) % ModeNames.Length;
            if (_runner != null) _runner._applied = null;
            Plugin.Log.LogInfo($"Environment audio test mode: {ModeNames[TestMode]} ({Entries.Count} sources)");
        }

        internal static void Describe(System.Text.StringBuilder sb, Vector3 here)
        {
            sb.AppendLine($"  -- environment audio (fire loops): mode {ModeNames[TestMode]}, applied env {(_runner != null ? _runner._applied?.ToString() ?? "none" : "no runner")} --");
            foreach (var e in Entries)
            {
                if (e.Source == null) continue;
                var src = e.Source;
                var g = src.outputAudioMixerGroup;
                sb.AppendLine($"  {Vector3.Distance(src.transform.position, here),5:0} m  playing {src.isPlaying} mute {src.mute} enabled {src.enabled} active {src.gameObject.activeInHierarchy} " +
                              $"group {(g != null ? g.audioMixer.name + "/" + g.name : "none")}  {src.transform.parent?.name}/{src.name} at {src.transform.position}");
                var holder = src.transform.parent;
                if (holder == null) continue;
                foreach (var ps in holder.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (!ps.name.EndsWith("_Fire")) continue;
                    var r = ps.GetComponent<ParticleSystemRenderer>();
                    sb.AppendLine($"        flame {ps.name}: active {ps.gameObject.activeInHierarchy} playing {ps.isPlaying} particles {ps.particleCount} " +
                                  $"renderer {(r != null ? $"enabled {r.enabled} visible {r.isVisible} forceOff {r.forceRenderingOff}" : "none")}");
                }
            }
        }

        internal static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!BundleSceneLoader.Owns(scene.path)) return;
            try
            {
                if (_rows == null) Load();
                var key = scene.name.EndsWith("_BP") ? scene.name.Substring(0, scene.name.Length - 3) : scene.name;
                var roots = scene.GetRootGameObjects();
                var groups = MixerBinder.Groups();
                int added = 0, missing = 0;
                foreach (var row in _rows)
                {
                    if (!string.Equals((string)row["scene"], key, StringComparison.OrdinalIgnoreCase)) continue;
                    var t = MixerBinder.FindPath(roots, (string)row["go"]);
                    var src = t != null ? t.GetComponent<AudioSource>() : null;
                    var indoor = Group(groups, (JArray)row["indoor"]);
                    var outdoor = Group(groups, (JArray)row["outdoor"]);
                    if (src == null || indoor == null || outdoor == null) { missing++; continue; }
                    Entries.Add(new Entry { Source = src, Indoor = indoor, Outdoor = outdoor });
                    src.outputAudioMixerGroup = outdoor;
                    added++;
                }
                if (added + missing > 0)
                {
                    var first = Entries.Count > 0 ? Entries[Entries.Count - 1] : null;
                    Plugin.Verbose($"Environment audio {scene.name}: {added} sources, {missing} not found" +
                        (first != null ? $"; indoor group {first.Indoor.audioMixer.name}/{first.Indoor.name}, outdoor group {first.Outdoor.audioMixer.name}/{first.Outdoor.name}" : ""));
                }
                if (added > 0 && _runner == null)
                {
                    var go = new GameObject("FumsCustoms EnvironmentAudio");
                    DontDestroyOnLoad(go);
                    _runner = go.AddComponent<EnvironmentAudio>();
                }
                if (_runner != null) _runner._applied = null;
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("Environment audio failed in " + scene.name + ": " + error);
            }
        }

        private static AudioMixerGroup Group(Dictionary<string, AudioMixerGroup> groups, JArray g)
        {
            if (g == null || g.Count < 2) return null;
            if (groups.TryGetValue((string)g[0] + "/" + (string)g[1], out var group)) return group;
            return groups.TryGetValue("*/" + (string)g[1], out group) ? group : null;
        }

        private static void Load()
        {
            _rows = new List<JObject>();
            var path = Path.Combine(Plugin.Root, FileName);
            if (!File.Exists(path)) { Plugin.Log.LogWarning("Environment audio list not found: " + path); return; }
            foreach (JObject row in JArray.Parse(File.ReadAllText(path))) _rows.Add(row);
        }

        private void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.25f;
            Entries.RemoveAll(e => e.Source == null);
            if (Entries.Count == 0) return;
            if (!Singleton<GameWorld>.Instantiated) return;
            var player = Singleton<GameWorld>.Instance.MainPlayer;
            if (player == null) return;
            var env = player.Environment;
            if (_applied == env && _appliedMode == TestMode) return;
            if (Plugin.VerboseLogs.Value && _applied != env)
                Plugin.Log.LogInfo($"Environment audio: player environment {env} at {player.Position}");
            _applied = env;
            _appliedMode = TestMode;
            foreach (var e in Entries)
            {
                e.Source.outputAudioMixerGroup = env == EnvironmentType.Indoor && TestMode == 0 ? e.Indoor : e.Outdoor;
                e.Source.mute = TestMode == 2;
            }
        }
    }
}
