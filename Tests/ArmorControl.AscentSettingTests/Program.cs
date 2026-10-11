using System;
using ArmorOverhaul.ArmorControl.Core;
using MuMech;
class Program {
 static void Check(bool value) { if(!value) throw new Exception("Ascent setting regression"); }
 static void Main() {
  var core=new MechJebCore(); var a=new MechJebModuleAscentAutopilot { enabled=true, desiredInclination=19.61, ascentPathIdxPublic=ascentType.PVG };
  var p=new MechJebModuleAscentPVG(); p.DesiredApoapsis.val=450000; a.desiredOrbitAltitude.val=150000;
  core.modules[typeof(MechJebModuleAscentAutopilot)]=a; core.modules[typeof(MechJebModuleAscentPVG)]=p;
  var guidance = new MechJebModuleAscentGuidance();
  guidance.desiredInclination.val=19.61;
  core.modules[typeof(MechJebModuleAscentGuidance)]=guidance;
  Check(AscentSettingBridge.Set(core,"inclination","29")==null);
  for (int frame=0;frame<120;frame++) guidance.DrawWindow(a);
  Check(a.desiredInclination==29 && a.enabled && a.desiredOrbitAltitude.val==150000);
  Check(AscentSettingBridge.Set(core,"desiredLan","42")==null);
  for (int frame=0;frame<120;frame++) guidance.DrawWindow(a);
  Check(a.desiredLAN==42 && a.desiredInclination==29);
  Check(AscentSettingBridge.Set(core,"inclination","NaN")!=null && a.desiredInclination==29);
  Check(AscentSettingBridge.Set(core,"inclination","181")!=null);
  Check(AscentSettingBridge.Set(core,"autoThrottle","garbage")!=null);
  Check(AscentSettingBridge.Set(core,"autoThrottle","true")==null && a.autoThrottle && a.enabled);
  Check(AscentSettingBridge.Set(core,"orbitAltitudeKm","500")!=null && a.desiredOrbitAltitude.val==150000);
  Check(AscentSettingBridge.Set(core,"orbitAltitudeKm","200")==null && a.desiredOrbitAltitude.val==200000);
  Check(AscentSettingBridge.Set(core,"pvgDesiredApoapsisKm","100")!=null && p.DesiredApoapsis.val==450000);
  Check(AscentSettingBridge.Set(core,"pvgStagingTrigger","1.5")!=null);
  Check(AscentSettingBridge.Set(core,"ascentPath","CLASSIC")!=null && a.ascentPathIdxPublic==ascentType.PVG);
  Check(AscentSettingBridge.Set(core,"unknown","1")!=null);
  Check(AscentSettingBridge.Set(core,"gtHoldApTime","10")!=null);
  core.modules[typeof(MechJebModuleAscentGuidance)]=new MechJebModuleAscentGuidance { launchingToPlane=true };
  Check(AscentSettingBridge.Set(core,"inclination","30")!=null && a.desiredInclination==29);
  Console.WriteLine("PASS native one-field write, units, ranges, invalid values, module/mode locks and no activation changes (MJ stubs)");
 }
}
