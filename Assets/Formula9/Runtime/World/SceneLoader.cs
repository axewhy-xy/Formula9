/*
 * Downloads and verifies a training scene package.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Part of the Unity client's 3D world. A port of the Qt client's TrainingSceneLoader. It
 *     downloads /api/v2/scenes/{revision}: the manifest, then each mandatory asset in turn, and
 *     decodes nothing until every byte matches the hashes the session pins. Failures are reported
 *     with the same SCENE_* codes as the Qt client.
 */

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
    // -----------------------------------------------------------------------------------
    // Class: ScenePackage
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class is a scene revision whose manifest and mandatory assets were verified against
    //     the session's pins.
    //
    //     Attributes: Manifest; ManifestSha256, the pinned hash of the manifest bytes; Assets,
    //     asset ID to verified bytes.
    public sealed class ScenePackage
    {
        public TrainingSceneRevision Manifest;
        public string ManifestSha256;
        public readonly Dictionary<string, byte[]> Assets = new Dictionary<string, byte[]>();
    }

    // -----------------------------------------------------------------------------------
    // Class: SceneException
    // -----------------------------------------------------------------------------------
    // Description:
    //     This exception means a scene cannot be shown.
    //
    //     Attributes: Code is one of the SCENE_* reasons the Qt client reports.
    public sealed class SceneException : Exception
    {
        public string Code { get; }
        // -----------------------------------------------------------------------------------
        // Function: SceneException (constructor)
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function creates the exception.
        //
        // Args:
        //     code (string): The SCENE_* reason.
        //     detail (string): What it concerns, for example an asset ID.
        public SceneException(string code, string detail = null) : base(detail == null ? code : $"{code}: {detail}") => Code = code;
    }

    // -----------------------------------------------------------------------------------
    // Class: SceneLoader
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class downloads scene packages and reports when one is ready.
    //
    //     Attributes: MaxManifestBytes (512 KiB) and MaxPackageBytes (16 MiB) bound what is
    //     downloaded.
    public sealed class SceneLoader : BackendClientBase
    {
        public const int MaxManifestBytes = 512 * 1024;
        public const long MaxPackageBytes = 16L * 1024 * 1024;

        // -----------------------------------------------------------------------------------
        // Function: SceneLoader (constructor)
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function creates the loader.
        //
        // Args:
        //     transport (IHttpTransport): How requests are sent.
        //     baseUrl (Uri): The backend address; the configured one when null.
        public SceneLoader(IHttpTransport transport, Uri baseUrl = null) : base(transport, baseUrl) { }

        // -----------------------------------------------------------------------------------
        // Function: ReportReadyAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function tells the backend the scene is loaded and drawn, so a spatial session
        //     may start. The manifest hash is over the received bytes, not a reserialisation of the
        //     parsed manifest.
        //
        // Args:
        //     package (ScenePackage): The loaded package.
        //     cancellation (CancellationToken): Cancels the request.
        //
        // Throws:
        //     SceneException: SCENE_READINESS_REPLY if the backend does not confirm this exact
        //         scene.
        //     BackendErrorException: If the backend refuses.
        //     NoReplyException: If no complete reply arrived.
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

        // -----------------------------------------------------------------------------------
        // Function: LoadAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function downloads a scene's manifest and mandatory assets and checks them
        //     against the pins: the manifest hash, its scene and revisions, the package size, and
        //     each asset's length and hash. Assets are fetched one at a time; optional assets fall
        //     back as the manifest says.
        //
        // Args:
        //     pins (SpatialReference): The session's spatial pins.
        //     cancellation (CancellationToken): Cancels the download.
        //
        // Returns:
        //     Task<ScenePackage>: The verified package.
        //
        // Throws:
        //     SceneException: With the SCENE_* code of the first check that failed.
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

        // -----------------------------------------------------------------------------------
        // Function: RenderedAsset
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function chooses the asset an entity renders: its own when loaded, otherwise its
        //     declared fallback.
        //
        // Args:
        //     package (ScenePackage): The package.
        //     entity (SceneEntity): The entity.
        //
        // Returns:
        //     string: The asset ID.
        //
        // Throws:
        //     SceneException: SCENE_REQUIRED_GEOMETRY if neither is loaded.
        public static string RenderedAsset(ScenePackage package, SceneEntity entity)
        {
            if (package.Assets.ContainsKey(entity.RenderAssetId)) return entity.RenderAssetId;
            var fallback = package.Manifest.Assets.FirstOrDefault(a => a.AssetId == entity.RenderAssetId)?.FallbackAssetId;
            if (fallback != null && package.Assets.ContainsKey(fallback)) return fallback;
            throw new SceneException("SCENE_REQUIRED_GEOMETRY", entity.EntityId);
        }

        // -----------------------------------------------------------------------------------
        // Function: DownloadAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function downloads raw bytes, refusing more than the limit.
        //
        // Args:
        //     path (string): The API path.
        //     limit (long): The most bytes allowed.
        //     cancellation (CancellationToken): Cancels the download.
        //
        // Returns:
        //     Task<byte[]>: The bytes.
        //
        // Throws:
        //     SceneException: SCENE_HTTP when the request fails, SCENE_BYTE_LIMIT when too large.
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

        // -----------------------------------------------------------------------------------
        // Function: Sha256
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function hashes bytes the way the pins are written.
        //
        // Args:
        //     data (byte[]): The bytes.
        //
        // Returns:
        //     string: The SHA-256 as lowercase hex.
        public static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(data).Select(b => b.ToString("x2")));
        }
    }
}
