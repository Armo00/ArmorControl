using System;
using System.Collections.Generic;
using System.Linq;
using ArmorOverhaul.ArmorControl.Core;

// Isolated KSP surface: test dispatch and changing PAW availability without running the game.
internal class PartModule { public List<BaseEvent> Events = new List<BaseEvent>(); }
internal class BaseEvent
{
    public string name, guiName;
    public bool active = true, guiActive = true;
    public int Invocations;
    public void Invoke() { Invocations++; }
}
internal static class HighLogic { public static bool LoadedSceneIsFlight = true; }
namespace KSP.Localization { internal static class Localizer { public static string Format(string value) { return value; } } }

internal static class PartFlightEventsTests
{
    private class LaunchClamp : PartModule { }
    private class RealChuteModule : PartModule { }
    private class ModuleActiveRadiator : PartModule { }
    private class ModuleDeployableRadiator : PartModule { }
    private class ModuleProceduralFairing : PartModule { }
    private class ModuleJettison : PartModule { }
    private class ProceduralFairingDecoupler : PartModule { }
    private class ProceduralFairingSide : PartModule { }
    private class ModuleSimpleAdjustableFairing : PartModule { }
    private class ModuleDecouplerBase : PartModule { }
    private class ModuleAnchoredDecoupler : ModuleDecouplerBase { }
    private class ModuleDockingNode : PartModule { }

    internal static void Run()
    {
        Check(new LaunchClamp(), "Release");
        Check(new RealChuteModule(), "GUIDeploy", "GUIArm", "GUIDisarm", "GUICut");
        Check(new ModuleActiveRadiator(), "Activate", "Shutdown");
        Check(new ModuleDeployableRadiator(), "Extend", "Retract");
        Check(new ModuleProceduralFairing(), "DeployFairing");
        Check(new ModuleJettison(), "Jettison");
        Check(new ProceduralFairingDecoupler(), "OnJettisonFairing");
        Check(new ProceduralFairingSide(), "TogglePetals");
        Check(new ModuleSimpleAdjustableFairing(), "DeployEvent");
        Check(new ModuleAnchoredDecoupler(), "Decouple");
        Check(new ModuleDockingNode(), "Undock", "UndockSameVessel", "Decouple");
        Console.WriteLine("PASS part flight events: native event dispatch, inheritance, stale-state rejection, flight-only allowlist");
    }

    private static void Check(PartModule module, params string[] names)
    {
        foreach (string name in names) module.Events.Add(new BaseEvent { name = name, guiName = name });
        module.Events.Add(new BaseEvent { name = "EditorOnly", guiName = "Not exposed" });
        var snapshots = PartFlightEvents.Capture(module, 4).ToArray();
        Require(snapshots.Length == names.Length, "capture only supported events");
        foreach (string name in names)
        {
            var evt = module.Events.First(item => item.name == name);
            string command = "partEvent." + name;
            Require(PartFlightEvents.Execute(module, command) && evt.Invocations == 1, "invoke native event once");
            evt.active = false;
            Require(!PartFlightEvents.Execute(module, command) && evt.Invocations == 1, "reject action disabled after snapshot");
            Require(PartFlightEvents.Capture(module, 4).Count() == names.Length - 1, "hide inactive action");
            evt.active = true; evt.guiActive = false;
            Require(!PartFlightEvents.Execute(module, command), "reject event unavailable in flight PAW");
            evt.guiActive = true; HighLogic.LoadedSceneIsFlight = false;
            Require(!PartFlightEvents.Execute(module, command), "reject outside flight");
            HighLogic.LoadedSceneIsFlight = true;
            Require(!PartFlightEvents.Execute(new PartModule { Events = module.Events }, command), "reject wrong module type");
        }
        Require(!PartFlightEvents.Execute(module, "partEvent.EditorOnly"), "reject event not on allowlist");
    }

    private static void Require(bool result, string message)
    {
        if (!result) throw new Exception("Part flight events: " + message);
    }
}
