using System;
using System.Reflection;
using EFT;
using HarmonyLib;

namespace Fums.Customs.Client
{
    // Logs the ORIGINAL exception (with its full stack) from raid-start methods
    // before other mods' finalizers rethrow it and reset the stack trace.
    // Observes only: the exception is passed on unchanged.
    internal static class StartupDiagnostics
    {
        private static readonly Harmony Harmony = new Harmony(Plugin.Guid + ".diagnostics");

        internal static void Enable()
        {
            Watch(AccessTools.Method(typeof(BotsController), nameof(BotsController.Init)));
            Watch(AccessTools.Method(typeof(GameWorld), nameof(GameWorld.SpawnLoot)));
        }

        private static void Watch(MethodBase target)
        {
            try
            {
                var finalizer = new HarmonyMethod(AccessTools.Method(typeof(StartupDiagnostics), nameof(Finalizer)))
                {
                    priority = Priority.First
                };
                Harmony.Patch(target, finalizer: finalizer);
            }
            catch (Exception error)
            {
                Plugin.Log.LogError($"Diagnostics patch on {target?.Name} failed: {error}");
            }
        }

        private static void Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception == null || !BundleSceneLoader.Active) return;
            Plugin.Log.LogError($"{__originalMethod?.DeclaringType?.Name}.{__originalMethod?.Name} threw: {__exception}");
        }
    }
}
