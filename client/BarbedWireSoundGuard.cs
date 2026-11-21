using System.Reflection;
using EFT.Interactive;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Fums.Customs.Client
{
    // The backported barbed wire's sound bank is an external reference into
    // live's shared assets, which the bundle does not carry, so _soundBank is
    // null and PlaySound throws (the damage and bleeding are applied before
    // the sound, so only the sound is lost). Skip the sound when there is no bank.
    internal sealed class BarbedWireSoundGuard : ModulePatch
    {
        private static int _skipped;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BarbedWire), nameof(BarbedWire.PlaySound));

        [PatchPrefix]
        private static bool Prefix(BarbedWire __instance)
        {
            if (__instance._soundBank != null) return true;
            if (++_skipped == 1) Plugin.Verbose("Barbed wire: no sound bank, sound skipped");
            return false;
        }
    }
}
