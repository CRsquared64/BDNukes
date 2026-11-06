using System.Collections.Generic;
using System.Reflection.Emit;
using BDArmory.Weapons.Missiles;
using HarmonyLib;
using UnityEngine;

namespace BDNukes
{
    /// <summary>Supplies BDArmory's existing orbital controller with its expected RCS emitter objects.</summary>
    public class ModuleNukeInterceptor : PartModule
    {
        [KSPField] public string divertSound = "BDNukes/Sounds/bdnukes_interceptor_divert";
        internal void PrepareRcs()
        {
            var prefab = GameDatabase.Instance.GetModel("BDArmory/Parts/hekv1/model");
            if (prefab == null) { Debug.LogError("[BDNukes]: HEKV RCS model is missing."); return; }
            foreach (string name in new[] { "rcsUp", "rcsDown", "rcsLeft", "rcsRight", "rcsForward" })
            {
                var nozzle = part.FindModelTransform(name);
                if (nozzle == null || nozzle.GetComponentInChildren<KSPParticleEmitter>() != null) continue;
                KSPParticleEmitter template = null;
                foreach (var emitter in prefab.GetComponentsInChildren<KSPParticleEmitter>(true))
                    if (emitter.name == name) { template = emitter; break; }
                if (template == null) continue;
                var clone = Instantiate(template.gameObject, nozzle, false);
                clone.name = name;
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = Quaternion.identity;
                clone.GetComponent<KSPParticleEmitter>().emit = false;
            }
            Destroy(prefab); // GameDatabase.GetModel returns an instantiated model.
        }
    }

    [HarmonyPatch(typeof(MissileLauncher), "OnStart")]
    internal static class Patch_InterceptorRcs
    {
        static void Prefix(MissileLauncher __instance)
        { __instance.part.FindModuleImplementing<ModuleNukeInterceptor>()?.PrepareRcs(); }
    }

    [HarmonyPatch(typeof(MissileLauncher), "DoRCS")]
    internal static class Patch_InterceptorSound
    {
        static string Sound(MissileLauncher missile) => missile.part.FindModuleImplementing<ModuleNukeInterceptor>()?.divertSound ?? "BDArmory/Sounds/popThrust";
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldstr && (string)instruction.operand == "BDArmory/Sounds/popThrust")
                {
                    var load = new CodeInstruction(OpCodes.Ldarg_0);
                    load.labels.AddRange(instruction.labels); load.blocks.AddRange(instruction.blocks);
                    yield return load;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Patch_InterceptorSound), nameof(Sound)));
                }
                else yield return instruction;
            }
        }
    }
}
