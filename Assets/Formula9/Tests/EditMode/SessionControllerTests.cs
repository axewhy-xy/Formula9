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
    public class SessionControllerTests
    {
        private static JObject Fixture(string name) =>
            JObject.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures", name + ".json")));

        private static HttpResponse Json(JToken body, int status = 200) => new HttpResponse { Status = status, Body = body.ToString() };

        private static HttpResponse Error(int status, string code) => Json(new JObject
        {
            ["error"] = new JObject { ["code"] = code, ["message"] = code, ["retryable"] = false },
        }, status);

        // A scripted backend serving the shared fixtures. Tests replace the handlers they need.
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

            public FakeBackend()
            {
                OnEvents = request => Json(Replay(After(request)));
                OnCommand = _ => Json(Status("APPLIED"), 202);
                OnStatus = _ => Json(Status("APPLIED"));
            }

            public IEnumerable<HttpRequest> To(string suffix, string method = null) =>
                Sent.Where(r => r.Url.AbsolutePath.EndsWith(suffix) && (method == null || r.Method == method));

            public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellation = default)
            {
                Sent.Add(request);
                var path = request.Url.AbsolutePath;
                if (request.Method == "POST" && path == "/api/v2/sessions") return Task.FromResult(OnCreate(request));
                if (path.EndsWith("/snapshot")) return Task.FromResult(SnapshotReply ?? Json(Snapshot));
                if (path.EndsWith("/events")) return Task.FromResult(OnEvents(request));
                if (request.Method == "POST" && path.EndsWith("/commands"))
                {
                    LastCommand = JObject.Parse(request.Body);
                    return Task.FromResult(OnCommand(request));
                }
                if (path.Contains("/commands/")) return Task.FromResult(OnStatus(request));
                return Task.FromResult(Error(404, "INVALID_REFERENCE"));
            }

            public static long After(HttpRequest request) =>
                long.Parse(request.Url.Query.TrimStart('?').Split('&').First(p => p.StartsWith("after=")).Substring(6));

            // An event page after the cursor: `count` new events, contiguous unless told otherwise.
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

            // The journaled status for the last submitted command.
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
        private SessionController controller;
        private List<TimeSpan> waits;
        private List<string> errors;
        private List<CommandOutcome> outcomes;
        private List<(FullSnapshot snapshot, bool reset)> snapshots;
        private const string SessionId = "session-001";

        [SetUp]
        public void SetUp()
        {
            backend = new FakeBackend();
            auth = new AuthSession();
            auth.Begin("trainee", "token-1", 3600);
            waits = new List<TimeSpan>();
            controller = new SessionController(new TrainingApi(backend, auth, new Uri("http://backend.test")), auth,
                delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
            errors = new List<string>();
            outcomes = new List<CommandOutcome>();
            snapshots = new List<(FullSnapshot, bool)>();
            controller.ErrorOccurred += errors.Add;
            controller.CommandFinished += outcomes.Add;
            controller.SnapshotAccepted += (s, reset) => snapshots.Add((s, reset));
        }

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

        // A controller whose retries wait forever, so an open command stays open during the test.
        private SessionController Stalled(List<CommandOutcome> outcomesSeen = null)
        {
            var stalled = new SessionController(new TrainingApi(backend, auth, new Uri("http://backend.test")), auth,
                delay: (_, __) => new TaskCompletionSource<bool>().Task);
            if (outcomesSeen != null) stalled.CommandFinished += outcomesSeen.Add;
            return stalled;
        }

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

        [Test]
        public async Task NothingIsSentWhileLoggedOut()
        {
            await controller.JoinAsync(SessionId);
            var before = backend.Sent.Count;
            auth.End();
            await controller.RefreshAsync();
            Assert.AreEqual(before, backend.Sent.Count);
        }
    }
}
