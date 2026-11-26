/*
 * Unit tests for the shuttle cameras (ShuttleCameraRig.cs, WorldView.cs).
 *
 * Author: Choo Jian Wei
 * Date Created: 10/11/2026
 * Last Modified: 14/11/2026
 *
 * Description:
 *     Builds the real WorldView with the training layout, a one-building city and the shared
 *     planar snapshot, then checks that the camera view starts hidden with its four cameras off,
 *     that showing it replaces the 3D view and mounts the cameras around the selected shuttle,
 *     that the camera shown large draws every frame while the thumbnails take turns, that a
 *     thumbnail can be shown large, that the cameras follow the shuttle as it moves and turns,
 *     and that the 3D view comes back when there is no shuttle left to watch or something
 *     else needs it. Also checks that the cameras match the city's sky and view distance,
 *     move to another shuttle the operator picks but stay on theirs when the app selects one,
 *     a shuttle not in the city is picked or the watched one is picked again, update the hint
 *     at once, leave the hidden 3D view still while following, and that traffic is drawn for
 *     every camera that can see it. And that the thumbnails draw into small images and the
 *     camera shown large into one large image, all freed on closing; that the large view names
 *     the watched shuttle; that the planning overlays are left out; and that closing, by the
 *     button or on its own, goes back to the 3D view.
 */

using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.World;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Formula9.Tests
{
    // -----------------------------------------------------------------------------------
    // Class: ShuttleCameraTests
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class tests the shuttle camera view against a real WorldView.
    public class ShuttleCameraTests
    {
        private const string Shuttle = "shuttle-01";
        private static readonly string[] Names = { "Front", "Rear", "Left", "Right" };

        // Serves a city of one building away from the shuttles, so the city loads at once.
        private sealed class OneBuilding : IHttpTransport
        {
            public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellation = default) =>
                Task.FromResult(new HttpResponse { Status = 200, Body = "{\"buildings\":[{\"id\":\"way/1\",\"type\":\"retail\",\"name\":\"\",\"height_m\":30,\"rings_m\":[[[0,100],[10,100],[10,110],[0,110],[0,100]]]}]}" });
        }

        // Holds the one-building city back until released, like a real download.
        private sealed class SlowBuilding : IHttpTransport
        {
            private readonly TaskCompletionSource<bool> released = new TaskCompletionSource<bool>();
            public void Release() => released.TrySetResult(true);
            public async Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellation = default)
            {
                await released.Task;
                return await new OneBuilding().SendAsync(request, cancellation);
            }
        }

        private GameObject rig;
        private WorldView view;
        private VisualElement root;

        // The shared planar snapshot with shuttle-01 at a heading and position.
        private static FullSnapshot Snapshot(double headingDeg = 58.9, double x = 10, double y = 0)
        {
            var path = Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/snapshot.json");
            var snapshot = (FullSnapshot)ContractParser.ParseMessage(File.ReadAllText(path));
            var vehicle = snapshot.Vehicles.First(v => v.VehicleId == Shuttle);
            vehicle.HeadingDeg = headingDeg;
            vehicle.Position.XM = x; vehicle.Position.YM = y;
            foreach (var depot in snapshot.Depots) depot.Position.XM = 1000;
            return snapshot;
        }

        // Builds a WorldView in the training layout around the given city transport.
        private void Build(IHttpTransport city)
        {
            root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Formula9/UI/Formula9App.uxml").CloneTree();
            rig = new GameObject("Shuttle camera test");
            view = rig.AddComponent<WorldView>();
            var backend = new Uri("http://backend.test");
            view.Attach(root.Q("world"), new SceneLoader(new OneBuilding(), backend), null, null, new CityClient(city, backend));
        }

        // Shows the snapshot in the 3D city and selects shuttle-01.
        [SetUp]
        public void SetUp()
        {
            Build(new OneBuilding());
            view.Show(Snapshot());
            view.ShowMap(false);
            view.SelectEntity(Shuttle);
            Frame();
        }

        [TearDown]
        public void TearDown() { if (rig != null) UnityEngine.Object.DestroyImmediate(rig); }

        // Runs one frame of the view, Update then LateUpdate as Unity does, which it does not do
        // in edit mode.
        private void Frame()
        {
            typeof(WorldView).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(view, null);
            typeof(WorldView).GetMethod("LateUpdate", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(view, null);
        }

        private VisualElement Panel() => root.Q(className: "camera-panel");
        private VisualElement[] Tiles() => Panel().Query(className: "camera-tile").ToList().ToArray();
        private Label MainLabel() => Panel().Q(className: "camera-main").Q<Label>();
        private Camera Cam(string name) => rig.GetComponentsInChildren<Camera>(true).Single(c => c.name == "Shuttle camera " + name);
        private Camera[] Cams() => Names.Select(Cam).ToArray();
        private Camera WorldCamera() => rig.GetComponentsInChildren<Camera>(true).Single(c => c.name == "World Camera");
        private Transform ShuttleTransform(string id = Shuttle) =>
            rig.GetComponentsInChildren<Transform>(true).First(t => t.name == "Shuttle " + id);

        // Checks every camera sits on its mount around the shuttle and looks out the right way.
        private void AssertMounted(string id = Shuttle)
        {
            var s = ShuttleTransform(id);
            Assert.Greater(Vector3.Dot(Cam("Front").transform.forward, s.forward), 0.999f, "front looks ahead");
            Assert.Greater(Vector3.Dot(Cam("Front").transform.position - s.position, s.forward), 6f, "front is at the nose");
            Assert.Greater(Vector3.Dot(Cam("Rear").transform.forward, -s.forward), 0.999f, "rear looks back");
            Assert.Less(Vector3.Dot(Cam("Rear").transform.position - s.position, s.forward), -6f, "rear is at the tail");
            Assert.Greater(Vector3.Dot(Cam("Left").transform.forward, -s.right), 0.999f, "left looks left");
            Assert.Greater(Vector3.Dot(Cam("Right").transform.forward, s.right), 0.999f, "right looks right");
            // Outside the plain 5 m wide box drawn when the bus model is missing, as well as the bus
            Assert.Greater(Vector3.Dot(Cam("Left").transform.position - s.position, -s.right), 2.5f, "left is outside the side");
            Assert.Greater(Vector3.Dot(Cam("Right").transform.position - s.position, s.right), 2.5f, "right is outside the side");
            foreach (var name in Names)
                Assert.AreEqual(s.position.y - 0.5f, Cam(name).transform.position.y, 1e-3f, name + " is about 2 m above the road");
        }

        [Test]
        public void ViewStartsHiddenWithFourLabelledThumbnailsAndCamerasOff()
        {
            Assert.AreEqual(DisplayStyle.None, Panel().style.display.value, "camera view hidden");
            Assert.IsFalse(view.CamerasShown);
            var labels = Tiles().Select(t => t.Q<Label>().text).ToArray();
            // The thumbnail of the camera shown large says so
            CollectionAssert.AreEqual(new[] { "Front · showing", "Rear", "Left", "Right" }, labels, "one labelled thumbnail per camera, in order");
            Assert.AreEqual("Front", MainLabel().text, "the front camera is shown large first");
            Assert.IsTrue(Tiles()[0].ClassListContains("camera-tile-selected"), "and its thumbnail is marked");
            Assert.IsTrue(Cams().All(c => !c.enabled), "cameras off while nobody watches");
            Assert.IsTrue(Cams().All(c => c.targetTexture == null), "no pictures are made until someone opens the cameras");
            Assert.IsFalse(rig.GetComponentsInChildren<Camera>(true).Any(c => c.name.Contains("Bird")), "no bird's-eye camera");
        }

        [Test]
        public void ShowingCamerasReplacesThe3DViewAndMountsThemOnTheShuttle()
        {
            Assert.IsTrue(view.CanShowCameras, "a selected shuttle in the 3D city can be watched");
            Assert.IsTrue(WorldCamera().enabled, "the 3D view draws before the cameras open");
            view.ShowCameras(true);
            Assert.IsTrue(Cams().All(c => c.enabled), "every camera draws once on opening, so no thumbnail starts blank");
            Assert.IsFalse(WorldCamera().enabled, "the 3D view stops drawing while the cameras cover it");
            AssertImages(0);
            var world = WorldCamera();
            var planning = 1 << CityView.PlanningLayer;
            Assert.AreNotEqual(0, world.cullingMask & planning, "the 3D view shows the planning overlays");
            foreach (var c in Cams())
            {
                Assert.AreEqual(world.clearFlags, c.clearFlags, c.name + " has the city's sky");
                Assert.AreEqual(world.backgroundColor, c.backgroundColor, c.name + " has the city's sky colour");
                Assert.AreEqual(world.farClipPlane, c.farClipPlane, c.name + " sees as far as the 3D view");
                Assert.AreEqual(world.cullingMask & ~planning, c.cullingMask, c.name + " draws what the 3D view draws but the planning overlays");
            }
            Frame();
            Assert.IsTrue(view.CamerasShown, "still open after a frame");
            Assert.AreEqual(DisplayStyle.Flex, Panel().style.display.value);
            AssertMounted();

            view.Show(Snapshot());
            Assert.IsFalse(WorldCamera().enabled, "a new snapshot does not bring the 3D view back");
        }

        [Test]
        public void TheLargeCameraDrawsEveryFrameAndTheThumbnailsTakeTurns()
        {
            view.ShowCameras(true);
            var turns = new int[Names.Length];
            for (var frame = 0; frame < 6; frame++)
            {
                Frame();
                Assert.IsTrue(Cam("Front").enabled, $"the large camera draws on frame {frame}");
                var on = Cams().Select((c, i) => (c, i)).Where(p => p.c.enabled).Select(p => p.i).ToList();
                Assert.AreEqual(2, on.Count, $"the large camera and one thumbnail draw on frame {frame}");
                turns[on.Single(i => i != 0)]++;
            }
            CollectionAssert.AreEqual(new[] { 0, 2, 2, 2 }, turns, "the other three take turns, twice each in six frames");

            view.SelectCamera(2);
            Frame();
            Assert.IsTrue(Cam("Left").enabled, "the newly chosen camera draws every frame");
            Assert.AreEqual(2, Cams().Count(c => c.enabled));

            view.ShowCameras(false);
            Frame();
            Assert.IsTrue(Cams().All(c => !c.enabled), "no turns while the view is closed");
        }

        [Test]
        public void AThumbnailCanBeShownLarge()
        {
            view.ShowCameras(true);
            view.SelectCamera(1);
            Assert.AreEqual(1, view.MainCamera);
            Assert.AreEqual($"{Shuttle} · Rear", MainLabel().text, "the large view names the shuttle and the camera");
            AssertImages(1);
            Assert.IsTrue(Tiles()[1].ClassListContains("camera-tile-selected"));
            Assert.IsFalse(Tiles()[0].ClassListContains("camera-tile-selected"), "only one thumbnail is marked");
        }

        // The camera shown large draws into the one large image and the rest into small ones.
        private void AssertImages(int main)
        {
            for (var i = 0; i < Names.Length; i++)
            {
                var image = Cams()[i].targetTexture;
                Assert.IsNotNull(image, Names[i] + " has an image while the cameras are open");
                if (i == main) Assert.AreEqual((960, 540), (image.width, image.height), Names[i] + ", shown large, fills the 3D view");
                else Assert.AreEqual((320, 180), (image.width, image.height), Names[i] + ", a thumbnail, draws small");
            }
        }

        [Test]
        public void ClosingFreesTheImages()
        {
            view.ShowCameras(true);
            var images = Cams().Select(c => c.targetTexture).ToArray();
            view.ShowCameras(false);
            Assert.IsTrue(Cams().All(c => c.targetTexture == null), "no camera keeps an image");
            Assert.IsTrue(images.All(t => t == null), "and the images themselves are destroyed");

            view.ShowCameras(true);
            AssertImages(0);
        }

        [Test]
        public void CamerasFollowTheShuttleWhenItMovesAndTurns()
        {
            view.ShowCameras(true);
            Frame();
            var before = Cam("Front").transform.position;
            view.Show(Snapshot(90, 10, 250));
            Frame();
            Assert.Greater(Vector3.Distance(Cam("Front").transform.position, before), 200, "went with the shuttle");
            Assert.Greater(Vector3.Dot(Cam("Front").transform.forward, Vector3.forward), 0.999f, "front now looks north");
            AssertMounted();
        }

        [Test]
        public void HidingCamerasBringsThe3DViewBack()
        {
            view.ShowCameras(true);
            Frame();
            view.ShowCameras(false);
            Assert.IsFalse(view.CamerasShown);
            Assert.AreEqual(DisplayStyle.None, Panel().style.display.value);
            Assert.IsTrue(Cams().All(c => !c.enabled));
            Assert.IsTrue(WorldCamera().enabled, "the 3D view draws again");
        }

        [Test]
        public void CamerasCloseWithoutAShuttleToWatch()
        {
            view.ShowCameras(true);
            Frame();
            var changed = 0;
            view.ViewChanged += () => changed++;
            var gone = Snapshot();
            gone.Vehicles.RemoveAll(v => v.VehicleId == Shuttle);
            view.Show(gone);
            Frame();
            Assert.IsFalse(view.CamerasShown, "the watched shuttle left the session");
            Assert.IsTrue(Cams().All(c => !c.enabled));
            Assert.Greater(changed, 0, "the workspace is told, so its Cameras buttons go back");

            view.Show(Snapshot());
            view.ShowMap(true);
            view.OpenCameras();
            Assert.IsTrue(view.CamerasShown, "opened from the map");
            Assert.IsFalse(view.ShowingMap);
            Frame();
            view.Show(gone);
            Frame();
            Assert.IsFalse(view.CamerasShown);
            Assert.IsFalse(view.ShowingMap, "closing on its own stays in the 3D view, wherever they were opened from");
            view.ShowMap(false);

            view.Show(Snapshot());
            view.SelectEntity(Shuttle);
            view.ShowCameras(true);
            Frame();
            view.ShowMap(true);
            Frame();
            Assert.IsFalse(view.CamerasShown, "the 2D map has no shuttle cameras");
        }

        // Picking another shuttle in the fleet table or the Fleet List moves the open cameras to it,
        // so there is no need to hide them first; clearing the selection keeps that feed.
        [Test]
        public void PickingAnotherShuttleSwitchesTheFeed()
        {
            view.ShowCameras(true);
            Frame();
            var other = Snapshot().Vehicles.First(v => v.VehicleId != Shuttle).VehicleId;
            view.PickShuttle(other);
            Frame();
            Assert.IsTrue(view.CamerasShown);
            Assert.AreEqual(other, view.WatchedId, "now watching the shuttle just picked");
            AssertMounted(other);

            view.PickShuttle(null);
            Frame();
            Assert.IsTrue(view.CamerasShown, "clearing the selection does not close them");
            Assert.AreEqual(other, view.WatchedId, "and keeps the shuttle being watched");
            AssertMounted(other);
        }

        // A shuttle the app selects by itself, such as one held at a closure, leaves the feed alone
        [Test]
        public void SelectingAShuttleInCodeKeepsTheFeed()
        {
            view.ShowCameras(true);
            Frame();
            var other = Snapshot().Vehicles.First(v => v.VehicleId != Shuttle).VehicleId;
            view.SelectEntity(other);
            Frame();
            Assert.IsTrue(view.CamerasShown);
            Assert.AreEqual(Shuttle, view.WatchedId, "still watching the same shuttle");
            AssertMounted(Shuttle);
        }

        // A shuttle with no place in the 3D city cannot be watched: the cameras stay where they are
        [Test]
        public void PickingAShuttleNotInTheCityKeepsTheFeed()
        {
            view.ShowCameras(true);
            Frame();
            view.PickShuttle("shuttle-99");
            Frame();
            Assert.IsTrue(view.CamerasShown, "the cameras stay open");
            Assert.AreEqual(Shuttle, view.WatchedId, "on the shuttle being watched");
            AssertMounted();
        }

        // Picking the shuttle already being watched changes nothing
        [Test]
        public void RepickingTheWatchedShuttleLeavesTheCamerasAlone()
        {
            view.ShowCameras(true);
            Frame();
            Cam("Front").farClipPlane = 123;   // a rebuild copies the 3D view's distance over this
            view.PickShuttle(Shuttle);
            Assert.AreEqual(123f, Cam("Front").farClipPlane, "the cameras were not set up again");
            Assert.AreEqual(Shuttle, view.WatchedId);
        }

        [Test]
        public void TheHintNamesTheShuttleAndCameraAtOnce()
        {
            Assume.That(view.Ready, "the one-building city is shown");
            view.ShowCameras(true);
            StringAssert.Contains($"Shuttle cameras · {Shuttle} · Front", view.Diagnostic, "on opening");
            view.SelectCamera(1);
            StringAssert.Contains($"Shuttle cameras · {Shuttle} · Rear", view.Diagnostic, "on switching, not at the next snapshot");
            view.ShowCameras(false);
            StringAssert.StartsWith("3D city", view.Diagnostic, "on closing");
        }

        // Following a shuttle does not move the hidden 3D view behind the cameras, and carries on
        // when they close.
        [Test]
        public void FollowingWaitsBehindTheCameras()
        {
            view.FollowSelected();
            Frame();
            view.ShowCameras(true);
            var before = WorldCamera().transform.position;
            view.Show(Snapshot(90, 10, 250));
            Frame();
            Assert.AreEqual(0, Vector3.Distance(WorldCamera().transform.position, before), 1e-3f, "the hidden 3D view stays put");

            view.ShowCameras(false);
            Frame();
            Assert.Greater(Vector3.Distance(WorldCamera().transform.position, before), 100, "following carries on");
        }

        // The traffic is culled against every camera that draws this frame: a car is drawn if any
        // of them can see it, and none is drawn when none of them draws.
        [Test]
        public void TrafficIsDrawnIfAnyDrawingCameraCanSeeIt()
        {
            var traffic = new CityTrafficRenderer();
            var front = new GameObject("Front").AddComponent<Camera>();
            var rear = new GameObject("Rear").AddComponent<Camera>();
            try
            {
                foreach (var c in new[] { front, rear }) { c.aspect = 16f / 9; c.fieldOfView = 60; c.farClipPlane = 300; }
                rear.transform.rotation = Quaternion.Euler(0, 180, 0);
                var ahead = new Vector3(0, 0, 50);
                var behind = new Vector3(0, 0, -50);
                var aside = new Vector3(200, 0, 0);
                var cams = new System.Collections.Generic.List<Camera> { front, rear };
                bool Seen(Vector3 p)
                {
                    traffic.SetFrustums(cams);
                    return traffic.InView(p);
                }

                Assert.IsTrue(Seen(ahead), "seen by the front camera");
                Assert.IsTrue(Seen(behind), "seen by the rear camera");
                Assert.IsFalse(Seen(aside), "seen by neither");

                rear.enabled = false;
                Assert.IsTrue(Seen(ahead));
                Assert.IsFalse(Seen(behind), "a camera that does not draw this frame does not count");

                front.enabled = false;
                Assert.IsFalse(Seen(ahead), "no camera draws, so no car is drawn");
            }
            finally
            {
                traffic.Dispose();
                UnityEngine.Object.DestroyImmediate(front.gameObject);
                UnityEngine.Object.DestroyImmediate(rear.gameObject);
            }
        }

        [Test]
        public void OverviewAndFollowSelectedPutThe3DViewBack()
        {
            view.ShowCameras(true);
            view.Overview();
            Assert.IsFalse(view.CamerasShown, "Overview needs the 3D view");
            Assert.IsTrue(WorldCamera().enabled);

            view.ShowCameras(true);
            view.FollowSelected();
            Assert.IsFalse(view.CamerasShown, "so does Follow selected");
            Assert.IsTrue(WorldCamera().enabled);
        }

        [Test]
        public void CamerasAreDrawnWithTheTraffic()
        {
            var traffic = (System.Collections.Generic.List<Camera>)typeof(WorldView)
                .GetField("trafficCameras", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(view);
            foreach (var c in Cams()) Assert.Contains(c, traffic);
        }

        // Pressing Cameras from the 2D map in a session whose city is still downloading: the
        // button opens the camera view straight away, so it should stay open once the city arrives.
        [UnityTest]
        public IEnumerator CamerasPressedWhileTheCityLoadsStayOpen()
        {
            UnityEngine.Object.DestroyImmediate(rig);
            var slow = new SlowBuilding();
            Build(slow);
            view.Show(Snapshot());
            view.SelectEntity(Shuttle);
            // What the Cameras button does
            view.ShowMap(false);
            view.ShowCameras(true);
            Frame();
            slow.Release();
            var deadline = Time.realtimeSinceStartup + 10;
            Func<bool> arrived = () => rig.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Shuttle " + Shuttle);
            while (!arrived() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(arrived(), "the city loaded");
            Frame();
            Assert.IsTrue(view.CamerasShown, "the camera view survived the city load");
            Assert.IsFalse(WorldCamera().enabled, "and still covers the 3D view");
        }

        [Test]
        public void DisposeRemovesTheCameras()
        {
            var cameraRig = new ShuttleCameraRig(rig.transform);
            var objects = cameraRig.Cameras.Select(c => c.gameObject).ToArray();
            cameraRig.Dispose();
            Assert.IsTrue(objects.All(o => o == null), "camera objects destroyed");
        }
    }
}
