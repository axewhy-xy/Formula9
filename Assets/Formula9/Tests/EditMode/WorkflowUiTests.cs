using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Backend;
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
                Assert.IsEmpty(errors);
                CollectionAssert.AreEqual(new[] { "Fleet Overview", "Route Planning", "Incident Monitor", "Charging", "Reports" }, root.Q("nav-items").Query<Label>().ToList().Select(l => l.text).ToList());
            }
            finally { session.Shutdown(); UnityEngine.Object.DestroyImmediate(rig); }
        }

        // The route page's toggle rides in a shuttle of the chosen route, keeps to the selected one
        // when that is on the route, and goes back to the map the route is drafted on.
        [Test]
        public async Task RoutePlanningTogglesTheDriverViewOfTheRoutesShuttle()
        {
            var raw = Fixture("snapshot"); raw["session"]["status"] = "READY";
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
            var rig = new GameObject("Route driver view test"); var world = rig.AddComponent<WorldView>();
            try
            {
                world.Attach(root.Q("world"), new SceneLoader(backend, url), null, null, new CityClient(backend, url));
                var workspace = new TrainingWorkspace(root, api, session, world, _ => { }, _ => { });
                var toggle = root.Q<Button>("route-driver");
                var press = typeof(TrainingWorkspace).GetMethod("ToggleDriverView", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                await session.JoinAsync((string)raw["session_id"]);
                world.Show(session.Store.Snapshot);
                workspace.ShowRoutePlanning();
                Assert.IsTrue(world.ShowingMap, "a route is drafted on the map");
                Assert.IsTrue(toggle.enabledSelf);

                press.Invoke(workspace, null);
                Assert.IsTrue(world.Driving);
                Assert.IsFalse(world.ShowingMap);
                Assert.AreEqual("shuttle-01", world.SelectedId, "the route's first shuttle");
                Assert.AreEqual("Leave driver view", toggle.text);
                Assert.IsTrue(toggle.ClassListContains("segment-selected"));

                press.Invoke(workspace, null);
                Assert.IsFalse(world.Driving);
                Assert.IsTrue(world.ShowingMap, "back on the map");
                Assert.AreEqual("Driver view", toggle.text);

                workspace.SelectVehicle("shuttle-03");
                press.Invoke(workspace, null);
                Assert.AreEqual("shuttle-03", world.SelectedId, "the selected shuttle, being on this route");
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
    }
}
