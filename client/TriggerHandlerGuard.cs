using System;
using System.Collections.Generic;
using System.Reflection;
using EFT.GameTriggers;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Fums.Customs.Client
{
    // TriggersEmitter.OnTriggerEvent calls every handler of a trigger in one
    // loop, so the first handler that throws stops the rest. Shooting a supply
    // box padlock runs HandlerInteractable, whose LootableContainer.Interact
    // trips a SAIN postfix (no player on a trigger-driven interact); the throw
    // then skipped the handlers that hide the padlock. Run the same loop with
    // each handler isolated, and log what threw.
    internal sealed class TriggerHandlerGuard : ModulePatch
    {
        private const int MaxLogged = 10;
        private static int _caught;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(TriggersEmitter), nameof(TriggersEmitter.OnTriggerEvent));

        [PatchPrefix]
        private static bool Prefix(TriggersEmitter __instance, TriggerEvent triggerEvent)
        {
            RunAll(__instance._allTimeSubscribers, triggerEvent);
            if (__instance._actionsMap.TryGetValue(triggerEvent.TriggerId, out var handlers))
                RunAll(handlers, triggerEvent);
            return false;
        }

        // A snapshot, so a handler that subscribes or unsubscribes mid-event
        // cannot break the iteration.
        private static void RunAll(List<Action<TriggerEvent>> handlers, TriggerEvent triggerEvent)
        {
            if (handlers == null || handlers.Count == 0) return;
            foreach (var handler in handlers.ToArray())
            {
                if (handler == null) continue;
                try { handler(triggerEvent); }
                catch (Exception error)
                {
                    if (++_caught > MaxLogged) continue;
                    var target = handler.Target?.GetType().Name ?? handler.Method.DeclaringType?.Name;
                    Plugin.Log.LogWarning(
                        $"Trigger {triggerEvent.TriggerId}: handler {target}.{handler.Method.Name} threw, " +
                        $"the other handlers still ran: {error}");
                }
            }
        }
    }
}
