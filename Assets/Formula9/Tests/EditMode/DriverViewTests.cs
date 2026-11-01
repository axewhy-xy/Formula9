/*
 * Unit tests for the driver view camera (WorldView.cs, CityView.cs).
 *
 * Author: Choo Jian Wei
 * Date Created: 31/10/2026
 * Last Modified: 31/10/2026
 *
 * Description:
 *     Task 4, the first-person view camera. Builds the real WorldView with the training layout, a
 *     one-building city and the shared planar snapshot, then checks that Driver view puts the
 *     camera at the selected shuttle's eye point looking along its heading, that it keeps up as the
 *     shuttle moves and turns, that looking around turns the view without moving the eye, and that
 *     Overview and a new session leave it. Shuttles jump straight to a pose more than 200 m away,
 *     so the tests need no real time to pass.
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
    // Class: DriverViewTests
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class tests the Driver view camera against a real WorldView.
    public class DriverViewTests
    {
        private const string Shuttle = "shuttle-01";
        private const float EyeHeightM = 1.5f, EyeForwardM = 5.5f;

        // -----------------------------------------------------------------------------------
        // Class: OneBuilding
        // -----------------------------------------------------------------------------------
        // Description:
        //     This class serves a city of one building away from the shuttles, so the city loads
        //     at once without a backend.
        private sealed class OneBuilding : IHttpTransport
        {
            // -----------------------------------------------------------------------------------
            // Function: SendAsync
            // -----------------------------------------------------------------------------------
            // Description:
            //     This function answers every request with the one-building city.
            //
            // Args:
            //     request (HttpRequest): The request.
            //     cancellation (CancellationToken): Unused.
            //
            // Returns:
            //     Task<HttpResponse>: The city reply.
            public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellation = default) =>
                Task.FromResult(new HttpResponse { Status = 200, Body = "{\"buildings\":[{\"id\":\"way/1\",\"type\":\"retail\",\"name\":\"\",\"height_m\":30,\"rings_m\":[[[0,100],[10,100],[10,110],[0,110],[0,100]]]}]}" });
        }

        // -----------------------------------------------------------------------------------
        // Class: SlowBuilding
        // -----------------------------------------------------------------------------------
        // Description:
        //     This class holds the one-building city back until released, like a real download.
        private sealed class SlowBuilding : IHttpTransport
        {
            private readonly TaskCompletionSource<bool> released = new TaskCompletionSource<bool>();

            // -----------------------------------------------------------------------------------
            // Function: Release
            // -----------------------------------------------------------------------------------
            // Description:
            //     This function lets the requests finish.
            public void Release() => released.TrySetResult(true);

            // -----------------------------------------------------------------------------------
            // Function: SendAsync
            // -----------------------------------------------------------------------------------
            // Description:
            //     This function answers with the one-building city once released.
            //
            // Args:
            //     request (HttpRequest): The request.
            //     cancellation (CancellationToken): Unused.
            //
            // Returns:
            //     Task<HttpResponse>: The city reply.
            public async Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellation = default)
            {
                await released.Task;
                return await new OneBuilding().SendAsync(request, cancellation);
            }
        }

        private GameObject rig;
        private WorldView view;
        private Camera camera;

        // -----------------------------------------------------------------------------------
        // Function: Snapshot
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reads the shared planar snapshot and points shuttle-01 at a heading.
        //
        // Args:
        //     headingDeg (double): The shuttle's heading, degrees anticlockwise from east.
        //     x (double): The shuttle's x in metres.
        //     y (double): The shuttle's y in metres.
        //
        // Returns:
        //     FullSnapshot: The snapshot.
        private static FullSnapshot Snapshot(double headingDeg = 58.9, double x = 10, double y = 0)
        {
            var path = Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/snapshot.json");
            var snapshot = (FullSnapshot)ContractParser.ParseMessage(File.ReadAllText(path));
            var vehicle = snapshot.Vehicles.First(v => v.VehicleId == Shuttle);
            vehicle.HeadingDeg = headingDeg;
            vehicle.Position.XM = x; vehicle.Position.YM = y;
            // The fixture's depot is at the origin among the shuttles; its charger posts would stand
            // right in front of the driver, and these tests are about the shuttle's own body
            foreach (var depot in snapshot.Depots) depot.Position.XM = 1000;
            return snapshot;
        }

        // -----------------------------------------------------------------------------------
        // Function: SetUp
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function builds a WorldView in the training layout, shows the snapshot in the
        //     3D city and selects shuttle-01.
        [SetUp]
        public void SetUp()
        {
            var root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Formula9/UI/Formula9App.uxml").CloneTree();
            rig = new GameObject("Driver view test");
            view = rig.AddComponent<WorldView>();
            var backend = new Uri("http://backend.test");
            view.Attach(root.Q("world"), new SceneLoader(new OneBuilding(), backend), null, null, new CityClient(new OneBuilding(), backend));
            view.Show(Snapshot());
            view.ShowMap(false);
            Assert.IsFalse(view.ShowingMap, "the 3D city is shown");
            camera = rig.GetComponentsInChildren<Camera>(true).Single(c => c.name == "World Camera");
            view.SelectEntity(Shuttle);
            Frame();
        }

        // -----------------------------------------------------------------------------------
        // Function: TearDown
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function removes the view, its city and its camera.
        [TearDown]
        public void TearDown() => UnityEngine.Object.DestroyImmediate(rig);

        // -----------------------------------------------------------------------------------
        // Function: Frame
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function runs one frame of the view, which Unity does not do in edit mode.
        private void Frame() =>
            typeof(WorldView).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(view, null);

        // -----------------------------------------------------------------------------------
        // Function: ShuttleTransform
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function finds the shuttle's drawn marker.
        //
        // Returns:
        //     Transform: shuttle-01 as the city draws it.
        private Transform ShuttleTransform() =>
            rig.GetComponentsInChildren<Transform>(true).First(t => t.name == "Shuttle " + Shuttle);

        // -----------------------------------------------------------------------------------
        // Function: AssertAtEye
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function checks that the camera sits at the shuttle's eye point, 1.5 m above
        //     its centre and 5.5 m forward.
        //
        // Args:
        //     headingDeg (float): The heading the shuttle should be facing.
        private void AssertAtEye(float headingDeg)
        {
            var heading = headingDeg * Mathf.Deg2Rad;
            var ahead = new Vector3(Mathf.Cos(heading), 0, Mathf.Sin(heading));
            var shuttle = ShuttleTransform();
            Assert.Greater(Vector3.Dot(shuttle.forward, ahead), 0.999f, "the shuttle itself faces its heading");
            var eye = shuttle.position + Vector3.up * EyeHeightM + ahead * EyeForwardM;
            Assert.Less(Vector3.Distance(camera.transform.position, eye), 1e-3f, "camera at the eye point");
            Assert.AreEqual(4f, camera.transform.position.y, 1e-3f, "4 m above the ground");
        }

        // -----------------------------------------------------------------------------------
        // Function: SetLook
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function sets how far the trainee has looked around, as dragging would.
        //
        // Args:
        //     yawDeg (float): Degrees turned right.
        //     pitchDeg (float): Degrees tilted down.
        private void SetLook(float yawDeg, float pitchDeg)
        {
            typeof(WorldView).GetField("lookYaw", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(view, yawDeg);
            typeof(WorldView).GetField("lookPitch", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(view, pitchDeg);
        }

        // -----------------------------------------------------------------------------------
        // Function: DriverViewSitsAtTheShuttlesEyeLookingAlongItsHeading
        // -----------------------------------------------------------------------------------
        // Description:
        //     Driver view puts the camera at the selected shuttle's eye point, looking where the
        //     shuttle is heading.
        [Test]
        public void DriverViewSitsAtTheShuttlesEyeLookingAlongItsHeading()
        {
            view.DriveSelected();
            Frame();
            AssertAtEye(58.9f);
            Assert.Greater(Vector3.Dot(camera.transform.forward, ShuttleTransform().forward), 0.9999f, "looking straight ahead");
        }

        // -----------------------------------------------------------------------------------
        // Function: DriverViewKeepsUpWhenTheShuttleMovesAndTurns
        // -----------------------------------------------------------------------------------
        // Description:
        //     When the next snapshot moves the shuttle 250 m and turns it to face north, the
        //     camera moves and turns with it on the next frame.
        [Test]
        public void DriverViewKeepsUpWhenTheShuttleMovesAndTurns()
        {
            view.DriveSelected();
            Frame();
            var before = camera.transform.position;
            view.Show(Snapshot(90, 10, 250));
            Frame();
            AssertAtEye(90);
            Assert.Greater(Vector3.Distance(camera.transform.position, before), 200, "the camera went with the shuttle");
            Assert.Greater(Vector3.Dot(camera.transform.forward, Vector3.forward), 0.999f, "now looking north (Unity +z)");
        }

        // -----------------------------------------------------------------------------------
        // Function: LookingAroundTurnsTheViewButNotTheEye
        // -----------------------------------------------------------------------------------
        // Description:
        //     Looking around turns the view relative to the shuttle while the eye stays put, and
        //     entering Driver view again starts looking straight ahead.
        [Test]
        public void LookingAroundTurnsTheViewButNotTheEye()
        {
            view.DriveSelected();
            Frame();
            var eye = camera.transform.position;
            SetLook(90, 0);
            Frame();
            Assert.Less(Vector3.Distance(camera.transform.position, eye), 1e-3f, "turning the head does not move the eye");
            Assert.Greater(Vector3.Dot(camera.transform.forward, ShuttleTransform().right), 0.999f, "90 degrees right of the shuttle");
            SetLook(0, 30);
            Frame();
            Assert.AreEqual(30, Vector3.Angle(camera.transform.forward, ShuttleTransform().forward), 1e-2f, "tilted 30 degrees");
            Assert.Less(camera.transform.forward.y, 0, "positive pitch looks down");
            view.DriveSelected();
            Frame();
            Assert.Greater(Vector3.Dot(camera.transform.forward, ShuttleTransform().forward), 0.9999f, "a new Driver view looks ahead again");
        }

        // -----------------------------------------------------------------------------------
        // Function: OverviewLeavesDriverView
        // -----------------------------------------------------------------------------------
        // Description:
        //     Overview takes the camera out of the shuttle, and it stays out on later frames.
        [Test]
        public void OverviewLeavesDriverView()
        {
            view.DriveSelected();
            Frame();
            var eye = camera.transform.position;
            view.Overview();
            Frame();
            Frame();
            Assert.Greater(Vector3.Distance(camera.transform.position, eye), 100, "back over the city");
        }

        // -----------------------------------------------------------------------------------
        // Function: ANewSessionLeavesDriverView
        // -----------------------------------------------------------------------------------
        // Description:
        //     Joining another session leaves Driver view, even when it has a shuttle with the
        //     same ID, so the camera is not left inside a shuttle of the old session.
        [Test]
        public void ANewSessionLeavesDriverView()
        {
            view.DriveSelected();
            Frame();
            var next = Snapshot(58.9, 10, 0);
            next.SessionId = next.Session.SessionId = "session-other";
            view.Show(next);
            view.ShowMap(false);
            view.SelectEntity(Shuttle);
            var outside = camera.transform.position = new Vector3(0, 500, 0);
            Frame();
            Assert.AreEqual(outside, camera.transform.position, "the camera is no longer placed in the shuttle");
        }

        // -----------------------------------------------------------------------------------
        // Function: DriverViewSeesPastItsOwnShuttle
        // -----------------------------------------------------------------------------------
        // Description:
        //     The real render from the eye shows the sky ahead, not the back of the shuttle's own
        //     dark windscreen. Skipped without a graphics device, as under -nographics.
        [Test]
        public void DriverViewSeesPastItsOwnShuttle()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("needs a graphics device");
            view.DriveSelected();
            Frame();
            var texture = new RenderTexture(64, 64, 24);
            try
            {
                camera.targetTexture = texture;
                camera.Render();
                RenderTexture.active = texture;
                var image = new Texture2D(64, 64, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
                image.Apply();
                RenderTexture.active = null;
                var above = image.GetPixel(32, 56);
                UnityEngine.Object.DestroyImmediate(image);
                Assert.AreEqual(camera.backgroundColor.b, above.b, 0.05f, $"sky above the horizon ahead, was {above}");
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = null;
                texture.Release();
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: LeavingDriverViewShowsTheShuttleAgain
        // -----------------------------------------------------------------------------------
        // Description:
        //     The shuttle the camera sits in is hidden only while it does: Overview, Follow
        //     selected and choosing another shuttle all bring it back.
        [Test]
        public void LeavingDriverViewShowsTheShuttleAgain()
        {
            var shuttle = ShuttleTransform().gameObject;
            view.DriveSelected();
            Frame();
            Assert.IsFalse(shuttle.activeSelf, "hidden from the inside");
            view.Overview();
            Assert.IsTrue(shuttle.activeSelf, "Overview");
            view.DriveSelected();
            Frame();
            view.FollowSelected();
            Frame();
            Assert.IsTrue(shuttle.activeSelf, "Follow selected");
            view.DriveSelected();
            Frame();
            view.SelectEntity(null);
            Frame();
            Assert.IsTrue(shuttle.activeSelf, "no selection");
        }

        // -----------------------------------------------------------------------------------
        // Function: FollowSelectedLeavesDriverView
        // -----------------------------------------------------------------------------------
        // Description:
        //     Follow selected takes the camera out of the shuttle, to watch it from above.
        [Test]
        public void FollowSelectedLeavesDriverView()
        {
            view.DriveSelected();
            Frame();
            view.FollowSelected();
            Frame();
            Assert.Greater(camera.transform.position.y, 20, "above the shuttle");
        }

        // -----------------------------------------------------------------------------------
        // Function: LosingTheShuttleLeavesDriverView
        // -----------------------------------------------------------------------------------
        // Description:
        //     When a snapshot no longer has the shuttle, its marker goes and the camera returns
        //     over the city instead of staying where the shuttle last was.
        [Test]
        public void LosingTheShuttleLeavesDriverView()
        {
            view.DriveSelected();
            Frame();
            var eye = camera.transform.position;
            var next = Snapshot();
            next.Vehicles.RemoveAll(v => v.VehicleId == Shuttle);
            view.Show(next);
            Frame();
            Assert.IsFalse(rig.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Shuttle " + Shuttle), "marker removed");
            Assert.Greater(Vector3.Distance(camera.transform.position, eye), 100, "back over the city");
        }

        // -----------------------------------------------------------------------------------
        // Function: DriverViewFromTheMapOpensTheCity
        // -----------------------------------------------------------------------------------
        // Description:
        //     A planar session starts on its 2D map, and Driver view goes to the 3D city from there.
        [Test]
        public void DriverViewFromTheMapOpensTheCity()
        {
            view.ShowMap(true);
            view.DriveSelected();
            Frame();
            Assert.IsFalse(view.ShowingMap, "the 3D city is shown");
            AssertAtEye(58.9f);
        }

        // -----------------------------------------------------------------------------------
        // Function: DriverViewWaitsForTheCityToLoad
        // -----------------------------------------------------------------------------------
        // Description:
        //     The first Driver view may have to load the buildings. It stays on through that and
        //     takes its seat when they arrive.
        //
        // Returns:
        //     IEnumerator: The test's frames.
        [UnityTest]
        public IEnumerator DriverViewWaitsForTheCityToLoad()
        {
            var transport = new SlowBuilding();
            var root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Formula9/UI/Formula9App.uxml").CloneTree();
            var slowRig = new GameObject("Slow driver view test");
            try
            {
                var slow = slowRig.AddComponent<WorldView>();
                var backend = new Uri("http://backend.test");
                slow.Attach(root.Q("world"), new SceneLoader(transport, backend), null, null, new CityClient(transport, backend));
                slow.Show(Snapshot());
                slow.SelectEntity(Shuttle);
                slow.DriveSelected();
                var update = typeof(WorldView).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance);
                update.Invoke(slow, null);
                transport.Release();
                var deadline = Time.realtimeSinceStartup + 10;
                Func<bool> arrived = () => slowRig.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Shuttle " + Shuttle);
                while (!arrived() && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsTrue(arrived(), "the city loaded");
                update.Invoke(slow, null);
                var eye = slowRig.GetComponentsInChildren<Camera>(true).Single(c => c.name == "World Camera").transform.position;
                Assert.AreEqual(4f, eye.y, 1e-3f, "in the shuttle, 4 m above the ground");
            }
            finally { UnityEngine.Object.DestroyImmediate(slowRig); }
        }

        // -----------------------------------------------------------------------------------
        // Function: DriverViewNeedsASelectedShuttle
        // -----------------------------------------------------------------------------------
        // Description:
        //     Without a selected shuttle, Driver view does nothing.
        [Test]
        public void DriverViewNeedsASelectedShuttle()
        {
            view.SelectEntity(null);
            var before = camera.transform.position = new Vector3(0, 500, 0);
            view.DriveSelected();
            Frame();
            Assert.AreEqual(before, camera.transform.position);
        }

        // -----------------------------------------------------------------------------------
        // Function: TheDriverViewButtonSitsWithTheOtherCameraButtons
        // -----------------------------------------------------------------------------------
        // Description:
        //     The training layout has a Driver view button in the same row as Follow selected
        //     and Overview.
        [Test]
        public void TheDriverViewButtonSitsWithTheOtherCameraButtons()
        {
            var root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Formula9/UI/Formula9App.uxml").CloneTree();
            var drive = root.Q<Button>("drive-selected");
            Assert.IsNotNull(drive);
            Assert.AreEqual("Driver view", drive.text);
            Assert.AreSame(root.Q<Button>("follow-selected").parent, drive.parent);
            Assert.AreSame(root.Q<Button>("camera-overview").parent, drive.parent);
        }
    }
}
