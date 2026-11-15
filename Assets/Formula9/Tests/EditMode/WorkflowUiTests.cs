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

        [TestCase("ARCHIVED")]
        [TestCase("COMPLETED")]
        [TestCase("FAILED")]
        public async Task ChargingFoldsTheStatusBarAndFinishedAttemptsReopenSetup(string terminalState)
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
                Assert.IsFalse(card.ClassListContains("session-tools-hidden"));
                await session.JoinAsync((string)raw["session_id"]);
                Assert.IsTrue(card.ClassListContains("session-tools-hidden"));
                Assert.IsFalse(card.ClassListContains("session-compact"), "Fleet keeps its status bar");
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                typeof(TrainingWorkspace).GetMethod("ShowPage", flags).Invoke(workspace, new object[] { 2 });
                Assert.IsTrue(card.ClassListContains("session-compact"), "Charging folds the extra status row and header spacing");
                var toggle = typeof(TrainingWorkspace).GetMethod("ToggleSessionTools", flags);
                toggle.Invoke(workspace, null);
                Assert.IsFalse(card.ClassListContains("session-tools-hidden"));
                Assert.IsFalse(card.ClassListContains("session-compact"));
                toggle.Invoke(workspace, null);
                Assert.IsTrue(card.ClassListContains("session-tools-hidden"), "explicitly closed before the session ends");
                raw["session"]["status"] = terminalState;
                raw["snapshot_id"] = Guid.NewGuid().ToString();
                await session.RefreshAsync();
                Assert.IsFalse(card.ClassListContains("session-tools-hidden"), "Create and Join must reappear when the attempt ends");
                Assert.IsFalse(card.ClassListContains("session-compact"));
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
                CollectionAssert.AreEqual(new[] { "Fleet Overview", "Route Planning", "Incident Monitor", "Charging", "Reports" }, root.Q("nav-items").Query<Label>().ToList().Select(l => l.text).ToList());
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
                var toggle = root.Q<Button>("fleet-cameras");
                var press = typeof(TrainingWorkspace).GetMethod("ToggleSelectedCameras", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var choose = typeof(TrainingWorkspace).GetMethod("ChooseShuttle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var picker = root.Q<DropdownField>("shuttles");
                var reason = root.Q<TextField>("route-reason");
                string Panel() => picker.value.Split(' ')[1];
                List<Button> Items() => root.Q("route-pending-items").Query<Button>().ToList();
                List<string> Strip() => Items().Select(b => b.text).ToList();
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
                CollectionAssert.AreEqual(new[] { "● Shuttle 01 · ~8 s" }, Strip());
                Assert.AreEqual("Needs a decision (1)", root.Q<Label>("route-pending-title").text);

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
                CollectionAssert.AreEqual(new[] { "● Shuttle 05 · ~2 s", "● Shuttle 01 · ~8 s" }, Strip(), "soonest first");
                Assert.IsTrue(Shown("page-fleet"), "the page stays");
                Assert.IsTrue(world.CamerasShown, "still watching");
                Assert.AreEqual("shuttle-04", world.WatchedId, "still watching the shuttle they chose");
                Assert.AreEqual("Hide cameras", toggle.text);

                // Mid-draft the panel never moves
                choose.Invoke(workspace, new object[] { "shuttle-04" });
                reason.value = "Keeping an eye on 04";
                await Offer(("shuttle-05", "ROAD_DETOUR", 2), ("shuttle-02", "KEEP_ROUTE", 1));
                workspace.Render();
                Assert.AreEqual("04", Panel(), "a reason is being typed");
                Assert.AreEqual("Keeping an eye on 04", reason.value);
                CollectionAssert.AreEqual(new[] { "● Shuttle 02 · ~1 s", "● Shuttle 05 · ~2 s", "● Shuttle 01 · ~8 s" }, Strip());
                Assert.AreEqual("Needs a decision (3)", root.Q<Label>("route-pending-title").text);

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
                Assert.IsTrue(world.ShowingMap, "its detour is drafted on the map");
                Assert.IsTrue(Items().Single(b => b.text.Contains("Shuttle 05")).ClassListContains("route-pending-item-selected"));
                Assert.IsFalse(Items().Single(b => b.text.Contains("Shuttle 01")).ClassListContains("route-pending-item-selected"));

                // Its options withdrawn: idle, the panel moves to the soonest still waiting
                await Offer(("shuttle-02", "KEEP_ROUTE", 1));
                workspace.Render();
                CollectionAssert.AreEqual(new[] { "● Shuttle 02 · ~1 s", "● Shuttle 01 · ~8 s" }, Strip());
                Assert.AreEqual("02", Panel());

                // Nobody waits any more: the strip goes, and the panel stays where it is
                raw["snapshot_id"] = Guid.NewGuid().ToString();
                raw["incidents"] = new JArray(); raw["candidates"] = new JArray();
                await session.RefreshAsync();
                workspace.Render();
                Assert.AreEqual(DisplayStyle.None, root.Q("route-pending").style.display.value);
                Assert.AreEqual("Shuttle 02 · route-a", picker.value);
                Assert.IsEmpty(notices);
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
                Assert.GreaterOrEqual(tables.Count, 7, "Charging, Incidents and Reports tables");
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

        // Pages are found by name, so a page the layout leaves out moves no other page and breaks nothing.
        [Test]
        public async Task ThePagesAreFoundByName()
        {
            using (var rig = new IncidentsRig(root => root.Q("page-incident").name = "page-gone"))
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
