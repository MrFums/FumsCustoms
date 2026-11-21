using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using DeferredDecals;
using EFT.Interactive;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Fums.Customs.Client
{
    // A raid crashed natively inside Transform.SetParent, called from
    // DeferredDecalRenderer.GetDynamicDecal while an explosion's decals were
    // attached to the colliders they hit. The decal's helper transform is parented
    // to the hit collider so the decal follows it if it moves. Static map geometry
    // never moves, so in backport scenes the helper is only parented to things that
    // can move (doors and other interactive objects, rigidbodies); otherwise it stays
    // unparented, and the decal looks the same. Vanilla scenes are untouched.
    internal sealed class DecalParentGuard : ModulePatch
    {
        private static int _skipped;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(DeferredDecalRenderer), nameof(DeferredDecalRenderer.GetDynamicDecal));

        [PatchTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var setParent = AccessTools.PropertySetter(typeof(Transform), nameof(Transform.parent));
            var safe = AccessTools.Method(typeof(DecalParentGuard), nameof(SafeSetParent));
            int replaced = 0;
            foreach (var instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) &&
                    instruction.operand is MethodInfo method && method == setParent)
                {
                    replaced++;
                    yield return new CodeInstruction(OpCodes.Call, safe);
                    continue;
                }
                yield return instruction;
            }
            if (replaced == 0) Plugin.Log.LogWarning("Decal parent guard: no parent assignment found in GetDynamicDecal; not active");
        }

        private static void SafeSetParent(Transform child, Transform parent)
        {
            if (child == null) return;
            if (parent == null || !BundleSceneLoader.Owns(parent.gameObject.scene.path)) { child.parent = parent; return; }
            if (parent.GetComponentInParent<WorldInteractiveObject>() != null || parent.GetComponentInParent<Rigidbody>() != null)
            {
                child.parent = parent;
                return;
            }
            if (child.parent != null) child.parent = null;
            if (++_skipped == 1) Plugin.Verbose("Decal parent guard: decals on static backport geometry stay unparented");
        }
    }
}
