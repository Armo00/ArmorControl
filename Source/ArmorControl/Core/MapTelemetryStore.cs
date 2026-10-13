using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Diagnostics;

namespace ArmorOverhaul.ArmorControl.Core
{
    // Network/main-thread boundary: only immutable requests, JSON and encoded images.
    internal sealed class MapWindowRequest
    {
        internal readonly bool Automatic;
        internal readonly double StartOffset;
        internal readonly int Hours;
        internal readonly string Key;

        internal MapWindowRequest(bool automatic, double startOffset, int hours)
        {
            if (double.IsNaN(startOffset) || double.IsInfinity(startOffset) || Math.Abs(startOffset) > 366 * 86400d)
                throw new ArgumentOutOfRangeException("startOffset");
            if (hours != 1 && hours != 3 && hours != 5 && hours != 10) throw new ArgumentOutOfRangeException("hours");
            Automatic = automatic;
            StartOffset = automatic ? 0 : Math.Round(startOffset);
            Hours = automatic ? 5 : hours;
            Key = automatic ? "auto" : StartOffset.ToString("R", CultureInfo.InvariantCulture) + ":" + hours;
        }
    }

    internal sealed class PlanetMapAsset
    {
        internal string Key;
        internal string Source;
        internal string Status;
        internal byte[] Png;
        internal double ReadbackMs, EncodeMs;
        internal bool Ready { get { return Png != null && Png.Length > 0; } }
    }

    internal sealed class MapTelemetryStore
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, MapWindowRequest> subscribers = new Dictionary<string, MapWindowRequest>();
        private readonly Dictionary<string, string> packets = new Dictionary<string, string>();
        private readonly Dictionary<string, PlanetMapAsset> assets = new Dictionary<string, PlanetMapAsset>();
        private long captureCount, captureTicks, maximumCaptureTicks;

        internal bool Subscribe(string clientId, MapWindowRequest request)
        {
            lock (sync)
            {
                if (request == null) { subscribers.Remove(clientId); Prune(); return true; }
                if (!subscribers.Values.Any(value => value.Key == request.Key)
                    && subscribers.Where(pair => pair.Key != clientId).Select(pair => pair.Value.Key).Distinct().Count() >= 16) return false;
                subscribers[clientId] = request;
                Prune();
                return true;
            }
        }

        private void Prune()
        {
            var active = new HashSet<string>(subscribers.Values.Select(value => value.Key));
            foreach (string key in packets.Keys.Where(key => !active.Contains(key)).ToArray()) packets.Remove(key);
        }

        internal MapWindowRequest[] Requests()
        {
            lock (sync) return subscribers.Values.GroupBy(value => value.Key).Select(group => group.First()).ToArray();
        }

        internal void Publish(string key, string json)
        {
            lock (sync) if (subscribers.Values.Any(value => value.Key == key)) packets[key] = json;
        }

        internal string LatestFor(string clientId)
        {
            lock (sync)
            {
                MapWindowRequest request;
                string packet;
                return subscribers.TryGetValue(clientId, out request) && packets.TryGetValue(request.Key, out packet) ? packet : null;
            }
        }

        internal void PutAsset(PlanetMapAsset asset)
        {
            lock (sync)
            {
                assets[asset.Key] = asset;
                while (assets.Count > 8) assets.Remove(assets.Keys.First(key => key != asset.Key));
            }
        }

        internal PlanetMapAsset GetAsset(string key)
        {
            lock (sync) { PlanetMapAsset asset; return assets.TryGetValue(key, out asset) ? asset : null; }
        }

        internal void RecordCapture(long ticks)
        {
            lock (sync) { captureCount++; captureTicks += ticks; maximumCaptureTicks = Math.Max(maximumCaptureTicks, ticks); }
        }

        internal string MetricsJson()
        {
            lock (sync)
            {
                double readback = assets.Count == 0 ? 0 : assets.Values.Max(asset => asset.ReadbackMs);
                double encoding = assets.Count == 0 ? 0 : assets.Values.Max(asset => asset.EncodeMs);
                Func<double, string> number = value => value.ToString("R", CultureInfo.InvariantCulture);
                return "{\"captureSamples\":" + captureCount.ToString(CultureInfo.InvariantCulture)
                    + ",\"captureAverageMs\":" + number(captureCount == 0 ? 0 : captureTicks * 1000d / Stopwatch.Frequency / captureCount)
                    + ",\"captureMaximumMs\":" + number(maximumCaptureTicks * 1000d / Stopwatch.Frequency)
                    + ",\"cachedReadbackMaximumMs\":" + number(readback) + ",\"cachedEncodeMaximumMs\":" + number(encoding)
                    + ",\"activeWindows\":" + subscribers.Values.Select(value => value.Key).Distinct().Count().ToString(CultureInfo.InvariantCulture) + "}";
            }
        }
    }
}
