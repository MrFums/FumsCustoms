using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Fums.Customs.Client
{
    // Static decals (graffiti, the sniper-zone arrows, stains, leaks) hold their
    // material in StaticDeferredDecal._material, not on a Renderer, so the
    // sceneLoaded shader rebind never saw them and every decal in our scenes kept
    // the SDK stand-in shader. Rebinding after load is also too late: on
    // OnEnable the decal registers with StaticDeferredDecalRenderer, which copies
    // the material (DecalsData.OwnMaterial) and draws the copy. Awake runs just
    // before OnEnable, so the material is rebound there.
    internal sealed class DecalShaderRebind : ModulePatch
    {
        internal static int Rebound;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(StaticDeferredDecal), nameof(StaticDeferredDecal.Awake));

        [PatchPrefix]
        private static void Prefix(StaticDeferredDecal __instance)
        {
            var material = __instance._material;
            if (material == null || !BundleSceneLoader.Owns(__instance.gameObject.scene.path)) return;
            Rebound += ShaderRebinder.RebindMaterials(new[] { material });
        }
    }
}
