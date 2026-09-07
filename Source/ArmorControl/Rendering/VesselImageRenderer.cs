using System;
using System.Collections.Generic;
using System.Reflection;
using ArmorOverhaul.ArmorControl.Core;
using ArmorOverhaul.ArmorControl.Protocol;
using UnityEngine;

namespace ArmorOverhaul.ArmorControl.Rendering
{
    internal sealed class VesselImageRenderer : IDisposable
    {
        private const int Width = 768;
        private const int Height = 768;
        private const float FallbackRedrawSeconds = 5f;
        private const float ActiveVisualRedrawSeconds = 1f;

        private readonly MainThreadBridge bridge;
        private VesselView.VesselViewer viewer;
        private RenderTexture renderTexture;
        private Texture2D readbackTexture;
        private string vesselId;
        private int partCount = -1;
        private long revision;
        private float nextFallbackRedraw;
        private float nextActiveVisualRedraw;
        private bool redrawRequested = true;
        private bool needsCenteringWarmup = true;
        private int colorMode = 3;
        private int drawPlane = 3;
        private uint? selectedPartId;
        private MethodInfo vesselViewPartColorMethod;
        private FieldInfo vesselViewWorldToScreenField;
        private Matrix4x4 vesselLocalToView;
        private bool hasProjection;

        internal VesselImageRenderer(MainThreadBridge bridge)
        {
            this.bridge = bridge;
        }

        internal void Tick(bool hasViewer)
        {
            if (!hasViewer || !HighLogic.LoadedSceneIsFlight || FlightGlobals.ActiveVessel == null)
            {
                return;
            }

            Vessel vessel = FlightGlobals.ActiveVessel;
            string currentVesselId = vessel.id.ToString("N");
            int currentPartCount = vessel.parts == null ? 0 : vessel.parts.Count;
            if (!string.Equals(vesselId, currentVesselId, StringComparison.Ordinal) || partCount != currentPartCount)
            {
                vesselId = currentVesselId;
                partCount = currentPartCount;
                redrawRequested = true;
                needsCenteringWarmup = true;
            }

            if (colorMode == 3) RequestActiveVisualRedraw();
            if (!redrawRequested && Time.realtimeSinceStartup < nextFallbackRedraw)
            {
                return;
            }

            Render(vesselId);
        }

        internal string SetMode(string mode)
        {
            switch ((mode ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "state": colorMode = 1; break;
                case "stage": colorMode = 2; break;
                case "heat": colorMode = 3; break;
                case "resource": colorMode = 4; break;
                default: return null;
            }
            redrawRequested = true;
            return mode.ToLowerInvariant();
        }

        internal string CycleView()
        {
            switch (drawPlane)
            {
                case 3: drawPlane = 0; return "XY";
                case 0: drawPlane = 1; return "XZ";
                case 1: drawPlane = 2; return "YZ";
                default: drawPlane = 3; return "ISOMETRIC";
            }
        }

        internal void Fit()
        {
            if (viewer != null)
            {
                viewer.basicSettings.autoCenter = true;
                viewer.basicSettings.centerRescale = 3;
            }
            needsCenteringWarmup = true;
            redrawRequested = true;
        }

        internal void RequestRedraw()
        {
            redrawRequested = true;
        }

        internal void RequestActiveVisualRedraw()
        {
            if (Time.realtimeSinceStartup < nextActiveVisualRedraw) return;
            nextActiveVisualRedraw = Time.realtimeSinceStartup + ActiveVisualRedrawSeconds;
            redrawRequested = true;
        }

        internal bool SelectPart(uint? partId)
        {
            if (selectedPartId == partId) return false;
            selectedPartId = partId;
            redrawRequested = true;
            return true;
        }

        internal bool TryProjectWorldPoint(Vessel vessel, Vector3 worldPoint, out Vector2 normalized)
        {
            normalized = Vector2.zero;
            if (!hasProjection || vessel == null || viewer == null) return false;
            Vector3 localPoint = vessel.transform.InverseTransformPoint(worldPoint);
            Vector3 viewPoint = vesselLocalToView.MultiplyPoint3x4(localPoint);
            float pixelX = viewer.basicSettings.scrOffX + viewPoint.x * viewer.basicSettings.scaleFact;
            float pixelY = viewer.basicSettings.scrOffY + viewPoint.y * viewer.basicSettings.scaleFact;
            float x = pixelX / Width;
            float y = 1f - pixelY / Height;
            if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y)
                || x < 0 || x > 1 || y < 0 || y > 1) return false;
            normalized = new Vector2(x, y);
            return true;
        }

        private void EnsureResources()
        {
            if (viewer == null)
            {
                viewer = new VesselView.VesselViewer();
                viewer.basicSettings.screenVisible = true;
                viewer.basicSettings.latency = 0;
                viewer.basicSettings.colorModeWire = 0;
                viewer.basicSettings.colorModeWireDull = true;
                viewer.basicSettings.colorModeBox = 8;
                viewer.basicSettings.displayCOM = true;
                viewer.basicSettings.displayEngines = true;
                viewer.basicSettings.displayAxes = false;
                viewer.basicSettings.displayGround = 0;
                viewer.basicSettings.autoCenter = true;
                viewer.basicSettings.centerOnRootH = false;
                viewer.basicSettings.centerOnRootV = false;
                viewer.basicSettings.centerRescale = 3;
                viewer.basicSettings.margin = 2;
                viewer.nilOffset(Width, Height);
                ConfigurePartHighlighting();
            }

            if (renderTexture == null)
            {
                renderTexture = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
                renderTexture.name = "ArmorControl Vessel View";
                renderTexture.Create();
            }
            if (readbackTexture == null)
            {
                readbackTexture = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                readbackTexture.name = "ArmorControl Vessel View Readback";
            }
        }

        private void ConfigurePartHighlighting()
        {
            vesselViewPartColorMethod = typeof(VesselView.VesselViewer).GetMethod("getPartColor", BindingFlags.Instance | BindingFlags.NonPublic);
            vesselViewWorldToScreenField = typeof(VesselView.VesselViewer).GetField("worldToScreen", BindingFlags.Instance | BindingFlags.NonPublic);
            var custom = new VesselView.CustomModeSettings
            {
                name = "ArmorControl live selection",
                ColorModeOverride = 2,
                OrientationOverride = 0,
                CenteringOverride = 0,
                MinimodesOverride = 0,
                focusSubset = new List<Part>()
            };
            custom.fillColorDelegate = (settings, part) => colorMode == 3 ? HeatColor(part) : IsSelected(part)
                ? new Color(1f, .63f, .12f, .96f) : VesselViewColor(part, colorMode);
            custom.wireColorDelegate = (settings, part) => IsSelected(part)
                ? (colorMode == 3 ? Color.white : new Color(1f, .86f, .42f, 1f)) : VesselViewColor(part, 0);
            custom.boxColorDelegate = (settings, part) => IsSelected(part)
                ? (colorMode == 3 ? Color.white : new Color(1f, .72f, .2f, .95f)) : VesselViewColor(part, 8);
            custom.fillColorDullDelegate = settings => false;
            custom.wireColorDullDelegate = settings => true;
            custom.boxColorDullDelegate = settings => false;
            viewer.setCustomMode(custom);
        }

        private bool IsSelected(Part part)
        {
            return selectedPartId.HasValue && part != null && part.flightID == selectedPartId.Value;
        }

        private Color VesselViewColor(Part part, int mode)
        {
            if (vesselViewPartColorMethod == null) return Color.clear;
            try { return (Color)vesselViewPartColorMethod.Invoke(viewer, new object[] { part, mode }); }
            catch { return Color.clear; }
        }

        private static Color HeatColor(Part part)
        {
            if (part == null) return Color.clear;
            double ratio = Math.Max(part.maxTemp > 0 ? part.temperature / part.maxTemp : 0,
                part.skinMaxTemp > 0 ? part.skinTemperature / part.skinMaxTemp : 0);
            var cool = new Color(.08f, .18f, .21f, 1f);
            if (ratio < .8) return cool;
            float heat = Mathf.Clamp01((float)((ratio - .8) / .2));
            // Bring out incipient overheating early; reach saturated red at the limit.
            var warning = new Color(1f, .85f, .02f, 1f);
            return heat < .5f ? Color.Lerp(cool, warning, Mathf.Sqrt(heat * 2f))
                : Color.Lerp(warning, new Color(1f, .02f, .08f, 1f), (heat - .5f) * 2f);
        }

        private void Render(string currentVesselId)
        {
            EnsureResources();
            Vessel vessel = FlightGlobals.ActiveVessel;
            viewer.basicSettings.colorModeFill = colorMode;
            viewer.basicSettings.colorModeFillDull = false;
            viewer.basicSettings.drawPlane = drawPlane;
            viewer.forceRedraw();
            viewer.drawCall(renderTexture);
            if (needsCenteringWarmup)
            {
                viewer.forceRedraw();
                viewer.drawCall(renderTexture);
                needsCenteringWarmup = false;
            }
            UpdateProjection(vessel);

            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = renderTexture;
                readbackTexture.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                readbackTexture.Apply(false, false);
                byte[] jpeg = readbackTexture.EncodeToJPG(82);
                bridge.PublishVesselImage(new VesselImageSnapshot(++revision, currentVesselId, jpeg));
                redrawRequested = false;
                nextFallbackRedraw = Time.realtimeSinceStartup + FallbackRedrawSeconds;
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        private void UpdateProjection(Vessel vessel)
        {
            hasProjection = false;
            if (vessel == null || viewer == null || vesselViewWorldToScreenField == null) return;
            try
            {
                Matrix4x4 worldToView = (Matrix4x4)vesselViewWorldToScreenField.GetValue(viewer);
                vesselLocalToView = worldToView * vessel.transform.localToWorldMatrix;
                hasProjection = true;
            }
            catch
            {
                hasProjection = false;
            }
        }

        public void Dispose()
        {
            if (renderTexture != null)
            {
                renderTexture.Release();
                UnityEngine.Object.Destroy(renderTexture);
                renderTexture = null;
            }
            if (readbackTexture != null)
            {
                UnityEngine.Object.Destroy(readbackTexture);
                readbackTexture = null;
            }
            hasProjection = false;
        }
    }
}
