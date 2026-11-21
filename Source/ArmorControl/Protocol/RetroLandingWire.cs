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
            AppendNullable(json, "status", value.Status);
            AppendNullable(json, "reason", value.Reason);
            json.Append(",\"tuning\":{\"maxG\":").Append(Number(value.Tuning.MaxG));
            Append(json, "finalSeconds", value.Tuning.FinalSeconds);
            Append(json, "roll", value.Tuning.Roll);
            Append(json, "coastPitch", value.Tuning.CoastPitch);
            Append(json, "maxIgnitionKm", value.Tuning.MaxIgnitionAltitude / 1000);
            Append(json, "entryAltitudeKm", value.Tuning.EntryAltitude / 1000);
            Append(json, "entrySpeed", value.Tuning.EntrySpeed);
            Append(json, "optimizeFuel", value.Tuning.OptimizeFuel);
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
