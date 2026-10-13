using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArmorOverhaul.ArmorControl.Core;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace ArmorOverhaul.ArmorControl.Rendering
{
    // All Unity, GameDatabase and texture operations are confined to the KSP main thread.
    internal sealed class PlanetMapProvider
    {
        private readonly MapTelemetryStore store;
        private readonly Dictionary<int, string> keys = new Dictionary<int, string>();
        private readonly Dictionary<int, float> retries = new Dictionary<int, float>();
        private readonly Dictionary<int, Task> jobs = new Dictionary<int, Task>();
        internal PlanetMapProvider(MapTelemetryStore store) { this.store = store; }

        internal PlanetMapAsset Get(CelestialBody body)
        {
            string key;
            if (!keys.TryGetValue(body.flightGlobalsIndex, out key))
                keys[body.flightGlobalsIndex] = key = "planet-" + body.flightGlobalsIndex + "-" + Guid.NewGuid().ToString("N");
            PlanetMapAsset previous = store.GetAsset(key);
            Task job;
            if (jobs.TryGetValue(body.flightGlobalsIndex, out job))
            {
                if (!job.IsCompleted) return previous;
                jobs.Remove(body.flightGlobalsIndex);
                previous = store.GetAsset(key);
            }
            float retry;
            if (previous != null && (previous.Ready || retries.TryGetValue(body.flightGlobalsIndex, out retry) && Time.realtimeSinceStartup < retry)) return previous;
            retries[body.flightGlobalsIndex] = Time.realtimeSinceStartup + 10;
            var asset = new PlanetMapAsset { Key = key, Status = "Planet color map is not available." };
            Texture texture = null;
            bool owned = false;
            long started = Stopwatch.GetTimestamp();
            try
            {
                if (body.scaledBody == null) throw new InvalidOperationException("Scaled body is not ready.");
                MeshFilter filter = body.scaledBody.GetComponent<MeshFilter>() ?? body.scaledBody.GetComponentInChildren<MeshFilter>();
                Renderer renderer = filter == null ? null : filter.GetComponent<Renderer>();
                Material material = renderer == null ? null : renderer.sharedMaterial;
                string property = null;
                if (material != null && material.HasProperty("_ColorMap"))
                { texture = material.GetTexture("_ColorMap"); property = "_ColorMap"; asset.Source = "Parallax color map"; }
                if (!Usable(texture))
                {
                    texture = LoadConfigured(body.bodyName, true); owned = texture != null; property = null;
                    if (texture != null) asset.Source = "Parallax color map";
                }
                if (!Usable(texture) && material != null)
                { texture = material.mainTexture; property = "_MainTex"; owned = false; asset.Source = "ScaledSpace color map"; }
                if (!Usable(texture))
                {
                    texture = LoadConfigured(body.bodyName, false); owned = texture != null; property = null;
                    if (texture != null) asset.Source = "PQS color map";
                }
                if (!Usable(texture)) throw new InvalidOperationException("No usable global color texture.");
                Vector2 scale, offset;
                Calibrate(body, filter, out scale, out offset);
                if (property != null && material.HasProperty(property))
                {
                    Vector2 materialScale = material.GetTextureScale(property), materialOffset = material.GetTextureOffset(property);
                    offset = Vector2.Scale(offset, materialScale) + materialOffset;
                    scale = Vector2.Scale(scale, materialScale);
                }
                byte[] pixels = ReadPixels(texture, scale, offset);
                double readbackMs = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
                asset.ReadbackMs = readbackMs;
                string source = asset.Source;
                asset.Status = "Preparing planet color map.";
                store.PutAsset(asset);
                jobs[body.flightGlobalsIndex] = Task.Run(() =>
                {
                    var completed = new PlanetMapAsset { Key = key, Source = source, ReadbackMs = readbackMs };
                    long encodeStarted = Stopwatch.GetTimestamp();
                    try { completed.Png = MapPngEncoder.Encode(pixels, 2048, 1024); completed.Status = "Planet color map ready."; }
                    catch { completed.Status = "Planet color map is not available."; }
                    completed.EncodeMs = (Stopwatch.GetTimestamp() - encodeStarted) * 1000d / Stopwatch.Frequency;
                    store.PutAsset(completed);
                });
                return asset;
            }
            catch (Exception exception)
            {
                asset.Status = "Unable to extract planet color map: " + exception.GetType().Name;
            }
            finally { if (owned && texture != null) UnityEngine.Object.Destroy(texture); }
            store.PutAsset(asset);
            return asset;
        }

        private static bool Usable(Texture texture)
        {
            return texture != null && texture.width >= 128 && texture.height >= 64
                && Math.Abs(texture.width / (double)texture.height - 2) < .1
                && (texture.name ?? "").IndexOf("dummy", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static Texture2D LoadConfigured(string name, bool parallax)
        {
            if (GameDatabase.Instance == null) return null;
            foreach (ConfigNode config in GameDatabase.Instance.GetConfigNodes(parallax ? "ParallaxTerrain" : "Kopernicus"))
            foreach (ConfigNode body in config.GetNodes("Body"))
            {
                if (body.GetValue("name") != name) continue;
                ConfigNode node = parallax ? body.GetNode("ParallaxScaledProperties") : body.GetNode("PQS");
                if (node == null) continue;
                node = node.GetNode(parallax ? "Material" : "Mods");
                if (node == null) continue;
                string path = parallax ? node.GetValue("_ColorMap") : node.GetNode("VertexColorMap")?.GetValue("map");
                if (string.IsNullOrWhiteSpace(path) || path.IndexOf("dummy", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                Texture2D loaded = ReadDdsMip(path);
                if (loaded != null) return loaded;
            }
            return null;
        }

        private static Texture2D ReadDdsMip(string relative)
        {
            string root = Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath, "GameData")) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
            Texture2D texture = null;
            try
            {
                using (var reader = new BinaryReader(File.OpenRead(path)))
                {
                    byte[] header = reader.ReadBytes(128);
                    if (header.Length != 128 || BitConverter.ToUInt32(header, 0) != 0x20534444 || BitConverter.ToUInt32(header, 4) != 124) return null;
                    int width = BitConverter.ToInt32(header, 16), height = BitConverter.ToInt32(header, 12), mips = Math.Max(1, BitConverter.ToInt32(header, 28));
                    if (width < 128 || height < 64 || width > 65536 || height > 32768 || mips > 24 || width != height * 2) return null;
                    uint fourcc = BitConverter.ToUInt32(header, 84);
                    TextureFormat format;
                    int block;
                    if (fourcc == 0x31545844) { format = TextureFormat.DXT1; block = 8; }
                    else if (fourcc == 0x35545844) { format = TextureFormat.DXT5; block = 16; }
                    else if (fourcc == 0x30315844)
                    {
                        byte[] dx10 = reader.ReadBytes(20);
                        if (dx10.Length != 20 || BitConverter.ToInt32(dx10, 4) != 3 || BitConverter.ToInt32(dx10, 12) != 1) return null;
                        int dxgi = BitConverter.ToInt32(dx10, 0);
                        if (dxgi != 98 && dxgi != 99) return null;
                        format = TextureFormat.BC7; block = 16;
                    }
                    else return null;
                    if (!SystemInfo.SupportsTextureFormat(format)) return null;
                    while (width > 2048 && mips > 1)
                    {
                        reader.BaseStream.Seek((long)Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * block, SeekOrigin.Current);
                        width = Math.Max(1, width / 2); height = Math.Max(1, height / 2); mips--;
                    }
                    // Reject enormous un-mipped textures instead of allocating them on a flight frame.
                    if (width > 4096) return null;
                    int bytes = checked(Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * block);
                    byte[] pixels = reader.ReadBytes(bytes);
                    if (pixels.Length != bytes) return null;
                    texture = new Texture2D(width, height, format, false);
                    texture.name = Path.GetFileName(path);
                    texture.LoadRawTextureData(pixels); texture.Apply(false, true);
                    return texture;
                }
            }
            catch { if (texture != null) UnityEngine.Object.Destroy(texture); return null; }
        }

        private static void Calibrate(CelestialBody body, MeshFilter filter, out Vector2 scale, out Vector2 offset)
        {
            if (filter == null || filter.sharedMesh == null) throw new InvalidOperationException("Planet UV mesh is unavailable.");
            Mesh mesh = filter.sharedMesh;
            Vector3[] vertices = mesh.vertices; Vector2[] uv = mesh.uv; int[] triangles = mesh.triangles;
            if (vertices.Length != uv.Length || triangles.Length == 0) throw new InvalidOperationException("Planet UVs are unavailable.");
            Vector2 zero = SurfaceUv(body, filter.transform, vertices, uv, triangles, 0, 0);
            Vector2 east = SurfaceUv(body, filter.transform, vertices, uv, triangles, 0, 90);
            Vector2 north = SurfaceUv(body, filter.transform, vertices, uv, triangles, 60, 0);
            float du = east.x - zero.x; du -= Mathf.Round(du);
            float horizontal = du * 4, vertical = (north.y - zero.y) * 3;
            if (Math.Abs(Math.Abs(horizontal) - 1) > .1 || Math.Abs(Math.Abs(vertical) - 1) > .1 || Math.Abs(zero.y - .5) > .03)
                throw new InvalidOperationException("Unsupported planet texture projection.");
            scale = new Vector2(Math.Sign(horizontal), Math.Sign(vertical));
            offset = new Vector2(zero.x - scale.x * .5f, zero.y - scale.y * .5f);
        }

        private static Vector2 SurfaceUv(CelestialBody body, Transform transform, Vector3[] vertices, Vector2[] uv, int[] triangles, double latitude, double longitude)
        {
            // UVs belong to the body-fixed mesh, not the current inertial world frame.
            // ScaledSpace may not yet have synchronized its world rotation on the first
            // flight frame. Mixing GetWorldSurfacePosition with that transform bakes
            // an arbitrary longitude shift into a texture that is cached for the session.
            // Cancel the scaled root's rotation and retain only a mesh child's local rotation.
            Vector3 bodyDirection = (Vector3)body.GetRelSurfaceNVector(latitude, longitude);
            Vector3 direction = transform.InverseTransformDirection(body.scaledBody.transform.TransformDirection(bodyDirection)).normalized;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int ia = triangles[i], ib = triangles[i + 1], ic = triangles[i + 2];
                Vector3 a = vertices[ia], ab = vertices[ib] - a, ac = vertices[ic] - a;
                Vector3 p = Vector3.Cross(direction, ac); float determinant = Vector3.Dot(ab, p);
                if (Math.Abs(determinant) < 1e-9f) continue;
                Vector3 t = -a; float b = Vector3.Dot(t, p) / determinant;
                Vector3 q = Vector3.Cross(t, ab); float c = Vector3.Dot(direction, q) / determinant;
                if (b < -.00001 || c < -.00001 || b + c > 1.00001 || Vector3.Dot(ac, q) / determinant <= 0) continue;
                Vector2 ua = uv[ia], ub = uv[ib], uc = uv[ic];
                if (Math.Max(ua.x, Math.Max(ub.x, uc.x)) - Math.Min(ua.x, Math.Min(ub.x, uc.x)) > .5f)
                { if (ua.x < .5f) ua.x++; if (ub.x < .5f) ub.x++; if (uc.x < .5f) uc.x++; }
                Vector2 result = ua * (1 - b - c) + ub * b + uc * c; result.x -= Mathf.Floor(result.x); return result;
            }
            throw new InvalidOperationException("Unable to locate surface UV.");
        }

        private static byte[] ReadPixels(Texture texture, Vector2 scale, Vector2 offset)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture raw = null, normalized = null;
            Texture2D readable = null;
            try
            {
                raw = RenderTexture.GetTemporary(2048, 1024, 0, RenderTextureFormat.ARGB32);
                normalized = RenderTexture.GetTemporary(2048, 1024, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(texture, raw); raw.wrapMode = TextureWrapMode.Repeat;
                Graphics.Blit(raw, normalized, scale, offset);
                RenderTexture.active = normalized;
                readable = new Texture2D(2048, 1024, TextureFormat.RGB24, false);
                readable.ReadPixels(new Rect(0, 0, 2048, 1024), 0, 0, false); readable.Apply(false, false);
                return readable.GetRawTextureData();
            }
            finally
            {
                RenderTexture.active = previous;
                if (readable != null) UnityEngine.Object.Destroy(readable);
                if (raw != null) RenderTexture.ReleaseTemporary(raw);
                if (normalized != null) RenderTexture.ReleaseTemporary(normalized);
            }
        }
    }
}
