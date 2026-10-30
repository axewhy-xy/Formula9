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
        public async Task ARouteGraphArrivingAfterARevisionChangeReloadsBeforeEnablingPreview()
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
                var validate = root.Query<Button>().ToList().Single(b => b.text == "Validate / preview");
                Assert.IsFalse(validate.enabledSelf, "the first graph is still in flight");
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
                Assert.IsTrue(validate.enabledSelf, string.Join("; ", errors.Select(e => e.Message)));
                Assert.IsEmpty(errors);
                CollectionAssert.IsSubsetOf(new[] { "Fleet", "Route planning", "Charging", "Incidents", "Reports" }, root.Query<Button>().ToList().Select(b => b.text));
            }
            finally { session.Shutdown(); UnityEngine.Object.DestroyImmediate(rig); }
        }
    }
}
