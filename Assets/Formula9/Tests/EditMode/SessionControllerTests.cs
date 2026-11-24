/*
 * Unit tests for the session layer (SessionController.cs, SessionStore.cs).
 *
 * Author: Choo Jian Wei
 * Contributors: Lau Xiang Yang (leaving a deleted session, polling traffic)
 * Date Created: 30/10/2026
 * Last Modified: 11/11/2026
 *
 * Description:
 *     Uses a scripted fake backend serving the shared fixtures and an instant retry delay, so the
 *     whole join, poll, event, command and creation flow runs without a backend. Covers event
 *     cursors, gaps and recovery, the one open command and its confirmation with the same key,
 *     retries and final refusals, logins expiring mid-command, leaving or switching sessions, and
 *     polling the traffic. And Archive all: it carries on past a session that fails, counts a
 *     queued archive only once the session is no longer live, and archives the joined session
 *     through its own command.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.Session;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Formula9.Tests
{
    // -----------------------------------------------------------------------------------
    // Class: SessionControllerTests
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class tests the session controller against a fake backend.
    public partial class SessionControllerTests
    {
        // -----------------------------------------------------------------------------------
        // Function: Fixture
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reads a shared fixture.
        //
        // Args:
        //     name (string): The fixture name, without .json.
        //
        // Returns:
        //     JObject: The fixture.
        private static JObject Fixture(string name) =>
            JObject.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures", name + ".json")));

        // -----------------------------------------------------------------------------------
        // Function: Json
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function makes a JSON reply.
        //
        // Args:
        //     body (JToken): The body.
        //     status (int): The status, 200 by default.
        //
        // Returns:
        //     HttpResponse: The reply.
        private static HttpResponse Json(JToken body, int status = 200) => new HttpResponse { Status = status, Body = body.ToString() };

        // -----------------------------------------------------------------------------------
        // Function: Error
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function makes a backend error reply.
        //
        // Args:
        //     status (int): The HTTP status.
        //     code (string): The error code.
        //
        // Returns:
        //     HttpResponse: The reply.
        private static HttpResponse Error(int status, string code) => Json(new JObject
        {
            ["error"] = new JObject { ["code"] = code, ["message"] = code, ["retryable"] = false },
        }, status);

        // -----------------------------------------------------------------------------------
        // Class: FakeBackend
        // -----------------------------------------------------------------------------------
        // Description:
        //     This class is a scripted backend serving the shared fixtures. Tests replace the
        //     handlers they need.
        //
        //     Attributes: Sent lists the requests; Snapshot is served for snapshot requests unless
        //     SnapshotReply is set; LastCommand is the last command submitted; OnEvents, OnCommand,
        //     OnStatus, OnCreate and OnTraffic answer those requests.
        private sealed class FakeBackend : IHttpTransport
        {
            public readonly List<HttpRequest> Sent = new List<HttpRequest>();
            public JObject Snapshot = Fixture("snapshot");
            public JObject LastCommand;
            public HttpResponse SnapshotReply;
            public Func<HttpRequest, HttpResponse> OnEvents;
            public Func<HttpRequest, HttpResponse> OnCommand;
            public Func<HttpRequest, HttpResponse> OnStatus;
            public Func<HttpRequest, HttpResponse> OnCreate;
            public Func<HttpRequest, HttpResponse> OnTraffic;
            // The session list and, when set, each session's own snapshot (Archive all asks for several)
            public Func<HttpRequest, HttpResponse> OnList = _ => Error(404, "INVALID_REFERENCE");
            public Func<HttpRequest, HttpResponse> OnSnapshot;

            // -----------------------------------------------------------------------------------
            // Function: FakeBackend (constructor)
            // -----------------------------------------------------------------------------------
            // Description:
            //     This function sets default handlers: no new events, commands applied at once, and
            //     no traffic, as a backend that drives none answers.
            public FakeBackend()
            {
                OnEvents = request => Json(Replay(After(request)));
                OnCommand = _ => Json(Status("APPLIED"), 202);
                OnStatus = _ => Json(Status("APPLIED"));
                OnTraffic = _ => Error(404, "INVALID_REFERENCE");
            }

            // -----------------------------------------------------------------------------------
            // Function: To
            // -----------------------------------------------------------------------------------
            // Description:
            //     This function finds the requests sent to paths ending in a suffix.
            //
            // Args:
            //     suffix (string): The path ending.
            //     method (string): The HTTP method, or null for any.
            //
            // Returns:
            //     IEnumerable<HttpRequest>: The matching requests.
            public IEnumerable<HttpRequest> To(string suffix, string method = null) =>
                Sent.Where(r => r.Url.AbsolutePath.EndsWith(suffix) && (method == null || r.Method == method));

            // -----------------------------------------------------------------------------------
            // Function: FakeBackend.SendAsync
            // -----------------------------------------------------------------------------------
            // Description:
            //     This function records the request and answers it with the handler for its path.
            //
            // Args:
            //     request (HttpRequest): The request.
            //     cancellation (CancellationToken): Unused.
            //
            // Returns:
            //     Task<HttpResponse>: The reply.
            public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellation = default)
            {
                Sent.Add(request);
                var path = request.Url.AbsolutePath;
                if (request.Method == "POST" && path == "/api/v2/sessions") return Task.FromResult(OnCreate(request));
                if (request.Method == "GET" && path == "/api/v2/sessions") return Task.FromResult(OnList(request));
                if (path.EndsWith("/snapshot") && OnSnapshot != null) return Task.FromResult(OnSnapshot(request));
                if (path.EndsWith("/snapshot")) return Task.FromResult(SnapshotReply ?? Json(Snapshot));
                if (path.EndsWith("/events")) return Task.FromResult(OnEvents(request));
                if (path.EndsWith("/traffic")) return Task.FromResult(OnTraffic(request));
                if (request.Method == "POST" && path.EndsWith("/commands"))
                {
                    LastCommand = JObject.Parse(request.Body);
                    return Task.FromResult(OnCommand(request));
                }
                if (path.Contains("/commands/")) return Task.FromResult(OnStatus(request));
                return Task.FromResult(Error(404, "INVALID_REFERENCE"));
            }

            // -----------------------------------------------------------------------------------
            // Function: After
            // -----------------------------------------------------------------------------------
            // Description:
            //     This function reads the after cursor of an events request.
            //
            // Args:
            //     request (HttpRequest): The events request.
            //
            // Returns:
            //     long: The cursor.
            public static long After(HttpRequest request) =>
                long.Parse(request.Url.Query.TrimStart('?').Split('&').First(p => p.StartsWith("after=")).Substring(6));

            // -----------------------------------------------------------------------------------
            // Function: AfterTick
            // -----------------------------------------------------------------------------------
            // Description:
            //     This function reads the after_tick cursor of a traffic request.
            //
            // Args:
            //     request (HttpRequest): The traffic request.
            //
            // Returns:
            //     long: The cursor.
            public static long AfterTick(HttpRequest request) =>
                long.Parse(request.Url.Query.TrimStart('?').Split('&').First(p => p.StartsWith("after_tick=")).Substring(11));

            // -----------------------------------------------------------------------------------
            // Function: Traffic
            // -----------------------------------------------------------------------------------
            // Description:
            //     This function makes a traffic reply for the snapshot's session with one frame a
            //     tick, each with one vehicle and one shuttle.
            //
            // Args:
            //     ticks (long[]): The frames' ticks, oldest first.
            //
            // Returns:
            //     JObject: The reply.
            public JObject Traffic(params long[] ticks) => new JObject
            {
                ["session_id"] = Snapshot["session_id"],
                ["frames"] = new JArray(ticks.Select(tick => new JObject
                {
                    ["tick"] = tick,
                    ["simulation_time_s"] = tick * 0.05,
                    ["vehicle_count"] = 1,
                    ["vehicles"] = TrafficFrame.PackVehicles(3, 1200, -450, 900, 1000),
                    ["shuttles"] = new JArray(new JObject
                    {
                        ["vehicle_id"] = "shuttle-01", ["x_m"] = 1.5, ["y_m"] = -2.0, ["heading_deg"] = 90.0, ["speed_mps"] = 4.2,
                    }),
                })),
            };

            // -----------------------------------------------------------------------------------
            // Function: Replay
            // -----------------------------------------------------------------------------------
            // Description:
            //     This function makes an event page after the cursor with count new events,
            //     contiguous unless told otherwise.
            //
            // Args:
            //     after (long): The cursor.
            //     count (int): How many events.
            //     firstSequence (long?): The first event's sequence, to make a gap; after + 1 when
            //         null.
            //
            // Returns:
            //     JObject: The replay.
            public JObject Replay(long after, int count = 0, long? firstSequence = null)
            {
                var template = Fixture("event_replay");
                var sample = (JObject)template["events"][0];
                var events = new JArray();
                var first = firstSequence ?? after + 1;
                for (var i = 0; i < count; i++)
                {
                    var e = (JObject)sample.DeepClone();
                    e["event_sequence"] = first + i;
                    e["event_id"] = $"event-{first + i}";
                    events.Add(e);
                }
                template["session_id"] = Snapshot["session_id"];
                template["session_epoch"] = Snapshot["session_epoch"];
                template["after_sequence"] = after;
                template["events"] = events;
                template["next_sequence"] = count == 0 ? after : first + count - 1;
                template["event_sequence"] = Math.Max((long)template["next_sequence"], (long)Snapshot["event_sequence"]);
                return template;
            }

            // -----------------------------------------------------------------------------------
            // Function: Status
            // -----------------------------------------------------------------------------------
            // Description:
            //     This function makes the journaled status for the last submitted command.
            //
            // Args:
            //     status (string): The status.
            //     failureCode (string): The failure code, or null.
            //
            // Returns:
            //     JObject: The command status.
            public JObject Status(string status, string failureCode = null)
            {
                var value = Fixture("command_status");
                foreach (var field in new[] { "command_id", "idempotency_key", "target_type", "target_id" })
                    if (LastCommand != null) value[field] = LastCommand[field];
                value["status"] = status;
                if (status != "APPLIED") value["applied_tick"] = value["result_event_id"] = null;
                value["failure_code"] = failureCode;
                return value;
            }
        }

        private FakeBackend backend;
        private AuthSession auth;
        private TrainingApi api;
        private SessionController controller;
        private List<TimeSpan> waits;
        private List<string> errors;
        private List<CommandOutcome> outcomes;
        private List<(FullSnapshot snapshot, bool reset)> snapshots;
        // The controller's clock, which a test moves on itself
        private DateTime clock;
        private const string SessionId = "session-001";

        // -----------------------------------------------------------------------------------
        // Function: SetUp
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function gives each test a fresh fake backend, a login, a controller with an
        //     instant retry delay and a clock of its own, and lists of the errors, outcomes and
        //     snapshots it reports.
        [SetUp]
        public void SetUp()
        {
            backend = new FakeBackend();
            auth = new AuthSession();
            auth.Begin("trainee", "token-1", 3600);
            waits = new List<TimeSpan>();
            clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            api = new TrainingApi(backend, auth, new Uri("http://backend.test"));
            controller = new SessionController(api, auth,
                delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; }, now: () => clock);
            errors = new List<string>();
            outcomes = new List<CommandOutcome>();
            snapshots = new List<(FullSnapshot, bool)>();
            controller.ErrorOccurred += errors.Add;
            controller.CommandFinished += outcomes.Add;
            controller.SnapshotAccepted += (s, reset) => snapshots.Add((s, reset));
        }

        // -----------------------------------------------------------------------------------
        // Function: ASessionDeletedElsewhereIsLeftAndNoLongerPolled
        // -----------------------------------------------------------------------------------
        // Description:
        //     A session another client deleted is left with a message and no longer polled.
        [Test]
        public async Task ASessionDeletedElsewhereIsLeftAndNoLongerPolled()
        {
            await controller.JoinAsync(SessionId);
            var left = 0;
            controller.SessionLeft += () => left++;
            backend.SnapshotReply = Error(404, "INVALID_REFERENCE");
            await controller.RefreshAsync();
            Assert.AreEqual(1, left);
            Assert.IsNull(controller.SessionId);
            Assert.IsNull(controller.Session);
            Assert.IsNull(controller.Store.Snapshot);
            Assert.IsFalse(controller.Available);
            StringAssert.Contains("no longer exists", errors.Last());
            var sent = backend.Sent.Count;
            await controller.RefreshAsync();
            Assert.AreEqual(sent, backend.Sent.Count);
        }

        // -----------------------------------------------------------------------------------
        // Function: TheBackendDroppingAndComingBackIsReportedOnceEach
        // -----------------------------------------------------------------------------------
        // Description:
        //     A joined session's failing polls report one drop, and the first poll that succeeds
        //     again reports one return.
        [Test]
        public async Task TheBackendDroppingAndComingBackIsReportedOnceEach()
        {
            var changes = new List<bool>();
            controller.ConnectionChanged += changes.Add;
            await controller.JoinAsync(SessionId);
            Assert.IsEmpty(changes, "joining is not a reconnection");
            backend.SnapshotReply = new HttpResponse { Failure = HttpFailure.Incomplete };
            await controller.RefreshAsync();
            await controller.RefreshAsync();
            CollectionAssert.AreEqual(new[] { false }, changes, "one drop, however many polls fail");
            backend.SnapshotReply = null;
            await controller.RefreshAsync();
            await controller.RefreshAsync();
            CollectionAssert.AreEqual(new[] { false, true }, changes, "one return");
        }

        // -----------------------------------------------------------------------------------
        // Function: JoiningLeavingAndResyncingAreNotConnectionChanges
        // -----------------------------------------------------------------------------------
        // Description:
        //     Availability also changes when joining, leaving or resyncing, but none of those is the
        //     backend dropping. A drop that ends in leaving reports no return.
        [Test]
        public async Task JoiningLeavingAndResyncingAreNotConnectionChanges()
        {
            var changes = new List<bool>();
            controller.ConnectionChanged += changes.Add;
            await controller.JoinAsync(SessionId);
            await controller.JoinAsync(SessionId);
            backend.SnapshotReply = Json(new JObject { ["not"] = "a snapshot" });
            await controller.RefreshAsync();
            backend.SnapshotReply = null;
            await controller.RefreshAsync();
            controller.Leave();
            Assert.IsEmpty(changes);

            await controller.JoinAsync(SessionId);
            backend.SnapshotReply = new HttpResponse { Failure = HttpFailure.Incomplete };
            await controller.RefreshAsync();
            controller.Leave();
            backend.SnapshotReply = null;
            await controller.JoinAsync(SessionId);
            CollectionAssert.AreEqual(new[] { false }, changes, "leaving ends the drop without a return");
        }

        // -----------------------------------------------------------------------------------
        // Function: AnErrorReplyIsNotADrop
        // -----------------------------------------------------------------------------------
        // Description:
        //     A backend that answers with an error is still up, so failing snapshots and event
        //     replays with error replies report no connection change.
        [Test]
        public async Task AnErrorReplyIsNotADrop()
        {
            var changes = new List<bool>();
            controller.ConnectionChanged += changes.Add;
            await controller.JoinAsync(SessionId);
            backend.SnapshotReply = Error(503, "QUEUE_FULL");
            await controller.RefreshAsync();
            backend.SnapshotReply = null;
            backend.OnEvents = _ => Error(500, "INTERNAL");
            await controller.RefreshAsync();
            Assert.IsEmpty(changes);
            Assert.IsNotEmpty(errors, "the failures are still reported as errors");
        }

        // -----------------------------------------------------------------------------------
        // Function: JoiningAnotherSessionAfterADropIsNotAReturn
        // -----------------------------------------------------------------------------------
        // Description:
        //     A drop belongs to the session it happened in, so joining a different session
        //     afterwards reports no return.
        [Test]
        public async Task JoiningAnotherSessionAfterADropIsNotAReturn()
        {
            var changes = new List<bool>();
            controller.ConnectionChanged += changes.Add;
            await controller.JoinAsync(SessionId);
            backend.SnapshotReply = new HttpResponse { Failure = HttpFailure.Incomplete };
            await controller.RefreshAsync();
            backend.SnapshotReply = null;
            backend.Snapshot = (JObject)backend.Snapshot.DeepClone();
            backend.Snapshot["session_id"] = backend.Snapshot["session"]["session_id"] = "session-002";
            await controller.JoinAsync("session-002");
            Assert.AreEqual("session-002", controller.SessionId);
            CollectionAssert.AreEqual(new[] { false }, changes);
        }

        // -----------------------------------------------------------------------------------
        // Function: JoiningAnUnknownSessionDoesNotLeaveTheCurrentOne
        // -----------------------------------------------------------------------------------
        // Description:
        //     A failed join of an unknown session leaves the joined one in place.
        [Test]
        public async Task JoiningAnUnknownSessionDoesNotLeaveTheCurrentOne()
        {
            await controller.JoinAsync(SessionId);
            var left = 0;
            controller.SessionLeft += () => left++;
            backend.SnapshotReply = Error(404, "INVALID_REFERENCE");
            await controller.JoinAsync("missing-session");
            Assert.AreEqual(0, left);
            Assert.AreEqual(SessionId, controller.SessionId);
        }

        // -----------------------------------------------------------------------------------
        // Function: LeavingSettlesAnOpenCommandAsUnconfirmed
        // -----------------------------------------------------------------------------------
        // Description:
        //     Leaving a session settles its open command as unconfirmed, and a late reply does not
        //     revive it.
        [Test]
        public async Task LeavingSettlesAnOpenCommandAsUnconfirmed()
        {
            await controller.JoinAsync(SessionId);
            // Left while the reply is outstanding: a late reply must not revive the command.
            backend.OnCommand = _ =>
            {
                controller.Leave();
                return new HttpResponse { Failure = HttpFailure.Incomplete };
            };
            await controller.SendControlAsync("ARCHIVE");
            Assert.IsFalse(controller.HasPendingCommand);
            Assert.AreEqual("NEEDS_RECONFIRMATION", outcomes.Last().Status);
        }

        // -----------------------------------------------------------------------------------
        // Function: JoinShowsTheSnapshotThenReplaysEventsFromItsSequence
        // -----------------------------------------------------------------------------------
        // Description:
        //     A join shows the snapshot, then asks for events from its sequence.
        [Test]
        public async Task JoinShowsTheSnapshotThenReplaysEventsFromItsSequence()
        {
            backend.OnEvents = request => Json(backend.Replay(FakeBackend.After(request), 2));
            await controller.JoinAsync(SessionId);
            Assert.IsTrue(controller.Available);
            Assert.AreEqual(SessionId, controller.SessionId);
            Assert.AreEqual(1, snapshots.Count);
            Assert.AreEqual(7, FakeBackend.After(backend.To("/events").Single()), "replay starts at the snapshot's sequence");
            Assert.AreEqual(9, controller.Store.LastEventSequence);
            Assert.IsEmpty(errors);
        }

        // -----------------------------------------------------------------------------------
        // Function: PollContinuesFromTheCommittedCursor
        // -----------------------------------------------------------------------------------
        // Description:
        //     Each poll asks for events after the last batch the store accepted.
        [Test]
        public async Task PollContinuesFromTheCommittedCursor()
        {
            backend.OnEvents = request => Json(backend.Replay(FakeBackend.After(request), 1));
            await controller.JoinAsync(SessionId);
            backend.Snapshot["tick"] = 210;
            backend.Snapshot["snapshot_id"] = "snapshot-210";
            await controller.RefreshAsync();
            CollectionAssert.AreEqual(new long[] { 7, 8 }, backend.To("/events").Select(FakeBackend.After));
        }

        // -----------------------------------------------------------------------------------
        // Function: AnEventGapResynchronisesFromAFreshSnapshot
        // -----------------------------------------------------------------------------------
        // Description:
        //     A batch with a gap is refused and the session reloads from a fresh snapshot.
        [Test]
        public async Task AnEventGapResynchronisesFromAFreshSnapshot()
        {
            backend.OnEvents = request => Json(backend.Replay(FakeBackend.After(request), 1, FakeBackend.After(request) + 2));
            await controller.JoinAsync(SessionId);
            StringAssert.Contains("resynchronizing from snapshot", errors.Single());
            Assert.AreEqual(2, snapshots.Count);
            Assert.IsTrue(snapshots[1].reset, "the resync snapshot resets the event log");
            Assert.AreEqual(1, backend.To("/events").Count(), "no replay loop straight after a resync");
            Assert.IsTrue(controller.Available);
        }

        // -----------------------------------------------------------------------------------
        // Function: ANewEpochFetchesTheEventsRecoveryAppended
        // -----------------------------------------------------------------------------------
        // Description:
        //     A new epoch fetches the events recovery appended before it, and reports them.
        [Test]
        public async Task ANewEpochFetchesTheEventsRecoveryAppended()
        {
            await controller.JoinAsync(SessionId);
            var recovered = new List<Contracts.Event>();
            controller.RecoveryEventsReceived += batch => recovered.AddRange(batch);
            backend.Snapshot["session_epoch"] = 2;
            backend.Snapshot["session"]["session_epoch"] = 2;
            backend.Snapshot["event_sequence"] = 9;
            backend.OnEvents = request => FakeBackend.After(request) == 7
                ? Json(backend.Replay(7, 2))
                : Json(backend.Replay(FakeBackend.After(request)));
            await controller.RefreshAsync();
            CollectionAssert.AreEqual(new long[] { 8, 9 }, recovered.Select(e => e.EventSequence));
        }

        // -----------------------------------------------------------------------------------
        // Function: AReconnectToARecoveredSessionSaysSo
        // -----------------------------------------------------------------------------------
        // Description:
        //     A return after an ordinary drop is a plain reconnect. A return to a session the
        //     backend recovered after a crash, whose SESSION_RECOVERED event announces it, says so
        //     while the return is reported, and only then.
        [Test]
        public async Task AReconnectToARecoveredSessionSaysSo()
        {
            var returns = new List<bool>();
            controller.ConnectionChanged += connected => { if (connected) returns.Add(controller.ReconnectRecovered); };
            await controller.JoinAsync(SessionId);
            backend.SnapshotReply = new HttpResponse { Failure = HttpFailure.Incomplete };
            await controller.RefreshAsync();
            backend.SnapshotReply = null;
            await controller.RefreshAsync();
            CollectionAssert.AreEqual(new[] { false }, returns, "a Wi-Fi blip or timeout is a plain reconnect");

            backend.SnapshotReply = new HttpResponse { Failure = HttpFailure.Incomplete };
            await controller.RefreshAsync();
            backend.SnapshotReply = null;
            backend.Snapshot["session_epoch"] = 2;
            backend.Snapshot["session"]["session_epoch"] = 2;
            backend.Snapshot["event_sequence"] = 9;
            backend.OnEvents = request => FakeBackend.After(request) == 7
                ? Json(backend.Replay(7, 2))
                : Json(backend.Replay(FakeBackend.After(request)));
            await controller.RefreshAsync();
            CollectionAssert.AreEqual(new[] { false, true }, returns, "a crash recovery says so");
            Assert.IsFalse(controller.ReconnectRecovered, "only while the return is reported");
        }

        // -----------------------------------------------------------------------------------
        // Function: TakenIsCalledOnlyForACommandThatGoesOut
        // -----------------------------------------------------------------------------------
        // Description:
        //     The taken callback, which the confirmation sound hangs on, runs once a command has
        //     passed every check and before its reply, and not for a command that is refused or
        //     rejected.
        [Test]
        public async Task TakenIsCalledOnlyForACommandThatGoesOut()
        {
            var taken = new List<int>();
            var command = SessionController.ControlCommand(SessionId, 1, 3, "PAUSE");
            Assert.IsFalse(await controller.SubmitAsync(command, () => taken.Add(0)), "no session joined");
            await controller.JoinAsync(SessionId);
            Assert.ThrowsAsync<ArgumentException>(() => controller.SubmitAsync(SessionController.ControlCommand("session-002", 1, 3, "PAUSE"), () => taken.Add(0)));
            Assert.IsEmpty(taken);
            Assert.IsTrue(await controller.SubmitAsync(command, () => taken.Add(backend.To("/commands", "POST").Count())));
            CollectionAssert.AreEqual(new[] { 0 }, taken, "once, before the command was posted");
        }

        // -----------------------------------------------------------------------------------
        // Function: StartIsSentWithTheSessionRevisionAndConfirmed
        // -----------------------------------------------------------------------------------
        // Description:
        //     Start is sent with the session's epoch and revision, and its applied status is
        //     reported.
        [Test]
        public async Task StartIsSentWithTheSessionRevisionAndConfirmed()
        {
            await controller.JoinAsync(SessionId);
            Assert.IsTrue(await controller.SendControlAsync("START"));
            var sent = backend.LastCommand;
            Assert.AreEqual("START", (string)sent["command_type"]);
            Assert.AreEqual(1, (long)sent["expected_epoch"]);
            Assert.AreEqual(3, (long)sent["expected_revision"]);
            Assert.AreEqual("APPLIED", outcomes.Single().Status);
            Assert.IsFalse(controller.HasPendingCommand);
        }

        // -----------------------------------------------------------------------------------
        // Function: ALostReplyIsConfirmedWithTheSameKeyNotANewCommand
        // -----------------------------------------------------------------------------------
        // Description:
        //     After a lost reply the same command and key are resent, never a new command.
        [Test]
        public async Task ALostReplyIsConfirmedWithTheSameKeyNotANewCommand()
        {
            await controller.JoinAsync(SessionId);
            var attempts = 0;
            backend.OnCommand = _ => ++attempts < 3 ? new HttpResponse { Failure = HttpFailure.Incomplete } : Json(backend.Status("APPLIED"), 202);
            await controller.SendControlAsync("PAUSE");
            var posts = backend.To("/commands", "POST").Select(r => JObject.Parse(r.Body)).ToList();
            Assert.AreEqual(3, posts.Count);
            Assert.AreEqual(1, posts.Select(p => (string)p["idempotency_key"]).Distinct().Count());
            Assert.AreEqual(1, posts.Select(p => (string)p["command_id"]).Distinct().Count());
            CollectionAssert.AreEqual(new[] { 100.0, 200.0 }, waits.Select(w => w.TotalMilliseconds));
            Assert.AreEqual("APPLIED", outcomes.Single().Status);
        }

        // -----------------------------------------------------------------------------------
        // Function: QueuedAndRecoveringRepliesAreRetriedUntilFinal
        // -----------------------------------------------------------------------------------
        // Description:
        //     QUEUED and SESSION_RECOVERING replies are retried with a growing wait until final.
        [Test]
        public async Task QueuedAndRecoveringRepliesAreRetriedUntilFinal()
        {
            await controller.JoinAsync(SessionId);
            var replies = new Queue<Func<HttpResponse>>(new Func<HttpResponse>[]
            {
                () => Error(503, "QUEUE_FULL"),
                () => Error(409, "SESSION_RECOVERING"),
                () => Json(backend.Status("QUEUED"), 202),
                () => Json(backend.Status("APPLIED"), 202),
            });
            backend.OnCommand = _ => replies.Dequeue()();
            await controller.SendControlAsync("RESUME");
            Assert.AreEqual(4, backend.To("/commands", "POST").Count());
            CollectionAssert.AreEqual(new[] { "QUEUED", "APPLIED" }, outcomes.Select(o => o.Status));
            Assert.IsFalse(controller.HasPendingCommand);
        }

        // -----------------------------------------------------------------------------------
        // Function: ACompleteRefusalIsFinal
        // -----------------------------------------------------------------------------------
        // Description:
        //     A complete refusal of a submission ends the command without retrying.
        [Test]
        public async Task ACompleteRefusalIsFinal()
        {
            await controller.JoinAsync(SessionId);
            backend.OnCommand = _ => Error(409, "STALE_REVISION");
            await controller.SendControlAsync("PAUSE");
            Assert.IsFalse(controller.HasPendingCommand);
            Assert.AreEqual(1, backend.To("/commands", "POST").Count());
            StringAssert.Contains("STALE_REVISION", errors.Last());
        }

        // -----------------------------------------------------------------------------------
        // Function: Stalled
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function makes a controller whose retries wait forever, so an open command stays
        //     open during the test.
        //
        // Args:
        //     outcomesSeen (List<CommandOutcome>): Collects the outcomes it reports, or null.
        //
        // Returns:
        //     SessionController: The joined controller.
        private SessionController Stalled(List<CommandOutcome> outcomesSeen = null)
        {
            var stalled = new SessionController(new TrainingApi(backend, auth, new Uri("http://backend.test")), auth,
                delay: (_, __) => new TaskCompletionSource<bool>().Task);
            if (outcomesSeen != null) stalled.CommandFinished += outcomesSeen.Add;
            return stalled;
        }

        // -----------------------------------------------------------------------------------
        // Function: OnlyOneCommandMayBeOpen
        // -----------------------------------------------------------------------------------
        // Description:
        //     A second command is refused while one is open.
        [Test]
        public async Task OnlyOneCommandMayBeOpen()
        {
            var stalled = Stalled();
            await stalled.JoinAsync(SessionId);
            backend.OnCommand = _ => new HttpResponse { Failure = HttpFailure.Incomplete };
            await stalled.SendControlAsync("PAUSE");
            Assert.IsTrue(stalled.HasPendingCommand);
            Assert.IsFalse(await stalled.SendControlAsync("RESUME"));
            Assert.IsFalse(backend.To("/commands", "POST").Any(c => (string)JObject.Parse(c.Body)["command_type"] == "RESUME"));
        }

        // -----------------------------------------------------------------------------------
        // Function: AnExpiredLoginKeepsTheCommandForTheSameAccount
        // -----------------------------------------------------------------------------------
        // Description:
        //     A login that expires mid-command keeps the command, which is resent only after the
        //     same account logs in again.
        [Test]
        public async Task AnExpiredLoginKeepsTheCommandForTheSameAccount()
        {
            await controller.JoinAsync(SessionId);
            backend.OnCommand = _ => Error(401, "session_expired");
            await controller.SendControlAsync("PAUSE");
            Assert.IsTrue(controller.HasPendingCommand);
            Assert.IsFalse(auth.IsLoggedIn);

            auth.Begin("someone-else", "token-2", 3600);
            backend.OnCommand = _ => Json(backend.Status("APPLIED"), 202);
            var postsBefore = backend.To("/commands", "POST").Count();
            await controller.RefreshAsync();
            Assert.AreEqual(postsBefore, backend.To("/commands", "POST").Count(), "another account must not resend it");
            Assert.IsTrue(controller.HasPendingCommand);

            auth.Begin("trainee", "token-3", 3600);
            await controller.RefreshAsync();
            Assert.IsFalse(controller.HasPendingCommand);
            Assert.AreEqual("APPLIED", outcomes.Last().Status);
        }

        // -----------------------------------------------------------------------------------
        // Function: JoiningAnotherSessionClosesAnOpenCommandAsUnconfirmed
        // -----------------------------------------------------------------------------------
        // Description:
        //     Joining another session closes the open command as unconfirmed.
        [Test]
        public async Task JoiningAnotherSessionClosesAnOpenCommandAsUnconfirmed()
        {
            var closed = new List<CommandOutcome>();
            var blocking = Stalled(closed);
            await blocking.JoinAsync(SessionId);
            backend.OnCommand = _ => new HttpResponse { Failure = HttpFailure.Incomplete };
            await blocking.SendControlAsync("PAUSE");
            await blocking.JoinAsync("session-002");
            Assert.IsFalse(blocking.HasPendingCommand);
            Assert.AreEqual("NEEDS_RECONFIRMATION", closed.Single().Status);
            Assert.IsFalse(closed.Single().Confirmed);
        }

        // -----------------------------------------------------------------------------------
        // Function: AfterAnEpochChangeTheCommandIsOnlyCheckedNotResent
        // -----------------------------------------------------------------------------------
        // Description:
        //     After an epoch change an open command's status is checked, and it is never resent.
        [Test]
        public async Task AfterAnEpochChangeTheCommandIsOnlyCheckedNotResent()
        {
            var closed = new List<CommandOutcome>();
            var blocking = Stalled(closed);
            await blocking.JoinAsync(SessionId);
            backend.OnCommand = _ => new HttpResponse { Failure = HttpFailure.Incomplete };
            await blocking.SendControlAsync("PAUSE");
            backend.Snapshot["session_epoch"] = 2;
            backend.Snapshot["session"]["session_epoch"] = 2;
            backend.OnStatus = _ => Error(404, "INVALID_REFERENCE");
            var postsBefore = backend.To("/commands", "POST").Count();
            await blocking.RefreshAsync();
            Assert.AreEqual(postsBefore, backend.To("/commands", "POST").Count(), "a recovered session is asked, not resent");
            Assert.IsTrue(backend.To("/commands/" + (string)backend.LastCommand["idempotency_key"]).Any());
            Assert.AreEqual("NEEDS_RECONFIRMATION", closed.Single().Status);
        }

        // -----------------------------------------------------------------------------------
        // Function: CreationKeepsItsKeyUntilItSucceedsThenJoins
        // -----------------------------------------------------------------------------------
        // Description:
        //     A creation retries with the same key until it succeeds, then joins the session.
        [Test]
        public async Task CreationKeepsItsKeyUntilItSucceedsThenJoins()
        {
            var keys = new List<string>();
            var created = Fixture("snapshot")["session"];
            var fail = true;
            backend.OnCreate = request =>
            {
                keys.Add(request.Headers["Idempotency-Key"]);
                return fail ? new HttpResponse { Failure = HttpFailure.Incomplete } : Json(created, 201);
            };
            await controller.CreateAsync();
            Assert.IsTrue(controller.CreationPending);
            StringAssert.Contains("Retry to confirm the same attempt", errors.Last());
            controller.Scenario = "scenario-m2-r1"; // a later pick must not change the open attempt
            fail = false;
            await controller.RefreshAsync();
            Assert.AreEqual(2, keys.Count);
            Assert.AreEqual(keys[0], keys[1]);
            var bodies = backend.To("/api/v2/sessions", "POST").Select(r => (string)JObject.Parse(r.Body)["scenario_revision_id"]).ToList();
            CollectionAssert.AreEqual(new[] { SessionController.IntegratedScenario, SessionController.IntegratedScenario }, bodies);
            Assert.IsFalse(controller.CreationPending);
            Assert.AreEqual(SessionId, controller.SessionId);
        }

        // -----------------------------------------------------------------------------------
        // Function: CreationWaitsForItsOwnAccount
        // -----------------------------------------------------------------------------------
        // Description:
        //     A pending creation is only confirmed by the account that started it.
        [Test]
        public async Task CreationWaitsForItsOwnAccount()
        {
            backend.OnCreate = _ => new HttpResponse { Failure = HttpFailure.Incomplete };
            await controller.CreateAsync();
            auth.Begin("someone-else", "token-2", 3600);
            await controller.CreateAsync();
            StringAssert.Contains("Log in as trainee", errors.Last());
            Assert.AreEqual(1, backend.To("/api/v2/sessions", "POST").Count());
        }

        // -----------------------------------------------------------------------------------
        // Function: TrafficIsPolledFromTheNewestFrameAndAskedAgainLaterWhenThereIsNone
        // -----------------------------------------------------------------------------------
        // Description:
        //     The first traffic poll of a session asks for every frame the backend keeps, and each
        //     later one only for frames after the newest taken, which are reported in order, with
        //     how long the backend may wait for one. A session the backend has no traffic for is
        //     not asked again for a while, then is, and picks up where it left off once the
        //     backend has traffic for it again.
        [Test]
        public async Task TrafficIsPolledFromTheNewestFrameAndAskedAgainLaterWhenThereIsNone()
        {
            var accepted = new List<IReadOnlyList<TrafficFrame>>();
            controller.TrafficFramesAccepted += accepted.Add;
            await controller.JoinAsync(SessionId);
            backend.OnTraffic = _ => Json(backend.Traffic(10, 12));
            Assert.AreEqual(2, await controller.RefreshTrafficAsync());
            backend.OnTraffic = _ => Json(backend.Traffic(14));
            Assert.AreEqual(1, await controller.RefreshTrafficAsync(SessionController.TrafficWait));
            CollectionAssert.AreEqual(new long[] { -1, 12 }, backend.To("/traffic").Select(FakeBackend.AfterTick).ToList());
            var waiting = backend.To("/traffic").Last();
            StringAssert.Contains("wait_ms=1000", waiting.Url.Query);
            Assert.Greater(waiting.TimeoutSeconds, 10, "the request outlasts the backend's wait");
            backend.OnTraffic = _ => Json(backend.Traffic());
            Assert.AreEqual(0, await controller.RefreshTrafficAsync(SessionController.TrafficWait), "the wait ran out with nothing new");
            Assert.AreEqual(14L, FakeBackend.AfterTick(backend.To("/traffic").Last()));
            Assert.AreEqual(2, accepted.Count);
            CollectionAssert.AreEqual(new long[] { 10, 12 }, accepted[0].Select(f => f.Tick).ToList());
            CollectionAssert.AreEqual(new long[] { 14 }, accepted[1].Select(f => f.Tick).ToList());
            var shuttle = accepted[1][0].Shuttles.Single();
            Assert.AreEqual("shuttle-01", shuttle.VehicleId);
            Assert.AreEqual(4.2, shuttle.SpeedMps, 1e-9);
            CollectionAssert.AreEqual(new[] { 3, 1200, -450, 900, 1000 }, accepted[1][0].Vehicles);

            backend.OnTraffic = _ => Error(404, "INVALID_REFERENCE");
            Assert.AreEqual(-1, await controller.RefreshTrafficAsync());
            Assert.AreEqual(14L, FakeBackend.AfterTick(backend.To("/traffic").Last()));
            var asked = backend.To("/traffic").Count();
            Assert.AreEqual(-1, await controller.RefreshTrafficAsync(), "nothing asked");
            await controller.RefreshTrafficAsync();
            clock += SessionController.NoTrafficRetryInterval - TimeSpan.FromSeconds(0.1);
            await controller.RefreshTrafficAsync();
            Assert.AreEqual(asked, backend.To("/traffic").Count(), "a session without traffic is not asked again for a while");
            Assert.AreEqual(2, accepted.Count);
            Assert.IsEmpty(errors, "no traffic is not an error");

            // Having none may have been passing, as while the backend recovered: it is asked again later
            clock += TimeSpan.FromSeconds(0.1);
            backend.OnTraffic = _ => Json(backend.Traffic(16));
            Assert.AreEqual(1, await controller.RefreshTrafficAsync());
            Assert.AreEqual(asked + 1, backend.To("/traffic").Count());
            Assert.AreEqual(14L, FakeBackend.AfterTick(backend.To("/traffic").Last()), "from the newest frame taken");
            CollectionAssert.AreEqual(new long[] { 16 }, accepted[2].Select(f => f.Tick).ToList());
        }

        // -----------------------------------------------------------------------------------
        // Function: NothingIsSentWhileLoggedOut
        // -----------------------------------------------------------------------------------
        // Description:
        //     Nothing is sent to the backend while logged out.
        [Test]
        public async Task NothingIsSentWhileLoggedOut()
        {
            await controller.JoinAsync(SessionId);
            var before = backend.Sent.Count;
            auth.End();
            await controller.RefreshAsync();
            Assert.AreEqual(before, backend.Sent.Count);
        }

        // A session list in which the given sessions are live.
        private static JObject LiveList(params string[] live)
        {
            var list = Fixture("session_list");
            list["sessions"] = new JArray(live.Select(id => new JObject
            {
                ["occupies_capacity"] = true, ["scenario_revision_id"] = "scenario-r1", ["session_id"] = id, ["status"] = "RUNNING",
            }));
            list["live_count"] = live.Length;
            list["total"] = live.Length;
            return list;
        }

        // The session named in a request's path, as in /api/v2/sessions/{id}/snapshot.
        private static string PathSession(HttpRequest request) => Uri.UnescapeDataString(request.Url.AbsolutePath.Split('/')[4]);

        // A snapshot of a session that is not joined, at revision 7.
        private static JObject SnapshotOf(string id)
        {
            var snapshot = Fixture("snapshot");
            snapshot["session_id"] = id;
            snapshot["session"]["session_id"] = id;
            snapshot["session"]["aggregate_revision"] = 7;
            return snapshot;
        }

        // -----------------------------------------------------------------------------------
        // Function: ArchiveAllCarriesOnPastAFailureAndCountsWhatIsArchived
        // -----------------------------------------------------------------------------------
        // Description:
        //     One session timing out does not stop the rest; each is archived at its own revision;
        //     a queued archive counts once the session is no longer live, and the failed one not.
        [Test]
        public async Task ArchiveAllCarriesOnPastAFailureAndCountsWhatIsArchived()
        {
            var lists = 0;
            backend.OnList = _ => Json(++lists switch
            {
                1 => LiveList("session-a", "session-b", "session-c"),
                // The first look: session-c's archive is still queued
                2 => LiveList("session-b", "session-c"),
                _ => LiveList("session-b"),
            });
            backend.OnSnapshot = request => PathSession(request) == "session-b"
                ? new HttpResponse { Failure = HttpFailure.Incomplete }
                : Json(SnapshotOf(PathSession(request)));
            var archived = new List<JObject>();
            backend.OnCommand = request =>
            {
                var command = JObject.Parse(request.Body);
                archived.Add(command);
                return Json(backend.Status((string)command["session_id"] == "session-c" ? "QUEUED" : "APPLIED"), 202);
            };

            var (live, done) = await controller.ArchiveAllAsync();

            Assert.AreEqual(3, live);
            Assert.AreEqual(2, done, "session-a, and session-c once its queued archive applied; not session-b");
            CollectionAssert.AreEqual(new[] { "session-a", "session-c" }, archived.Select(c => (string)c["session_id"]).ToArray(),
                "the timeout on session-b did not stop session-c");
            foreach (var command in archived)
            {
                Assert.AreEqual("ARCHIVE", (string)command["command_type"]);
                Assert.AreEqual((string)command["session_id"], (string)command["target_id"]);
                Assert.AreEqual(7, (long)command["expected_revision"], "the revision its snapshot gave");
            }
            Assert.That(waits, Has.Member(SessionController.ArchiveCheckInterval), "it looked again before counting");
        }

        // -----------------------------------------------------------------------------------
        // Function: AQueuedArchiveThatNeverAppliesIsNotCounted
        // -----------------------------------------------------------------------------------
        // Description:
        //     A session whose archive stays queued is still live, so it is not reported as archived.
        [Test]
        public async Task AQueuedArchiveThatNeverAppliesIsNotCounted()
        {
            backend.OnList = _ => Json(LiveList("session-a"));
            backend.OnSnapshot = request => Json(SnapshotOf(PathSession(request)));
            backend.OnCommand = _ => Json(backend.Status("QUEUED"), 202);

            var (live, done) = await controller.ArchiveAllAsync();

            Assert.AreEqual(1, live);
            Assert.AreEqual(0, done);
        }

        // -----------------------------------------------------------------------------------
        // Function: ArchiveAllArchivesTheJoinedSessionThroughItsOwnCommand
        // -----------------------------------------------------------------------------------
        // Description:
        //     The joined session is archived as its Archive button does, through the one open
        //     command, so the controller knows of it; its snapshot is not fetched a second way.
        [Test]
        public async Task ArchiveAllArchivesTheJoinedSessionThroughItsOwnCommand()
        {
            await controller.JoinAsync(SessionId);
            var lists = 0;
            backend.OnList = _ => Json(++lists == 1 ? LiveList(SessionId) : LiveList());
            var finished = outcomes.Count;

            var (live, done) = await controller.ArchiveAllAsync();

            Assert.AreEqual((1, 1), (live, done));
            Assert.AreEqual("ARCHIVE", (string)backend.LastCommand["command_type"]);
            Assert.AreEqual(SessionId, (string)backend.LastCommand["session_id"]);
            Assert.Greater(outcomes.Count, finished, "the controller saw its own command finish");
        }
    }
}
