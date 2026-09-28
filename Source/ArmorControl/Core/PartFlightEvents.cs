using System;
using System.Collections.Generic;
using ArmorOverhaul.ArmorControl.Protocol;

namespace ArmorOverhaul.ArmorControl.Core
{
    // Explicit flight-event allowlist: never expose arbitrary PAW or editor events.
    internal static class PartFlightEvents
    {
        private sealed class Binding
        {
            internal string Module, Event, Kind, Title, Label;
            internal Binding(string module, string evt, string kind, string title, string label)
            { Module = module; Event = evt; Kind = kind; Title = title; Label = label; }
        }

        private static readonly Binding[] Bindings = {
            new Binding("LaunchClamp", "Release", "clamp", "Launch clamp", "Release"),
            new Binding("RealChuteModule", "GUIDeploy", "parachute", "RealChute parachute / drag chute", "Deploy parachute"),
            new Binding("RealChuteModule", "GUIArm", "parachute", "RealChute parachute / drag chute", "Arm parachute"),
            new Binding("RealChuteModule", "GUIDisarm", "parachute", "RealChute parachute / drag chute", "Disarm parachute"),
            new Binding("RealChuteModule", "GUICut", "parachute", "RealChute parachute / drag chute", "Cut parachute"),
            new Binding("ModuleActiveRadiator", "Activate", "radiator", "Radiator", "Activate cooling"),
            new Binding("ModuleActiveRadiator", "Shutdown", "radiator", "Radiator", "Stop cooling"),
            new Binding("ModuleDeployableRadiator", "Extend", "radiator", "Deployable radiator", "Extend radiator"),
            new Binding("ModuleDeployableRadiator", "Retract", "radiator", "Deployable radiator", "Retract radiator"),
            new Binding("ModuleProceduralFairing", "DeployFairing", "fairing", "Fairing", "Jettison fairing"),
            new Binding("ModuleJettison", "Jettison", "fairing", "Jettisonable shroud", "Jettison shroud"),
            new Binding("ProceduralFairingDecoupler", "OnJettisonFairing", "fairing", "Procedural Fairings", "Jettison fairing"),
            new Binding("ProceduralFairingSide", "TogglePetals", "fairing", "Procedural Fairings", "Toggle petals"),
            new Binding("ModuleSimpleAdjustableFairing", "DeployEvent", "fairing", "Simple Adjustable Fairings", "Jettison fairing"),
            new Binding("ModuleDecouplerBase", "Decouple", "decoupler", "Decoupler", "Decouple"),
            new Binding("ModuleDockingNode", "Undock", "docking", "Docking ports", "Undock"),
            new Binding("ModuleDockingNode", "UndockSameVessel", "docking", "Docking ports", "Undock same vessel"),
            new Binding("ModuleDockingNode", "Decouple", "docking", "Docking ports", "Decouple connection")
        };

        private static bool Matches(PartModule module, Binding binding)
        {
            for (Type type = module.GetType(); type != null; type = type.BaseType)
                if (type.Name == binding.Module) return true;
            return false;
        }

        private static BaseEvent AvailableEvent(PartModule module, string name)
        {
            if (module.Events == null) return null;
            foreach (BaseEvent evt in module.Events)
                if (evt != null && evt.name == name && evt.active && evt.guiActive) return evt;
            return null;
        }

        internal static IEnumerable<PartActionSnapshot> Capture(PartModule module, int moduleIndex)
        {
            foreach (Binding binding in Bindings)
            {
                if (!Matches(module, binding)) continue;
                BaseEvent evt = AvailableEvent(module, binding.Event);
                if (evt == null) continue;
                yield return new PartActionSnapshot(binding.Kind, moduleIndex, binding.Title,
                    KSP.Localization.Localizer.Format(evt.guiName ?? binding.Label), false,
                    "partEvent." + binding.Event, binding.Label, true, null, null, null);
            }
        }

        internal static bool Execute(PartModule module, string action)
        {
            if (!HighLogic.LoadedSceneIsFlight) return false;
            foreach (Binding binding in Bindings)
            {
                if (action != "partEvent." + binding.Event || !Matches(module, binding)) continue;
                BaseEvent evt = AvailableEvent(module, binding.Event);
                if (evt == null) return false;
                evt.Invoke();
                return true;
            }
            return false;
        }
    }
}
