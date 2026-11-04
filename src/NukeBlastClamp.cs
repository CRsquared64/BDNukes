using System.Collections.Generic;
using System.Reflection;
using BDArmory.FX;
using HarmonyLib;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Caps the speed change a nuclear blast can give any one part. BDArmory's NukeFX pushes each part with
    /// Rigidbody.AddForceAtPosition from ExecutePartBlastEvent; close to a big yield that impulse grows without bound
    /// (a Tsar Bomba threw a craft to 37,000 km/s, carrying the camera off into space). The call is swapped for one
    /// that limits the velocity change per hit to nukeMaxDeltaV, still a violent tumble, and drops non-finite forces.
    /// Damage is untouched.
    /// </summary>
    [HarmonyPatch(typeof(NukeFX), "ExecutePartBlastEvent")]
    internal static class Patch_NukeFX_BlastForceClamp
    {
        static readonly MethodInfo add3 = AccessTools.Method(typeof(Rigidbody), nameof(Rigidbody.AddForceAtPosition),
            new[] { typeof(Vector3), typeof(Vector3), typeof(ForceMode) });
        static readonly MethodInfo add2 = AccessTools.Method(typeof(Rigidbody), nameof(Rigidbody.AddForceAtPosition),
            new[] { typeof(Vector3), typeof(Vector3) });
        static readonly MethodInfo clamped3 = AccessTools.Method(typeof(Patch_NukeFX_BlastForceClamp), nameof(AddForceAtPosition3));
        static readonly MethodInfo clamped2 = AccessTools.Method(typeof(Patch_NukeFX_BlastForceClamp), nameof(AddForceAtPosition2));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            int replaced = 0;
            foreach (var ins in code)
            {
                if ((ins.opcode == System.Reflection.Emit.OpCodes.Callvirt || ins.opcode == System.Reflection.Emit.OpCodes.Call) && ins.operand is MethodInfo mi)
                {
                    if (mi == add3) { ins.opcode = System.Reflection.Emit.OpCodes.Call; ins.operand = clamped3; replaced++; }
                    else if (mi == add2) { ins.opcode = System.Reflection.Emit.OpCodes.Call; ins.operand = clamped2; replaced++; }
                }
                yield return ins;
            }
            if (replaced == 0) Debug.LogWarning("[BDNukes]: Blast force clamp found no AddForceAtPosition in NukeFX.ExecutePartBlastEvent; craft may be thrown at extreme speed.");
        }

        static void AddForceAtPosition3(Rigidbody rb, Vector3 force, Vector3 position, ForceMode mode)
        {
            if (rb == null) return;
            rb.AddForceAtPosition(Clamp(rb, force, mode), position, mode);
        }

        static void AddForceAtPosition2(Rigidbody rb, Vector3 force, Vector3 position)
        {
            if (rb == null) return;
            rb.AddForceAtPosition(Clamp(rb, force, ForceMode.Force), position);
        }

        static Vector3 Clamp(Rigidbody rb, Vector3 force, ForceMode mode)
        {
            if (float.IsNaN(force.x) || float.IsNaN(force.y) || float.IsNaN(force.z) ||
                float.IsInfinity(force.x) || float.IsInfinity(force.y) || float.IsInfinity(force.z))
                return Vector3.zero;
            float cap = NukeSettings.Current.nukeMaxDeltaV;
            if (cap <= 0f) return force;
            float mass = Mathf.Max(rb.mass, 1e-4f);
            float dt = Time.fixedDeltaTime;
            float mag = force.magnitude;
            // the speed change this call would give the part
            float dv;
            switch (mode)
            {
                case ForceMode.Impulse: dv = mag / mass; break;
                case ForceMode.VelocityChange: dv = mag; break;
                case ForceMode.Acceleration: dv = mag * dt; break;
                default: dv = mag * dt / mass; break;
            }
            return dv > cap ? force * (cap / dv) : force;
        }
    }
}
