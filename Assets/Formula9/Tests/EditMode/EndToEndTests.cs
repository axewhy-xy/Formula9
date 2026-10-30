using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Formula9.Audio;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.Session;
using Formula9.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Formula9.Tests
{
    // The whole client against a real backend. Runs only when FORMULA9_E2E_BACKEND_URL points at an
    // isolated backend with throwaway accounts (in-memory store, temporary training database);
    // it signs up a random user, so never point it at the team's shared backend.
    [Category("EndToEnd")]
    public class EndToEndTests
    {
        private Uri backend;
        private UnityHttpTransport transport;
        private AuthSession auth;
        private SessionController session;
        private readonly List<string> errors = new List<string>();
        private readonly List<CommandOutcome> outcomes = new List<CommandOutcome>();
        private readonly List<string> alerts = new List<string>();

        [SetUp]
        public void SetUp()
        {
            var url = Environment.GetEnvironmentVariable("FORMULA9_E2E_BACKEND_URL");
            if (string.IsNullOrEmpty(url)) Assert.Ignore("Set FORMULA9_E2E_BACKEND_URL to an isolated test backend");
            backend = new Uri(url);
            // NUnit reuses the fixture instance, so start each test with empty records.
            errors.Clear();
            outcomes.Clear();
            alerts.Clear();
            transport = new UnityHttpTransport();
            auth = new AuthSession();
            session = new SessionController(new TrainingApi(transport, auth, backend), auth);
            session.ErrorOccurred += errors.Add;
            session.CommandFinished += outcomes.Add;
            var trainingAlerts = new TrainingAlerts(alerts.Add);
            session.EventsAccepted += events => trainingAlerts.OnEvents(events, true);
        }

        [TearDown]
        public void TearDown() => session?.Shutdown();

        private static IEnumerator Await(Task task, double seconds = 30)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!task.IsCompleted)
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("Timed out");
                yield return null;
            }
            if (task.IsFaulted) throw task.Exception.InnerException;
        }

        // Polls once a second, as the app does, until the condition holds.
        private IEnumerator PollUntil(Func<bool> done, string what, double seconds = 30)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!done())
            {
                if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for {what}; errors: {string.Join(" | ", errors)}");
                yield return Await(session.RefreshAsync());
                var pause = DateTime.UtcNow.AddMilliseconds(500);
                while (DateTime.UtcNow < pause) yield return null;
            }
        }

        private IEnumerator Control(string command, string expectedStatus)
        {
            var before = outcomes.Count;
            var send = session.SendControlAsync(command);
            yield return Await(send);
            Assert.IsTrue(send.Result, $"{command} was refused: {string.Join(" | ", errors)}");
            yield return PollUntil(() => outcomes.Skip(before).Any(o => o.Status != "QUEUED"), $"{command} outcome");
            var outcome = outcomes.Skip(before).Last();
            Assert.AreEqual("APPLIED", outcome.Status, $"{command}: {outcome.FailureCode}; {string.Join(" | ", errors)}");
            yield return PollUntil(() => session.Session.Status == expectedStatus, $"{expectedStatus} after {command}");
        }

        private IEnumerator SignUpAndLogIn()
        {
            var name = "e2e-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var password = Guid.NewGuid().ToString("N");
            var accounts = new AuthClient(transport, auth, backend);
            yield return Await(accounts.SignUpAsync(name, password));
            auth.End();
            yield return Await(accounts.LogInAsync(name, password));
            Assert.IsTrue(auth.IsLoggedIn);
            Assert.AreEqual(name, auth.Username);
        }

        // The integrated exercise opens with a shuttle on a closed road; the backend refuses START
        // until a detour is approved. The client must predict that (Start disabled) and, if sent
        // anyway, report the refusal. Approving the detour needs the route planning screen.
        [UnityTest]
        public IEnumerator IntegratedExerciseWaitsForADetourBeforeStarting()
        {
            yield return SignUpAndLogIn();
            session.Scenario = SessionController.IntegratedScenario;
            yield return Await(session.CreateAsync());
            Assert.IsTrue(session.Available, string.Join(" | ", errors));
            var snapshot = session.Store.Snapshot;
            Assert.AreEqual("READY", snapshot.Session.Status);
            Assert.IsTrue(UI.TrainingPresenter.AssignedRouteIsClosed(snapshot));
            Assert.IsFalse(UI.TrainingPresenter.Controls(snapshot, true, true).Start, "the UI keeps Start disabled");

            var send = session.SendControlAsync("START");
            yield return Await(send);
            yield return PollUntil(() => outcomes.Any(o => o.Status != "QUEUED"), "the START outcome");
            var outcome = outcomes.Last();
            Assert.AreEqual("REJECTED", outcome.Status);
            Assert.AreEqual("UNKNOWN_CONSTRAINT", outcome.FailureCode);
            StringAssert.StartsWith("Can't start yet", UI.TrainingPresenter.Outcome(outcome));
            yield return Control("ARCHIVE", "ARCHIVED");
        }

        [UnityTest]
        public IEnumerator IntegratedExerciseApprovesDetourChargingAndIncidentDecisions()
        {
            yield return SignUpAndLogIn();
            session.Scenario = SessionController.IntegratedScenario;
            yield return Await(session.CreateAsync());
            var api = new TrainingApi(transport, auth, backend);
            var s = session.Store.Snapshot;
            var route = s.Vehicles[0].RouteId;
            var context = api.GetRouteEditorAsync(s.SessionId, route);
            yield return Await(context);
            var preview = api.PreviewRouteAsync(s.SessionId, new RouteEditRequest
            { RouteId = route, ExpectedRouteRevision = context.Result.RouteRevision, DraftRevision = 1 });
            yield return Await(preview);
            Assert.IsTrue(preview.Result.Approvable, string.Join("; ", preview.Result.Checks.Select(c => c.Message)));
            var before = outcomes.Count;
            yield return Await(session.ApproveRouteAsync(preview.Result, "Use validated bypass before departure"));
            yield return PollUntil(() => outcomes.Skip(before).Any(o => o.Status == "APPLIED"), "detour approval");
            yield return Await(session.RefreshAsync());
            Assert.IsFalse(UI.TrainingPresenter.AssignedRouteIsClosed(session.Store.Snapshot));
            yield return Control("START", "RUNNING");

            yield return PollUntil(() => session.Store.Snapshot.ChargeOptions.Any(o => o.Action == "CHARGE_NOW" && o.Validation == "PASS"), "charging offers");
            s = session.Store.Snapshot;
            var offer = s.ChargeOptions.First(o => o.Action == "CHARGE_NOW" && o.Validation == "PASS");
            before = outcomes.Count;
            yield return Await(session.SubmitAsync(DecisionRules.ChargeCommand(s, offer.OptionId, "Reserve a bay to preserve battery reserve")));
            yield return PollUntil(() => outcomes.Skip(before).Any(o => o.Status == "APPLIED"), "charger reservation");

            yield return PollUntil(() => session.Store.Snapshot.Incidents.Any(i => i.State == "OPTIONS_READY"), "incident options", 45);
            s = session.Store.Snapshot;
            var incident = s.Incidents.First(i => i.State == "OPTIONS_READY");
            var candidate = s.Candidates.First(c => c.IncidentId == incident.IncidentId && c.Action == "WAIT" && c.Validation == "PASS");
            var command = DecisionRules.IncidentCommand(s, incident.IncidentId, candidate.CandidateId, "Hold safely until the obstruction clears");
            var branch = api.PreviewIncidentAsync(command); yield return Await(branch);
            Assert.IsTrue((bool)branch.Result["source"]["authored"]);
            Assert.AreNotEqual((string)branch.Result["base_checkpoint_id"], (string)branch.Result["branch_id"]);
            Assert.AreEqual("OPTIONS_READY", session.Store.Snapshot.Incidents.Single(i => i.IncidentId == incident.IncidentId).State);
            var history = api.GetIncidentReportAsync(s.SessionId, incident.IncidentId, false); yield return Await(history);
            Assert.AreEqual("HISTORICAL_SIMULATION", (string)history.Result["source_mode"]);
            before = outcomes.Count;
            yield return Await(session.SubmitAsync(command));
            yield return PollUntil(() => outcomes.Skip(before).Any(o => o.Status == "APPLIED"), "incident decision");
            var debrief = api.GetIncidentReportAsync(s.SessionId, incident.IncidentId, true); yield return Await(debrief);
            Assert.IsNotEmpty((Newtonsoft.Json.Linq.JArray)debrief.Result["decisions"]);
            Assert.IsTrue(session.Store.Events.Any(e => e.EventType == "ROUTE_APPROVED"));
            yield return Control("ARCHIVE", "ARCHIVED");
        }

        [UnityTest]
        public IEnumerator PlanarExerciseRunsThroughItsLifecycle()
        {
            yield return SignUpAndLogIn();
            var list = new TrainingApi(transport, auth, backend).ListSessionsAsync();
            yield return Await(list);
            Assert.GreaterOrEqual(list.Result.Capacity, 1);

            session.Scenario = "scenario-m2-r1";
            yield return Await(session.CreateAsync());
            Assert.IsFalse(session.CreationPending, string.Join(" | ", errors));
            Assert.IsTrue(session.Available, string.Join(" | ", errors));
            Assert.AreEqual("READY", session.Session.Status);
            var snapshot = session.Store.Snapshot;
            Assert.Greater(snapshot.Vehicles.Count, 0);
            Debug.Log($"E2E: created {session.SessionId} ({snapshot.Vehicles.Count} shuttles, scenario {snapshot.Session.ScenarioRevisionId})");

            yield return Control("START", "RUNNING");
            var startTick = session.Store.Snapshot.Tick;
            yield return PollUntil(() => session.Store.Snapshot.Tick > startTick + 5, "the clock to advance");
            yield return Control("PAUSE", "PAUSED");
            var pausedTick = session.Store.Snapshot.Tick;
            yield return PollUntil(() => true, "one more poll");
            Assert.AreEqual(pausedTick, session.Store.Snapshot.Tick, "a paused session does not advance");
            yield return Control("RESUME", "RUNNING");
            Assert.Greater(session.Store.Events.Count, 0, "events were replayed into the store");
            yield return Control("ARCHIVE", "ARCHIVED");
            Debug.Log($"E2E: lifecycle done at tick {session.Store.Snapshot.Tick}; {session.Store.Events.Count} events; alerts [{string.Join(", ", alerts)}]");
        }

        [UnityTest]
        public IEnumerator SpatialExerciseLoadsAndPlacesItsWorld()
        {
            yield return SignUpAndLogIn();
            session.Scenario = "scenario-m2-road-3d-r1";
            yield return Await(session.CreateAsync());
            Assert.IsTrue(session.Available, string.Join(" | ", errors));
            var snapshot = session.Store.Snapshot;
            Assert.IsNotNull(snapshot.Spatial, "a 3D session pins its scene");

            var load = new SceneLoader(transport, backend).LoadAsync(snapshot.Spatial);
            yield return Await(load);
            var build = WorldBuilder.BuildAsync(load.Result, null);
            yield return Await(build, 60);
            var world = build.Result;
            try
            {
                Assert.IsNull(world.ApplyPoses(snapshot));
                yield return Control("START", "RUNNING");
                var before = world.Entities[snapshot.Vehicles[0].VehicleId].position;
                var startTick = session.Store.Snapshot.Tick;
                yield return PollUntil(() => session.Store.Snapshot.Tick > startTick + 20, "the 3D clock to advance");
                Assert.IsNull(world.ApplyPoses(session.Store.Snapshot));
                var moved = session.Store.Snapshot.Vehicles.Count(v =>
                    Vector3.Distance(world.Entities[v.VehicleId].position, WorldMath.ToUnity(v.Spatial.PositionM)) < 0.01f);
                Assert.AreEqual(session.Store.Snapshot.Vehicles.Count, moved, "every shuttle sits on its live pose");
                Debug.Log($"E2E: 3D session {session.SessionId}, {world.Entities.Count} objects; shuttle-01 moved " +
                          $"{Vector3.Distance(before, world.Entities[snapshot.Vehicles[0].VehicleId].position):0.00} m");
                yield return Control("ARCHIVE", "ARCHIVED");
            }
            finally
            {
                WorldBuilder.Remove(world.Root.gameObject);
            }
        }
    }
}
