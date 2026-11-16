/*
 * End-to-end tests of the whole Unity client against a real backend.
 *
 * Author: Choo Jian Wei
 * Contributors: Lau Xiang Yang (detour, charging and incident decisions)
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Runs only when FORMULA9_E2E_BACKEND_URL points at an isolated backend with throwaway accounts
 *     (Tools/isolated_backend.py: in-memory accounts, temporary training database). Each test signs
 *     up a random user, so never point it at the team's shared backend. The tests go over real
 *     HTTP: log in, create, start, pause, resume and archive sessions, approve the integrated
 *     detour, reserve a charger, decide an incident, and load and place a live 3D world.
 */

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
    // -----------------------------------------------------------------------------------
    // Class: EndToEndTests
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class drives the real client layers against an isolated backend (category EndToEnd).
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

        // -----------------------------------------------------------------------------------
        // Function: SetUp
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function skips the test unless an isolated backend is configured, and builds a
        //     fresh login and session controller. NUnit reuses the fixture instance, so each test
        //     starts with empty records of errors, outcomes and alerts.
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

        // -----------------------------------------------------------------------------------
        // Function: TearDown
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function stops the session controller.
        [TearDown]
        public void TearDown() => session?.Shutdown();

        // -----------------------------------------------------------------------------------
        // Function: Await
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function yields until a task finishes, failing on timeout and rethrowing its
        //     error.
        //
        // Args:
        //     task (Task): The task.
        //     seconds (double): The deadline.
        //
        // Returns:
        //     IEnumerator: The wait.
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

        // -----------------------------------------------------------------------------------
        // Function: PollUntil
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function polls the session about twice a second, as the app does, until a
        //     condition holds.
        //
        // Args:
        //     done (Func<bool>): The condition.
        //     what (string): What is awaited, for the failure message.
        //     seconds (double): The deadline.
        //
        // Returns:
        //     IEnumerator: The wait.
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

        // -----------------------------------------------------------------------------------
        // Function: Control
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function sends a session control, waits until it is applied, then until the
        //     session reaches the expected status.
        //
        // Args:
        //     command (string): START, PAUSE, RESUME or ARCHIVE.
        //     expectedStatus (string): The status it leads to.
        //
        // Returns:
        //     IEnumerator: The step.
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

        // -----------------------------------------------------------------------------------
        // Function: SignUpAndLogIn
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function signs up a random throwaway user, then logs in as it.
        //
        // Returns:
        //     IEnumerator: The step.
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

        // -----------------------------------------------------------------------------------
        // Function: IntegratedExerciseStartsWithItsRoutesOpen
        // -----------------------------------------------------------------------------------
        // Description:
        //     The integrated exercise opens with every route clear, so Start is offered and the
        //     backend applies it straight away.
        [UnityTest]
        public IEnumerator IntegratedExerciseStartsWithItsRoutesOpen()
        {
            yield return SignUpAndLogIn();
            session.Scenario = SessionController.IntegratedScenario;
            yield return Await(session.CreateAsync());
            Assert.IsTrue(session.Available, string.Join(" | ", errors));
            var snapshot = session.Store.Snapshot;
            Assert.AreEqual("READY", snapshot.Session.Status);
            Assert.IsFalse(UI.TrainingPresenter.AssignedRouteIsClosed(snapshot));
            Assert.IsTrue(UI.TrainingPresenter.Controls(snapshot, true, true).Start, "the UI offers Start");
            yield return Control("START", "RUNNING");
            yield return Control("ARCHIVE", "ARCHIVED");
        }

        // A time slot goes through the backend's check, then is booked, moved and cancelled.
        [UnityTest]
        public IEnumerator AChargingSlotIsCheckedBookedMovedAndCancelledOnTheRealBackend()
        {
            yield return SignUpAndLogIn();
            session.Scenario = SessionController.IntegratedScenario;
            yield return Await(session.CreateAsync());
            var api = new TrainingApi(transport, auth, backend);
            var s = session.Store.Snapshot;
            var route = s.Vehicles[0].RouteId;
            var context = api.GetRouteEditorAsync(s.SessionId, route); yield return Await(context);
            var draft = api.PreviewRouteAsync(s.SessionId, new RouteEditRequest { RouteId = route, ExpectedRouteRevision = context.Result.RouteRevision, DraftRevision = 1 });
            yield return Await(draft);
            var before = outcomes.Count;
            yield return Await(session.ApproveRouteAsync(draft.Result, "Use validated bypass before departure"));
            yield return PollUntil(() => outcomes.Skip(before).Any(o => o.Status == "APPLIED"), "detour approval");
            yield return Await(session.RefreshAsync());
            yield return Control("START", "RUNNING");

            s = session.Store.Snapshot;
            var (charger, start) = UI.ChargingPresenter.SuggestSlot(s);
            start += 2000;
            var check = api.PreviewChargeScheduleAsync(s.SessionId, new ChargeScheduleRequest { VehicleId = "shuttle-01", ChargerId = charger, StartTick = start });
            yield return Await(check);
            Assert.AreNotEqual("FAIL", check.Result.Validation, string.Join("; ", check.Result.Issues.Select(i => i.Message)));

            before = outcomes.Count;
            yield return Await(session.SubmitAsync(DecisionRules.ScheduleChargeCommand(s, "shuttle-01", charger, start, null, "Book ahead to keep cover")));
            yield return PollUntil(() => outcomes.Skip(before).Any(o => o.Status == "APPLIED"), "slot booking");
            yield return Await(session.RefreshAsync());
            var booked = session.Store.Snapshot.ChargeBookings.Single(b => b.VehicleId == "shuttle-01" && b.Status == "SCHEDULED");
            Assert.AreEqual(start, booked.StartTick);

            before = outcomes.Count;
            yield return Await(session.SubmitAsync(DecisionRules.ScheduleChargeCommand(session.Store.Snapshot, "shuttle-01", charger, start + 400, booked.BookingId, "Later keeps more cover")));
            yield return PollUntil(() => outcomes.Skip(before).Any(o => o.Status == "APPLIED"), "slot move");
            yield return Await(session.RefreshAsync());
            var moved = session.Store.Snapshot.ChargeBookings.Single(b => b.VehicleId == "shuttle-01" && b.Status == "SCHEDULED");
            Assert.AreEqual(start + 400, moved.StartTick);
            Assert.AreNotEqual(booked.BookingId, moved.BookingId);

            before = outcomes.Count;
            yield return Await(session.SubmitAsync(DecisionRules.CancelChargeBookingCommand(session.Store.Snapshot, moved.BookingId, "No longer needed")));
            yield return PollUntil(() => outcomes.Skip(before).Any(o => o.Status == "APPLIED"), "slot cancel");
            yield return Await(session.RefreshAsync());
            Assert.IsFalse(session.Store.Snapshot.ChargeBookings.Any(b => b.VehicleId == "shuttle-01" && b.Status == "SCHEDULED"));
        }

        // -----------------------------------------------------------------------------------
        // Function: IntegratedExerciseApprovesDetourChargingAndIncidentDecisions
        // -----------------------------------------------------------------------------------
        // Description:
        //     The integrated exercise runs after a detour is approved, then a charger reservation and
        //     an incident decision are applied, and the incident's history and debrief can be read.
        //     The incident is the collision the backend seeds (Tools/isolated_backend.py
        //     --seed-incidents), decided before the shuttles reach it.
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

            // A charging option is reissued as the forecasts move: one chosen from a snapshot that is already
            // out of date is refused as stale, and is chosen again from a fresh one, as the app does
            CommandOutcome reserved = null;
            for (var attempt = 0; attempt < 3 && reserved?.Status != "APPLIED"; attempt++)
            {
                yield return PollUntil(() => session.Store.Snapshot.ChargeOptions.Any(o => o.Action == "CHARGE_NOW" && o.Validation == "PASS"), "charging offers");
                s = session.Store.Snapshot;
                var offer = s.ChargeOptions.First(o => o.Action == "CHARGE_NOW" && o.Validation == "PASS");
                before = outcomes.Count;
                yield return Await(session.SubmitAsync(DecisionRules.ChargeCommand(s, offer.OptionId, "Reserve a bay to preserve battery reserve")));
                yield return PollUntil(() => outcomes.Skip(before).Any(o => o.Status != "QUEUED" && o.Status != "PENDING_CONFIRMATION"), "charger reservation");
                reserved = outcomes.Skip(before).Last();
                if (reserved.Status != "APPLIED") Assert.AreEqual("STALE_REVISION", reserved.FailureCode, $"charger reservation: {reserved.Status}; {string.Join(" | ", errors)}");
            }
            Assert.AreEqual("APPLIED", reserved.Status, "charger reservation, chosen afresh after each stale refusal");

            const string Seeded = "scheduled-two-shuttles";
            bool KeepRoute(Candidate c) => c.IncidentId == Seeded && (c.Action == "KEEP_ROUTE" || c.Action == "WAIT") && c.Validation == "PASS";
            yield return PollUntil(() => session.Store.Snapshot.Candidates.Any(KeepRoute), "the seeded collision's Keep route option (run the backend with --seed-incidents)", 60);
            s = session.Store.Snapshot;
            var incident = s.Incidents.Single(i => i.IncidentId == Seeded);
            var candidate = s.Candidates.First(KeepRoute);
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

        // -----------------------------------------------------------------------------------
        // Function: PlanarExerciseRunsThroughItsLifecycle
        // -----------------------------------------------------------------------------------
        // Description:
        //     A planar session is listed, created, started, paused, resumed and archived, with
        //     alerts and outcomes reported along the way.
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

        // -----------------------------------------------------------------------------------
        // Function: SpatialExerciseLoadsAndPlacesItsWorld
        // -----------------------------------------------------------------------------------
        // Description:
        //     A 3D session's scene downloads and verifies against its pins, builds, and places
        //     every shuttle at its snapshot pose.
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
