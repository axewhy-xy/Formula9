/*
 * Unit tests for the backend layer (BackendApi.cs, AuthSession.cs).
 *
 * Author: Choo Jian Wei
 * Contributors: Lau Xiang Yang (session list and cleanup tests)
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Uses a scripted fake transport and clock, so no backend is needed. Covers login and the
 *     token's lifetime, that training requests are never anonymous, 401 handling across re-logins,
 *     the three kinds of failure, every training call's request and reply checks, and the session
 *     cleanup calls.
 */

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
    // -----------------------------------------------------------------------------------
    // Class: BackendApiTests
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class tests the login and training clients against a fake transport.
    public class BackendApiTests
    {
        // -----------------------------------------------------------------------------------
        // Class: FakeTransport
        // -----------------------------------------------------------------------------------
        // Description:
        //     This class answers each request from a script and records what was sent.
        //
        //     Attributes: Sent lists the requests; Reply answers each one (500 by default).
        private sealed class FakeTransport : IHttpTransport
        {
            public readonly List<HttpRequest> Sent = new List<HttpRequest>();
            public Func<HttpRequest, HttpResponse> Reply = _ => new HttpResponse { Status = 500, Body = "{}" };

            // -----------------------------------------------------------------------------------
            // Function: FakeTransport.SendAsync
            // -----------------------------------------------------------------------------------
            // Description:
            //     This function records the request and returns the scripted reply.
            //
            // Args:
            //     request (HttpRequest): The request.
            //     cancellation (CancellationToken): Unused.
            //
            // Returns:
            //     Task<HttpResponse>: The scripted reply.
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

        // -----------------------------------------------------------------------------------
        // Function: Fixture
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reads a shared contract fixture.
        //
        // Args:
        //     name (string): The fixture name, without .json.
        //
        // Returns:
        //     string: The fixture's JSON.
        private static string Fixture(string name) =>
            File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures", name + ".json"));

        // -----------------------------------------------------------------------------------
        // Function: Ok
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function makes a successful reply.
        //
        // Args:
        //     body (string): The body.
        //     status (int): The status, 200 by default.
        //
        // Returns:
        //     HttpResponse: The reply.
        private static HttpResponse Ok(string body, int status = 200) => new HttpResponse { Status = status, Body = body };

        // -----------------------------------------------------------------------------------
        // Function: Error
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function makes a backend error reply.
        //
        // Args:
        //     status (int): The HTTP status.
        //     code (string): The error code.
        //     retryable (bool): The retryable flag.
        //
        // Returns:
        //     HttpResponse: The reply.
        private static HttpResponse Error(int status, string code, bool retryable = false) => new HttpResponse
        {
            Status = status,
            Body = new JObject { ["error"] = new JObject { ["code"] = code, ["message"] = code + " message", ["retryable"] = retryable } }.ToString(),
        };

        // -----------------------------------------------------------------------------------
        // Function: SetUp
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function gives each test a fixed clock, a logged-out session and a fresh fake
        //     transport.
        [SetUp]
        public void SetUp()
        {
            now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
            session = new AuthSession(() => now);
            transport = new FakeTransport();
            training = new TrainingApi(transport, session, Base);
        }

        // -----------------------------------------------------------------------------------
        // Function: LogIn
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function starts a one-hour login.
        //
        // Args:
        //     token (string): The token to use.
        private void LogIn(string token = "token-1") => session.Begin("trainee", token, 3600);

        // -----------------------------------------------------------------------------------
        // Function: LoginStoresTheTokenAndTrimsTheUsername
        // -----------------------------------------------------------------------------------
        // Description:
        //     A login sends the trimmed username and keeps the returned token.
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

        // -----------------------------------------------------------------------------------
        // Function: WrongPasswordIsABackendErrorAndLeavesTheUserLoggedOut
        // -----------------------------------------------------------------------------------
        // Description:
        //     A refused login is a backend error and starts no login.
        [Test]
        public void WrongPasswordIsABackendErrorAndLeavesTheUserLoggedOut()
        {
            transport.Reply = _ => Error(401, "invalid_credentials");
            var error = Assert.ThrowsAsync<BackendErrorException>(() => new AuthClient(transport, session, Base).LogInAsync("trainee", "wrong"));
            Assert.AreEqual("invalid_credentials", error.Error.Code);
            Assert.IsFalse(session.IsLoggedIn);
        }

        // -----------------------------------------------------------------------------------
        // Function: TokensAreTreatedAsExpiredAMinuteEarly
        // -----------------------------------------------------------------------------------
        // Description:
        //     A token counts as expired one minute before its real expiry.
        [Test]
        public void TokensAreTreatedAsExpiredAMinuteEarly()
        {
            session.Begin("trainee", "abc", 120);
            now = now.AddSeconds(59);
            Assert.IsTrue(session.IsLoggedIn);
            now = now.AddSeconds(2);
            Assert.IsFalse(session.IsLoggedIn);
        }

        // -----------------------------------------------------------------------------------
        // Function: TrainingCallsAreNeverSentAnonymously
        // -----------------------------------------------------------------------------------
        // Description:
        //     Without a login, training calls fail before anything is sent.
        [Test]
        public void TrainingCallsAreNeverSentAnonymously()
        {
            Assert.ThrowsAsync<NotLoggedInException>(() => training.GetSnapshotAsync("session-1"));
            Assert.IsEmpty(transport.Sent);
        }

        // -----------------------------------------------------------------------------------
        // Function: SnapshotRequestCarriesTheTokenAndEncodesTheId
        // -----------------------------------------------------------------------------------
        // Description:
        //     A snapshot request carries the bearer token and escapes the session ID.
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

        // -----------------------------------------------------------------------------------
        // Function: SnapshotForAnotherSessionIsRejected
        // -----------------------------------------------------------------------------------
        // Description:
        //     A snapshot for a different session than asked is a contract error.
        [Test]
        public void SnapshotForAnotherSessionIsRejected()
        {
            LogIn();
            transport.Reply = _ => Ok(Fixture("snapshot"));
            Assert.ThrowsAsync<ContractException>(() => training.GetSnapshotAsync("some-other-session"));
        }

        // -----------------------------------------------------------------------------------
        // Function: ExpiredTokenEndsTheLogin
        // -----------------------------------------------------------------------------------
        // Description:
        //     A 401 for the current token ends the login and is reported as not logged in.
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

        // -----------------------------------------------------------------------------------
        // Function: A401ForAnOldTokenDoesNotEndANewerLogin
        // -----------------------------------------------------------------------------------
        // Description:
        //     A late 401 for a token from before a re-login leaves the new login alone.
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

        // -----------------------------------------------------------------------------------
        // Function: CompleteBackendErrorsKeepTheirCodeAndRetryFlag
        // -----------------------------------------------------------------------------------
        // Description:
        //     A complete error reply keeps its status, code and retryable flag.
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

        // -----------------------------------------------------------------------------------
        // Function: MissingOrUnreadableRepliesAreUnknownOutcomes
        // -----------------------------------------------------------------------------------
        // Description:
        //     No reply, or an unreadable one, is reported as an unknown outcome.
        [Test]
        public void MissingOrUnreadableRepliesAreUnknownOutcomes()
        {
            LogIn();
            transport.Reply = _ => new HttpResponse { Failure = HttpFailure.Incomplete };
            Assert.ThrowsAsync<NoReplyException>(() => training.ListSessionsAsync());
            transport.Reply = _ => Ok("<html>Bad gateway</html>", 502);
            Assert.ThrowsAsync<NoReplyException>(() => training.ListSessionsAsync());
        }

        // -----------------------------------------------------------------------------------
        // Function: SessionListAsksForOnePage
        // -----------------------------------------------------------------------------------
        // Description:
        //     The session list asks for one page of the given size.
        [Test]
        public async Task SessionListAsksForOnePage()
        {
            LogIn();
            transport.Reply = _ => Ok(Fixture("session_list"));
            var list = await training.ListSessionsAsync(50);
            Assert.AreEqual("?limit=50", transport.Sent[0].Url.Query);
            Assert.AreEqual(2, list.Total);
        }

        // -----------------------------------------------------------------------------------
        // Function: DeleteAcceptsAnEmptyReplyAndEncodesTheId
        // -----------------------------------------------------------------------------------
        // Description:
        //     Delete escapes the session ID and accepts an empty 204 reply.
        [Test]
        public async Task DeleteAcceptsAnEmptyReplyAndEncodesTheId()
        {
            LogIn();
            transport.Reply = _ => Ok("", 204);
            await training.DeleteSessionAsync("session 1");
            Assert.AreEqual("DELETE", transport.Sent[0].Method);
            Assert.AreEqual("/api/v2/sessions/session%201", transport.Sent[0].Url.AbsolutePath);
        }

        // -----------------------------------------------------------------------------------
        // Function: PurgeAndCompactSendMaintenanceRequestsWithALongerTimeout
        // -----------------------------------------------------------------------------------
        // Description:
        //     Purge and compact use their own paths and a longer timeout, and return their results.
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

        // -----------------------------------------------------------------------------------
        // Function: MalformedMaintenanceRepliesAreContractErrors
        // -----------------------------------------------------------------------------------
        // Description:
        //     Purge and compact replies missing their fields are contract errors.
        [Test]
        public void MalformedMaintenanceRepliesAreContractErrors()
        {
            LogIn();
            transport.Reply = _ => Ok("{}");
            Assert.ThrowsAsync<ContractException>(() => training.PurgeSessionsAsync(20));
            Assert.ThrowsAsync<ContractException>(() => training.CompactSessionsAsync());
        }

        // -----------------------------------------------------------------------------------
        // Function: CreateSendsTheIdempotencyKeyAndValidatesTheRequestFirst
        // -----------------------------------------------------------------------------------
        // Description:
        //     Create sends its idempotency key, and an invalid request is refused before sending.
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

        // -----------------------------------------------------------------------------------
        // Function: CommandsReturnTheirJournaledStatus
        // -----------------------------------------------------------------------------------
        // Description:
        //     Submitting a command and asking its status both return the journaled status.
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

        // -----------------------------------------------------------------------------------
        // Function: ArchivingASessionReadsItsRevisionThenSendsArchive
        // -----------------------------------------------------------------------------------
        // Description:
        //     Archive all archives a session it has not joined: it reads the session's snapshot,
        //     then sends ARCHIVE with that snapshot's epoch and revision, so the backend accepts it.
        [Test]
        public async Task ArchivingASessionReadsItsRevisionThenSendsArchive()
        {
            LogIn();
            var snapshot = JObject.Parse(Fixture("snapshot"));
            var id = (string)snapshot["session_id"];
            transport.Reply = request => request.Method == "GET" ? Ok(snapshot.ToString()) : Ok(Fixture("command_status"), 202);
            var status = await training.ArchiveSessionAsync(id);

            Assert.AreEqual("COMMAND_STATUS", status.MessageType);
            Assert.AreEqual(2, transport.Sent.Count, "the snapshot, then the command");
            Assert.AreEqual($"/api/v2/sessions/{id}/snapshot", transport.Sent[0].Url.AbsolutePath);
            Assert.AreEqual("POST", transport.Sent[1].Method);
            Assert.AreEqual($"/api/v2/sessions/{id}/commands", transport.Sent[1].Url.AbsolutePath);
            var sent = JObject.Parse(transport.Sent[1].Body);
            Assert.AreEqual("ARCHIVE", (string)sent["command_type"]);
            Assert.AreEqual(id, (string)sent["session_id"]);
            Assert.AreEqual(id, (string)sent["target_id"]);
            Assert.AreEqual("SESSION", (string)sent["target_type"]);
            Assert.AreEqual((long)snapshot["session_epoch"], (long)sent["expected_epoch"], "the session's current epoch");
            Assert.AreEqual((long)snapshot["session"]["aggregate_revision"], (long)sent["expected_revision"], "the session's current revision");
        }

        // -----------------------------------------------------------------------------------
        // Function: EventReplayMustAnswerTheCursorThatWasAskedFor
        // -----------------------------------------------------------------------------------
        // Description:
        //     An event replay for a different cursor than asked is a contract error.
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
