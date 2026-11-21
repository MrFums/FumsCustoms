using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Fums.Customs.Client
{
    // 1.3 rewrote AreaLight's shadow tail, so the backported lights carry 0.16.9's
    // light shape, colour and flags but none of its asset refs. Every vanilla
    // 0.16.9 Customs area light points at the same assets: Unity's built-in Quad
    // and Cube meshes and the always-loaded Hidden/AreaLight proxy shader (shadows
    // are off on all of them, so the shadowmap shaders are only set when found).
    // Fill them before Init, or the light never builds its proxy and stays dark.
    internal sealed class AreaLightFix : ModulePatch
    {
        private static Mesh _quad, _cube;
        private static Shader _proxy, _shadowmap, _blur;
        private static int _filled;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(AreaLight), nameof(AreaLight.Init));

        [PatchPrefix]
        private static void Prefix(AreaLight __instance)
        {
            if (__instance.m_Quad != null && __instance.m_Cube != null && __instance.m_ProxyShader != null) return;
            if (_quad == null) _quad = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            if (_cube == null) _cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            if (_proxy == null) _proxy = Shader.Find("Hidden/AreaLight");
            if (_shadowmap == null) _shadowmap = Shader.Find("Hidden/Shadowmap");
            if (_blur == null) _blur = Shader.Find("Hidden/BlurShadowmap");
            if (__instance.m_Quad == null) __instance.m_Quad = _quad;
            if (__instance.m_Cube == null) __instance.m_Cube = _cube;
            if (__instance.m_ProxyShader == null) __instance.m_ProxyShader = _proxy;
            if (__instance.m_ShadowmapShader == null) __instance.m_ShadowmapShader = _shadowmap;
            if (__instance.m_BlurShadowmapShader == null) __instance.m_BlurShadowmapShader = _blur;
            if (++_filled == 1)
                Plugin.Verbose($"Area lights: filling built-in refs (quad {_quad != null}, cube {_cube != null}, " +
                                   $"proxy shader {_proxy != null}, shadowmap {_shadowmap != null})");
        }
    }
}
