using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Formula9.Tests
{
    public class BackendApiTests
    {
        // Answers each request from a script and records what was sent.
        private sealed class FakeTransport : IHttpTransport
        {
            public readonly List<HttpRequest> Sent = new List<HttpRequest>();
            public Func<HttpRequest, HttpResponse> Reply = _ => new HttpResponse { Status = 500, Body = "{}" };

            public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellation = default)
            {
                Sent.Add(request);
                return Task.FromResult(Reply(request));
            }
        }

        private static readonly Uri Base = new Uri("http://backend.test:8000");
        private DateTime now;
        private AuthSession session;
        private FakeTransport transport;
        private TrainingApi training;

        private static string Fixture(string name) =>
            File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures", name + ".json"));

        private static HttpResponse Ok(string body, int status = 200) => new HttpResponse { Status = status, Body = body };

        private static HttpResponse Error(int status, string code, bool retryable = false) => new HttpResponse
        {
            Status = status,
            Body = new JObject { ["error"] = new JObject { ["code"] = code, ["message"] = code + " message", ["retryable"] = retryable } }.ToString(),
        };

        [SetUp]
        public void SetUp()
        {
            now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
            session = new AuthSession(() => now);
            transport = new FakeTransport();
            training = new TrainingApi(transport, session, Base);
        }

        private void LogIn(string token = "token-1") => session.Begin("trainee", token, 3600);

        [Test]
        public async Task LoginStoresTheTokenAndTrimsTheUsername()
        {
            transport.Reply = _ => Ok("{\"username\":\"trainee\",\"token\":\"abc\",\"expires_at\":\"2026-10-30T13:00:00Z\",\"expires_in\":3600}");
            await new AuthClient(transport, session, Base).LogInAsync("  trainee ", "secret");
            Assert.IsTrue(session.IsLoggedIn);
            Assert.AreEqual("abc", session.Token);
            Assert.AreEqual("/api/auth/login", transport.Sent[0].Url.AbsolutePath);
            Assert.AreEqual("trainee", (string)JObject.Parse(transport.Sent[0].Body)["username"]);
        }

        [Test]
        public void WrongPasswordIsABackendErrorAndLeavesTheUserLoggedOut()
        {
            transport.Reply = _ => Error(401, "invalid_credentials");
            var error = Assert.ThrowsAsync<BackendErrorException>(() => new AuthClient(transport, session, Base).LogInAsync("trainee", "wrong"));
            Assert.AreEqual("invalid_credentials", error.Error.Code);
            Assert.IsFalse(session.IsLoggedIn);
        }

        [Test]
        public void TokensAreTreatedAsExpiredAMinuteEarly()
        {
            session.Begin("trainee", "abc", 120);
            now = now.AddSeconds(59);
            Assert.IsTrue(session.IsLoggedIn);
            now = now.AddSeconds(2);
            Assert.IsFalse(session.IsLoggedIn);
        }

        [Test]
        public void TrainingCallsAreNeverSentAnonymously()
        {
            Assert.ThrowsAsync<NotLoggedInException>(() => training.GetSnapshotAsync("session-1"));
            Assert.IsEmpty(transport.Sent);
        }

        [Test]
        public async Task SnapshotRequestCarriesTheTokenAndEncodesTheId()
        {
            LogIn();
            var fixture = JObject.Parse(Fixture("snapshot"));
            var id = (string)fixture["session_id"];
            transport.Reply = _ => Ok(fixture.ToString());
            var snapshot = await training.GetSnapshotAsync(id);
            Assert.AreEqual(id, snapshot.SessionId);
            Assert.AreEqual("Bearer token-1", transport.Sent[0].Headers["Authorization"]);
            Assert.AreEqual($"/api/v2/sessions/{Uri.EscapeDataString(id)}/snapshot", transport.Sent[0].Url.AbsolutePath);
        }

        [Test]
        public void SnapshotForAnotherSessionIsRejected()
        {
            LogIn();
            transport.Reply = _ => Ok(Fixture("snapshot"));
            Assert.ThrowsAsync<ContractException>(() => training.GetSnapshotAsync("some-other-session"));
        }

        [Test]
        public void ExpiredTokenEndsTheLogin()
        {
            LogIn();
            var ended = 0;
            session.Ended += () => ended++;
            transport.Reply = _ => Error(401, "session_expired", true);
            Assert.ThrowsAsync<NotLoggedInException>(() => training.ListSessionsAsync());
            Assert.AreEqual(1, ended);
            Assert.IsFalse(session.IsLoggedIn);
        }

        [Test]
        public void A401ForAnOldTokenDoesNotEndANewerLogin()
        {
            LogIn("old-token");
            transport.Reply = _ =>
            {
                LogIn("new-token"); // the user logged in again while the old request was in flight
                return Error(401, "session_expired", true);
            };
            Assert.ThrowsAsync<NotLoggedInException>(() => training.ListSessionsAsync());
            Assert.IsTrue(session.IsLoggedIn);
            Assert.AreEqual("new-token", session.Token);
        }

        [Test]
        public void CompleteBackendErrorsKeepTheirCodeAndRetryFlag()
        {
            LogIn();
            transport.Reply = _ => Error(409, "SESSION_RECOVERING", true);
            var error = Assert.ThrowsAsync<BackendErrorException>(() => training.GetSnapshotAsync("session-1"));
            Assert.AreEqual(409, error.Error.Status);
            Assert.AreEqual("SESSION_RECOVERING", error.Error.Code);
            Assert.IsTrue(error.Error.Retryable);
        }

        [Test]
        public void MissingOrUnreadableRepliesAreUnknownOutcomes()
        {
            LogIn();
            transport.Reply = _ => new HttpResponse { Failure = HttpFailure.Incomplete };
            Assert.ThrowsAsync<NoReplyException>(() => training.ListSessionsAsync());
            transport.Reply = _ => Ok("<html>Bad gateway</html>", 502);
            Assert.ThrowsAsync<NoReplyException>(() => training.ListSessionsAsync());
        }

        [Test]
        public async Task SessionListAsksForOnePage()
        {
            LogIn();
            transport.Reply = _ => Ok(Fixture("session_list"));
            var list = await training.ListSessionsAsync(50);
            Assert.AreEqual("?limit=50", transport.Sent[0].Url.Query);
            Assert.AreEqual(2, list.Total);
        }

        [Test]
        public async Task DeleteAcceptsAnEmptyReplyAndEncodesTheId()
        {
            LogIn();
            transport.Reply = _ => Ok("", 204);
            await training.DeleteSessionAsync("session 1");
            Assert.AreEqual("DELETE", transport.Sent[0].Method);
            Assert.AreEqual("/api/v2/sessions/session%201", transport.Sent[0].Url.AbsolutePath);
        }

        [Test]
        public async Task PurgeAndCompactSendMaintenanceRequestsWithALongerTimeout()
        {
            LogIn();
            transport.Reply = _ => Ok("{\"deleted_count\":3}");
            Assert.AreEqual(3, await training.PurgeSessionsAsync(20));
            Assert.AreEqual("/api/v2/sessions/purge", transport.Sent[0].Url.AbsolutePath);
            Assert.AreEqual("?keep_latest=20", transport.Sent[0].Url.Query);
            Assert.Greater(transport.Sent[0].TimeoutSeconds, 10);

            transport.Reply = _ => Ok("{\"database_bytes_before\":2097152,\"database_bytes_after\":1048576,\"wal_truncated\":true}");
            var compacted = await training.CompactSessionsAsync();
            Assert.AreEqual(1048576, compacted.DatabaseBytesAfter);
            Assert.Greater(transport.Sent[1].TimeoutSeconds, 10);
        }

        [Test]
        public void MalformedMaintenanceRepliesAreContractErrors()
        {
            LogIn();
            transport.Reply = _ => Ok("{}");
            Assert.ThrowsAsync<ContractException>(() => training.PurgeSessionsAsync(20));
            Assert.ThrowsAsync<ContractException>(() => training.CompactSessionsAsync());
        }

        [Test]
        public async Task CreateSendsTheIdempotencyKeyAndValidatesTheRequestFirst()
        {
            LogIn();
            var created = JObject.Parse(Fixture("snapshot"))["session"].ToString();
            transport.Reply = _ => Ok(created, 201);
            var result = await training.CreateSessionAsync(new CreateSessionRequest { ScenarioRevisionId = "scenario-m2-r1" }, "create-key-1");
            Assert.IsNotNull(result.SessionId);
            Assert.AreEqual("create-key-1", transport.Sent[0].Headers["Idempotency-Key"]);
            Assert.AreEqual("scenario-m2-r1", (string)JObject.Parse(transport.Sent[0].Body)["scenario_revision_id"]);

            Assert.ThrowsAsync<ContractException>(() =>
                training.CreateSessionAsync(new CreateSessionRequest { ScenarioRevisionId = "not valid!" }, "create-key-2"));
            Assert.AreEqual(1, transport.Sent.Count, "an invalid request is never sent");
        }

        [Test]
        public async Task CommandsReturnTheirJournaledStatus()
        {
            LogIn();
            var command = ContractParser.Parse<DispatcherCommand>(Fixture("command"));
            transport.Reply = _ => Ok(Fixture("command_status"), 202);
            var status = await training.SubmitCommandAsync(command);
            Assert.AreEqual("COMMAND_STATUS", status.MessageType);
            Assert.AreEqual($"/api/v2/sessions/{command.SessionId}/commands", transport.Sent[0].Url.AbsolutePath);
            Assert.AreEqual(command.IdempotencyKey, (string)JObject.Parse(transport.Sent[0].Body)["idempotency_key"]);
        }

        [Test]
        public void EventReplayMustAnswerTheCursorThatWasAskedFor()
        {
            LogIn();
            var replay = JObject.Parse(Fixture("event_replay"));
            transport.Reply = _ => Ok(replay.ToString());
            var asked = (long)replay["after_sequence"] + 1;
            Assert.ThrowsAsync<ContractException>(() => training.GetEventsAsync((string)replay["session_id"], asked));
            StringAssert.Contains($"after={asked}", transport.Sent[0].Url.Query);
        }
    }
}
