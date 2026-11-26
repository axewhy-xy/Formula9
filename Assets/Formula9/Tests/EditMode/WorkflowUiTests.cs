using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.Session;
using Formula9.UI;
using Formula9.World;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Formula9.Tests
{
    public class WorkflowUiTests
    {
        private static JObject Fixture(string name) => JObject.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures", name + ".json")));

        private sealed class Backend : IHttpTransport
        {
            public Func<HttpRequest, Task<HttpResponse>> Reply;
            public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellation = default) => Reply(request);
        }

        // Ready assessments can wait without a new snapshot while their stream sends heartbeats.
        [TestCase(false)]
        [TestCase(true)]
        public async Task ReadyAssessmentRoutingRemainsUsableWithHeartbeatsOrSuccessfulPolling(bool streaming)
        {
            var raw = Fixture("snapshot");
            raw["session"]["status"] = "READY";
            raw["scenario"]["assessment_duration_s"] = 120;
            raw["disruptions"] = new JArray();
            var routeId = (string)raw["routes"][0]["route_id"];
            var context = Fixture("route_editor_context");
            context["route_id"] = routeId;
            context["route_revision"] = raw["routes"][0]["revision"].DeepClone();
            var checkedRoute = Fixture("route_edit_preview");
            checkedRoute["route_id"] = routeId;
            checkedRoute["route_revision"] = context["route_revision"].DeepClone();
            checkedRoute["closure_revisions"] = new JObject();
            var snapshotRequests = 0;
            var failPoll = false;
            var backend = new Backend();
            backend.Reply = request =>
            {
                var path = request.Url.AbsolutePath;
                if (path.EndsWith("/snapshot"))
                {
                    snapshotRequests++;
                    return Task.FromResult(failPoll ? new HttpResponse { Failure = HttpFailure.Incomplete } :
                        new HttpResponse { Status = 200, Body = raw.ToString() });
                }
                if (path.EndsWith("/editor")) return Task.FromResult(new HttpResponse { Status = 200, Body = context.ToString() });
                if (path.EndsWith("/route-previews"))
                {
                    checkedRoute["preview_revision"] = JObject.Parse(request.Body)["draft_revision"].DeepClone();
                    return Task.FromResult(new HttpResponse { Status = 200, Body = checkedRoute.ToString() });
                }
                if (path.EndsWith("/events"))
                {
                    var replay = Fixture("event_replay");
                    replay["events"] = new JArray(); replay["has_more"] = false;
                    replay["after_sequence"] = raw["event_sequence"].DeepClone();
                    replay["next_sequence"] = raw["event_sequence"].DeepClone();
                    return Task.FromResult(new HttpResponse { Status = 200, Body = replay.ToString() });
                }
                return Task.FromResult(new HttpResponse { Failure = HttpFailure.Incomplete });
            };
            var auth = new AuthSession(); auth.Begin("trainee", "token", 3600);
            var api = new TrainingApi(backend, auth, new Uri("http://backend.test"));
            var socket = new SessionControllerTests.FakeStreamSocket();
            api.StreamSockets = () => socket;
            var clock = DateTime.UtcNow;
            var session = new SessionController(api, auth, now: () => clock) { UseStream = streaming };
            var root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Formula9/UI/Formula9App.uxml").CloneTree();
            var rig = new GameObject("Ready assessment routing test");
            var world = rig.AddComponent<WorldView>();
            var errors = new List<Exception>();
            async void Run(Task task) { try { await task; } catch (Exception error) { errors.Add(error); } }
            try
            {
                world.Attach(root.Q("world"), new SceneLoader(backend, new Uri("http://backend.test")));
                var workspace = new TrainingWorkspace(root, api, session, world, Run, _ => { });
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                void Update() => typeof(TrainingWorkspace).GetMethod("UpdateActions", flags).Invoke(workspace, null);
                void AgeSnapshot() => typeof(TrainingWorkspace).GetField("acceptedAt", flags).SetValue(workspace, Time.realtimeSinceStartup - 10);
                await session.JoinAsync((string)raw["session_id"]);
                if (streaming)
                {
                    session.PumpStream(false);
                    socket.Push(new JObject { ["type"] = "snapshot", ["session_id"] = raw["session_id"], ["snapshot"] = raw.DeepClone() }.ToString());
                    session.PumpStream(false);
                    clock += TimeSpan.FromSeconds(10);
                    socket.Push(new JObject { ["type"] = "heartbeat", ["session_id"] = raw["session_id"] }.ToString());
                    session.PumpStream(false);
                    Assert.IsTrue(session.Streaming);
                }
                AgeSnapshot(); Update();
                var suggest = root.Q<Button>("route-suggest");
                Assert.AreEqual(streaming, suggest.enabledSelf, "A live stream keeps the unchanged Ready snapshot current.");
                var polled = snapshotRequests;
                await session.RefreshAsync();
                Assert.AreEqual(polled + (streaming ? 0 : 1), snapshotRequests);
                Assert.IsTrue(suggest.enabledSelf, string.Join("; ", errors.Select(e => e.Message)));
                Assert.IsTrue(root.Q<Button>("route-start").enabledSelf);
                // Start lives in the outcome card, which stays even with no problem to report
                Assert.AreEqual(DisplayStyle.Flex, root.Q("route-outcome").style.display.value, "Start is on the page");
                Assert.AreEqual(DisplayStyle.Flex, root.Q<Button>("route-start").style.display.value);
                typeof(TrainingWorkspace).GetMethod("SuggestDetour", flags).Invoke(workspace, null);
                root.Q<TextField>("route-reason").value = "Use the checked detour";
                Update();
                var approve = root.Q<Button>("route-approve");
                Assert.IsTrue(approve.enabledSelf, root.Q<Label>("route-apply-note").text);
                AgeSnapshot(); Update();
                Assert.AreEqual(streaming, approve.enabledSelf);
                if (!streaming)
                {
                    StringAssert.Contains("fresh session update", root.Q<Label>("route-apply-note").text);
                    await session.RefreshAsync();
                    Assert.IsTrue(approve.enabledSelf);
                }
                var preview = (RouteEditPreview)typeof(TrainingWorkspace).GetField("preview", flags).GetValue(workspace);
                preview.RouteRevision++; Update();
                Assert.IsFalse(approve.enabledSelf, "A heartbeat never authorizes an outdated route preview.");
                StringAssert.Contains("session changed", root.Q<Label>("route-apply-note").text);
                preview.RouteRevision--; preview.Approvable = false; Update();
                StringAssert.StartsWith("Fix the problems", root.Q<Label>("route-apply-note").text);
                preview.Approvable = true;
                root.Q<TextField>("route-reason").value = ""; Update();
                Assert.IsFalse(approve.enabledSelf);
                StringAssert.StartsWith("Say why", root.Q<Label>("route-apply-note").text);
                root.Q<TextField>("route-reason").value = "Use the checked detour";
                if (streaming)
                {
                    clock += SessionController.ActionFreshnessLimit + TimeSpan.FromMilliseconds(1);
                    AgeSnapshot(); Update();
                    Assert.IsTrue(session.Streaming);
                    Assert.IsFalse(suggest.enabledSelf, "A connected socket cannot keep old state actionable.");
                    Assert.IsFalse(approve.enabledSelf);
                    StringAssert.Contains("fresh session update", root.Q<Label>("route-apply-note").text);
                    socket.Push(new JObject { ["type"] = "heartbeat", ["session_id"] = raw["session_id"] }.ToString());
                    session.PumpStream(false); Update();
                    Assert.IsTrue(approve.enabledSelf, "A new heartbeat confirms the unchanged Ready state.");
                }
                var selected = typeof(TrainingWorkspace).GetField("selectedVehicle", flags);
                var originalVehicle = selected.GetValue(workspace);
                selected.SetValue(workspace, null); Update();
                StringAssert.StartsWith("Select a shuttle", root.Q<Label>("route-apply-note").text);
                selected.SetValue(workspace, originalVehicle);
                var state = session.Store.Snapshot;
                state.Session.Status = "PAUSED"; Update();
                StringAssert.Contains("Resume", root.Q<Label>("route-apply-note").text);
                state.Session.Status = "RUNNING";
                var candidates = state.Candidates.ToList(); state.Candidates.Clear(); Update();
                StringAssert.StartsWith("No reroute is currently offered", root.Q<Label>("route-apply-note").text);
                typeof(TrainingWorkspace).GetField("rerouting", flags).SetValue(workspace, true); Update();
                StringAssert.StartsWith("The reroute offer is no longer available", root.Q<Label>("route-apply-note").text);
                typeof(TrainingWorkspace).GetField("rerouting", flags).SetValue(workspace, false);
                state.Candidates.AddRange(candidates); state.Session.Status = "READY"; Update();
                if (streaming)
                {
                    clock += SessionController.StreamSilenceLimit + TimeSpan.FromSeconds(1);
                    session.PumpStream(false);
                    Assert.IsFalse(session.Streaming);
                    Assert.IsTrue(socket.Disposed);
                    Assert.AreEqual(polled + 1, snapshotRequests, "A silent stream hands control back to polling.");
                    Assert.IsTrue(approve.enabledSelf, "The successful fallback poll restores route actions.");
                }
                failPoll = true;
                await session.RefreshAsync();
                Assert.IsFalse(suggest.enabledSelf);
                Assert.IsFalse(approve.enabledSelf);
                Assert.IsFalse(root.Q<Button>("route-start").enabledSelf);
                StringAssert.Contains("reconnect", root.Q<Label>("route-apply-note").text);
                Assert.IsEmpty(errors);
            }
            finally { session.Shutdown(); UnityEngine.Object.DestroyImmediate(rig); }
        }

        [TestCase("ARCHIVED")]
        [TestCase("COMPLETED")]
        [TestCase("FAILED")]
        public async Task ChargingFoldsTheStatusBarAndSetupOpensOnlyFromItsButton(string terminalState)
        {
            var raw = Fixture("snapshot"); raw["session"]["status"] = "RUNNING";
            var context = Fixture("route_editor_context");
            context["route_id"] = raw["routes"][0]["route_id"].DeepClone();
            context["route_revision"] = raw["routes"][0]["revision"].DeepClone();
            var backend = new Backend();
            backend.Reply = request =>
            {
                var path = request.Url.AbsolutePath;
                if (path.EndsWith("/snapshot")) return Task.FromResult(new HttpResponse { Status = 200, Body = raw.ToString() });
                if (path.EndsWith("/editor")) return Task.FromResult(new HttpResponse { Status = 200, Body = context.ToString() });
                if (path.EndsWith("/outcome")) return Task.FromResult(new HttpResponse { Failure = HttpFailure.Incomplete });
                var replay = Fixture("event_replay"); replay["events"] = new JArray(); replay["has_more"] = false;
                replay["after_sequence"] = raw["event_sequence"].DeepClone(); replay["next_sequence"] = raw["event_sequence"].DeepClone();
                return Task.FromResult(new HttpResponse { Status = 200, Body = replay.ToString() });
            };
            var auth = new AuthSession(); auth.Begin("trainee", "token", 3600);
            var api = new TrainingApi(backend, auth, new Uri("http://backend.test"));
            var session = new SessionController(api, auth);
            var root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Formula9/UI/Formula9App.uxml").CloneTree();
            var rig = new GameObject("Session toolbar test"); var world = rig.AddComponent<WorldView>();
            var errors = new List<Exception>();
            async void Run(Task task) { try { await task; } catch (Exception error) { errors.Add(error); } }
            try
            {
                world.Attach(root.Q("world"), new SceneLoader(backend, new Uri("http://backend.test")));
                var workspace = new TrainingWorkspace(root, api, session, world, Run, _ => { });
                var card = root.Q("content-card");
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var toggle = typeof(TrainingWorkspace).GetMethod("ToggleSessionTools", flags);
                Assert.IsTrue(card.ClassListContains("session-tools-hidden"), "closed until Create/join training session opens it");
                toggle.Invoke(workspace, null);
                Assert.IsFalse(card.ClassListContains("session-tools-hidden"));
                await session.JoinAsync((string)raw["session_id"]);
                Assert.IsTrue(card.ClassListContains("session-tools-hidden"), "joining closes it again");
                Assert.IsFalse(card.ClassListContains("session-compact"), "Fleet keeps its status bar");
                typeof(TrainingWorkspace).GetMethod("ShowPage", flags).Invoke(workspace, new object[] { 2 });
                Assert.IsTrue(card.ClassListContains("session-compact"), "Charging folds the extra status row and header spacing");
                toggle.Invoke(workspace, null);
                Assert.IsFalse(card.ClassListContains("session-tools-hidden"));
                Assert.IsFalse(card.ClassListContains("session-compact"));
                toggle.Invoke(workspace, null);
                Assert.IsTrue(card.ClassListContains("session-tools-hidden"), "explicitly closed before the session ends");
                raw["session"]["status"] = terminalState;
                raw["snapshot_id"] = Guid.NewGuid().ToString();
                await session.RefreshAsync();
                Assert.IsTrue(card.ClassListContains("session-tools-hidden"), "an ended attempt leaves setup closed; its button stays");
                Assert.IsTrue(card.ClassListContains("session-compact"));
                Assert.AreEqual(DisplayStyle.Flex, root.Q<Button>("session-toggle").style.display.value);
                Assert.IsEmpty(errors);
            }
            finally { session.Shutdown(); UnityEngine.Object.DestroyImmediate(rig); }
        }

        [Test]
        public async Task RoadsArrivingAfterARevisionChangeReloadBeforeEnablingTheEditor()
        {
            var raw = Fixture("snapshot"); raw["session"]["status"] = "READY";
            var routeId = (string)raw["routes"][0]["route_id"];
            var context = Fixture("route_editor_context");
            context["route_id"] = routeId; context["route_revision"] = raw["routes"][0]["revision"].DeepClone();
            var staleContext = context.ToString();
            var firstGraph = new TaskCompletionSource<HttpResponse>(); var graphs = 0;
            var backend = new Backend();
            backend.Reply = request =>
            {
                if (request.Url.AbsolutePath.EndsWith("/editor"))
                    return ++graphs == 1 ? firstGraph.Task : Task.FromResult(new HttpResponse { Status = 200, Body = context.ToString() });
                if (request.Url.AbsolutePath.EndsWith("/snapshot"))
                    return Task.FromResult(new HttpResponse { Status = 200, Body = raw.ToString() });
                var replay = Fixture("event_replay"); replay["events"] = new JArray(); replay["has_more"] = false;
                replay["after_sequence"] = raw["event_sequence"].DeepClone(); replay["next_sequence"] = raw["event_sequence"].DeepClone();
                return Task.FromResult(new HttpResponse { Status = 200, Body = replay.ToString() });
            };
            var auth = new AuthSession(); auth.Begin("trainee", "token", 3600);
            var api = new TrainingApi(backend, auth, new Uri("http://backend.test"));
            var session = new SessionController(api, auth);
            var root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Formula9/UI/Formula9App.uxml").CloneTree();
            var rig = new GameObject("Workflow UI test"); var world = rig.AddComponent<WorldView>();
            var errors = new List<Exception>();
            async void Run(Task task) { try { await task; } catch (Exception error) { errors.Add(error); } }
            try
            {
                world.Attach(root.Q("world"), new SceneLoader(backend, new Uri("http://backend.test")));
                var workspace = new TrainingWorkspace(root, api, session, world, Run, _ => { });
                await session.JoinAsync((string)raw["session_id"]);
                var suggest = root.Q<Button>("route-suggest");
                Assert.IsFalse(suggest.enabledSelf, "the first graph is still in flight");
                Assert.IsFalse(world.RouteEditing, "no graph, nothing to pick on the map");
                Assert.AreEqual(1, graphs);

                raw["snapshot_id"] = Guid.NewGuid().ToString();
                raw["routes"][0]["revision"] = (long)raw["routes"][0]["revision"] + 1;
                foreach (var vehicle in (JArray)raw["vehicles"]) vehicle["route_revision"] = raw["routes"][0]["revision"].DeepClone();
                context["route_revision"] = raw["routes"][0]["revision"].DeepClone();
                await session.RefreshAsync();
                firstGraph.SetResult(new HttpResponse { Status = 200, Body = staleContext });
                // The stale response cannot install a graph; the queued reload obtains the new one.
                for (var i = 0; i < 30 && graphs < 2; i++) await Task.Delay(10);
                Assert.AreEqual(2, graphs);
                Assert.IsTrue(suggest.enabledSelf, string.Join("; ", errors.Select(e => e.Message)));
                Assert.IsTrue(world.RouteEditing, "the map picks route nodes while the route is edited");
                Assert.IsEmpty(errors);
                CollectionAssert.AreEqual(new[] { "Fleet Overview", "Route Planning", "Charging", "Scenarios", "Reports" }, root.Q("nav-items").Query<Label>().ToList().Where(l => !l.ClassListContains("nav-badge")).Select(l => l.text).ToList());
            }
            finally { session.Shutdown(); UnityEngine.Object.DestroyImmediate(rig); }
        }

        // The route page's toggle shows the cameras of a shuttle of the chosen route, in place of
        // the 3D view, and keeps to the selected one when that is on the route.
        [Test]
        public async Task RoutePlanningTogglesTheCamerasOfTheRoutesShuttle()
        {
            var raw = Fixture("snapshot"); raw["session"]["status"] = "READY";
            // A second route, served by shuttle-05
            var routeB = (JObject)raw["routes"][0].DeepClone(); routeB["route_id"] = "route-b";
            ((JArray)raw["routes"]).Add(routeB);
            raw["vehicles"][4]["route_id"] = "route-b";
            var backend = new Backend();
            backend.Reply = request =>
            {
                var path = request.Url.AbsolutePath;
                if (path.EndsWith("/snapshot")) return Task.FromResult(new HttpResponse { Status = 200, Body = raw.ToString() });
                if (path.EndsWith("/editor") || path.EndsWith("/outcome")) return Task.FromResult(new HttpResponse { Failure = HttpFailure.Incomplete });
                if (path.Contains("/events"))
                {
                    var replay = Fixture("event_replay"); replay["events"] = new JArray(); replay["has_more"] = false;
                    replay["after_sequence"] = raw["event_sequence"].DeepClone(); replay["next_sequence"] = raw["event_sequence"].DeepClone();
                    return Task.FromResult(new HttpResponse { Status = 200, Body = replay.ToString() });
                }
                return Task.FromResult(new HttpResponse { Status = 200, Body = "{\"buildings\":[{\"id\":\"way/1\",\"type\":\"retail\",\"name\":\"\",\"height_m\":30,\"rings_m\":[[[0,100],[10,100],[10,110],[0,110],[0,100]]]}]}" });
            };
            var auth = new AuthSession(); auth.Begin("trainee", "token", 3600);
            var url = new Uri("http://backend.test");
            var api = new TrainingApi(backend, auth, url);
            var session = new SessionController(api, auth);
            var root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Formula9/UI/Formula9App.uxml").CloneTree();
            var rig = new GameObject("Route cameras test"); var world = rig.AddComponent<WorldView>();
            try
            {
                world.Attach(root.Q("world"), new SceneLoader(backend, url), null, null, new CityClient(backend, url));
                var workspace = new TrainingWorkspace(root, api, session, world, _ => { }, _ => { });
                Assert.IsNull(root.Q<Button>("route-driver"), "no driver view any more");
                var toggle = root.Q<Button>("route-cameras");
                var press = typeof(TrainingWorkspace).GetMethod("ToggleCameras", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                await session.JoinAsync((string)raw["session_id"]);
                world.Show(session.Store.Snapshot);
                workspace.ShowRoutePlanning();
                Assert.IsTrue(world.ShowingMap, "a route is drafted on the map");
                Assert.IsTrue(toggle.enabledSelf);

                press.Invoke(workspace, null);
                Assert.IsTrue(world.CamerasShown);
                Assert.IsFalse(world.ShowingMap, "the cameras are watched in the 3D city");
                Assert.AreEqual("shuttle-01", world.SelectedId, "the route's first shuttle");
                Assert.AreEqual("Hide cameras", toggle.text);
                Assert.IsTrue(toggle.ClassListContains("segment-selected"));

                press.Invoke(workspace, null);
                Assert.IsFalse(world.CamerasShown);
                Assert.IsFalse(world.ShowingMap, "Hide cameras goes back to the 3D view");
                Assert.AreEqual("Cameras", toggle.text);

                // The panel is for one shuttle: its cameras, whichever is selected elsewhere
                workspace.SelectVehicle("shuttle-03");
                press.Invoke(workspace, null);
                Assert.AreEqual("shuttle-01", world.SelectedId, "the panel's shuttle, not the one selected in the fleet");
                press.Invoke(workspace, null);

                // The picker lists shuttles with the route each drives. Outside a panel it sends no
                // change event, so do what its callback does.
                var picker = root.Q<DropdownField>("shuttles");
                CollectionAssert.AreEqual(new[] { "Shuttle 01 · route-a", "Shuttle 02 · route-a", "Shuttle 03 · route-a", "Shuttle 04 · route-a", "Shuttle 05 · route-b" }, picker.choices);
                Assert.AreEqual("Shuttle 01 · route-a", picker.value, "the first shuttle whose route is closed");
                var choose = typeof(TrainingWorkspace).GetMethod("ChooseShuttle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                choose.Invoke(workspace, new object[] { "shuttle-03" });
                press.Invoke(workspace, null);
                Assert.AreEqual("shuttle-03", world.SelectedId, "the shuttle picked");

                // Another shuttle while watching: the cameras move to it
                choose.Invoke(workspace, new object[] { "shuttle-05" });
                Assert.AreEqual("Shuttle 05 · route-b", picker.value);
                Assert.IsTrue(world.CamerasShown);
                Assert.AreEqual("shuttle-05", world.WatchedId, "the new shuttle");
                press.Invoke(workspace, null);
                Assert.IsFalse(world.ShowingMap, "still the 3D view after another shuttle");
            }
            finally { session.Shutdown(); UnityEngine.Object.DestroyImmediate(rig); }
        }

        // A shuttle needing a decision never moves the operator: not the page, the 3D view, the cameras
        // or the selection, and nothing is said (the incident alert does that). Route Planning moves to
        // it by itself only while idle and on a shuttle that needs none; Decide opens it there. Picking
        // a shuttle in the fleet moves the cameras, except on Route Planning to another shuttle.
        [Test]
        public async Task ADecisionNeverMovesTheOperator()
        {
            var raw = Fixture("snapshot");
            // shuttle-05 is on another route, a copy of route-a
            var routeB = (JObject)raw["routes"][0].DeepClone(); routeB["route_id"] = "route-b";
            ((JArray)raw["routes"]).Add(routeB);
            raw["vehicles"][4]["route_id"] = "route-b";
            var backend = new Backend();
            backend.Reply = request =>
            {
                var path = request.Url.AbsolutePath;
                if (path.EndsWith("/snapshot")) return Task.FromResult(new HttpResponse { Status = 200, Body = raw.ToString() });
                if (path.EndsWith("/editor") || path.EndsWith("/outcome")) return Task.FromResult(new HttpResponse { Failure = HttpFailure.Incomplete });
                if (path.Contains("/events"))
                {
                    var replay = Fixture("event_replay"); replay["events"] = new JArray(); replay["has_more"] = false;
                    replay["after_sequence"] = raw["event_sequence"].DeepClone(); replay["next_sequence"] = raw["event_sequence"].DeepClone();
                    return Task.FromResult(new HttpResponse { Status = 200, Body = replay.ToString() });
                }
                return Task.FromResult(new HttpResponse { Status = 200, Body = "{\"buildings\":[{\"id\":\"way/1\",\"type\":\"retail\",\"name\":\"\",\"height_m\":30,\"rings_m\":[[[0,100],[10,100],[10,110],[0,110],[0,100]]]}]}" });
            };
            var auth = new AuthSession(); auth.Begin("trainee", "token", 3600);
            var url = new Uri("http://backend.test");
            var api = new TrainingApi(backend, auth, url);
            var session = new SessionController(api, auth);
            var root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Formula9/UI/Formula9App.uxml").CloneTree();
            var rig = new GameObject("Pending decision test"); var world = rig.AddComponent<WorldView>();
            // incident-1 offers shuttle-01 a wait, 8 s away. incident-2 offers the shuttles given here an option each.
            var first = (JObject)raw["incidents"][0].DeepClone();
            var wait = (JObject)raw["candidates"][0];
            async Task Offer(params (string Vehicle, string Action, double Eta)[] offers)
            {
                var second = (JObject)first.DeepClone();
                second["incident_id"] = "incident-2"; second["vehicle_id"] = JValue.CreateNull(); second["detected_tick"] = 190;
                second["exposures"] = new JArray(offers.Select(o => new JObject { ["vehicle_id"] = o.Vehicle, ["eta_s"] = o.Eta, ["distance_m"] = o.Eta * 5, ["is_estimate"] = true }));
                second["exposed_vehicle_ids"] = new JArray(offers.Select(o => o.Vehicle));
                var options = offers.Select(o =>
                {
                    var option = (JObject)wait.DeepClone();
                    option["candidate_id"] = $"candidate-{o.Vehicle}"; option["incident_id"] = "incident-2";
                    option["vehicle_id"] = o.Vehicle; option["action"] = o.Action; option["dependency_revisions"] = new JObject { ["incident-2"] = 2 };
                    return option;
                }).ToList();
                second["candidate_ids"] = new JArray(options.Select(o => o["candidate_id"]));
                raw["snapshot_id"] = Guid.NewGuid().ToString();
                raw["incidents"] = offers.Length == 0 ? new JArray(first.DeepClone()) : new JArray(first.DeepClone(), second);
                raw["candidates"] = new JArray(new[] { wait.DeepClone() }.Concat(options));
                await session.RefreshAsync();
            }
            try
            {
                world.Attach(root.Q("world"), new SceneLoader(backend, url), null, null, new CityClient(backend, url));
                var notices = new List<string>();
                var workspace = new TrainingWorkspace(root, api, session, world, _ => { }, message => notices.Add(message));
                var toggle = root.Q<Button>("view-cameras");
                var press = typeof(TrainingWorkspace).GetMethod("ToggleSelectedCameras", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var choose = typeof(TrainingWorkspace).GetMethod("ChooseShuttle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var picker = root.Q<DropdownField>("shuttles");
                var reason = root.Q<TextField>("route-reason");
                string Panel() => picker.value.Split(' ')[1];
                List<Button> Items() => root.Q("route-pending-items").Query<Button>().ToList();
                // Each entry: the shuttle's name (in its red box), then what it needs
                List<string> Strip() => Items().Select(b => string.Join(" ", b.Query<Label>().ToList().Select(l => l.text))).ToList();
                bool Shown(string page) => root.Q(page).style.display.value == DisplayStyle.Flex;
                await session.JoinAsync((string)raw["session_id"]);
                world.Show(session.Store.Snapshot);
                workspace.ShowSection("operations");
                workspace.SelectVehicle("shuttle-03");
                press.Invoke(workspace, null);
                Assert.IsTrue(world.CamerasShown);

                // Joining mid-session, the panel starts on the shuttle waiting for a decision
                Assert.AreEqual("01", Panel());
                StringAssert.EndsWith("● needs a decision", picker.value);
                CollectionAssert.AreEqual(new[] { "Shuttle 01 needs a decision (1) · ~8 s" }, Strip());
                var badge = root.Q<TextElement>("nav-route-badge");
                Assert.AreEqual("1", badge.text, "the Route Planning nav item counts the shuttles waiting");
                Assert.AreNotEqual(DisplayStyle.None, badge.style.display.value);

                // Picking in the fleet moves the cameras; on Route Planning, not to another shuttle
                workspace.PickVehicle("shuttle-02");
                Assert.AreEqual("shuttle-02", world.WatchedId, "the fleet pick moves the cameras");
                workspace.ShowRoutePlanning();
                workspace.PickVehicle("shuttle-05");
                Assert.AreEqual("shuttle-02", world.WatchedId, "shuttle-05 is not the panel's shuttle: the feed stays");
                workspace.ShowSection("operations");
                workspace.PickVehicle("shuttle-03");
                Assert.AreEqual("shuttle-03", world.WatchedId);

                // The operator's own pick stands while the shuttles waiting stay the same
                choose.Invoke(workspace, new object[] { "shuttle-04" });
                workspace.Render();
                Assert.AreEqual("04", Panel(), "not pulled back to shuttle-01");
                Assert.AreEqual("shuttle-04", world.WatchedId, "a pick in the panel moves open cameras to it");

                // Another shuttle comes to need a decision. Idle and on a shuttle that needs none, the
                // panel moves to the soonest; nothing else does.
                await Offer(("shuttle-05", "ROAD_DETOUR", 2));
                workspace.Render();
                Assert.AreEqual("05", Panel(), "the soonest of the shuttles waiting");
                Assert.AreEqual("Shuttle 05 · route-b   ● needs a decision", picker.value);
                CollectionAssert.AreEqual(new[] { "Shuttle 05 needs a decision (1) · ~2 s", "Shuttle 01 needs a decision (1) · ~8 s" }, Strip(), "soonest first");
                Assert.IsTrue(Shown("page-fleet"), "the page stays");
                Assert.IsTrue(world.CamerasShown, "still watching");
                Assert.AreEqual("shuttle-04", world.WatchedId, "still watching the shuttle they chose");
                Assert.IsTrue(toggle.ClassListContains("segment-selected"), "the map bar's Cameras segment is lit while they show");

                // Mid-draft the panel never moves
                choose.Invoke(workspace, new object[] { "shuttle-04" });
                reason.value = "Keeping an eye on 04";
                await Offer(("shuttle-05", "ROAD_DETOUR", 2), ("shuttle-02", "KEEP_ROUTE", 1));
                workspace.Render();
                Assert.AreEqual("04", Panel(), "a reason is being typed");
                Assert.AreEqual("Keeping an eye on 04", reason.value);
                CollectionAssert.AreEqual(new[] { "Shuttle 02 needs a decision (1) · ~1 s", "Shuttle 05 needs a decision (1) · ~2 s", "Shuttle 01 needs a decision (1) · ~8 s" }, Strip());
                Assert.AreEqual("3", badge.text);
                Assert.AreEqual("Route Planning: 3 shuttles need a decision", root.Q<Button>("nav-route").tooltip);

                // Closing the cameras is just that
                press.Invoke(workspace, null);
                Assert.IsFalse(world.CamerasShown);
                Assert.IsFalse(world.ShowingMap, "leaving stays in 3D");
                Assert.AreEqual("shuttle-04", world.SelectedId, "the selection does not jump");
                Assert.IsEmpty(notices, "the panel says nothing");

                // Decide opens Route Planning on that shuttle: the operator's own choice, even mid-draft
                workspace.DecideFor("shuttle-05");
                Assert.IsTrue(Shown("page-route"));
                Assert.AreEqual("05", Panel());
                Assert.AreEqual("shuttle-05", world.SelectedId);
                Assert.IsEmpty(reason.value, "the draft for shuttle-04 is gone");
                Assert.IsFalse(world.ShowingMap, "nothing is drafted until the operator chooses to re-route");
                Assert.AreEqual(DisplayStyle.Flex, root.Q("route-options").style.display.value, "its options are offered");
                Assert.AreEqual(DisplayStyle.None, root.Q("route-decide").style.display.value);
                typeof(TrainingWorkspace).GetMethod("ChooseOption", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Invoke(workspace, new object[] { "ROAD_DETOUR" });
                Assert.IsTrue(world.ShowingMap, "its detour is drafted on the map");
                Assert.AreEqual(DisplayStyle.Flex, root.Q("route-decide").style.display.value);
                // The re-route's steps are numbered down the page: the problem (with its options), then the detour
                Assert.AreEqual("1", root.Q("route-problem-card").Q<Label>("route-step-number").text);
                Assert.AreEqual(root.Q("route-problem-card"), root.Q("route-options").parent, "the options are in the problem's box");
                Assert.AreEqual("2", root.Q("route-decide").Q<Label>("route-step-number").text);
                Assert.IsTrue(root.Q("route-problem-card").Q("route-step-mark").ClassListContains("route-step-done"));
                Assert.IsTrue(root.Q("route-decide").Q<Label>("route-step-number").ClassListContains("route-step-current"), "drawing the detour");
                // With a route drawn, pressing Re-route again first says what a second press does,
                // so one press by mistake loses nothing
                var drawn = typeof(TrainingWorkspace).GetField("draft", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                drawn.SetValue(workspace, new List<RouteWaypoint> { new RouteWaypoint { EdgeId = "edge-1", Road = "Lorong 6 Toa Payoh" } });
                typeof(TrainingWorkspace).GetMethod("ChooseOption", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Invoke(workspace, new object[] { "ROAD_DETOUR" });
                Assert.AreEqual(DisplayStyle.Flex, root.Q("route-decide").style.display.value, "still re-routing");
                Assert.AreEqual(1, ((List<RouteWaypoint>)drawn.GetValue(workspace)).Count, "the route is kept");
                Assert.AreEqual(RoutePresenter.LeaveRerouteNote, root.Q<Label>("route-options-hint").text);
                typeof(TrainingWorkspace).GetMethod("ChooseOption", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Invoke(workspace, new object[] { "ROAD_DETOUR" });
                Assert.AreEqual(DisplayStyle.None, root.Q("route-decide").style.display.value, "the second press leaves");
                Assert.IsEmpty((List<RouteWaypoint>)drawn.GetValue(workspace));
                Assert.AreNotEqual(RoutePresenter.LeaveRerouteNote, root.Q<Label>("route-options-hint").text);
                typeof(TrainingWorkspace).GetMethod("ChooseOption", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Invoke(workspace, new object[] { "ROAD_DETOUR" });
                Assert.AreEqual(DisplayStyle.Flex, root.Q("route-decide").style.display.value, "re-routing again");
                // A suggested detour leaves the drawn route empty, but is kept the same way
                var suggesting = typeof(TrainingWorkspace).GetField("suggesting", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                suggesting.SetValue(workspace, true);
                typeof(TrainingWorkspace).GetMethod("ChooseOption", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Invoke(workspace, new object[] { "ROAD_DETOUR" });
                Assert.AreEqual(DisplayStyle.Flex, root.Q("route-decide").style.display.value, "the suggestion is kept");
                Assert.AreEqual(RoutePresenter.LeaveRerouteNote, root.Q<Label>("route-options-hint").text);
                suggesting.SetValue(workspace, false);
                // Changing the route takes the first press back, and its note
                typeof(TrainingWorkspace).GetMethod("ApplyDraft", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Invoke(workspace, new object[] { new List<RouteWaypoint>() });
                Assert.AreNotEqual(RoutePresenter.LeaveRerouteNote, root.Q<Label>("route-options-hint").text);
                Assert.IsFalse((bool)typeof(TrainingWorkspace).GetField("leavingReroute", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(workspace));
                Assert.IsTrue(Items().Single(b => b.Q<Label>(className: "route-pending-name").text == "Shuttle 05").ClassListContains("route-pending-item-selected"));
                Assert.IsFalse(Items().Single(b => b.Q<Label>(className: "route-pending-name").text == "Shuttle 01").ClassListContains("route-pending-item-selected"));

                // Its options withdrawn: idle, the panel moves to the soonest still waiting
                await Offer(("shuttle-02", "KEEP_ROUTE", 1));
                workspace.Render();
                CollectionAssert.AreEqual(new[] { "Shuttle 02 needs a decision (1) · ~1 s", "Shuttle 01 needs a decision (1) · ~8 s" }, Strip());
                Assert.AreEqual("02", Panel());

                // A marker on the map opens the first shuttle still waiting for a decision there
                workspace.ShowSection("operations");
                var selections = 0;
                workspace.IncidentSelected += () => selections++;
                workspace.DecideForIncident("incident-1");
                Assert.IsTrue(Shown("page-route"));
                Assert.AreEqual("01", Panel());
                workspace.DecideForIncident("incident-2");
                Assert.AreEqual("02", Panel());
                Assert.AreEqual("incident-2", workspace.SelectedIncident, "the Fleet List marks it selected");
                Assert.AreEqual(2, selections, "the Fleet List is told each time");

                // Nobody waits any more: the strip goes, and the panel stays where it is
                raw["snapshot_id"] = Guid.NewGuid().ToString();
                raw["incidents"] = new JArray(); raw["candidates"] = new JArray();
                await session.RefreshAsync();
                workspace.Render();
                Assert.AreEqual(DisplayStyle.None, root.Q("route-pending").style.display.value);
                Assert.AreEqual(DisplayStyle.None, badge.style.display.value, "no badge with nobody waiting");
                Assert.AreEqual("Shuttle 02 · route-a", picker.value);
                Assert.IsNull(workspace.SelectedIncident, "an incident that has gone is no longer selected");
                Assert.AreEqual(3, selections, "and the Fleet List is told so");
                Assert.IsEmpty(notices);
            }
            finally { session.Shutdown(); UnityEngine.Object.DestroyImmediate(rig); }
        }

        // An option is chosen on Route Planning and confirmed with a reason. The option sent is the one
        // the snapshot has when Confirm is pressed; a refusal keeps the row open with the backend's
        // message, and options reissued while it is open close it.
        [Test]
        public async Task ConfirmingAnOptionDecidesForThatShuttle()
        {
            var raw = Fixture("incident_decisions_snapshot");
            var commands = new List<JObject>();
            Func<JObject, HttpResponse> answer = null;
            var backend = new Backend();
            backend.Reply = request =>
            {
                var path = request.Url.AbsolutePath;
                if (path.EndsWith("/snapshot")) return Task.FromResult(new HttpResponse { Status = 200, Body = raw.ToString() });
                if (request.Method == "POST" && path.EndsWith("/commands"))
                {
                    var command = JObject.Parse(request.Body); commands.Add(command);
                    return Task.FromResult(answer(command));
                }
                if (path.Contains("/events"))
                {
                    var replay = Fixture("event_replay"); replay["events"] = new JArray(); replay["has_more"] = false;
                    replay["after_sequence"] = raw["event_sequence"].DeepClone(); replay["next_sequence"] = raw["event_sequence"].DeepClone();
                    return Task.FromResult(new HttpResponse { Status = 200, Body = replay.ToString() });
                }
                return Task.FromResult(new HttpResponse { Failure = HttpFailure.Incomplete });
            };
            HttpResponse Applied(JObject command)
            {
                var status = Fixture("command_status");
                foreach (var field in new[] { "command_id", "idempotency_key", "target_type", "target_id" }) status[field] = command[field].DeepClone();
                return new HttpResponse { Status = 200, Body = status.ToString() };
            }
            // A new revision of the collision, which reissues shuttle-04's options under new IDs
            void Reissue(int revision)
            {
                var collision = (JObject)raw["incidents"][1];
                collision["aggregate_revision"] = revision;
                foreach (var candidate in ((JArray)raw["candidates"]).Where(c => (string)c["incident_id"] == "scheduled-collision-1"))
                {
                    candidate["candidate_id"] = ((string)candidate["candidate_id"]).Replace($"-r{revision - 1}-", $"-r{revision}-");
                    candidate["dependency_revisions"]["scheduled-collision-1"] = revision;
                }
                collision["candidate_ids"] = new JArray(((JArray)raw["candidates"]).Where(c => (string)c["incident_id"] == "scheduled-collision-1").Select(c => c["candidate_id"].DeepClone()));
                raw["snapshot_id"] = Guid.NewGuid().ToString();
            }
            var auth = new AuthSession(); auth.Begin("trainee", "token", 3600);
            var api = new TrainingApi(backend, auth, new Uri("http://backend.test"));
            var session = new SessionController(api, auth);
            var root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Formula9/UI/Formula9App.uxml").CloneTree();
            var rig = new GameObject("Decision card test"); var world = rig.AddComponent<WorldView>();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            try
            {
                world.Attach(root.Q("world"), new SceneLoader(backend, new Uri("http://backend.test")));
                var workspace = new TrainingWorkspace(root, api, session, world, _ => { }, _ => { });
                void Choose(string action) => typeof(TrainingWorkspace).GetMethod("ChooseOption", flags).Invoke(workspace, new object[] { action });
                Task Confirm() => (Task)typeof(TrainingWorkspace).GetMethod("ConfirmChoice", flags).Invoke(workspace, null);
                bool Shown(string name) => root.Q(name).style.display.value == DisplayStyle.Flex;
                Button Option(string name) => root.Q<Button>(name);
                // Before joining, the page says to join a session: a shuttle can't be added yet
                workspace.Render();
                Assert.AreEqual("Join a session to see its routes.", root.Q<Label>("route-empty").text);
                Assert.IsTrue(Shown("route-empty"));
                await session.JoinAsync((string)raw["session_id"]);
                // The options' figures are in the backend's OPTIONS_READY event
                var ready = ContractParser.Parse<EventReplay>(Fixture("event_replay").ToString()).Events.Single();
                ready.EventSequence = session.Store.LastEventSequence + 1; ready.EntityId = "scheduled-collision-1";
                ready.Data = new JObject
                {
                    ["options"] = new JObject
                    {
                        ["scheduled-collision-1-r3-shuttle-04-keep_route"] = new JObject { ["estimated_delay_s"] = 25.0 },
                        ["scheduled-collision-1-r3-shuttle-04-hold_at_stop"] = new JObject { ["estimated_delay_s"] = 120.0, ["hold_stop"] = new JObject { ["stop_id"] = "52449-kim-keat-stop" } },
                    },
                };
                Assert.IsNull(session.Store.AcceptEvents(session.SessionId, session.Store.Snapshot.SessionEpoch, new[] { ready }));
                workspace.DecideFor("shuttle-04");

                Assert.IsTrue(Shown("route-options"));
                Assert.AreEqual("What should Shuttle 04 do?", root.Q<Label>("route-options-title").text);
                Assert.AreEqual("Keep route ~+25 s", Option("option-keep").text);
                Assert.AreEqual("Hold at Kim Keat Stop ~+2 min", Option("option-hold").text);
                Assert.IsTrue(Option("option-hold").enabledSelf);
                Assert.IsFalse(Option("option-assist").enabledSelf);
                Assert.AreEqual("Assistance has already been requested.", Option("option-assist").tooltip);
                Assert.IsFalse(Option("option-reroute").enabledSelf);
                Assert.IsFalse(Shown("route-confirm"), "nothing chosen yet");
                Assert.IsFalse(Shown("route-outcome"));
                Assert.IsFalse(Shown("route-decide"));
                Assert.AreEqual("1", root.Q("route-problem-card").Q<Label>("route-step-number").text, "the problem is step 1");
                // Nothing chosen yet: the exercise is still at the problem, not past it
                Assert.IsTrue(root.Q("route-problem-card").Q<Label>("route-step-number").ClassListContains("route-step-current"));
                Assert.IsFalse(root.Q("route-problem-card").Q("route-step-mark").ClassListContains("route-step-done"));

                // One confirm step, with a reason to start from
                Choose("HOLD_AT_STOP");
                Assert.IsTrue(Shown("route-confirm"));
                // Confirming is its own step after the problem, as the detour is for the re-route
                Assert.AreEqual("2", root.Q("route-confirm").Q<Label>("route-step-number").text);
                Assert.IsTrue(root.Q("route-confirm").Q<Label>("route-step-number").ClassListContains("route-step-current"));
                Assert.IsTrue(root.Q("route-problem-card").Q("route-step-mark").ClassListContains("route-step-done"));
                Assert.IsTrue(Option("option-hold").ClassListContains("segment-selected"));
                Assert.AreEqual("Hold Shuttle 04 at Kim Keat Stop until the collision on Lorong 6 Toa Payoh clears.", root.Q<Label>("route-confirm-summary").text);
                var reason = root.Q<TextField>("route-confirm-reason");
                Assert.IsNotEmpty(reason.value);
                Assert.IsTrue(root.Q<Button>("route-confirm-send").enabledSelf);
                reason.value = "Hold before the queue, clears soon";

                // Refused: the row stays open with the backend's own words
                answer = _ => new HttpResponse { Status = 409, Body = "{\"error\":{\"code\":\"STALE_REVISION\",\"message\":\"The incident moved on\"}}" };
                await Confirm();
                Assert.AreEqual(1, commands.Count);
                Assert.AreEqual("APPROVE_INCIDENT_OPTION", (string)commands[0]["command_type"]);
                Assert.AreEqual("scheduled-collision-1", (string)commands[0]["target_id"]);
                Assert.AreEqual("scheduled-collision-1-r3-shuttle-04-hold_at_stop", (string)commands[0]["payload"]["candidate_id"]);
                Assert.AreEqual("Hold before the queue, clears soon", (string)commands[0]["reason"]);
                Assert.IsTrue(Shown("route-confirm"), "kept open for another try");
                StringAssert.Contains("The incident moved on", root.Q<Label>("route-confirm-note").text);

                // The options are reissued while it is open: it closes, and says so
                Reissue(4);
                await session.RefreshAsync();
                Assert.IsFalse(Shown("route-confirm"));
                Assert.AreEqual(RoutePresenter.OptionsRefreshed, root.Q<Label>("route-options-hint").text);
                Assert.AreEqual("Hold at a stop", Option("option-hold").text, "the event describes the old options: no figures");

                // Chosen again and confirmed: sent by the ID it has now, and the row closes
                Choose("HOLD_AT_STOP");
                Assert.IsTrue(Shown("route-confirm"));
                Assert.AreEqual("", root.Q<Label>("route-options-hint").text);
                answer = command =>
                {
                    var collision = (JObject)raw["incidents"][1];
                    ((JArray)collision["responses"]).Add(new JObject { ["action"] = "HOLD_AT_STOP", ["before_arrival"] = true, ["decided_tick"] = 200, ["vehicle_id"] = "shuttle-04", ["hold_stop_id"] = "52449-kim-keat-stop" });
                    raw["candidates"] = new JArray(((JArray)raw["candidates"]).Where(c => (string)c["incident_id"] != "scheduled-collision-1"));
                    collision["candidate_ids"] = new JArray();
                    raw["snapshot_id"] = Guid.NewGuid().ToString();
                    return Applied(command);
                };
                await Confirm();
                Assert.AreEqual(2, commands.Count);
                Assert.AreEqual("scheduled-collision-1-r4-shuttle-04-hold_at_stop", (string)commands[1]["payload"]["candidate_id"]);
                Assert.IsFalse(Shown("route-confirm"));
                // Idle again, the panel moves on to the shuttle still waiting
                StringAssert.StartsWith("Shuttle 01", root.Q<DropdownField>("shuttles").value);
                workspace.DecideFor("shuttle-04");
                Assert.IsFalse(Shown("route-options"), "shuttle-04 has been decided for");
                Assert.IsTrue(Shown("route-outcome"));
                StringAssert.Contains("Decided for Shuttle 04: hold at Kim Keat Stop", root.Q<Label>("route-problem").text);

                // Pressing the chosen option again closes its confirm card without sending anything
                workspace.DecideFor("shuttle-01");
                Choose("KEEP_ROUTE");
                Assert.IsTrue(Shown("route-confirm"));
                Choose("KEEP_ROUTE");
                Assert.IsFalse(Shown("route-confirm"));
                Assert.IsFalse(Option("option-keep").ClassListContains("segment-selected"));
                Assert.AreEqual(2, commands.Count);

                // Paused with an option chosen: the others grey out, but the chosen one can still be
                // pressed to close its confirm card, as there is no Back
                Choose("KEEP_ROUTE");
                session.Store.Snapshot.Session.Status = "PAUSED";
                workspace.Render();
                Assert.IsTrue(Shown("route-confirm"));
                Assert.IsFalse(Option("option-hold").enabledSelf);
                Assert.IsTrue(Option("option-keep").enabledSelf, "the way back from the confirm card");
                Assert.AreEqual(RoutePresenter.CloseChoiceHint, Option("option-keep").tooltip, "it says what pressing it does, not that the session is paused");
                Assert.IsFalse(root.Q<Button>("route-confirm-send").enabledSelf);
                Choose("KEEP_ROUTE");
                Assert.IsFalse(Shown("route-confirm"));
                Assert.IsFalse(Option("option-keep").enabledSelf, "closed, it is greyed out like the rest");
                Assert.AreEqual(2, commands.Count);
            }
            finally { session.Shutdown(); UnityEngine.Object.DestroyImmediate(rig); }
        }

        [Test]
        public async Task LeavingASessionClearsItsReportsAndFilters()
        {
            var raw = Fixture("snapshot");
            var backend = new Backend();
            backend.Reply = request =>
            {
                var path = request.Url.AbsolutePath;
                if (path.EndsWith("/snapshot")) return Task.FromResult(new HttpResponse { Status = 200, Body = raw.ToString() });
                if (path.EndsWith("/commands"))
                    return Task.FromResult(new HttpResponse { Status = 409, Body = "{\"error\":{\"code\":\"STALE_REVISION\",\"message\":\"Refresh the session\"}}" });
                if (path.EndsWith("/events"))
                {
                    var replay = Fixture("event_replay"); replay["events"] = new JArray(); replay["has_more"] = false;
                    replay["after_sequence"] = raw["event_sequence"].DeepClone(); replay["next_sequence"] = raw["event_sequence"].DeepClone();
                    return Task.FromResult(new HttpResponse { Status = 200, Body = replay.ToString() });
                }
                return Task.FromResult(new HttpResponse { Failure = HttpFailure.Incomplete });
            };
            var auth = new AuthSession(); auth.Begin("trainee", "token", 3600);
            var api = new TrainingApi(backend, auth, new Uri("http://backend.test"));
            var session = new SessionController(api, auth);
            var root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Formula9/UI/Formula9App.uxml").CloneTree();
            var rig = new GameObject("Reports session boundary test"); var world = rig.AddComponent<WorldView>();
            try
            {
                world.Attach(root.Q("world"), new SceneLoader(backend, new Uri("http://backend.test")));
                var workspace = new TrainingWorkspace(root, api, session, world, _ => { }, _ => { });
                await session.JoinAsync((string)raw["session_id"]);
                var observed = ContractParser.Parse<EventReplay>(Fixture("event_replay").ToString()).Events.Single();
                observed.EventSequence = session.Store.LastEventSequence + 1;
                Assert.IsNull(session.Store.AcceptEvents(session.SessionId, session.Store.Snapshot.SessionEpoch, new[] { observed }));
                workspace.Render();
                Assert.IsTrue(await session.SendControlAsync("PAUSE"));
                Assert.AreEqual("Showing 1 of 1 retained events", root.Q<Label>("reports-event-count").text);
                Assert.AreEqual("Showing 1 of 1 retained commands", root.Q<Label>("reports-command-count").text);
                root.Q<TextField>("reports-event-search").SetValueWithoutNotify("shuttle-01");
                root.Q<DropdownField>("reports-command-status").SetValueWithoutNotify("NOT_ACCEPTED");

                session.Leave();

                Assert.AreEqual("Showing 0 of 0 retained events", root.Q<Label>("reports-event-count").text);
                Assert.AreEqual("Showing 0 of 0 retained commands", root.Q<Label>("reports-command-count").text);
                Assert.IsEmpty(root.Q<TextField>("reports-event-search").value);
                Assert.AreEqual("All statuses", root.Q<DropdownField>("reports-command-status").value);
                Assert.AreEqual(DisplayStyle.None, root.Q("events-table-host").style.display.value);
                Assert.AreEqual(DisplayStyle.None, root.Q("ledger-table-host").style.display.value);
            }
            finally { session.Shutdown(); UnityEngine.Object.DestroyImmediate(rig); }
        }

        // A horizontal ScrollView lays its children out side by side. Each data table must keep its
        // heading and rows in one column inside it, or the rows render beside the heading, out of view.
        [Test]
        public async Task DataTableRowsSitUnderTheirHeadings()
        {
            var raw = Fixture("snapshot");
            var backend = new Backend();
            backend.Reply = request =>
            {
                if (request.Url.AbsolutePath.EndsWith("/snapshot")) return Task.FromResult(new HttpResponse { Status = 200, Body = raw.ToString() });
                if (request.Url.AbsolutePath.EndsWith("/editor")) return Task.FromResult(new HttpResponse { Failure = HttpFailure.Incomplete });
                var replay = Fixture("event_replay"); replay["events"] = new JArray();
                replay["after_sequence"] = raw["event_sequence"].DeepClone(); replay["next_sequence"] = raw["event_sequence"].DeepClone();
                return Task.FromResult(new HttpResponse { Status = 200, Body = replay.ToString() });
            };
            var auth = new AuthSession(); auth.Begin("trainee", "token", 3600);
            var api = new TrainingApi(backend, auth, new Uri("http://backend.test"));
            var session = new SessionController(api, auth);
            var root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Formula9/UI/Formula9App.uxml").CloneTree();
            var rig = new GameObject("Table layout test"); var world = rig.AddComponent<WorldView>();
            try
            {
                world.Attach(root.Q("world"), new SceneLoader(backend, new Uri("http://backend.test")));
                var workspace = new TrainingWorkspace(root, api, session, world, _ => { }, _ => { });
                await session.JoinAsync((string)raw["session_id"]);
                workspace.Render();
                var tables = root.Query<ScrollView>(className: "data-table").ToList();
                Assert.GreaterOrEqual(tables.Count, 6, "Charging, Reports and Route Planning tables");
                foreach (var table in tables)
                {
                    Assert.AreEqual(1, table.contentContainer.childCount, "one column holds heading and rows");
                    Assert.IsTrue(table.contentContainer[0].ClassListContains("data-table-body"));
                }
                // The battery table's first data row is in the same column, directly under the heading.
                var cell = root.Query<Label>(className: "data-cell").ToList().First(l => l.text == "shuttle-01" && !l.ClassListContains("data-heading"));
                var column = cell.parent.parent.parent;
                Assert.IsTrue(column.ClassListContains("data-table-body"));
                Assert.IsTrue(column[0].Children().All(c => c.ClassListContains("data-heading")), "the heading row comes first");
            }
            finally { session.Shutdown(); UnityEngine.Object.DestroyImmediate(rig); }
        }

        // The Reports Incidents tab lists the session's incidents, read-only, and describes the one
        // picked without asking the operator for a decision.
        [Test]
        public async Task TheReportsListTheSessionsIncidents()
        {
            var raw = Fixture("snapshot");
            var backend = new Backend();
            backend.Reply = request =>
            {
                if (request.Url.AbsolutePath.EndsWith("/snapshot")) return Task.FromResult(new HttpResponse { Status = 200, Body = raw.ToString() });
                if (request.Url.AbsolutePath.EndsWith("/editor")) return Task.FromResult(new HttpResponse { Failure = HttpFailure.Incomplete });
                var replay = Fixture("event_replay"); replay["events"] = new JArray();
                replay["after_sequence"] = raw["event_sequence"].DeepClone(); replay["next_sequence"] = raw["event_sequence"].DeepClone();
                return Task.FromResult(new HttpResponse { Status = 200, Body = replay.ToString() });
            };
            var auth = new AuthSession(); auth.Begin("trainee", "token", 3600);
            var api = new TrainingApi(backend, auth, new Uri("http://backend.test"));
            var session = new SessionController(api, auth);
            var root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Formula9/UI/Formula9App.uxml").CloneTree();
            var rig = new GameObject("Reports incidents test"); var world = rig.AddComponent<WorldView>();
            try
            {
                world.Attach(root.Q("world"), new SceneLoader(backend, new Uri("http://backend.test")));
                var workspace = new TrainingWorkspace(root, api, session, world, _ => { }, _ => { });
                await session.JoinAsync((string)raw["session_id"]);
                workspace.Render();
                var host = root.Q("reports-incident-table-host");
                var cells = host.Query<Label>(className: "data-cell").ToList().Where(l => !l.ClassListContains("data-heading")).Select(l => l.text).ToList();
                Assert.Contains("incident-1", cells, "a row for the session's incident");
                Assert.IsFalse(host.Query<Label>(className: "data-heading").ToList().Any(l => l.text == "Options"), "nothing to decide here");
                Assert.AreEqual("incident-1", root.Q<DropdownField>("reports-incidents").value);
                var summary = root.Q<Label>("reports-incident-summary").text;
                StringAssert.Contains("OPTIONS_READY", summary);
                StringAssert.DoesNotContain("Decide for each shuttle", summary);
            }
            finally { session.Shutdown(); UnityEngine.Object.DestroyImmediate(rig); }
        }

        private sealed class IncidentsWindow : EditorWindow { }

        [Test]
        public void DisablingTheAppCancelsItsScheduledAssessmentNotice()
        {
            var window = ScriptableObject.CreateInstance<IncidentsWindow>();
            var rig = new GameObject("Assessment notice cleanup test");
            rig.SetActive(false);
            var app = rig.AddComponent<Formula9App>();
            var label = new Label();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            try
            {
                window.ShowUtility();
                window.rootVisualElement.Add(label);
                var item = label.schedule.Execute(() => Assert.Fail("A disabled app must not run its pending notice callback.")).StartingIn(5000);
                typeof(Formula9App).GetField("assessmentEndNoticeExpiry", flags).SetValue(app, item);
                Assert.IsTrue(item.isActive);
                typeof(Formula9App).GetMethod("OnDisable", flags).Invoke(app, null);
                Assert.IsFalse(item.isActive);
                Assert.IsNull(typeof(Formula9App).GetField("assessmentEndNoticeExpiry", flags).GetValue(app));
                UnityEngine.Object.DestroyImmediate(rig);
                Assert.IsFalse(item.isActive, "Destroying the owner cannot leave its callback queued on the surviving panel.");
            }
            finally
            {
                if (rig != null) UnityEngine.Object.DestroyImmediate(rig);
                window.Close();
            }
        }

        // The app on a real panel, so value-change and click events reach their callbacks. The
        // session has incident-2 beside incident-1, and answers History and Debrief through Report.
        private sealed class IncidentsRig : IDisposable
        {
            public readonly JObject Raw = Fixture("snapshot");
            public readonly VisualElement Root;
            public readonly SessionController Session;
            public readonly TrainingWorkspace Workspace;
            public readonly List<Task> Runs = new List<Task>();
            public readonly List<string> Asked = new List<string>();
            public Func<string, Task<HttpResponse>> Report;
            private readonly GameObject rig;
            private readonly IncidentsWindow window;

            public IncidentsRig(Action<VisualElement> layout = null)
            {
                var second = (JObject)Raw["incidents"][0].DeepClone();
                second["incident_id"] = "incident-2"; second["candidate_ids"] = new JArray(); second["detected_tick"] = 190;
                second["location"]["road_name"] = "Lorong 4 Toa Payoh";
                ((JArray)Raw["incidents"]).Add(second);
                Report = path => Task.FromResult(Answer(path));
                var backend = new Backend();
                backend.Reply = request =>
                {
                    var path = request.Url.AbsolutePath;
                    if (path.EndsWith("/history") || path.EndsWith("/debrief")) { Asked.Add(path); return Report(path); }
                    if (path.EndsWith("/snapshot")) return Task.FromResult(new HttpResponse { Status = 200, Body = Raw.ToString() });
                    if (path.EndsWith("/events"))
                    {
                        var replay = Fixture("event_replay"); replay["events"] = new JArray(); replay["has_more"] = false;
                        replay["after_sequence"] = Raw["event_sequence"].DeepClone(); replay["next_sequence"] = Raw["event_sequence"].DeepClone();
                        return Task.FromResult(new HttpResponse { Status = 200, Body = replay.ToString() });
                    }
                    return Task.FromResult(new HttpResponse { Failure = HttpFailure.Incomplete });
                };
                var auth = new AuthSession(); auth.Begin("trainee", "token", 3600);
                var api = new TrainingApi(backend, auth, new Uri("http://backend.test"));
                Session = new SessionController(api, auth);
                Root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Formula9/UI/Formula9App.uxml").CloneTree();
                layout?.Invoke(Root);
                window = ScriptableObject.CreateInstance<IncidentsWindow>();
                window.ShowUtility();
                window.rootVisualElement.Add(Root);
                rig = new GameObject("Reports incidents test"); var world = rig.AddComponent<WorldView>();
                world.Attach(Root.Q("world"), new SceneLoader(backend, new Uri("http://backend.test")));
                Workspace = new TrainingWorkspace(Root, api, Session, world, Runs.Add, _ => { });
            }

            // A history of two samples, or a debrief, for the incident the path names
            public HttpResponse Answer(string path)
            {
                var incident = path.Split('/').Reverse().Skip(1).First();
                var document = new JObject { ["incident_id"] = incident };
                if (path.EndsWith("/history"))
                {
                    var later = (JObject)Raw.DeepClone(); later["tick"] = (long)Raw["tick"] + 10;
                    document["complete"] = true; document["unavailable_reason"] = JValue.CreateNull();
                    document["snapshots"] = new JArray(Raw.DeepClone(), later);
                }
                else document["assessment"] = "held for 30 s";
                return new HttpResponse { Status = 200, Body = document.ToString() };
            }

            public async Task Join() { await Session.JoinAsync((string)Raw["session_id"]); Workspace.Render(); }
            // Waits on what the tab started. The route editor's graph is left unanswered here, so its load gives up.
            public async Task Settle()
            {
                foreach (var run in Runs.ToArray()) { try { await run; } catch (NoReplyException) { } }
                Runs.Clear();
            }
            public T Q<T>(string name) where T : VisualElement => Root.Q<T>(name);
            public void Press(string button) { var b = Q<Button>(button); using (var e = NavigationSubmitEvent.GetPooled()) { e.target = b; b.SendEvent(e); } }
            public VisualElement Row(string incident) => Root.Q("reports-incident-table-host").Query(className: "row").ToList().First(r => r.childCount > 0 && (r[0] as Label)?.text == incident);

            public void Dispose()
            {
                Session.Shutdown(); UnityEngine.Object.DestroyImmediate(rig);
                if (window != null) window.Close();
            }
        }

        // Incident IDs repeat across sessions: leaving one clears its selected incident, and the Fleet List
        // is told, so the next session's incident of the same ID is not marked.
        [Test]
        public async Task LeavingASessionClearsTheSelectedIncident()
        {
            using (var rig = new IncidentsRig())
            {
                await rig.Join();
                var selections = 0;
                rig.Workspace.IncidentSelected += () => selections++;
                rig.Workspace.DecideForIncident("incident-1");
                Assert.AreEqual("incident-1", rig.Workspace.SelectedIncident);
                rig.Session.Leave();
                rig.Workspace.Render();
                Assert.IsNull(rig.Workspace.SelectedIncident);
                Assert.AreEqual(2, selections, "selected, then cleared");
            }
        }

        // Pages are found by name, so a page the layout leaves out moves no other page and breaks nothing.
        [Test]
        public async Task ThePagesAreFoundByName()
        {
            using (var rig = new IncidentsRig(root => root.Q("page-charging").name = "page-gone"))
            {
                await rig.Join();
                VisualElement Page(string name) => rig.Root.Q(name);
                rig.Workspace.ShowSection("reports");
                Assert.AreEqual(DisplayStyle.Flex, Page("page-reports").style.display.value, "Reports, not the page after it");
                Assert.AreEqual(DisplayStyle.None, Page("page-scenarios").style.display.value);
                rig.Workspace.ShowSection("scenarios");
                Assert.AreEqual(DisplayStyle.Flex, Page("page-scenarios").style.display.value);
                Assert.AreEqual(DisplayStyle.None, Page("page-reports").style.display.value);
                rig.Workspace.ShowSection("operations");
                Assert.AreEqual(DisplayStyle.Flex, Page("page-fleet").style.display.value);
                Assert.AreEqual(DisplayStyle.None, Page("page-scenarios").style.display.value);
                rig.Workspace.ShowRoutePlanning();
                Assert.AreEqual(DisplayStyle.Flex, Page("page-route").style.display.value);
                Assert.AreEqual(DisplayStyle.None, Page("page-fleet").style.display.value);
            }
        }

        // The Fleet List floats over the map and collapses only on Fleet Overview, and stays
        // collapsed while the operator visits another page. Route Planning keeps its panel beside
        // the map, whole.
        [Test]
        public async Task TheFleetListStaysCollapsedAcrossPages()
        {
            using (var rig = new IncidentsRig())
            {
                await rig.Join();
                var panel = rig.Q<VisualElement>("fleet-panel");
                rig.Workspace.ShowSection("operations");
                Assert.IsTrue(panel.parent.ClassListContains("body-stacked"), "Fleet Overview: the list floats over the map");
                Assert.IsFalse(panel.ClassListContains("fleet-panel-collapsed"), "it starts expanded");
                rig.Press("fleet-toggle-btn");
                Assert.IsTrue(panel.ClassListContains("fleet-panel-collapsed"), "the chevron collapses it");
                rig.Workspace.ShowRoutePlanning();
                Assert.IsFalse(panel.parent.ClassListContains("body-stacked"), "Route Planning: the panel is beside the map");
                Assert.IsFalse(panel.ClassListContains("fleet-panel-collapsed"), "and whole");
                rig.Workspace.ShowSection("operations");
                Assert.IsTrue(panel.ClassListContains("fleet-panel-collapsed"), "back on Fleet Overview, still collapsed");
                rig.Press("fleet-toggle-btn");
                Assert.IsFalse(panel.ClassListContains("fleet-panel-collapsed"), "one press expands it again");
            }
        }

        // Scenarios has its own sidebar item now that the top bar is gone, and it is marked when shown
        [Test]
        public async Task ScenariosOpensFromTheSidebar()
        {
            using (var rig = new IncidentsRig())
            {
                await rig.Join();
                rig.Press("nav-scenarios");
                Assert.AreEqual(DisplayStyle.Flex, rig.Q<VisualElement>("page-scenarios").style.display.value);
                Assert.IsTrue(rig.Q<Button>("nav-scenarios").ClassListContains("nav-item-selected"));
                Assert.IsFalse(rig.Q<Button>("nav-fleet").ClassListContains("nav-item-selected"));
            }
        }

        // Clicking a row picks its incident and describes it at once; a snapshot that changes no row
        // keeps the rows, so a click is never split across two of them.
        [Test]
        public async Task ClickingAnIncidentRowPicksIt()
        {
            using (var rig = new IncidentsRig())
            {
                await rig.Join();
                var picker = rig.Q<DropdownField>("reports-incidents");
                Assert.AreEqual("incident-1", picker.value);
                var row = rig.Row("incident-2");
                using (var click = ClickEvent.GetPooled()) { click.target = row; row.SendEvent(click); }
                Assert.AreEqual("incident-2", picker.value);
                StringAssert.Contains("Lorong 4 Toa Payoh", rig.Q<Label>("reports-incident-summary").text, "described before the next snapshot");
                rig.Workspace.Render();
                Assert.AreSame(row, rig.Row("incident-2"), "an unchanged table keeps its rows");
            }
        }

        // History replays the samples on the map; Debrief shows the assessment in their place.
        [Test]
        public async Task HistoryAndDebriefShowOnTheIncidentsTab()
        {
            using (var rig = new IncidentsRig())
            {
                await rig.Join();
                rig.Press("reports-incident-history"); await rig.Settle();
                Assert.AreEqual(1, rig.Asked.Count);
                StringAssert.EndsWith("/session-001/incidents/incident-1/history", rig.Asked[0]);
                StringAssert.StartsWith("HISTORICAL SIMULATION · complete: True", rig.Q<Label>("reports-incident-report").text);
                Assert.AreEqual(DisplayStyle.Flex, rig.Q<FleetMap>("reports-evidence-map").style.display.value);
                var slider = rig.Q<SliderInt>("reports-history-sample");
                Assert.AreEqual(DisplayStyle.Flex, slider.style.display.value, "two samples to step through");
                Assert.AreEqual(1, slider.highValue);
                StringAssert.Contains("sample 1/2", rig.Q<Label>("reports-evidence-status").text);
                Assert.IsTrue(rig.Q<Button>("reports-incident-history").enabledSelf, "free again once answered");

                rig.Press("reports-incident-debrief"); await rig.Settle();
                var report = rig.Q<Label>("reports-incident-report").text;
                StringAssert.StartsWith("DEBRIEF", report);
                StringAssert.Contains("held for 30 s", report);
                Assert.AreEqual(DisplayStyle.None, rig.Q<FleetMap>("reports-evidence-map").style.display.value, "the history is cleared");
                Assert.AreEqual(DisplayStyle.None, slider.style.display.value);
            }
        }

        // An answer for an incident no longer picked is dropped, and History is free again after it.
        [Test]
        public async Task AStaleReportIsDropped()
        {
            using (var rig = new IncidentsRig())
            {
                await rig.Join();
                var held = new TaskCompletionSource<HttpResponse>();
                rig.Report = _ => held.Task;
                rig.Press("reports-incident-history");
                Assert.IsFalse(rig.Q<Button>("reports-incident-history").enabledSelf, "waiting on the answer");
                rig.Q<DropdownField>("reports-incidents").value = "incident-2";
                held.SetResult(rig.Answer("/sessions/session-001/incidents/incident-1/history"));
                await rig.Settle();
                Assert.IsEmpty(rig.Q<Label>("reports-incident-report").text, "incident-1's history is not shown under incident-2");
                Assert.AreEqual(DisplayStyle.None, rig.Q<FleetMap>("reports-evidence-map").style.display.value);
                Assert.IsTrue(rig.Q<Button>("reports-incident-history").enabledSelf);
            }
        }

        // The picked incident leaving the session moves the menu without a change event; its history goes too.
        [Test]
        public async Task AnIncidentLeavingTheSnapshotClearsItsHistory()
        {
            using (var rig = new IncidentsRig())
            {
                await rig.Join();
                rig.Q<DropdownField>("reports-incidents").value = "incident-2";
                rig.Press("reports-incident-history"); await rig.Settle();
                Assert.IsNotEmpty(rig.Q<Label>("reports-incident-report").text);

                ((JArray)rig.Raw["incidents"]).RemoveAt(1);
                rig.Raw["snapshot_id"] = Guid.NewGuid().ToString();
                await rig.Session.RefreshAsync();
                Assert.AreEqual("incident-1", rig.Q<DropdownField>("reports-incidents").value);
                Assert.IsEmpty(rig.Q<Label>("reports-incident-report").text, "incident-2's history is gone");
                Assert.AreEqual(DisplayStyle.None, rig.Q<FleetMap>("reports-evidence-map").style.display.value);
                StringAssert.Contains("Lorong 6 Toa Payoh", rig.Q<Label>("reports-incident-summary").text, "incident-1 is described");
            }
        }

        // A new session is not kept waiting on the old one's report, and that report is dropped when it comes.
        [Test]
        public async Task ANewSessionFreesHistory()
        {
            using (var rig = new IncidentsRig())
            {
                await rig.Join();
                var held = new TaskCompletionSource<HttpResponse>();
                rig.Report = _ => held.Task;
                rig.Press("reports-incident-history");
                var history = rig.Q<Button>("reports-incident-history");
                Assert.IsFalse(history.enabledSelf);
                // The tab as it sees the next epoch of the session
                var tab = (IncidentReportsTab)typeof(TrainingWorkspace).GetField("incidentReports", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(rig.Workspace);
                var next = ContractParser.Parse<FullSnapshot>(rig.Raw.ToString()); next.SessionEpoch = 2;
                tab.Render(next);
                Assert.IsTrue(history.enabledSelf, "free in the new epoch");
                held.SetResult(rig.Answer("/sessions/session-001/incidents/incident-1/history"));
                await rig.Settle();
                Assert.IsEmpty(rig.Q<Label>("reports-incident-report").text, "the old epoch's history is dropped");
                Assert.IsTrue(history.enabledSelf);
            }
        }
    }
}
