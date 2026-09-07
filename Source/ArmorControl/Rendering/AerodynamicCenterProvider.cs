using System;
using System.Reflection;
using ArmorOverhaul.ArmorControl.Protocol;
using UnityEngine;

namespace ArmorOverhaul.ArmorControl.Rendering
{
    internal static class AerodynamicCenterProvider
    {
        private const double MinimumDynamicPressureKpa = 0.00001;
        private const float MinimumForceSquared = 1e-8f;
        private static MethodInfo vesselForceMethod;
        private static MethodInfo vesselTorqueMethod;
        private static bool farBindingAttempted;

        internal static AerodynamicCenterSnapshot Capture(Vessel vessel, VesselImageRenderer renderer)
        {
            if (vessel == null) return AerodynamicCenterSnapshot.Unavailable("No active vessel.");
            if (vessel.dynamicPressurekPa <= MinimumDynamicPressureKpa)
                return AerodynamicCenterSnapshot.Unavailable("Dynamic pressure is too low for a stable FAR aerodynamic center.");

            try
            {
                if (!EnsureFarBinding())
                    return AerodynamicCenterSnapshot.Unavailable("FAR 0.16.1.2 public API is not loaded.");
                Vector3 force = (Vector3)vesselForceMethod.Invoke(null, new object[] { vessel });
                Vector3 torqueAtCom = (Vector3)vesselTorqueMethod.Invoke(null, new object[] { vessel });
                if (!Finite(force) || !Finite(torqueAtCom) || force.sqrMagnitude <= MinimumForceSquared)
                    return AerodynamicCenterSnapshot.Unavailable("FAR aerodynamic force is not available yet.");

                // FAR 0.16.1.2 reports this torque about vessel.CoM.  The perpendicular
                // minimum-torque application point satisfies M = r x F, therefore
                // r = (F x M) / |F|^2.  The component parallel to F is intentionally
                // undefined and omitted, matching FARCenterQuery.GetMinTorquePos.
                Vector3 offset = Vector3.Cross(force, torqueAtCom) / force.sqrMagnitude;
                Vector3 worldCenter = vessel.CoM + offset;
                Transform reference = vessel.ReferenceTransform != null ? vessel.ReferenceTransform : vessel.transform;
                double forward = Vector3.Dot(offset, reference.forward);
                double right = Vector3.Dot(offset, reference.right);
                double up = Vector3.Dot(offset, reference.up);

                Vector2 normalized = Vector2.zero;
                bool projected = renderer != null && renderer.TryProjectWorldPoint(vessel, worldCenter, out normalized);
                return new AerodynamicCenterSnapshot(true, projected, "FAR 0.16.1.2",
                    projected ? "Current FAR aerodynamic force center." : "FAR center is outside the current vessel view.",
                    projected ? normalized.x : 0, projected ? normalized.y : 0, offset.magnitude,
                    forward, right, up);
            }
            catch (Exception exception)
            {
                return AerodynamicCenterSnapshot.Unavailable("FAR aerodynamic center unavailable: " + exception.Message);
            }
        }

        private static bool EnsureFarBinding()
        {
            if (vesselForceMethod != null && vesselTorqueMethod != null) return true;
            if (farBindingAttempted) return false;
            farBindingAttempted = true;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.Equals(assembly.GetName().Name, "FerramAerospaceResearch", StringComparison.Ordinal)) continue;
                Type api = assembly.GetType("FerramAerospaceResearch.FARAPI", false);
                if (api == null) continue;
                vesselForceMethod = api.GetMethod("VesselAerodynamicForce", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(Vessel) }, null);
                vesselTorqueMethod = api.GetMethod("VesselAerodynamicTorque", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(Vessel) }, null);
                break;
            }
            return vesselForceMethod != null && vesselTorqueMethod != null;
        }

        private static bool Finite(Vector3 value)
        {
            return Finite(value.x) && Finite(value.y) && Finite(value.z);
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
