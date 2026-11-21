using System;
using System.IO;
using System.Linq;
using EFT.EnvironmentEffect;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Fums.Customs.Client
{
    // custom_Scripts stays vanilla (it holds 0.16.9's own sky, weather and
    // renderers), but 1.3 changed its geometry-bound content. scripts_patch.json
    // (analysis/gen_scripts_patch.py) carries live's version of it:
    //  - indoor exposure volumes: 9 new (big factory, middle region, quest
    //    container), 1 moved; the trigger-group tree is rebuilt afterwards
    //  - rain blockers: WeatherObstacle merged its DryPlane quads in Awake, so the
    //    collider is rebuilt from live's quads (13 new, 102 moved)
    //  - one moved airdrop point
    internal static class VanillaScriptsPatch
    {
        private const string SceneName = "custom_Scripts";
        private const string FileName = "scripts_patch.json";

        internal static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != SceneName || !BundleSceneLoader.Active) return;
            var path = Path.Combine(Plugin.Root, FileName);
            if (!File.Exists(path)) { Plugin.Log.LogWarning("Scripts patch not found: " + path); return; }
            try { Apply(scene, JObject.Parse(File.ReadAllText(path))); }
            catch (Exception error) { Plugin.Log.LogError("custom_Scripts patch failed: " + error); }
        }

        private static void Apply(Scene scene, JObject patch)
        {
            var roots = scene.GetRootGameObjects();
            int moved = 0, created = 0, fields = 0, missing = 0;
            foreach (JObject m in patch["moved"])
            {
                var t = MixerBinder.FindPath(roots, (string)m["path"]);
                if (t == null) { missing++; continue; }
                SetLocal(t, m);
                moved++;
            }
            foreach (JObject f in patch["trigger_fields"])
            {
                var t = MixerBinder.FindPath(roots, (string)f["path"]);
                var trigger = t != null ? t.GetComponent<IndoorTrigger>() : null;
                if (trigger == null) { missing++; continue; }
                fields += SetFields(trigger, (JObject)f["fields"]);
            }
            foreach (JObject n in patch["new_triggers"])
            {
                var parent = MixerBinder.FindPath(roots, (string)n["parent"]);
                if (parent == null) { missing++; continue; }
                var go = new GameObject((string)n["name"]);
                go.SetActive(false);
                go.transform.SetParent(parent, false);
                SetLocal(go.transform, n);
                var sibling = (int)n["sibling"];
                if (sibling >= 0 && sibling < parent.childCount) go.transform.SetSiblingIndex(sibling);
                var trigger = go.AddComponent<IndoorTrigger>();
                fields += SetFields(trigger, (JObject)n["fields"]);
                go.SetActive((bool)n["active"]);
                created++;
            }
            // children first, so each group encloses its rebuilt subgroups
            var groups = roots.SelectMany(r => r.GetComponentsInChildren<TriggerGroup>(true))
                .OrderByDescending(g => Depth(g.transform)).ToList();
            foreach (var g in groups) g.Reinit();

            int quads = 0;
            var quad = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            foreach (var kv in (JObject)patch["dry_planes"])
            {
                var t = MixerBinder.FindPath(roots, kv.Key);
                var obstacle = t != null ? t.GetComponent<WeatherObstacle>() : null;
                if (obstacle == null) { missing++; continue; }
                var list = (JArray)kv.Value;
                var combine = new CombineInstance[list.Count];
                for (int i = 0; i < list.Count; i++)
                {
                    var a = list[i].Select(x => (float)x).ToArray();
                    combine[i] = new CombineInstance
                    {
                        mesh = quad,
                        transform = Matrix4x4.TRS(new Vector3(a[0], a[1], a[2]), new Quaternion(a[3], a[4], a[5], a[6]),
                            new Vector3(a[7], a[8], a[9]))
                    };
                }
                var mesh = new Mesh { name = "WeatherObstacle GetMesh (1.3)" };
                mesh.CombineMeshes(combine, mergeSubMeshes: true, useMatrices: true);
                // same as WeatherObstacle.CombineMesh: world matrices into the obstacle's collider
                var collider = obstacle.GetComponent<MeshCollider>();
                if (collider == null) collider = obstacle.gameObject.AddComponent<MeshCollider>();
                collider.sharedMesh = mesh;
                AccessTools.Field(typeof(WeatherObstacle), "mesh")?.SetValue(obstacle, mesh);
                quads += list.Count;
            }
            Plugin.Verbose($"custom_Scripts 1.3 patch: moved {moved}, indoor triggers added {created}, fields {fields}, " +
                               $"trigger groups rebuilt {groups.Count}, rain blocker quads {quads}, not found {missing}");
            if (patch["values"] is JArray values) ApplyValues(roots, values);
        }

        // 1.3 sky/weather settings (WeatherController.TimeOfDayController gradients and
        // ranges, RainFallDrops): "member" walks fields from the component; value types
        // are written back on the way out; a "gradient" is rebuilt from its serialized form.
        private static void ApplyValues(GameObject[] roots, JArray values)
        {
            int set = 0, failed = 0;
            foreach (JObject v in values)
            {
                try
                {
                    var t = MixerBinder.FindPath(roots, (string)v["path"]);
                    var type = AccessTools.TypeByName((string)v["class"]);
                    var comp = t != null && type != null ? t.GetComponent(type) : null;
                    var member = ((JArray)v["member"]).Select(x => (string)x).ToList();
                    if (comp != null && SetMember(comp, member, 0, v["value"], (string)v["kind"])) set++;
                    else
                    {
                        failed++;
                        Plugin.Log.LogWarning($"custom_Scripts 1.3 value not applied: {v["class"]}.{string.Join(".", member)}");
                    }
                }
                catch (Exception error)
                {
                    failed++;
                    Plugin.Log.LogWarning("custom_Scripts 1.3 value failed: " + error.Message);
                }
            }
            Plugin.Verbose($"custom_Scripts 1.3 sky/weather values: set {set}, failed {failed}");
        }

        private static bool SetMember(object target, System.Collections.Generic.List<string> member, int i, JToken value, string kind)
        {
            if (target == null) return false;
            var f = AccessTools.Field(target.GetType(), member[i]);
            if (f == null) return false;
            if (i == member.Count - 1)
            {
                object v;
                if (kind == "gradient") v = ToGradient((JObject)value);
                else if (f.FieldType == typeof(double)) v = (double)value;
                else v = Convert(value, f.FieldType);
                if (v == null) return false;
                f.SetValue(target, v);
                return true;
            }
            var inner = f.GetValue(target);
            if (!SetMember(inner, member, i + 1, value, kind)) return false;
            if (inner != null && inner.GetType().IsValueType) f.SetValue(target, inner);  // Vector2 etc.
            return true;
        }

        private static Gradient ToGradient(JObject j)
        {
            int nc = (int)j["m_NumColorKeys"], na = (int)j["m_NumAlphaKeys"];
            var colors = new GradientColorKey[nc];
            for (int k = 0; k < nc; k++)
            {
                var c = j["key" + k];
                colors[k] = new GradientColorKey(new Color((float)c["r"], (float)c["g"], (float)c["b"]), (int)j["ctime" + k] / 65535f);
            }
            var alphas = new GradientAlphaKey[na];
            for (int k = 0; k < na; k++)
                alphas[k] = new GradientAlphaKey((float)j["key" + k]["a"], (int)j["atime" + k] / 65535f);
            var g = new Gradient();
            g.SetKeys(colors, alphas);
            if (j["m_Mode"] != null) g.mode = (GradientMode)(int)j["m_Mode"];
            // Gradient.colorSpace (2022.2+): -1 uninitialized, 0 gamma, 1 linear
            var cs = AccessTools.Property(typeof(Gradient), "colorSpace");
            if (cs != null && cs.CanWrite && j["m_ColorSpace"] != null)
                cs.SetValue(g, Enum.ToObject(cs.PropertyType, (int)j["m_ColorSpace"]), null);
            return g;
        }

        private static int Depth(Transform t)
        {
            int d = 0;
            for (; t != null; t = t.parent) d++;
            return d;
        }

        private static void SetLocal(Transform t, JObject j)
        {
            var p = j["pos"].Select(x => (float)x).ToArray();
            var r = j["rot"].Select(x => (float)x).ToArray();
            var s = j["scale"].Select(x => (float)x).ToArray();
            t.localPosition = new Vector3(p[0], p[1], p[2]);
            t.localRotation = new Quaternion(r[0], r[1], r[2], r[3]);
            t.localScale = new Vector3(s[0], s[1], s[2]);
        }

        // plain values only (bool/int/float/enum, Bounds, vectors); refs are not carried
        private static int SetFields(object target, JObject values)
        {
            int n = 0;
            foreach (var kv in values)
            {
                var f = AccessTools.Field(target.GetType(), kv.Key);
                if (f == null) continue;
                var v = Convert(kv.Value, f.FieldType);
                if (v == null) continue;
                f.SetValue(target, v);
                n++;
            }
            return n;
        }

        private static object Convert(JToken j, Type type)
        {
            try
            {
                if (type == typeof(bool)) return (int)j != 0;
                if (type == typeof(int)) return (int)j;
                if (type == typeof(float)) return (float)j;
                if (type.IsEnum) return Enum.ToObject(type, (int)j);
                if (type == typeof(Vector3)) return V3(j);
                if (type == typeof(Bounds)) return new Bounds(V3(j["m_Center"]), V3(j["m_Extent"]) * 2f);
            }
            catch (Exception) { }
            return null;
        }

        private static Vector3 V3(JToken j) => new Vector3((float)j["x"], (float)j["y"], (float)j["z"]);
    }
}
