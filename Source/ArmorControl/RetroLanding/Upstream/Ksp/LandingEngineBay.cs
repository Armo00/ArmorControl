using System;
using System.Collections.Generic;
using System.Globalization;
using LandingPoc;
using UnityEngine;

namespace KspRetroLanding
{
    internal sealed partial class LandingEngineBay : IDisposable
    {
        private static readonly Color Background = new Color(0.07f, 0.11f, 0.13f);
        private static readonly Color Structure = new Color(0.41f, 0.55f, 0.57f);
        private static readonly Color On = new Color(0.53f, 0.87f, 0.73f);
        private static readonly Color OnFill = new Color(0.14f, 0.30f, 0.25f);
        private static readonly Color ThrustCenter = new Color(0.97f, 0.74f, 0.45f);

        private struct EngineReading
        {
            internal ModuleEngines Engine;
            internal Vector2 Mount, Installation;
            internal float Thrust;
            internal bool On, Commanded, HasMount;
            internal string Label;
        }

        private struct NozzleReading
        {
            internal int Slot;
            internal Vector2 Position;
        }

        private readonly List<EngineBinding> idleEngines = new List<EngineBinding>();
        private readonly List<EngineReading> readings = new List<EngineReading>();
        private readonly List<NozzleReading> nozzles = new List<NozzleReading>();
        private Vessel vessel;
        private int parts;
        private Transform reference;
        private IList<EngineBinding> source;
        private double nextIdleMeasure;
        private ModuleEngines selected;
        private Vector2 center, forceCenter;
        private float radius, totalThrust;
        private int producing, commanded;
        private Texture2D ring, disk, diamond, rim;
        private GUIStyle nozzleText;

        internal void Draw(Vessel active, IList<EngineBinding> controlled, long command)
        {
            if (Event.current.type == EventType.Layout)
                Capture(active, controlled, command);

            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            GUILayout.Label("Engine bay  |  Aft view");
            float size = Mathf.Clamp(Screen.height - 440, 168, 276);
            Rect plot = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
            DrawPlot(plot);
            GUILayout.Label("● On    ○ Off    ◇ Thrust center");
            GUILayout.EndVertical();

            GUILayout.BeginVertical(GUILayout.MinWidth(138));
            GUILayout.Label("Producing  " + producing + " / " + readings.Count);
            GUILayout.Label("Thrust  " + totalThrust.ToString("N1", CultureInfo.InvariantCulture) + " kN");
            GUILayout.Label("Commanded  " + commanded);
            GUILayout.Space(8);
            int inspected = readings.FindIndex(e => e.Engine == selected);
            EngineReading engine = inspected >= 0 ? readings[inspected] : default;
            GUILayout.Label(inspected >= 0 ? "Engine " + engine.Label + (engine.On ? "  |  On" : "  |  Off") : "Engine  —");
            GUILayout.Label(inspected >= 0 ? engine.Thrust.ToString("N1", CultureInfo.InvariantCulture) + " kN" : "—");
            GUILayout.Label(inspected >= 0 && engine.Commanded ? "Selected for firing" : " ");
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        private void Capture(Vessel active, IList<EngineBinding> controlled, long command)
        {
            Transform frame = active.ReferenceTransform;
            IList<EngineBinding> desired = controlled ?? idleEngines;
            bool changed = vessel != active || reference != frame || parts != active.parts.Count || source != desired;
            if (changed)
            {
                vessel = active;
                reference = frame;
                parts = active.parts.Count;
                source = desired;
                selected = null;
                if (controlled == null)
                {
                    idleEngines.Clear();
                    foreach (Part part in active.parts)
                        foreach (ModuleEngines engine in part.FindModulesImplementing<ModuleEngines>())
                            if (engine.isEnabled && engine.moduleIsEnabled && !EngineBinding.UsesSolidPropellant(engine))
                                idleEngines.Add(new EngineBinding(engine));
                    MeasureIdleEngines(active);
                    EngineModuleCatalog.KeepStrongest(idleEngines, e => e.Max, e => e.Engine.part.flightID,
                        e => e.Engine.part.Modules.IndexOf(e.Engine));
                }
            }
            double now = Planetarium.GetUniversalTime();
            if (controlled == null && now >= nextIdleMeasure) MeasureIdleEngines(active);

            readings.Clear();
            nozzles.Clear();
            center = forceCenter = Vector2.zero;
            radius = totalThrust = 0;
            producing = commanded = 0;
            int mounted = 0;
            float thrustWithPosition = 0;
            for (int i = 0; i < source.Count; i++)
            {
                EngineBinding binding = source[i];
                ModuleEngines engine = binding.Engine;
                if (engine == null) continue;
                var reading = new EngineReading { Engine = engine, Thrust = Mathf.Max(0, engine.finalThrust),
                    On = binding.ThrustEstablished, Commanded = (command & (1L << i)) != 0,
                    Label = (i + 1).ToString(CultureInfo.InvariantCulture) };
                float weights = 0;
                for (int j = 0; j < engine.thrustTransforms.Count; j++)
                {
                    Transform nozzle = engine.thrustTransforms[j];
                    if (nozzle == null) continue;
                    Vector3 local = frame.InverseTransformDirection(nozzle.position - frame.position);
                    var point = new Vector2(local.x, local.z);
                    nozzles.Add(new NozzleReading { Slot = readings.Count, Position = point });
                    float weight = j < engine.thrustTransformMultipliers.Count ? engine.thrustTransformMultipliers[j] : 1;
                    reading.Mount += point * weight;
                    weights += weight;
                }
                if (weights > 0)
                {
                    reading.HasMount = true;
                    reading.Mount /= weights;
                    reading.Installation = binding.InstallationPosition ?? reading.Mount;
                    center += reading.Installation;
                    forceCenter += reading.Mount * reading.Thrust;
                    thrustWithPosition += reading.Thrust;
                    mounted++;
                }
                totalThrust += reading.Thrust;
                if (reading.On) producing++;
                if (reading.Commanded) commanded++;
                readings.Add(reading);
            }
            if (mounted > 0) center /= mounted;
            if (thrustWithPosition > 0) forceCenter /= thrustWithPosition;
            foreach (NozzleReading nozzle in nozzles)
                radius = Mathf.Max(radius, (nozzle.Position - center).magnitude);
            int central = -1;
            for (int i = 0; i < readings.Count; i++)
            {
                if (!readings[i].HasMount ||
                    (readings[i].Installation - center).magnitude > Mathf.Max(0.04f, 0.015f * radius)) continue;
                if (central >= 0) { central = -1; break; }
                central = i;
            }
            if (central >= 0)
            {
                EngineReading engine = readings[central];
                engine.Label = "C";
                readings[central] = engine;
                if (selected == null) selected = engine.Engine;
            }
            if (selected == null && readings.Count > 0) selected = readings[0].Engine;
        }

        private void MeasureIdleEngines(Vessel active)
        {
            foreach (EngineBinding entry in idleEngines)
            {
                entry.Measure(active.staticPressurekPa / 101.325, TimeWarp.fixedDeltaTime);
                entry.Spec(active.ReferenceTransform, (Vector3)active.CoMD);
            }
            nextIdleMeasure = Planetarium.GetUniversalTime() + 0.5;
        }

        private void DrawPlot(Rect plot)
        {
            if (ring == null)
            {
                ring = ShapeTexture(true);
                disk = ShapeTexture(false);
                diamond = ShapeTexture(true, diamond: true);
                rim = ShapeTexture(true, stroke: 1.5f);
            }
            if (nozzleText == null) nozzleText = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
            bool repaint = Event.current.type == EventType.Repaint;
            float scale = plot.width * 0.32f / Mathf.Max(0.2f, radius);
            float diameter = Mathf.Clamp(32 - readings.Count * 0.55f, 15, 29);
            if (repaint)
            {
                DrawTexture(plot, Texture2D.whiteTexture, Background);
                DrawTexture(new Rect(plot.center.x - 0.5f, plot.y + 10, 1, plot.height - 20), Texture2D.whiteTexture, Structure * 0.55f);
                DrawTexture(new Rect(plot.x + 10, plot.center.y - 0.5f, plot.width - 20, 1), Texture2D.whiteTexture, Structure * 0.55f);
                DrawTexture(At(plot.center, plot.width * 0.86f), rim, Structure);
                GUI.Label(new Rect(plot.xMax - 24, plot.center.y - 12, 24, 21), "+X");
                GUI.Label(new Rect(plot.center.x + 4, plot.y + 3, 24, 21), "+Z");
            }
            foreach (NozzleReading nozzle in nozzles)
            {
                EngineReading engine = readings[nozzle.Slot];
                Vector2 point = Project(plot, nozzle.Position, scale);
                Rect hit = At(point, diameter);
                if (GUI.Button(hit, GUIContent.none, GUIStyle.none)) selected = engine.Engine;
                if (!repaint) continue;
                Color outline = engine.On ? On : Structure;
                DrawTexture(hit, disk, engine.On ? OnFill : Background);
                DrawTexture(hit, ring, outline);
                DrawTexture(At(point, diameter - 6), ring, outline);
                if (engine.Engine == selected) DrawTexture(At(point, diameter + 4), ring, Color.white);
                GUI.Label(hit, engine.Label, nozzleText);
            }
            if (repaint && producing > 0)
                DrawTexture(At(Project(plot, forceCenter, scale), 12), diamond, ThrustCenter);
        }

        private Vector2 Project(Rect area, Vector2 position, float scale)
        {
            Vector2 offset = (position - center) * scale;
            return area.center + new Vector2(offset.x, -offset.y);
        }

        private static Rect At(Vector2 position, float size) =>
            new Rect(position.x - size / 2, position.y - size / 2, size, size);

        private static void DrawTexture(Rect area, Texture texture, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(area, texture);
            GUI.color = previous;
        }

        private static Texture2D ShapeTexture(bool outline, bool diamond = false, float stroke = 3)
        {
            const int size = 96;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - size / 2f, dy = y + 0.5f - size / 2f;
                    float distance = diamond ? Mathf.Abs(dx) + Mathf.Abs(dy) : Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = Mathf.Clamp01(size / 2f - distance);
                    if (outline) alpha *= Mathf.Clamp01(distance - size / 2f + stroke);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(255 * alpha));
                }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        public void Dispose()
        {
            if (ring != null) UnityEngine.Object.Destroy(ring);
            if (disk != null) UnityEngine.Object.Destroy(disk);
            if (diamond != null) UnityEngine.Object.Destroy(diamond);
            if (rim != null) UnityEngine.Object.Destroy(rim);
        }
    }
}
