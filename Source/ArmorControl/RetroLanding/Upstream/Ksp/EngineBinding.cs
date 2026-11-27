using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using LandingPoc;
using UnityEngine;

namespace KspRetroLanding
{
    internal sealed class EngineBinding
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly HashSet<string> SolidPropellants = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SolidFuel", "HTPB", "PBAN", "HNIW", "NGNC", "PSPC"
        };
        public readonly ModuleEngines Engine;
        private readonly FieldInfo ignitions, ullage, ullageSet, ignitionResources, rfIgnited, rfSpoolTime;
        private readonly MethodInfo rfPropellantAvailable;
        private readonly List<ModuleGimbal> gimbals;
        public bool EverArmed, Faulted, Issued;
        public double RequestedAt = double.NaN;
        public double ActivatedAt = double.NaN;
        public double StartDeadline = double.PositiveInfinity;
        public double Min, Max, Isp, SpoolSeconds;
        public Vector3 NeutralMoment { get; private set; }
        internal Vector2? InstallationPosition { get; private set; }
        public string SpoolSource { get; private set; }
        private bool restoreThrottle, previousIndependentThrottle;
        private float previousThrottlePercentage;

        public EngineBinding(ModuleEngines engine)
        {
            Engine = engine;
            gimbals = engine.part.FindModulesImplementing<ModuleGimbal>();
            EverArmed = engine.EngineIgnited;
            Type type = engine.GetType();
            if (type.FullName == "RealFuels.ModuleEnginesRF")
            {
                ignitions = type.GetField("ignitions", Fields);
                ullage = type.GetField("ullage", Fields);
                ullageSet = type.GetField("ullageSet", Fields);
                ignitionResources = type.GetField("ignitionResources", Fields);
                rfIgnited = type.GetField("ignited", Fields);
                rfSpoolTime = type.GetField("effectiveSpoolUpTime", Fields);
                rfPropellantAvailable = type.GetMethod("PropellantAvailable", Fields, null, Type.EmptyTypes, null);
            }
        }

        public bool Rf { get { return Engine.GetType().FullName == "RealFuels.ModuleEnginesRF"; } }
        public bool? RfCombusting { get { return rfIgnited == null ? (bool?)null : (bool)rfIgnited.GetValue(Engine); } }
        public bool? RfPropellantAvailable
        {
            get { return rfPropellantAvailable == null ? (bool?)null : (bool)rfPropellantAvailable.Invoke(Engine, null); }
        }
        public int Ignitions { get { return ignitions != null ? (int)ignitions.GetValue(Engine) : Rf ? 0 : -1; } }
        public bool NeedsUllage { get { return Rf && (ullage == null || (bool)ullage.GetValue(Engine)); } }
        internal static bool UsesSolidPropellant(ModuleEngines engine)
            => engine.engineType == EngineType.SolidBooster ||
                (engine.propellants?.Exists(p => p != null && p.ratio > 0 && SolidPropellants.Contains(p.name)) == true);

        public bool Burning
        {
            get
            {
                // RF reports a small nonzero thrust while its ignition ramp
                // is still building pressure below the controllable range.
                double threshold = Rf ? Math.Max(0.1, 0.1 * Min) : 0.1;
                return Engine.finalThrust > threshold && !Engine.flameout;
            }
        }

        internal bool ThrustEstablished => Burning && Engine.finalThrust >= 0.9 * Min;

        public double Stability
        {
            get
            {
                if (!NeedsUllage) return 1;
                object value = ullageSet == null ? null : ullageSet.GetValue(Engine);
                if (value == null) return 0;
                MethodInfo method = value.GetType().GetMethod("GetUllageStability", Fields);
                return method == null ? 0 : Convert.ToDouble(method.Invoke(value, null));
            }
        }

        private double measuredShutdownTime = double.NaN;
        private double shutdownStart, shutdownPrevious, shutdownArea;

        internal void BeginBoostbackShutdown()
        {
            shutdownStart = shutdownPrevious = Math.Max(0, Engine.finalThrust);
            shutdownArea = 0;
        }

        internal void ObserveBoostbackShutdown(double elapsed)
        {
            if (shutdownStart <= 0 || elapsed <= 0) return;
            double thrust = Math.Max(0, Engine.finalThrust);
            shutdownArea += 0.5 * (shutdownPrevious + thrust) * elapsed;
            shutdownPrevious = thrust;
            if (thrust > 0.1) return;
            measuredShutdownTime = shutdownArea / shutdownStart;
            shutdownStart = 0;
        }

        internal double BoostbackShutdownTime(double physicsStep)
        {
            if (EngineResponse.Known(measuredShutdownTime)) return measuredShutdownTime;
            double configured = EngineResponse.StockLerp(Engine.useEngineResponseTime, Engine.engineDecelerationSpeed, physicsStep);
            // Until this engine's tail has been observed, start with the
            // configured decay estimate plus one control interval.
            return physicsStep + (EngineResponse.Known(configured) ? configured : 0);
        }

        public void Measure(double pressureAtm, double physicsStep)
        {
            Isp = Engine.atmosphereCurve.Evaluate((float)Math.Max(0, pressureAtm));
            double flow = Math.Max(0, Engine.multFlow);
            double lo = Engine.minFuelFlow * Isp * Settings.G0 * flow;
            double hi = Engine.maxFuelFlow * Isp * Settings.G0 * flow;
            Max = lo + (hi - lo) * Engine.thrustPercentage / 100.0;
            Min = Engine.throttleLocked ? Max : lo;
            if (Rf)
            {
                SpoolSource = "RF effectiveSpoolUpTime";
                SpoolSeconds = rfSpoolTime != null ? Convert.ToDouble(rfSpoolTime.GetValue(Engine)) : double.NaN;
            }
            else if (Engine.throttleUseAlternate)
            {
                double limiter = Math.Max(0, Math.Min(1, Engine.thrustPercentage / 100.0));
                double target = Engine.throttleMin + (1 - Engine.throttleMin) * limiter;
                SpoolSource = "stock alternate throttle response";
                SpoolSeconds = EngineResponse.StockAlternate(Engine.throttleInstant, target,
                    Engine.throttleResponseRate, 0.01 * Engine.throttleIgniteLevelMult,
                    Engine.throttleStartupMult, Engine.throttleStartedMult, physicsStep);
            }
            else
            {
                SpoolSource = Engine.useEngineResponseTime ? "stock engineAccelerationSpeed" : "stock instant";
                SpoolSeconds = EngineResponse.StockLerp(Engine.useEngineResponseTime, Engine.engineAccelerationSpeed, physicsStep);
            }
            if (!EngineResponse.Known(SpoolSeconds)) SpoolSource += " unavailable";
        }

        private static bool GimbalAvailable(ModuleGimbal gimbal)
        {
            return gimbal.isEnabled && gimbal.moduleIsEnabled && !gimbal.gimbalLock && gimbal.gimbalLimiter > 0;
        }

        internal bool UsesGimbal(Transform pivot)
        {
            if (pivot == null) return false;
            foreach (Transform nozzle in Engine.thrustTransforms)
                if (nozzle != null && (nozzle == pivot || nozzle.IsChildOf(pivot))) return true;
            return false;
        }

        public Engine Spec(Transform frame, Vector3 center)
        {
            Vector3 mounting = Vector3.zero, moment = Vector3.zero, authority = Vector3.zero;
            Vector3 axis = Vector3.zero;
            float totalWeight = 0;
            for (int i = 0; i < Engine.thrustTransforms.Count; i++)
            {
                Transform nozzle = Engine.thrustTransforms[i];
                float weight = i < Engine.thrustTransformMultipliers.Count ? Engine.thrustTransformMultipliers[i] : 1;
                Vector3 force = -nozzle.forward, position = nozzle.position;
                ModuleGimbal matched = null;
                Transform pivot = null;
                foreach (ModuleGimbal gimbal in gimbals)
                {
                    if (gimbal.gimbalTransforms == null) continue;
                    int index = gimbal.gimbalTransforms.FindIndex(t => t != null && (nozzle == t || nozzle.IsChildOf(t)));
                    if (index < 0) continue;
                    matched = gimbal;
                    pivot = gimbal.gimbalTransforms[index];
                    if (pivot.parent != null && gimbal.initRots != null && index < gimbal.initRots.Count)
                    {
                        Quaternion neutral = pivot.parent.rotation * gimbal.initRots[index] * Quaternion.Inverse(pivot.rotation);
                        force = neutral * force;
                        position = pivot.position + neutral * (position - pivot.position);
                    }
                    break;
                }
                Vector3 direction = frame.InverseTransformDirection(force);
                Vector3 offset = frame.InverseTransformDirection(position - center);
                Vector3 mount = pivot == null ? offset : frame.InverseTransformDirection(pivot.position - center);
                mounting += weight * mount;
                axis += weight * direction;
                moment += weight * Vector3.Cross(offset, direction);
                totalWeight += weight;
                if (matched != null && GimbalAvailable(matched))
                    authority += weight * GimbalAuthority(matched, mount, direction);
            }
            NeutralMoment = totalWeight > 0 ? moment / totalWeight : Vector3.zero;
            InstallationPosition = null;
            if (totalWeight <= 0) return new Engine(0, 0, 0, 0, Ignitions, SpoolSeconds, Engine.allowShutdown,
                false, default, default, hasMount: false);
            mounting /= totalWeight;
            Vector3 installed = mounting + frame.InverseTransformDirection(center - frame.position);
            InstallationPosition = new Vector2(installed.x, installed.z);
            authority /= totalWeight;
            axis /= totalWeight;
            // Thrust directed away from the landing axis cannot contribute to
            // a burn that points the vessel's reference axis along braking.
            bool usable = axis.y > 0;
            return new Engine(mounting.x, mounting.z, usable ? Min : 0, usable ? Max : 0, Ignitions,
                SpoolSeconds, Engine.allowShutdown, authority.x > 0 || authority.z > 0,
                new Torque(NeutralMoment.x, NeutralMoment.y, NeutralMoment.z), new Torque(authority.x, authority.y, authority.z),
                new Torque(axis.x, axis.y, axis.z), Isp);
        }

        private static Vector3 GimbalAuthority(ModuleGimbal gimbal, Vector3 offset, Vector3 direction)
        {
            // Use the smaller travel in each direction to budget bidirectional
            // steering. Moment per kN is the moment arm of the nozzle change.
            float x = SymmetricTravel(gimbal.gimbalRangeXP, gimbal.gimbalRangeXN, gimbal.gimbalRange);
            float y = SymmetricTravel(gimbal.gimbalRangeYP, gimbal.gimbalRangeYN, gimbal.gimbalRange);
            float travel = Mathf.Sin(Mathf.Clamp(Mathf.Min(x, y) * gimbal.gimbalLimiter / 100, 0, 90) * Mathf.Deg2Rad);
            return travel * new Vector3(
                gimbal.enablePitch ? Mathf.Abs(Vector3.Cross(offset, Vector3.Cross(Vector3.right, direction)).x) : 0,
                gimbal.enableRoll ? Mathf.Abs(Vector3.Cross(offset, Vector3.Cross(Vector3.up, direction)).y) : 0,
                gimbal.enableYaw ? Mathf.Abs(Vector3.Cross(offset, Vector3.Cross(Vector3.forward, direction)).z) : 0);
        }

        private static float SymmetricTravel(float positive, float negative, float fallback)
        {
            return Mathf.Min(positive >= 0 ? positive : fallback, negative >= 0 ? negative : fallback);
        }

        public Vector3d ThrustVector()
        {
            Vector3d axis = Vector3d.zero;
            double weight = 0;
            for (int i = 0; i < Engine.thrustTransforms.Count; i++)
            {
                double multiplier = i < Engine.thrustTransformMultipliers.Count
                    ? Engine.thrustTransformMultipliers[i] : 1;
                // In KSP +Z of the nozzle transform points along the exhaust.
                axis -= (Vector3d)Engine.thrustTransforms[i].forward * multiplier;
                weight += multiplier;
            }
            return weight > 0 ? Engine.finalThrust * axis / weight : Vector3d.zero;
        }

        public bool CanStart()
        {
            if (Faulted || !Engine.isEnabled || !Engine.moduleIsEnabled || Max <= 0) return false;
            if (!EngineResponse.Known(SpoolSeconds)) return false;
            if (Ignitions == 0 || (EverArmed && !Engine.allowRestart)) return false;
            foreach (Propellant p in Engine.propellants)
                if (!Available(p.id, p.GetFlowMode(), 0.0001)) return false;
            if (!Rf) return true;
            if (ignitionResources == null || rfIgnited == null) return false;
            var resources = ignitionResources.GetValue(Engine) as IEnumerable;
            if (resources == null) return true;
            foreach (object item in resources)
            {
                var resource = item as ModuleResource;
                if (resource == null || !Available(resource.id, resource.flowMode, resource.amount)) return false;
            }
            return true;
        }

        private bool Available(int id, ResourceFlowMode mode, double needed)
        {
            double amount, capacity;
            Engine.part.GetConnectedResourceTotals(id, mode, out amount, out capacity, true);
            return amount >= needed;
        }

        internal PropellantDemand[] MeasurePropellant()
        {
            var demand = new List<PropellantDemand>();
            double mixtureMass = 0;
            foreach (Propellant p in Engine.propellants)
            {
                PartResourceDefinition resource = PartResourceLibrary.Instance.GetDefinition(p.id);
                if (resource != null && !p.ignoreForIsp && resource.density > 0 && p.ratio > 0)
                    mixtureMass += resource.density * p.ratio;
            }
            // Massless resources still use the existing live flow/ignition
            // gate. Their consumption has no mass-based Isp conversion here.
            if (mixtureMass <= 0) return demand.ToArray();
            foreach (Propellant p in Engine.propellants)
            {
                PartResourceDefinition resource = PartResourceLibrary.Instance.GetDefinition(p.id);
                if (resource == null || resource.density <= 0 || p.ratio <= 0) continue;
                double amount, capacity;
                Engine.part.GetConnectedResourceTotals(p.id, p.GetFlowMode(), out amount, out capacity,
                    Math.Max(0, p.minResToLeave), true);
                double available = double.IsNaN(amount) || double.IsInfinity(amount) ? 0 : Math.Max(0, amount);
                demand.Add(new PropellantDemand(p.id, p.ratio * resource.density / mixtureMass, available * resource.density));
            }
            return demand.ToArray();
        }

        public void TakeIndependentThrottle(float value)
        {
            previousIndependentThrottle = Engine.independentThrottle;
            previousThrottlePercentage = Engine.independentThrottlePercentage;
            restoreThrottle = true;
            Engine.independentThrottlePercentage = 100 * value;
            Engine.independentThrottle = true;
        }

        public void RestoreThrottle()
        {
            if (!restoreThrottle) return;
            Engine.independentThrottlePercentage = previousThrottlePercentage;
            Engine.independentThrottle = previousIndependentThrottle;
            restoreThrottle = false;
        }

        internal void ReturnToSharedThrottle()
        {
            if (!restoreThrottle) return;
            Engine.independentThrottlePercentage = 100;
            Engine.independentThrottle = false;
            restoreThrottle = false;
        }

        internal void SaveOriginalThrottle(ConfigNode snapshot)
        {
            if (!restoreThrottle) return;
            snapshot.SetValue("independentThrottle", previousIndependentThrottle.ToString(), true);
            snapshot.SetValue("independentThrottlePercentage",
                previousThrottlePercentage.ToString("R", CultureInfo.InvariantCulture), true);
        }

        public void Cancel(bool shutdown)
        {
            if (shutdown && Engine.EngineIgnited && Engine.allowShutdown) Engine.Shutdown();
            RequestedAt = double.NaN;
            ActivatedAt = double.NaN;
            StartDeadline = double.PositiveInfinity;
            Issued = false;
        }

        public void QueueStart(double now, double preparationSeconds)
        {
            RequestedAt = now;
            StartDeadline = now + preparationSeconds + Math.Max(6, SpoolSeconds + 2);
        }

        public void Activate(double now)
        {
            if (!Engine.EngineIgnited) Engine.Activate();
            EverArmed = true;
            Issued = true;
            ActivatedAt = now;
            StartDeadline = now + Math.Max(6, SpoolSeconds + 2);
        }
    }
}
