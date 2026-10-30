using System.IO;
using System.Linq;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.Session;
using Formula9.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Formula9.Tests
{
    public class PresenterTests
    {
        private static FullSnapshot Snapshot(System.Action<JObject> change = null)
        {
            var value = JObject.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/snapshot.json")));
            change?.Invoke(value);
            return (FullSnapshot)ContractParser.ParseMessage(value.ToString());
        }

        // The fixture's scripted closure blocks route-a; clear it so only the status decides.
        private static FullSnapshot WithStatus(string status) => Snapshot(v =>
        {
            v["session"]["status"] = status;
            v["disruptions"] = new JArray();
        });

        [Test]
        public void TheFixturesScriptedClosureBlocksStart()
        {
            var snapshot = Snapshot(v => v["session"]["status"] = "READY");
            Assert.IsTrue(TrainingPresenter.AssignedRouteIsClosed(snapshot));
            Assert.IsFalse(TrainingPresenter.Controls(snapshot, true, true).Start);
        }

        [Test]
        public void LoginMessagesMatchTheQtClient()
        {
            Assert.AreEqual("Can't reach the server. Start the backend (run.bat) and try again.", LoginMessages.For(new NoReplyException("down")));
            Assert.AreEqual("Incorrect username or password.",
                LoginMessages.For(new BackendErrorException(new ApiError { Status = 401, Code = "invalid_credentials" })));
            StringAssert.StartsWith("Login isn't available", LoginMessages.For(new NoReplyException("html", 404)));
            Assert.AreEqual("Username is taken.",
                LoginMessages.For(new BackendErrorException(new ApiError { Status = 409, Code = "username_taken", Message = "Username is taken." })));
            StringAssert.Contains("error 503", LoginMessages.For(new BackendErrorException(new ApiError { Status = 503, Code = "x", Message = "x" })));
            Assert.AreEqual("Passwords do not match.", LoginMessages.Validate("a", "secret1", true, "secret2"));
            Assert.AreEqual("Enter your username and password.", LoginMessages.Validate(" ", "x", false, null));
            Assert.IsNull(LoginMessages.Validate("trainee", "secret", false, null));
        }

        [Test]
        public void StatusLineAndTitleDescribeTheSnapshot()
        {
            var snapshot = Snapshot();
            Assert.AreEqual("Session session-001 · epoch 1 · snapshot snapshot-200 · tick 200 · RUNNING · current",
                TrainingPresenter.StatusLine(snapshot, true));
            StringAssert.EndsWith("stale/offline", TrainingPresenter.StatusLine(snapshot, false));
            StringAssert.StartsWith("Singapore, real streets", TrainingPresenter.Title(snapshot));
        }

        [TestCase("READY", true, false, false, true)]
        [TestCase("RUNNING", false, true, false, true)]
        [TestCase("PAUSED", false, false, true, true)]
        [TestCase("ARCHIVED", false, false, false, false)]
        public void ControlsFollowTheSessionStatus(string status, bool start, bool pause, bool resume, bool archive)
        {
            var controls = TrainingPresenter.Controls(WithStatus(status), true, false);
            Assert.AreEqual(new[] { start, pause, resume, archive }, new[] { controls.Start, controls.Pause, controls.Resume, controls.Archive });
        }

        [Test]
        public void NothingIsEnabledWhileOfflineOrWithoutASession()
        {
            var offline = TrainingPresenter.Controls(WithStatus("RUNNING"), false, false);
            Assert.IsFalse(offline.Pause || offline.Archive);
            var none = TrainingPresenter.Controls(null, true, true);
            Assert.IsFalse(none.Start || none.Pause || none.Resume || none.Archive);
        }

        [Test]
        public void StartWaitsWhileAnAssignedRouteCrossesAnActiveClosure()
        {
            var snapshot = Snapshot(v =>
            {
                v["session"]["status"] = "READY";
                var edge = (string)v["routes"][0]["directed_edge_ids"][0];
                var closure = (JObject)v["disruptions"][0];
                closure["affected_edge_ids"] = new JArray(edge);
                closure["active_from_tick"] = 0;
                closure["active_until_tick"] = null;
            });
            Assert.IsTrue(TrainingPresenter.AssignedRouteIsClosed(snapshot));
            var controls = TrainingPresenter.Controls(snapshot, true, true);
            Assert.IsFalse(controls.Start);
            StringAssert.Contains("closed road", controls.StartHint);
        }

        [Test]
        public void FleetRowsAreSortedAndReadable()
        {
            var rows = TrainingPresenter.Fleet(Snapshot());
            CollectionAssert.IsOrdered(rows.Select(r => r.ShuttleId).ToList());
            var first = rows.First();
            Assert.AreEqual("shuttle-01", first.ShuttleId);
            Assert.AreEqual("route-a / 2", first.Route);
            Assert.AreEqual("28 / 40 kWh", first.Battery);
        }

        [Test]
        public void OutcomeMessagesMatchTheQtClient()
        {
            var applied = new CommandOutcome { CommandId = "c-1", TargetId = "session-001", TargetType = "SESSION", Status = "APPLIED", AppliedTick = 210 };
            Assert.AreEqual("Decision c-1 APPLIED for session-001 at tick 210.", TrainingPresenter.Outcome(applied));
            var blocked = new CommandOutcome { CommandId = "c-2", TargetId = "session-001", TargetType = "SESSION", Status = "REJECTED", FailureCode = "UNKNOWN_CONSTRAINT" };
            StringAssert.StartsWith("Can't start yet", TrainingPresenter.Outcome(blocked));
            var unconfirmed = new CommandOutcome { CommandId = "c-3", TargetId = "session-001", Status = "NEEDS_RECONFIRMATION" };
            StringAssert.Contains("needs reconfirmation", TrainingPresenter.Outcome(unconfirmed));
        }

        [Test]
        public void TheScenariosAreOfferedInTheQtOrder()
        {
            Assert.AreEqual("scenario-m2-road-3d-r1", TrainingPresenter.Scenarios[0].Id);
            Assert.AreEqual(SessionController.IntegratedScenario, TrainingPresenter.Scenarios[1].Id);
            StringAssert.StartsWith("Singapore", TrainingPresenter.ScenarioTitle(SessionController.IntegratedScenario));
        }

        [TestCase("ARCHIVED", false, true)]
        [TestCase("COMPLETED", false, true)]
        [TestCase("FAILED", false, true)]
        [TestCase("READY", true, false)]
        [TestCase("RUNNING", true, false)]
        [TestCase("PAUSED", true, false)]
        [TestCase("PAUSED", false, false)] // a stored fixture without a live actor is not finished either
        [TestCase("ARCHIVED", true, false)]
        public void OnlyFinishedSessionsCanBeDeleted(string status, bool occupiesCapacity, bool expected)
        {
            Assert.AreEqual(expected, TrainingPresenter.CanDelete(new SessionListEntry { Status = status, OccupiesCapacity = occupiesCapacity }));
            Assert.IsFalse(TrainingPresenter.CanDelete(null));
        }

        [Test]
        public void CapacityCountsAllStoredSessionsAndSaysWhenTheListIsPartial()
        {
            var list = new SessionListResponse { LiveCount = 1, Capacity = 16, Total = 2, Sessions = new System.Collections.Generic.List<SessionListEntry> { new SessionListEntry(), new SessionListEntry() } };
            Assert.AreEqual("1/16 live sessions · 2 total", TrainingPresenter.Capacity(list));
            list.Total = 120;
            Assert.AreEqual("1/16 live sessions · 120 total · showing 2", TrainingPresenter.Capacity(list));
        }

        [Test]
        public void ReclaimedSpaceIsShownInMegabytesAndMentionsAHeldWal()
        {
            var result = new SessionCompaction { DatabaseBytesBefore = 3145728, DatabaseBytesAfter = 1048576, WalTruncated = true };
            Assert.AreEqual("Training database reduced from 3.0 MB to 1.0 MB.", TrainingPresenter.Reclaimed(result));
            result.WalTruncated = false;
            StringAssert.Contains("try again", TrainingPresenter.Reclaimed(result));
        }
    }
}
