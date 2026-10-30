using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Backend;
using UnityEngine;

namespace Formula9.World
{
    // Fetches and caches the raster basemap tiles under the 2D fleet map. Port of the Qt MapTiles.
    //
    // The source is OpenStreetMap's standard tile server unless FORMULA9_TILE_URL names another
    // ({z}/{x}/{y} placeholders), or is "none" to turn the basemap off (tests, offline demos). Tiles
    // stay in memory while recently drawn and on disk for a week, so panning back is free and the
    // OSM tile usage policy is respected.
    public sealed class MapTiles : IDisposable
    {
        private const string DefaultUrl = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
        // The OSM tile policy asks apps to identify themselves and not to open many connections
        private const string UserAgent = "Formula9-FleetMap/1.0 (student fleet training project)";
        private const int MaxInFlight = 6, MemoryTiles = 384, TimeoutSeconds = 15;
        private const float RetryAfterFailureSeconds = 30;
        private const long DiskCacheBytes = 300L * 1024 * 1024;
        private static readonly TimeSpan DiskMaxAge = TimeSpan.FromDays(7);

        private readonly IHttpTransport transport;
        private readonly string urlTemplate, cacheDirectory;
        private readonly Dictionary<long, LinkedListNode<(long Id, Texture2D Texture)>> memory = new Dictionary<long, LinkedListNode<(long, Texture2D)>>();
        private readonly LinkedList<(long Id, Texture2D Texture)> recent = new LinkedList<(long, Texture2D)>();
        private readonly List<long> queue = new List<long>();
        private readonly HashSet<long> inFlight = new HashSet<long>();
        // A tile that failed is not asked for again until this time on the clock
        private readonly Dictionary<long, float> retryAfter = new Dictionary<long, float>();
        private readonly CancellationTokenSource closing = new CancellationTokenSource();

        public string Attribution { get; }
        public event Action TileLoaded;

        // Null when the basemap is turned off.
        public static MapTiles FromEnvironment(IHttpTransport transport)
        {
            var configured = Environment.GetEnvironmentVariable("FORMULA9_TILE_URL");
            if (configured == null) return new MapTiles(transport, DefaultUrl, CacheFolderFor(DefaultUrl));
            configured = configured.Trim();
            if (configured.Length == 0 || configured.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
            return new MapTiles(transport, configured, CacheFolderFor(configured));
        }

        private static string CacheFolderFor(string template) =>
            Path.Combine(Application.temporaryCachePath, "map_tiles", Hash128.Compute(template).ToString());

        public MapTiles(IHttpTransport transport, string urlTemplate, string cacheDirectory = null)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.urlTemplate = urlTemplate;
            this.cacheDirectory = cacheDirectory;
            // Another server may need its own credit; it is shown alongside OSM's data credit
            Attribution = urlTemplate == DefaultUrl ? "© OpenStreetMap contributors"
                : $"© OpenStreetMap contributors · tiles: {new Uri(urlTemplate.Replace("{z}", "0").Replace("{x}", "0").Replace("{y}", "0")).Host}";
            TrimDiskCache();
        }

        // Forget the previous layout's wishes; tiles already downloading carry on
        public void BeginFrame() => queue.Clear();

        // The tile when it is in memory; otherwise null, and the tile is queued to download
        public Texture2D Tile(int zoom, int x, int y)
        {
            var id = Key(zoom, x, y);
            var texture = Cached(zoom, x, y);
            if (texture != null || inFlight.Contains(id) || queue.Contains(id)) return texture;
            if (retryAfter.TryGetValue(id, out var time) && time > Time.realtimeSinceStartup) return null;
            queue.Add(id);
            Pump();
            return Cached(zoom, x, y);
        }

        // The tile only if it is already in memory, never queued (for drawing a parent as a stand-in)
        public Texture2D Cached(int zoom, int x, int y)
        {
            if (!memory.TryGetValue(Key(zoom, x, y), out var node)) return null;
            recent.Remove(node);
            recent.AddFirst(node);
            return node.Value.Texture;
        }

        public void Dispose()
        {
            closing.Cancel();
            queue.Clear();
            foreach (var entry in recent) Remove(entry.Texture);
            recent.Clear();
            memory.Clear();
        }

        private static long Key(int zoom, int x, int y) => ((long)zoom << 48) | ((long)x << 24) | (long)y;

        private void Pump()
        {
            while (inFlight.Count < MaxInFlight && queue.Count > 0)
            {
                var id = queue[0];
                queue.RemoveAt(0);
                inFlight.Add(id);
                _ = FetchAsync(id);
            }
        }

        private async Task FetchAsync(long id)
        {
            var zoom = (int)(id >> 48); var x = (int)((id >> 24) & 0xFFFFFF); var y = (int)(id & 0xFFFFFF);
            var bytes = await LoadAsync(zoom, x, y);
            if (closing.IsCancellationRequested) return;
            inFlight.Remove(id);
            var texture = bytes == null ? null : Decode(bytes);
            if (texture == null) retryAfter[id] = Time.realtimeSinceStartup + RetryAfterFailureSeconds;
            else
            {
                retryAfter.Remove(id);
                memory[id] = recent.AddFirst((id, texture));
                while (recent.Count > MemoryTiles)
                {
                    var oldest = recent.Last;
                    recent.RemoveLast();
                    memory.Remove(oldest.Value.Id);
                    Remove(oldest.Value.Texture);
                }
                TileLoaded?.Invoke();
            }
            Pump();
        }

        // A fresh disk copy, else the server's answer, else a stale disk copy; null when there is none
        private async Task<byte[]> LoadAsync(int zoom, int x, int y)
        {
            var path = cacheDirectory == null ? null : Path.Combine(cacheDirectory, $"{zoom}_{x}_{y}.png");
            try
            {
                if (path != null && File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < DiskMaxAge)
                    return File.ReadAllBytes(path);
            }
            catch (IOException) { }
            try
            {
                var request = new HttpRequest
                {
                    Url = new Uri(urlTemplate.Replace("{z}", zoom.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString())),
                    TimeoutSeconds = TimeoutSeconds,
                };
                request.Headers["User-Agent"] = UserAgent;
                var reply = await transport.SendAsync(request, closing.Token);
                if (reply.Complete && reply.Status == 200 && reply.Data != null && reply.Data.Length > 0)
                {
                    Save(path, reply.Data);
                    return reply.Data;
                }
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                Debug.LogWarning($"Map tile {zoom}/{x}/{y}: {error.Message}");
            }
            catch (OperationCanceledException) { return null; }
            try { return path != null && File.Exists(path) ? File.ReadAllBytes(path) : null; }
            catch (IOException) { return null; }
        }

        private static Texture2D Decode(byte[] bytes)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            if (texture.LoadImage(bytes, true)) return texture;
            Remove(texture);
            return null;
        }

        private static void Remove(Texture2D texture)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
            else UnityEngine.Object.DestroyImmediate(texture);
        }

        private static void Save(string path, byte[] bytes)
        {
            if (path == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, bytes);
            }
            catch (IOException error) { Debug.LogWarning($"Map tile cache: {error.Message}"); }
        }

        // Oldest files go first once the folder is over its limit.
        private void TrimDiskCache()
        {
            if (cacheDirectory == null || !Directory.Exists(cacheDirectory)) return;
            try
            {
                var files = new DirectoryInfo(cacheDirectory).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc);
                long total = 0;
                foreach (var file in files)
                {
                    total += file.Length;
                    if (total > DiskCacheBytes) file.Delete();
                }
            }
            catch (IOException error) { Debug.LogWarning($"Map tile cache: {error.Message}"); }
        }
    }
}
