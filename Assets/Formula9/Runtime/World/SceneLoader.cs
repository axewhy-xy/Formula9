using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;
using Newtonsoft.Json.Linq;

namespace Formula9.World
{
    // A scene revision whose manifest and mandatory assets were verified against the session's pins.
    public sealed class ScenePackage
    {
        public TrainingSceneRevision Manifest;
        public string ManifestSha256;
        public readonly Dictionary<string, byte[]> Assets = new Dictionary<string, byte[]>();
    }

    // A scene that cannot be shown. Code is one of the SCENE_* reasons the Qt client reports.
    public sealed class SceneException : Exception
    {
        public string Code { get; }
        public SceneException(string code, string detail = null) : base(detail == null ? code : $"{code}: {detail}") => Code = code;
    }

    // Downloads /api/v2/scenes/{revision}: the manifest, then each mandatory asset in turn. Port of
    // the Qt TrainingSceneLoader. Nothing is decoded until every byte matches the pinned hashes.
    public sealed class SceneLoader : BackendClientBase
    {
        public const int MaxManifestBytes = 512 * 1024;
        public const long MaxPackageBytes = 16L * 1024 * 1024;

        public SceneLoader(IHttpTransport transport, Uri baseUrl = null) : base(transport, baseUrl) { }

        public async Task ReportReadyAsync(ScenePackage package, CancellationToken cancellation = default)
        {
            var manifest = package.Manifest;
            var request = new ReadinessRequest
            {
                SceneId = manifest.SceneId, SceneRevision = manifest.SceneRevision,
                SpatialRevision = manifest.SpatialRevision, SpatialSchemaVersion = manifest.SpatialSchemaVersion,
                TransformRevision = manifest.TransformRevision,
                AssetProfile = manifest.AssetProfile, RequiredCapabilities = new System.Collections.Generic.List<string> { "world_3d" },
                AssetHashes = package.Assets.ToDictionary(a => a.Key, a => Sha256(a.Value)), RenderReady = true,
            };
            // The hash is over the received bytes, not a reserialisation of the parsed manifest.
            request.ManifestSha256 = package.ManifestSha256;
            var json = await SendAsync(Request("POST", $"/api/v2/scenes/{Uri.EscapeDataString(manifest.SceneRevision)}/readiness", ContractParser.Serialize(request)), cancellation);
            JObject reply;
            try { reply = JObject.Parse(json); }
            catch (Newtonsoft.Json.JsonException) { throw new SceneException("SCENE_READINESS_REPLY"); }
            if ((bool?)reply["ready"] != true || (string)reply["scene_revision"] != manifest.SceneRevision ||
                (string)reply["manifest_sha256"] != request.ManifestSha256) throw new SceneException("SCENE_READINESS_REPLY");
        }

        public async Task<ScenePackage> LoadAsync(SpatialReference pins, CancellationToken cancellation = default)
        {
            if (pins == null) throw new SceneException("SCENE_NOT_SPATIAL", "this session has no 3D world");
            var revision = Uri.EscapeDataString(pins.SceneRevision);
            var manifestBytes = await DownloadAsync($"/api/v2/scenes/{revision}/manifest", MaxManifestBytes, cancellation);
            if (Sha256(manifestBytes) != pins.ManifestSha256) throw new SceneException("SCENE_MANIFEST_HASH");

            TrainingSceneRevision manifest;
            try { manifest = ContractParser.Parse<TrainingSceneRevision>(Encoding.UTF8.GetString(manifestBytes)); }
            catch (ContractException error) { throw new SceneException("SCENE_MANIFEST_INVALID", error.Message); }
            if (manifest.SceneId != pins.SceneId || manifest.SceneRevision != pins.SceneRevision
                || manifest.SpatialRevision != pins.SpatialRevision || manifest.TransformRevision != pins.TransformRevision)
                throw new SceneException("SCENE_REVISION_MISMATCH");
            if (manifestBytes.Length + manifest.Assets.Sum(a => a.ByteLength) > MaxPackageBytes) throw new SceneException("SCENE_PACKAGE_LIMIT");

            var package = new ScenePackage { Manifest = manifest, ManifestSha256 = pins.ManifestSha256 };
            // The mandatory tier only, one at a time; optional assets fall back as the manifest says.
            foreach (var asset in manifest.Assets.Where(a => a.Mandatory))
            {
                var data = await DownloadAsync($"/api/v2/scenes/{revision}/assets/{Uri.EscapeDataString(asset.AssetId)}",
                    asset.ByteLength, cancellation);
                if (data.Length != asset.ByteLength || Sha256(data) != asset.Sha256)
                    throw new SceneException("SCENE_REQUIRED_ASSET_HASH", asset.AssetId);
                package.Assets[asset.AssetId] = data;
            }
            return package;
        }

        // The asset each entity renders: its own when loaded, otherwise its declared fallback.
        public static string RenderedAsset(ScenePackage package, SceneEntity entity)
        {
            if (package.Assets.ContainsKey(entity.RenderAssetId)) return entity.RenderAssetId;
            var fallback = package.Manifest.Assets.FirstOrDefault(a => a.AssetId == entity.RenderAssetId)?.FallbackAssetId;
            if (fallback != null && package.Assets.ContainsKey(fallback)) return fallback;
            throw new SceneException("SCENE_REQUIRED_GEOMETRY", entity.EntityId);
        }

        private async Task<byte[]> DownloadAsync(string path, long limit, CancellationToken cancellation)
        {
            HttpResponse reply;
            try { reply = await SendForReplyAsync(Request("GET", path), cancellation); }
            catch (Exception error) when (error is NoReplyException || error is BackendErrorException)
            {
                throw new SceneException("SCENE_HTTP", error.Message);
            }
            var data = reply.Data ?? Array.Empty<byte>();
            if (data.Length > limit) throw new SceneException("SCENE_BYTE_LIMIT", path);
            return data;
        }

        public static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(data).Select(b => b.ToString("x2")));
        }
    }
}
