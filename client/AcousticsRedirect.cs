using System.IO;
using System.Reflection;
using Audio.SpatialSystem;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Fums.Customs.Client
{
    // The backport scenes carry 1.3's acoustic geometry, acoustic map and spatial
    // audio graph, which read 1.3 bake files from StreamingAssets. Live's files ship
    // in the plugin's Audio13 folder (same sub-paths as under StreamingAssets);
    // components in our scenes are pointed there, vanilla scenes keep vanilla files.
    internal static class AcousticsRedirect
    {
        internal const string Folder = "Audio13";
        private static int _redirected;

        // "StreamingAssets/Acoustics/x/y.xrageo" -> path relative to Application.dataPath
        // into the plugin folder (no "StreamingAssets" in it, so the native file read is used)
        internal static string Redirect(string relativeFilePath, GameObject owner)
        {
            if (string.IsNullOrEmpty(relativeFilePath) || owner == null) return null;
            if (!BundleSceneLoader.Owns(owner.scene.path)) return null;
            const string prefix = "StreamingAssets/";
            var rel = relativeFilePath.Replace('\\', '/');
            if (!rel.StartsWith(prefix)) return null;
            var target = Path.Combine(Plugin.Root, Folder, rel.Substring(prefix.Length));
            if (!File.Exists(target)) return null;
            if (++_redirected <= 3) Plugin.Verbose("Acoustics: using 1.3 file " + rel.Substring(prefix.Length));
            return Path.GetRelativePath(Application.dataPath, target).Replace('\\', '/');
        }
    }

    internal sealed class AcousticGeometryRedirectPatch : ModulePatch
    {
        private static readonly FieldInfo PathField = AccessTools.Field(typeof(MetaXRAcousticGeometry), "relativeFilePath");

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(MetaXRAcousticGeometry), "ReadFile");

        [PatchPrefix]
        private static void Prefix(MetaXRAcousticGeometry __instance)
        {
            var to = AcousticsRedirect.Redirect((string)PathField.GetValue(__instance), __instance.gameObject);
            if (to != null) PathField.SetValue(__instance, to);
        }
    }

    internal sealed class AcousticMapRedirectPatch : ModulePatch
    {
        private static readonly FieldInfo PathField = AccessTools.Field(typeof(MetaXRAcousticMap), "relativeFilePath");

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(MetaXRAcousticMap), "StartInternal");

        [PatchPrefix]
        private static void Prefix(MetaXRAcousticMap __instance)
        {
            var to = AcousticsRedirect.Redirect((string)PathField.GetValue(__instance), __instance.gameObject);
            if (to != null) PathField.SetValue(__instance, to);
        }
    }

    // SpatialAudioDataLoader(dataPath, runner) reads streamingAssetsPath + dataPath;
    // an absolute dataPath wins in Path.Combine.
    internal sealed class SpatialBakeRedirectPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Constructor(typeof(SpatialAudioDataLoader), new[] { typeof(string), typeof(MonoBehaviour) });

        [PatchPrefix]
        private static void Prefix(ref string dataPath, MonoBehaviour runner)
        {
            if (runner == null || !BundleSceneLoader.Owns(runner.gameObject.scene.path) || string.IsNullOrEmpty(dataPath)) return;
            var target = Path.Combine(Plugin.Root, AcousticsRedirect.Folder, dataPath.Replace('\\', '/'));
            if (!File.Exists(target)) return;
            Plugin.Verbose("Spatial audio: using 1.3 bake " + dataPath);
            dataPath = target;
        }
    }
}
