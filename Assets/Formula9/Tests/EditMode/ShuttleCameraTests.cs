/*
 * Unit tests for the shuttle camera panel (ShuttleCameraRig.cs, WorldView.cs).
 *
 * Author: Choo Jian Wei
 * Date Created: 10/11/2026
 * Last Modified: 10/11/2026
 *
 * Description:
 *     Builds the real WorldView with the training layout, a one-building city and the shared
 *     planar snapshot, then checks that the camera panel starts hidden with its six cameras off,
 *     that showing it switches them on and mounts them around the selected shuttle, that they
 *     follow the shuttle as it moves and turns, and that the panel closes itself when there is
 *     no shuttle left to watch.
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
    //     This class tests the shuttle camera panel against a real WorldView.
    public class ShuttleCameraTests
    {
        private const string Shuttle = "shuttle-01";
        private static readonly string[] Names = { "Front", "Rear", "Left", "Right", "Cabin", "Bird's eye" };

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

        // Runs one frame of the view, which Unity does not do in edit mode.
        private void Frame() =>
            typeof(WorldView).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(view, null);

        private VisualElement Panel() => root.Q(className: "camera-panel");
        private Camera Cam(string name) => rig.GetComponentsInChildren<Camera>(true).Single(c => c.name == "Shuttle camera " + name);
        private Camera[] Cams() => Names.Select(Cam).ToArray();
        private Transform ShuttleTransform() =>
            rig.GetComponentsInChildren<Transform>(true).First(t => t.name == "Shuttle " + Shuttle);

        // Checks every camera sits on its mount around the shuttle and looks out the right way.
        private void AssertMounted()
        {
            var s = ShuttleTransform();
            Assert.Greater(Vector3.Dot(Cam("Front").transform.forward, s.forward), 0.999f, "front looks ahead");
            Assert.Greater(Vector3.Dot(Cam("Front").transform.position - s.position, s.forward), 6f, "front is at the nose");
            Assert.Greater(Vector3.Dot(Cam("Rear").transform.forward, -s.forward), 0.999f, "rear looks back");
            Assert.Less(Vector3.Dot(Cam("Rear").transform.position - s.position, s.forward), -6f, "rear is at the tail");
            Assert.Greater(Vector3.Dot(Cam("Left").transform.forward, -s.right), 0.999f, "left looks left");
            Assert.Greater(Vector3.Dot(Cam("Right").transform.forward, s.right), 0.999f, "right looks right");
            Assert.Greater(Vector3.Dot(Cam("Cabin").transform.forward, -s.forward), 0.9f, "cabin looks back into the shuttle");
            Assert.Less(Cam("Cabin").transform.forward.y, 0, "cabin tilts down");
            Assert.Greater(Vector3.Dot(Cam("Bird's eye").transform.forward, Vector3.down), 0.999f, "bird's eye looks straight down");
            Assert.AreEqual(s.position.y + 25, Cam("Bird's eye").transform.position.y, 1e-3f, "bird's eye is 25 m up");
            Assert.IsTrue(Cam("Bird's eye").orthographic, "bird's eye is orthographic");
        }

        [Test]
        public void PanelStartsHiddenWithSixLabelledTilesAndCamerasOff()
        {
            Assert.AreEqual(DisplayStyle.None, Panel().style.display.value, "panel hidden");
            Assert.IsFalse(view.CamerasShown);
            var labels = Panel().Query<Label>(className: "camera-label").ToList().Select(l => l.text).ToArray();
            CollectionAssert.AreEqual(Names, labels, "one labelled tile per camera, in order");
            Assert.IsTrue(Cams().All(c => !c.enabled), "cameras off while nobody watches");
            Assert.IsTrue(Cams().All(c => c.targetTexture != null && c.targetTexture.width == 320 && c.targetTexture.height == 180));
        }

        [Test]
        public void ShowingCamerasSwitchesThemOnAndMountsThemOnTheShuttle()
        {
            Assert.IsTrue(view.CanShowCameras, "a selected shuttle in the 3D city can be watched");
            view.ShowCameras(true);
            Frame();
            Assert.IsTrue(view.CamerasShown, "still open after a frame");
            Assert.AreEqual(DisplayStyle.Flex, Panel().style.display.value);
            Assert.IsTrue(Cams().All(c => c.enabled), "cameras on");
            AssertMounted();
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
        public void HidingCamerasSwitchesThemOff()
        {
            view.ShowCameras(true);
            Frame();
            view.ShowCameras(false);
            Assert.IsFalse(view.CamerasShown);
            Assert.AreEqual(DisplayStyle.None, Panel().style.display.value);
            Assert.IsTrue(Cams().All(c => !c.enabled));
        }

        [Test]
        public void PanelClosesItselfWithoutAShuttleToWatch()
        {
            view.ShowCameras(true);
            Frame();
            view.SelectEntity(null);
            Frame();
            Assert.IsFalse(view.CamerasShown, "nothing selected");
            Assert.IsTrue(Cams().All(c => !c.enabled));

            view.SelectEntity(Shuttle);
            view.ShowCameras(true);
            Frame();
            view.ShowMap(true);
            Frame();
            Assert.IsFalse(view.CamerasShown, "the 2D map has no shuttle cameras");
        }

        [Test]
        public void CamerasAreDrawnWithTheTraffic()
        {
            var traffic = (System.Collections.Generic.List<Camera>)typeof(WorldView)
                .GetField("trafficCameras", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(view);
            foreach (var c in Cams()) Assert.Contains(c, traffic);
        }

        // Pressing Cameras from the 2D map in a session whose city is still downloading: the
        // button opens the panel straight away, so it should stay open once the city arrives.
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
            Assert.IsTrue(view.CamerasShown, "the panel survived the city load");
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
