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
            new Binding("LaunchClamp", "Release", "clamp", "发射架", "释放 / Release"),
            new Binding("RealChuteModule", "GUIDeploy", "parachute", "RealChute 降落伞 / 减速伞", "开伞"),
            new Binding("RealChuteModule", "GUIArm", "parachute", "RealChute 降落伞 / 减速伞", "预备开伞"),
            new Binding("RealChuteModule", "GUIDisarm", "parachute", "RealChute 降落伞 / 减速伞", "取消预备"),
            new Binding("RealChuteModule", "GUICut", "parachute", "RealChute 降落伞 / 减速伞", "切断伞绳"),
            new Binding("ModuleActiveRadiator", "Activate", "radiator", "散热器", "启动散热"),
            new Binding("ModuleActiveRadiator", "Shutdown", "radiator", "散热器", "停止散热"),
            new Binding("ModuleDeployableRadiator", "Extend", "radiator", "可展开散热器", "展开散热器"),
            new Binding("ModuleDeployableRadiator", "Retract", "radiator", "可展开散热器", "收起散热器"),
            new Binding("ModuleProceduralFairing", "DeployFairing", "fairing", "整流罩", "分离整流罩"),
            new Binding("ModuleJettison", "Jettison", "fairing", "可抛弃护罩", "抛弃护罩"),
            new Binding("ProceduralFairingDecoupler", "OnJettisonFairing", "fairing", "Procedural Fairings", "分离整流罩"),
            new Binding("ProceduralFairingSide", "TogglePetals", "fairing", "Procedural Fairings", "花瓣开合 / Petals"),
            new Binding("ModuleSimpleAdjustableFairing", "DeployEvent", "fairing", "Simple Adjustable Fairings", "分离整流罩"),
            new Binding("ModuleDecouplerBase", "Decouple", "decoupler", "分离器", "分离"),
            new Binding("ModuleDockingNode", "Undock", "docking", "对接口", "解除对接"),
            new Binding("ModuleDockingNode", "UndockSameVessel", "docking", "对接口", "解除同载具对接"),
            new Binding("ModuleDockingNode", "Decouple", "docking", "对接口", "分离连接")
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
