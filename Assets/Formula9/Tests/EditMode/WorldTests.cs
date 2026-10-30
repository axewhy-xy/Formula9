/*
 * Unit tests for the 3D world (SceneLoader.cs, WorldBuilder.cs, WorldView.cs).
 *
 * Author: Choo Jian Wei
 * Contributors: Lau Xiang Yang (route drafting and mesh picking tests)
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Uses the shared M2 scene package served by a fake backend. Checks that only a package
 *     matching the session's pins loads, that the real scene builds with every entity at its
 *     declared size, that shuttles follow snapshot poses, that picking and drafting use the shared
 *     golden points, and that the overview renders (with a GPU).
 */

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
    // -----------------------------------------------------------------------------------
    // Class: WorldTests
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class tests loading, building, picking and rendering the 3D world.
    public class WorldTests
    {
        private static readonly string Scene = Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/m2_scene");

        // -----------------------------------------------------------------------------------
        // Class: SceneBackend
        // -----------------------------------------------------------------------------------
        // Description:
        //     This class serves the shared scene package as the backend's /api/v2/scenes routes do.
        //
        //     Attributes: Files maps the manifest and each asset ID to its bytes; tests change them
        //     to simulate tampering. Requested lists what was downloaded.
        private sealed class SceneBackend : IHttpTransport
        {
            public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
            public readonly List<string> Requested = new List<string>();

            // -----------------------------------------------------------------------------------
            // Function: SceneBackend (constructor)
            // -----------------------------------------------------------------------------------
            // Description:
            //     This function loads the manifest and its assets from the shared package.
            //
            // Args:
            //     manifest (TrainingSceneRevision): The package's manifest.
            public SceneBackend(TrainingSceneRevision manifest)
            {
                Files["manifest"] = File.ReadAllBytes(Path.Combine(Scene, "manifest.json"));
                foreach (var asset in manifest.Assets) Files[asset.AssetId] = File.ReadAllBytes(Path.Combine(Scene, asset.Path));
            }

            // -----------------------------------------------------------------------------------
            // Function: SceneBackend.SendAsync
            // -----------------------------------------------------------------------------------
            // Description:
            //     This function serves the file the request names, or a 404.
            //
            // Args:
            //     request (HttpRequest): The request.
            //     cancellation (CancellationToken): Unused.
            //
            // Returns:
            //     Task<HttpResponse>: The reply.
            public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellation = default)
            {
                var name = request.Url.AbsolutePath.Split('/').Last();
                Requested.Add(name);
                return Task.FromResult(Files.TryGetValue(name, out var data)
                    ? new HttpResponse { Status = 200, Data = data, Body = "" }
                    : new HttpResponse { Status = 404, Body = "{\"error\":{\"code\":\"INVALID_REFERENCE\",\"message\":\"missing\"}}" });
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: SpatialSnapshot
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reads the shared spatial snapshot fixture.
        //
        // Returns:
        //     FullSnapshot: The snapshot.
        private static FullSnapshot SpatialSnapshot() =>
            (FullSnapshot)ContractParser.ParseMessage(File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/spatial_snapshot.json")));

        // -----------------------------------------------------------------------------------
        // Function: Manifest
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reads the shared scene manifest.
        //
        // Returns:
        //     TrainingSceneRevision: The manifest.
        private static TrainingSceneRevision Manifest() =>
            ContractParser.Parse<TrainingSceneRevision>(File.ReadAllText(Path.Combine(Scene, "manifest.json")));

        // -----------------------------------------------------------------------------------
        // Function: LoadFails
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function loads a scene that must fail, and returns why.
        //
        // Args:
        //     backend (SceneBackend): The fake backend.
        //     pins (SpatialReference): The pins to load.
        //
        // Returns:
        //     SceneException: The failure.
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

        // -----------------------------------------------------------------------------------
        // Function: TheVerifiedPackageHoldsEveryMandatoryAsset
        // -----------------------------------------------------------------------------------
        // Description:
        //     A matching package loads with every mandatory asset.
        [Test]
        public void TheVerifiedPackageHoldsEveryMandatoryAsset()
        {
            var manifest = Manifest();
            var backend = new SceneBackend(manifest);
            var package = new SceneLoader(backend, new Uri("http://backend.test")).LoadAsync(SpatialSnapshot().Spatial).GetAwaiter().GetResult();
            CollectionAssert.AreEquivalent(manifest.Assets.Where(a => a.Mandatory).Select(a => a.AssetId), package.Assets.Keys);
            Assert.IsFalse(backend.Requested.Any(name => manifest.Assets.Any(a => !a.Mandatory && a.AssetId == name)), "optional assets are not fetched");
        }

        // -----------------------------------------------------------------------------------
        // Function: AChangedManifestIsRefused
        // -----------------------------------------------------------------------------------
        // Description:
        //     A manifest whose bytes differ from the pinned hash is refused.
        [Test]
        public void AChangedManifestIsRefused()
        {
            var backend = new SceneBackend(Manifest());
            backend.Files["manifest"] = backend.Files["manifest"].Concat(new[] { (byte)' ' }).ToArray();
            Assert.AreEqual("SCENE_MANIFEST_HASH", LoadFails(backend, SpatialSnapshot().Spatial).Code);
        }

        // -----------------------------------------------------------------------------------
        // Function: AChangedAssetIsRefused
        // -----------------------------------------------------------------------------------
        // Description:
        //     An asset whose bytes differ from the manifest's hash is refused.
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

        // -----------------------------------------------------------------------------------
        // Function: AManifestForAnotherRevisionIsRefused
        // -----------------------------------------------------------------------------------
        // Description:
        //     A manifest for another scene revision is refused even with a matching hash.
        [Test]
        public void AManifestForAnotherRevisionIsRefused()
        {
            var pins = SpatialSnapshot().Spatial;
            pins.SpatialRevision = "spatial-other";
            Assert.AreEqual("SCENE_REVISION_MISMATCH", LoadFails(new SceneBackend(Manifest()), pins).Code);
        }

        // -----------------------------------------------------------------------------------
        // Function: Await
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function yields until a task finishes, failing after a minute and rethrowing its
        //     error.
        //
        // Args:
        //     task (Task): The task.
        //
        // Returns:
        //     IEnumerator: The wait.
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

        // -----------------------------------------------------------------------------------
        // Function: Package
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function loads the shared scene package.
        //
        // Returns:
        //     ScenePackage: The verified package.
        private static ScenePackage Package() =>
            new SceneLoader(new SceneBackend(Manifest()), new Uri("http://backend.test")).LoadAsync(SpatialSnapshot().Spatial).GetAwaiter().GetResult();

        // -----------------------------------------------------------------------------------
        // Function: DraftNodesMustBelongToTheChosenSurfaceAndSceneRevision
        // -----------------------------------------------------------------------------------
        // Description:
        //     A route node can be drafted only on the chosen road surface of the same scene
        //     revision, not on another deck above it.
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

        // -----------------------------------------------------------------------------------
        // Function: MeshPickingMatchesTheSharedCanonicalGoldenPoints
        // -----------------------------------------------------------------------------------
        // Description:
        //     Picking the built scene's meshes hits the shared golden points in canonical
        //     coordinates.
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

        // -----------------------------------------------------------------------------------
        // Function: TheRealSceneBuildsAndEveryEntityMatchesItsDimensions
        // -----------------------------------------------------------------------------------
        // Description:
        //     The real scene builds, and every entity fills exactly its declared dimensions.
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

        // -----------------------------------------------------------------------------------
        // Function: ShuttlesFollowTheSnapshotPoses
        // -----------------------------------------------------------------------------------
        // Description:
        //     Shuttles move to each snapshot's poses, and a snapshot that does not fit the scene is
        //     reported.
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

        // -----------------------------------------------------------------------------------
        // Function: ShuttlesGlideToTheSnapshotPoses
        // -----------------------------------------------------------------------------------
        // Description:
        //     With a motion, a shuttle reaches its pose over the frames after a snapshot instead of
        //     jumping there, and a snapshot that does not fit the scene is still reported.
        [UnityTest]
        public IEnumerator ShuttlesGlideToTheSnapshotPoses()
        {
            var build = WorldBuilder.BuildAsync(Package(), null);
            yield return Await(build);
            var world = build.Result;
            try
            {
                var motion = new ShuttleMotion();
                var snapshot = SpatialSnapshot();
                Assert.IsNull(world.ApplyPoses(snapshot, motion, 0));
                var vehicle = snapshot.Vehicles[0];
                var shuttle = world.Entities[vehicle.VehicleId];
                var before = shuttle.position;
                vehicle.Spatial.PositionM[0] += 2;
                vehicle.Spatial.Movement = "CONTINUOUS";
                Assert.IsNull(world.ApplyPoses(snapshot, motion, 1));
                Assert.Less(Vector3.Distance(shuttle.position, before), 0.001f, "the snapshot does not move it by itself");
                world.Advance(motion, 1.55f);
                Assert.Less(Vector3.Distance(shuttle.position, before + Vector3.right), 0.001f, "halfway at halfway");
                world.Advance(motion, 5);
                Assert.Less(Vector3.Distance(shuttle.position, before + Vector3.right * 2), 0.001f, "it ends on the pose");

                vehicle.Spatial.RenderAssetId = "asset-building";
                StringAssert.Contains("does not match its scene binding", world.ApplyPoses(snapshot, motion, 6));
            }
            finally
            {
                WorldBuilder.Remove(world.Root.gameObject);
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: TheOverviewShowsTheScene
        // -----------------------------------------------------------------------------------
        // Description:
        //     Renders the real scene from the manifest's overview bookmark and checks the image is
        //     not empty. Needs a GPU, so it is skipped under -nographics. Set FORMULA9_RENDER_OUT
        //     to keep the PNG for a visual check.
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

        // -----------------------------------------------------------------------------------
        // Function: AWrongDeclaredSizeIsReportedNotDrawn
        // -----------------------------------------------------------------------------------
        // Description:
        //     An entity whose declared size does not match its geometry fails the build with
        //     SCENE_BODY_DIMENSIONS.
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
