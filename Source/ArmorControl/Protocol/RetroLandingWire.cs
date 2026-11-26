using System.Text;
using UnityEngine;

namespace ArmorOverhaul.ArmorControl.Protocol
{
    internal static partial class WireProtocol
    {
        private static void AppendRetroLanding(StringBuilder json, RetroLandingSnapshot value)
        {
            json.Append(",\"retroLanding\":");
            if (value == null)
            {
                json.Append("null");
                return;
            }
            json.Append("{\"active\":").Append(value.Active ? "true" : "false");
            Append(json, "canStart", value.CanStart);
            Append(json, "canStartTargeted", value.CanStartTargeted);
            AppendNullable(json, "vesselTargetId", value.VesselTargetId);
            AppendNullable(json, "vesselTargetName", value.VesselTargetName);
            if (value.Targeted != null)
            {
                json.Append(",\"targeted\":{\"waypoint\":").Append(Quote(value.Targeted.Waypoint));
                Append(json, "state", value.Targeted.State);
                Append(json, "vesselTarget", value.Targeted.VesselTarget);
                if (value.Targeted.VesselTarget)
                {
                    Append(json, "eastOffset", value.Targeted.EastOffset);
                    Append(json, "northOffset", value.Targeted.NorthOffset);
                }
                Append(json, "fallback", value.Targeted.Fallback);
                AppendNullable(json, "warning", value.Targeted.Warning);
                Append(json, "pitch", value.Targeted.Pitch);
                Append(json, "bank", value.Targeted.Bank);
                Append(json, "boostRemaining", value.Targeted.BoostRemaining);
                json.Append('}');
            }
            AppendNullable(json, "status", value.Status);
            AppendNullable(json, "reason", value.Reason);
            json.Append(",\"tuning\":{\"maxG\":").Append(Number(value.Tuning.MaxG));
            Append(json, "boostbackMaxG", value.Tuning.BoostbackMaxG);
            Append(json, "entryMaxG", value.Tuning.EntryMaxG);
            Append(json, "boostbackStartAoa", value.Tuning.BoostbackStartAoa);
            Append(json, "boostbackFineKm", value.Tuning.BoostbackFineDistance / 1000);
            Append(json, "finalSeconds", value.Tuning.FinalSeconds);
            Append(json, "roll", value.Tuning.Roll);
            Append(json, "coastPitch", value.Tuning.CoastPitch);
            Append(json, "coastPitchMin", value.Tuning.CoastPitchMin);
            Append(json, "coastPitchMax", value.Tuning.CoastPitchMax);
            Append(json, "vesselTargetEastOffset", value.Tuning.VesselTargetEastOffset);
            Append(json, "vesselTargetNorthOffset", value.Tuning.VesselTargetNorthOffset);
            Append(json, "maxIgnitionKm", value.Tuning.MaxIgnitionAltitude / 1000);
            Append(json, "entryAltitudeKm", value.Tuning.EntryAltitude / 1000);
            Append(json, "entrySpeed", value.Tuning.EntrySpeed);
            Append(json, "optimizeFuel", value.Tuning.OptimizeFuel);
            Append(json, "optimizeRecoveryTrajectory", value.Tuning.OptimizeRecoveryTrajectory);
            json.Append('}');
            if (value.Flight is RetroLandingFlight flight)
            {
                json.Append(",\"flight\":{\"height\":").Append(Number(flight.Height));
                Append(json, "verticalSpeed", flight.VerticalSpeed);
                Append(json, "horizontalSpeed", flight.HorizontalSpeed);
                Append(json, "ignitionWait", flight.IgnitionWait);
                Append(json, "pitchAoa", flight.PitchAoa);
                Append(json, "targetAoa", flight.TargetAoa);
                Append(json, "surfaceSpeed", flight.SurfaceSpeed);
                Append(json, "entrySpeed", flight.EntrySpeed);
                if (flight.Touchdown is RetroTouchdown touchdown)
                {
                    json.Append(",\"touchdown\":{\"status\":");
                    json.Append(Quote(touchdown.Status));
                    AppendNullable(json, "waypoint", touchdown.Waypoint);
                    Append(json, "far", touchdown.UsesFar);
                    Append(json, "atmospheric", touchdown.Atmospheric);
                    if (touchdown.HasPoint)
                    {
                        Append(json, "latitude", touchdown.Latitude);
                        Append(json, "longitude", touchdown.Longitude);
                        Append(json, "missDistance", touchdown.MissDistance);
                        Append(json, "missBearing", touchdown.MissBearing);
                        Append(json, "missEast", touchdown.MissEast);
                        Append(json, "missNorth", touchdown.MissNorth);
                        Append(json, "eta", touchdown.Eta);
                        Append(json, "entrySeconds", touchdown.EntrySeconds);
                        Append(json, "landingSeconds", touchdown.LandingSeconds);
                    }
                    json.Append('}');
                }
                json.Append('}');
            }
            if (value.EngineBay != null) AppendRetroEngineBay(json, value.EngineBay);
            json.Append('}');
        }

        private static void AppendRetroEngineBay(StringBuilder json, RetroEngineBaySnapshot bay)
        {
            json.Append(",\"engineBay\":{\"radius\":").Append(Number(bay.Radius));
            Append(json, "thrust", bay.Thrust);
            Append(json, "producing", bay.Producing);
            Append(json, "commanded", bay.Commanded);
            AppendPoint(json, "center", bay.Center);
            if (bay.Producing > 0) AppendPoint(json, "thrustCenter", bay.ThrustCenter);
            json.Append(",\"engines\":[");
            for (int i = 0; i < bay.Engines.Length; i++)
            {
                if (i > 0) json.Append(',');
                var engine = bay.Engines[i];
                json.Append("{\"partId\":").Append(engine.PartId.ToString(Invariant));
                Append(json, "moduleIndex", engine.ModuleIndex);
                Append(json, "name", engine.Name);
                Append(json, "label", engine.Label);
                Append(json, "producing", engine.Producing);
                Append(json, "commanded", engine.Commanded);
                Append(json, "thrust", engine.Thrust);
                json.Append(",\"nozzles\":[");
                for (int j = 0; j < engine.Nozzles.Length; j++)
                {
                    if (j > 0) json.Append(',');
                    AppendPoint(json, engine.Nozzles[j]);
                }
                json.Append("]}");
            }
            json.Append("]}");
        }

        private static void AppendPoint(StringBuilder json, string name, Vector2 point)
        {
            json.Append(",\"").Append(name).Append("\":");
            AppendPoint(json, point);
        }

        private static void AppendPoint(StringBuilder json, Vector2 point)
        {
            json.Append('[').Append(Number(point.x)).Append(',').Append(Number(point.y)).Append(']');
        }
    }
}
