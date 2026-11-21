using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Fums.Customs.Client
{
    // The 1.3 sound scene's 3D ambient sources (the steam hiss at the power station,
    // max 7 m, routed to the indoor ambient group) are heard indoors across the
    // whole map in 0.16.9, even with their rolloff curve clamped. Until the cause in
    // the 0.16.9 mixer/attenuation path is known, each 3D source in that scene is
    // muted while the listener is beyond its max distance. Within range it plays
    // as in 1.3.
    internal sealed class AmbientRangeGate : MonoBehaviour
    {
        private const float Interval = 0.2f, Margin = 2f;
        private static readonly List<AudioSource> Sources = new List<AudioSource>();
        private static readonly HashSet<AudioSource> MutedByUs = new HashSet<AudioSource>();
        private static readonly HashSet<int> Described = new HashSet<int>();
        private static AudioListener _listener;
        private float _next;

        internal static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!BundleSceneLoader.Owns(scene.path) || !scene.name.StartsWith("Custom_Sound")) return;
            Sources.Clear(); MutedByUs.Clear(); Described.Clear();
            foreach (var root in scene.GetRootGameObjects())
                Sources.AddRange(root.GetComponentsInChildren<AudioSource>(true));
            Plugin.Verbose($"Ambient range gate {scene.name}: watching {Sources.Count} sources");
        }

        private void Update()
        {
            if (Sources.Count == 0 || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + Interval;
            // the Fums-Debug sound finder mutes sources itself: stay out of its way
            if (SoundFinderRunning()) return;
            if (_listener == null || !_listener.isActiveAndEnabled)
            {
                _listener = FindObjectsOfType<AudioListener>().FirstOrDefault(l => l.isActiveAndEnabled);
                if (_listener == null) return;
            }
            var here = _listener.transform.position;
            for (int i = Sources.Count - 1; i >= 0; i--)
            {
                var s = Sources[i];
                if (s == null) { Sources.RemoveAt(i); continue; }
                bool far = s.isPlaying && s.spatialBlend >= 0.5f &&
                           Vector3.Distance(s.transform.position, here) > s.maxDistance + Margin;
                if (far)
                {
                    if (!s.mute) { s.mute = true; MutedByUs.Add(s); Describe(s); }
                }
                else if (MutedByUs.Remove(s)) s.mute = false;
            }
        }

        // Fums-Debug's sound finder (DebugExtract.MuteBisect.Running), found by reflection.
        private static System.Reflection.PropertyInfo _finderRunning;
        private static bool _finderLooked;
        private static bool SoundFinderRunning()
        {
            if (!_finderLooked)
            {
                _finderLooked = true;
                foreach (var a in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.IsDynamic || a.GetName().Name != "DebugExtract") continue;
                    _finderRunning = a.GetType("DebugExtract.MuteBisect", false)?.GetProperty("Running",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    break;
                }
            }
            try { return _finderRunning != null && (bool)_finderRunning.GetValue(null); }
            catch { return false; }
        }

        // Once per source: what the 0.16.9 engine has for it, to find the real cause.
        private static void Describe(AudioSource s)
        {
            if (!Plugin.VerboseLogs.Value || !Described.Add(s.GetInstanceID())) return;
            var c = s.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
            string keys = c != null ? string.Join(" ", c.keys.Select(k => $"({k.time:0.##},{k.value:0.##})")) : "none";
            var g = s.outputAudioMixerGroup;
            Plugin.Verbose($"Ambient range gate: muting '{(s.clip != null ? s.clip.name : "no clip")}' on {s.name} beyond {s.maxDistance:0.#} m; " +
                               $"rolloff {s.rolloffMode} min {s.minDistance:0.#} curve {keys} wrap {c?.preWrapMode}/{c?.postWrapMode} " +
                               $"blend {s.spatialBlend:0.##} mixer '{(g != null ? g.name : "none")}'");
        }
    }
}
