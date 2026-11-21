using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Fums.Customs.Client
{
    // The rebake wrote every AnimationCurve inside a 1.3 MonoBehaviour with the
    // wrong wrap mode: it cast the serialized value (0 PingPong, 1 Repeat, 2 Clamp)
    // straight to WrapMode, so Clamp (2) became WrapMode.Loop. Vanilla 0.16.9 uses
    // Clamp on all of these curves.
    // The worst effect is sound: a rolloff curve on Loop repeats past max distance,
    // so the steam hiss at the power station (max 7 m) was heard across the map,
    // and room portals, signs and season ambience leaked the same way.
    // At bundle scene load, every curve on Loop is set back to Clamp, on the
    // components and on AudioSource custom curves already copied from them.
    internal static class CurveWrapFix
    {
        private const int MaxDepth = 6;
        private static readonly Dictionary<Type, FieldInfo[]> Plans = new Dictionary<Type, FieldInfo[]>();

        internal static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!BundleSceneLoader.Owns(scene.path)) return;
            try
            {
                var perClass = new Dictionary<string, int>();
                int fixedCurves = 0, sources = 0;
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (mb == null) continue;
                        int n = FixObject(mb, mb.GetType(), 0);
                        if (n == 0) continue;
                        fixedCurves += n;
                        var name = mb.GetType().Name;
                        perClass[name] = (perClass.TryGetValue(name, out var c) ? c : 0) + n;
                    }
                    foreach (var src in root.GetComponentsInChildren<AudioSource>(true))
                        if (src != null && FixSource(src)) sources++;
                }
                if (fixedCurves > 0 || sources > 0)
                    Plugin.Verbose($"Curve wrap fix {scene.name}: {fixedCurves} curves Loop -> Clamp, {sources} AudioSources; " +
                                       string.Join(", ", perClass.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key} {kv.Value}")));
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("Curve wrap fix failed in " + scene.name + ": " + error);
            }
        }

        private static bool FixSource(AudioSource src)
        {
            bool any = false;
            foreach (AudioSourceCurveType type in Enum.GetValues(typeof(AudioSourceCurveType)))
            {
                var curve = src.GetCustomCurve(type);
                if (curve != null && Fix(curve)) { src.SetCustomCurve(type, curve); any = true; }
            }
            return any;
        }

        private static bool Fix(AnimationCurve curve)
        {
            bool changed = false;
            if (curve.preWrapMode == WrapMode.Loop) { curve.preWrapMode = WrapMode.ClampForever; changed = true; }
            if (curve.postWrapMode == WrapMode.Loop) { curve.postWrapMode = WrapMode.ClampForever; changed = true; }
            return changed;
        }

        // Walks the serialized fields Unity would have written: curves, lists and
        // arrays of them, and nested [Serializable] classes and structs.
        // AnimationCurve is a reference type, so fixing it in place needs no write-back.
        private static int FixObject(object obj, Type type, int depth)
        {
            if (obj == null || depth > MaxDepth) return 0;
            int n = 0;
            foreach (var f in Plan(type))
            {
                object value;
                try { value = f.GetValue(obj); } catch { continue; }
                n += FixValue(value, f.FieldType, depth);
            }
            return n;
        }

        private static int FixValue(object value, Type type, int depth)
        {
            if (value == null) return 0;
            if (value is AnimationCurve curve) return Fix(curve) ? 1 : 0;
            if (value is UnityEngine.Object || value is string) return 0;
            if (value is IList list)
            {
                var element = type.IsArray ? type.GetElementType() : type.IsGenericType ? type.GetGenericArguments()[0] : null;
                if (element == null || !Walkable(element)) return 0;
                int n = 0;
                foreach (var item in list) if (item != null) n += FixValue(item, item.GetType(), depth + 1);
                return n;
            }
            return Walkable(value.GetType()) ? FixObject(value, value.GetType(), depth + 1) : 0;
        }

        private static bool Walkable(Type t)
        {
            if (t == typeof(AnimationCurve)) return true;
            if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t.IsPointer) return false;
            if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return false;
            if (t.IsArray) return Walkable(t.GetElementType());
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) return Walkable(t.GetGenericArguments()[0]);
            if (t.Namespace != null && t.Namespace.StartsWith("UnityEngine")) return false;
            return t.IsSerializable;
        }

        private static FieldInfo[] Plan(Type type)
        {
            if (Plans.TryGetValue(type, out var plan)) return plan;
            var fields = new List<FieldInfo>();
            for (var t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(object); t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.IsNotSerialized || f.IsInitOnly) continue;
                    if (!f.IsPublic && !f.IsDefined(typeof(SerializeField), true)) continue;
                    if (Walkable(f.FieldType)) fields.Add(f);
                }
            }
            plan = fields.ToArray();
            Plans[type] = plan;
            return plan;
        }
    }
}
