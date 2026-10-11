using System;
using System.Collections.Generic;
namespace MuMech {
 public enum ascentType { CLASSIC, GRAVITYTURN, PVG }
 public class Editable { public double val; }
 public class EditableInt { public int val; }
 public class MechJebCore { public Dictionary<Type,object> modules = new Dictionary<Type,object>(); public T GetComputerModule<T>() where T:class { return modules.TryGetValue(typeof(T),out var m)?(T)m:null; } }
 public class MechJebModuleAscentGuidance {
  public bool launchingToPlane;
  public Editable desiredInclination = new Editable();
  public Editable desiredLAN = new Editable();
  public void DrawWindow(MechJebModuleAscentAutopilot autopilot) {
   autopilot.desiredInclination = desiredInclination.val;
   autopilot.desiredLAN = desiredLAN.val;
  }
 }
 public class MechJebModuleAscentAutopilot {
 public bool enabled; public ascentType ascentPathIdxPublic;
 public Editable desiredOrbitAltitude = new Editable();
 public double desiredInclination;
 public double desiredLAN;
 public bool autoThrottle;
 public bool correctiveSteering;
 public bool autostage;
 public bool skipCircularization;
 public bool limitAoA;
 public Editable maxAoA = new Editable();
 public bool limitQaEnabled;
 public Editable limitQa = new Editable();
 public bool forceRoll;
 public Editable verticalRoll = new Editable();
 public Editable turnRoll = new Editable();
 public Editable correctiveSteeringGain = new Editable();
 public bool autodeploySolarPanels;
 public bool autoDeployAntennas;
 public Editable rollAltitude = new Editable();
 public Editable aoALimitFadeoutPressure = new Editable();
 public Editable launchPhaseAngle = new Editable();
 public Editable launchLANDifference = new Editable();
 }
 public class MechJebModuleAscentGT {
 public Editable turnStartAltitude = new Editable();
 public Editable turnStartVelocity = new Editable();
 public Editable turnStartPitch = new Editable();
 public Editable intermediateAltitude = new Editable();
 public Editable holdAPTime = new Editable();
 }
 public class MechJebModuleAscentClassic {
 public bool autoPath;
 public Editable turnStartAltitude = new Editable();
 public Editable turnStartVelocity = new Editable();
 public Editable turnEndAltitude = new Editable();
 public Editable turnEndAngle = new Editable();
 public Editable turnShapeExponent = new Editable();
 }
 public class MechJebModuleAscentPVG {
 public Editable PitchStartVelocity = new Editable();
 public Editable PitchRate = new Editable();
 public Editable DesiredApoapsis = new Editable();
 public bool AttachAltFlag;
 public Editable DesiredAttachAlt = new Editable();
 public Editable DynamicPressureTrigger = new Editable();
 public bool StagingTriggerFlag;
 public EditableInt StagingTrigger = new EditableInt();
 public bool FixedCoast;
 public Editable FixedCoastLength = new Editable();
 }
}
