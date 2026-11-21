using KspRetroLanding;
using UnityEngine;

namespace ArmorOverhaul.ArmorControl.Protocol
{
    internal sealed class RetroLandingSnapshot
    {
        internal LandingTuning Tuning;
        internal bool Active;
        internal bool CanStart;
        internal string Status;
        internal string Reason;
        internal RetroLandingFlight? Flight;
        internal RetroEngineBaySnapshot EngineBay;
    }

    internal struct RetroLandingFlight
    {
        internal double Height;
        internal double VerticalSpeed;
        internal double HorizontalSpeed;
        internal double IgnitionWait;
        internal double PitchAoa;
        internal double TargetAoa;
        internal double SurfaceSpeed;
        internal double EntrySpeed;
    }

    internal sealed class RetroEngineBaySnapshot
    {
        internal RetroEngineSnapshot[] Engines;
        internal Vector2 Center, ThrustCenter;
        internal float Radius, Thrust;
        internal int Producing, Commanded;
    }

    internal sealed class RetroEngineSnapshot
    {
        internal uint PartId;
        internal int ModuleIndex;
        internal string Name, Label;
        internal bool Producing, Commanded;
        internal float Thrust;
        internal Vector2[] Nozzles;
    }
}
