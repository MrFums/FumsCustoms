using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using EFT;
using EFT.AssetsManager;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine.SceneManagement;

namespace Fums.Customs.Client
{
    // Swaps the Customs preset for a clone whose scene keys point into our bundle.
    internal sealed class LoadPresetPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(LoadScenesFromPresetOperation), nameof(LoadScenesFromPresetOperation.LoadPresetAsync));

        [PatchPrefix]
        private static bool Prefix(LoadScenesFromPresetOperation __instance, ScenesPreset preset, ref Task __result) =>
            BundleSceneLoader.BeforeLoadPreset(__instance, preset, ref __result);
    }

    // Calls the game's own LoadPresetAsync with our cloned preset.
    internal sealed class LoadPresetReversePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(LoadScenesFromPresetOperation), nameof(LoadScenesFromPresetOperation.LoadPresetAsync));

        [PatchReverse]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Task LoadOriginal(LoadScenesFromPresetOperation instance, ScenesPreset preset) =>
            throw new NotSupportedException("Reverse patch was not applied");
    }

    // Loads scene keys we own from our bundle instead of the game's bundle system.
    internal sealed class LoadScenePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(AssetsManagerExtension), nameof(AssetsManagerExtension.LoadScene),
                new[] { typeof(IAssetsManager), typeof(ResourceKey), typeof(LoadSceneMode), typeof(bool), typeof(Action<float>) });

        [PatchPrefix]
        private static bool Prefix(ResourceKey resourceKey, LoadSceneMode loadSceneMode, bool allowSceneActivation,
            Action<float> progressCallback, ref LoadSceneOperation __result) =>
            BundleSceneLoader.BeforeLoadScene(resourceKey, loadSceneMode, allowSceneActivation, progressCallback, ref __result);
    }
}
