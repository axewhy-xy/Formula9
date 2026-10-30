using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Formula9.Tests
{
    public class WorldTests
    {
        private static readonly string Scene = Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/m2_scene");

        // Serves the shared scene package as the backend's /api/v2/scenes routes do.
        private sealed class SceneBackend : IHttpTransport
        {
            public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
            public readonly List<string> Requested = new List<string>();

            public SceneBackend(TrainingSceneRevision manifest)
            {
                Files["manifest"] = File.ReadAllBytes(Path.Combine(Scene, "manifest.json"));
                foreach (var asset in manifest.Assets) Files[asset.AssetId] = File.ReadAllBytes(Path.Combine(Scene, asset.Path));
            }

            public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellation = default)
            {
                var name = request.Url.AbsolutePath.Split('/').Last();
                Requested.Add(name);
                return Task.FromResult(Files.TryGetValue(name, out var data)
                    ? new HttpResponse { Status = 200, Data = data, Body = "" }
                    : new HttpResponse { Status = 404, Body = "{\"error\":{\"code\":\"INVALID_REFERENCE\",\"message\":\"missing\"}}" });
            }
        }

        private static FullSnapshot SpatialSnapshot() =>
            (FullSnapshot)ContractParser.ParseMessage(File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/spatial_snapshot.json")));

        private static TrainingSceneRevision Manifest() =>
            ContractParser.Parse<TrainingSceneRevision>(File.ReadAllText(Path.Combine(Scene, "manifest.json")));

        private static SceneException LoadFails(SceneBackend backend, SpatialReference pins)
        {
            try
            {
                new SceneLoader(backend, new Uri("http://backend.test")).LoadAsync(pins).GetAwaiter().GetResult();
            }
            catch (SceneException error)
            {
                return error;
            }
            Assert.Fail("the scene loaded");
            return null;
        }

        [Test]
        public void TheVerifiedPackageHoldsEveryMandatoryAsset()
        {
            var manifest = Manifest();
            var backend = new SceneBackend(manifest);
            var package = new SceneLoader(backend, new Uri("http://backend.test")).LoadAsync(SpatialSnapshot().Spatial).GetAwaiter().GetResult();
            CollectionAssert.AreEquivalent(manifest.Assets.Where(a => a.Mandatory).Select(a => a.AssetId), package.Assets.Keys);
            Assert.IsFalse(backend.Requested.Any(name => manifest.Assets.Any(a => !a.Mandatory && a.AssetId == name)), "optional assets are not fetched");
        }

        [Test]
        public void AChangedManifestIsRefused()
        {
            var backend = new SceneBackend(Manifest());
            backend.Files["manifest"] = backend.Files["manifest"].Concat(new[] { (byte)' ' }).ToArray();
            Assert.AreEqual("SCENE_MANIFEST_HASH", LoadFails(backend, SpatialSnapshot().Spatial).Code);
        }

        [Test]
        public void AChangedAssetIsRefused()
        {
            var manifest = Manifest();
            var backend = new SceneBackend(manifest);
            var asset = manifest.Assets.First(a => a.Mandatory).AssetId;
            backend.Files[asset] = (byte[])backend.Files[asset].Clone();
            backend.Files[asset][backend.Files[asset].Length - 1] ^= 0xFF;
            Assert.AreEqual("SCENE_REQUIRED_ASSET_HASH", LoadFails(backend, SpatialSnapshot().Spatial).Code);
        }

        [Test]
        public void AManifestForAnotherRevisionIsRefused()
        {
            var pins = SpatialSnapshot().Spatial;
            pins.SpatialRevision = "spatial-other";
            Assert.AreEqual("SCENE_REVISION_MISMATCH", LoadFails(new SceneBackend(Manifest()), pins).Code);
        }

        private static IEnumerator Await(Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted)
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("Timed out");
                yield return null;
            }
            if (task.IsFaulted) throw task.Exception.InnerException;
        }

        private static ScenePackage Package() =>
            new SceneLoader(new SceneBackend(Manifest()), new Uri("http://backend.test")).LoadAsync(SpatialSnapshot().Spatial).GetAwaiter().GetResult();

        [Test]
        public void DraftNodesMustBelongToTheChosenSurfaceAndSceneRevision()
        {
            var manifest = Manifest(); var surface = manifest.RoadSurfaces[0]; var point = surface.PathSamplesM[0];
            var node = new RouteEditorNode { NodeId = 1, X = point[0], Y = point[1], Z = point[2] };
            var context = new RouteEditorContext { SceneRevision = manifest.SceneRevision,
                Nodes = new List<RouteEditorNode> { node },
                Edges = new List<RouteEditorEdge> { new RouteEditorEdge { EdgeId = surface.DirectedEdgeId, Source = 1, Target = 2 } } };
            Assert.IsTrue(WorldInteraction.NodeOnSurface(manifest, context, node, surface.SurfaceId));
            node.Z += 5;
            Assert.IsFalse(WorldInteraction.NodeOnSurface(manifest, context, node, surface.SurfaceId), "another deck is not the same road support");
            node.Z -= 5; context.SceneRevision = "other-scene";
            Assert.IsFalse(WorldInteraction.NodeOnSurface(manifest, context, node, surface.SurfaceId));
        }

        [UnityTest]
        public IEnumerator MeshPickingMatchesTheSharedCanonicalGoldenPoints()
        {
            var build = WorldBuilder.BuildAsync(Package(), null); yield return Await(build);
            var world = build.Result;
            try
            {
                Assert.IsNull(world.ApplyPoses(SpatialSnapshot()));
                var fixture = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/native_world_interaction.json")));
                foreach (var pick in fixture["picks"])
                {
                    var origin = pick["origin_m"].ToObject<List<double>>(); var direction = pick["direction"].ToObject<List<double>>();
                    var found = WorldInteraction.TryPick(world, new Ray(WorldMath.ToUnity(origin), WorldMath.ToUnity(direction)), out var id, out var canonical);
                    Assert.AreEqual((string)pick["entity_id"], id);
                    if (found)
                    {
                        var expected = pick["point_m"].ToObject<List<float>>();
                        Assert.Less(Vector3.Distance(canonical, new Vector3(expected[0], expected[1], expected[2])), 0.02f);
                    }
                }
            }
            finally { world.Destroy(); }
        }

        [UnityTest]
        public IEnumerator TheRealSceneBuildsAndEveryEntityMatchesItsDimensions()
        {
            var package = Package();
            var build = WorldBuilder.BuildAsync(package, null);
            yield return Await(build);
            var world = build.Result;
            try
            {
                Assert.AreEqual(package.Manifest.Entities.Count, world.Entities.Count);
                Assert.AreEqual(5, world.ShuttleCount);
                Assert.IsTrue(world.Root.GetComponentsInChildren<MeshRenderer>().Length >= world.Entities.Count);
            }
            finally
            {
                WorldBuilder.Remove(world.Root.gameObject);
            }
        }

        [UnityTest]
        public IEnumerator ShuttlesFollowTheSnapshotPoses()
        {
            var build = WorldBuilder.BuildAsync(Package(), null);
            yield return Await(build);
            var world = build.Result;
            try
            {
                var snapshot = SpatialSnapshot();
                Assert.IsNull(world.ApplyPoses(snapshot));
                foreach (var vehicle in snapshot.Vehicles)
                {
                    var expected = WorldMath.ToUnity(vehicle.Spatial.PositionM);
                    var centre = world.Entities[vehicle.VehicleId].GetComponentsInChildren<MeshRenderer>()
                        .Select(r => r.bounds).Aggregate((a, b) => { a.Encapsulate(b); return a; }).center;
                    Assert.Less(Vector3.Distance(expected, centre), 0.01f, vehicle.VehicleId);
                }

                snapshot.Vehicles[0].Spatial.RenderAssetId = "asset-building";
                StringAssert.Contains("does not match its scene binding", world.ApplyPoses(snapshot));
            }
            finally
            {
                WorldBuilder.Remove(world.Root.gameObject);
            }
        }

        // Renders the real scene from the manifest's overview bookmark. Needs a GPU, so it is
        // skipped under -nographics. Set FORMULA9_RENDER_OUT to keep the PNG for a visual check.
        [UnityTest, Category("Render")]
        public IEnumerator TheOverviewShowsTheScene()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No GPU (-nographics)");
            var package = Package();
            var build = WorldBuilder.BuildAsync(package, null);
            yield return Await(build);
            var world = build.Result;
            var rig = new GameObject("Render rig");
            var texture = new RenderTexture(1280, 720, 24) { antiAliasing = 4 };
            try
            {
                Assert.IsNull(world.ApplyPoses(SpatialSnapshot()));
                WorldLighting.Apply(rig.transform);
                var camera = new GameObject("Camera", typeof(Camera)).GetComponent<Camera>();
                camera.transform.SetParent(rig.transform, true);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.10f, 0.10f, 0.12f);
                camera.fieldOfView = 50;
                camera.farClipPlane = 1200;
                var bookmark = package.Manifest.CameraBookmarks[0];
                camera.transform.position = WorldMath.ToUnity(bookmark.PositionM);
                camera.transform.LookAt(WorldMath.ToUnity(bookmark.TargetM), Vector3.up);
                camera.targetTexture = texture;
                camera.Render();

                RenderTexture.active = texture;
                var image = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                image.Apply();
                RenderTexture.active = null;
                var background = image.GetPixel(2, 2);
                var drawn = image.GetPixels().Count(p => Mathf.Abs(p.r - background.r) + Mathf.Abs(p.g - background.g) + Mathf.Abs(p.b - background.b) > 0.05f);
                var output = Environment.GetEnvironmentVariable("FORMULA9_RENDER_OUT");
                if (!string.IsNullOrEmpty(output)) File.WriteAllBytes(output, image.EncodeToPNG());
                Assert.Greater(drawn, texture.width * texture.height / 50, "the overview should show the scene's geometry");
                UnityEngine.Object.DestroyImmediate(image);
            }
            finally
            {
                texture.Release();
                UnityEngine.Object.DestroyImmediate(rig);
                WorldBuilder.Remove(world.Root.gameObject);
            }
        }

        [UnityTest]
        public IEnumerator AWrongDeclaredSizeIsReportedNotDrawn()
        {
            var package = Package();
            package.Manifest.Entities.First(e => e.Kind == "BUILDING").DimensionsM[0] += 1;
            var build = WorldBuilder.BuildAsync(package, null);
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!build.IsCompleted && DateTime.UtcNow < deadline) yield return null;
            Assert.IsTrue(build.IsFaulted);
            Assert.AreEqual("SCENE_BODY_DIMENSIONS", ((SceneException)build.Exception.InnerException).Code);
            Assert.IsNull(GameObject.Find($"World {package.Manifest.SceneRevision}"), "a refused world leaves nothing behind");
        }
    }
}
