using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT.Interactive;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Fums.Customs.Client
{
    // Vanilla custom_AI grenade-throw places that sit at a door run
    // Aggregate() over all doors to find theirs. With the backport's doors not
    // restored yet there are none, so it throws and the raid fails to start.
    // While the backport is loaded and no doors exist, treat these places as
    // door-less (the same state the game uses for places without a door).
    internal sealed class GrenadePlaceGuard : ModulePatch
    {
        private static int _cleared;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ThrowGrenadePlace), nameof(ThrowGrenadePlace.Init));

        [PatchPrefix]
        private static bool Prefix(ThrowGrenadePlace __instance, IEnumerable<Door> allDoors)
        {
            if (!BundleSceneLoader.Active || !__instance.HaveDoor || (allDoors != null && allDoors.Any())) return true;
            __instance.HaveDoor = false;
            __instance.Door = null;
            if (++_cleared == 1) Plugin.Verbose("Grenade places: no doors in scene, door links cleared");
            return false;
        }
    }
}
