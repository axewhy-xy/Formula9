/*
 * Unit tests for Presenters.cs.
 *
 * Author: Choo Jian Wei
 * Contributors: Rishika (fleet card tests), Lau Xiang Yang (session cleanup tests)
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Checks what the screens show, using the shared snapshot fixture: login and command messages
 *     match the Qt client, session controls follow the status (and Start waits for a detour when a
 *     route crosses a closed road), fleet rows are sorted and readable, and the session list and
 *     cleanup texts.
 */

using System.Collections.Generic;
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
    // -----------------------------------------------------------------------------------
    // Class: PresenterTests
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class tests the presenters.
    public class PresenterTests
    {
        // -----------------------------------------------------------------------------------
        // Function: Snapshot
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function parses the shared snapshot fixture, optionally changed first.
        //
        // Args:
        //     change (Action<JObject>): Changes the fixture's JSON before parsing, or null.
        //
        // Returns:
        //     FullSnapshot: The snapshot.
        private static FullSnapshot Snapshot(System.Action<JObject> change = null)
        {
            var value = JObject.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/snapshot.json")));
            change?.Invoke(value);
            return (FullSnapshot)ContractParser.ParseMessage(value.ToString());
        }

        // -----------------------------------------------------------------------------------
        // Function: WithStatus
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function makes a snapshot with a given session status. The fixture's scripted
        //     closure blocks route-a, so it is cleared and only the status decides.
        //
        // Args:
        //     status (string): The session status.
        //
        // Returns:
        //     FullSnapshot: The snapshot.
        private static FullSnapshot WithStatus(string status) => Snapshot(v =>
        {
            v["session"]["status"] = status;
            v["disruptions"] = new JArray();
        });

        // -----------------------------------------------------------------------------------
        // Function: TheFixturesScriptedClosureBlocksStart
        // -----------------------------------------------------------------------------------
        // Description:
        //     The unchanged fixture's closure blocks Start, which is why WithStatus clears it.
        [Test]
        public void TheFixturesScriptedClosureBlocksStart()
        {
            var snapshot = Snapshot(v => v["session"]["status"] = "READY");
            Assert.IsTrue(TrainingPresenter.AssignedRouteIsClosed(snapshot));
            Assert.IsFalse(TrainingPresenter.Controls(snapshot, true, true).Start);
        }

        // -----------------------------------------------------------------------------------
        // Function: LoginMessagesMatchTheQtClient
        // -----------------------------------------------------------------------------------
        // Description:
        //     Every login outcome shows the Qt client's wording.
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
            Assert.AreEqual("Exit Formula 9?", LoginMessages.ConfirmExit(false));
            Assert.AreEqual("Exit Formula 9? You will need to log in again next time.", LoginMessages.ConfirmExit(true));
        }

        // -----------------------------------------------------------------------------------
        // Function: TitleDescribesTheSnapshot
        // -----------------------------------------------------------------------------------
        // Description:
        //     The title describes the joined session.
        [Test]
        public void TitleDescribesTheSnapshot()
        {
            StringAssert.StartsWith("Singapore, real streets", TrainingPresenter.Title(Snapshot()));
        }

        // -----------------------------------------------------------------------------------
        // Function: ControlsFollowTheSessionStatus
        // -----------------------------------------------------------------------------------
        // Description:
        //     Each session status enables exactly the controls it allows.
        [TestCase("READY", true, false, false, true)]
        [TestCase("RUNNING", false, true, false, true)]
        [TestCase("PAUSED", false, false, true, true)]
        [TestCase("ARCHIVED", false, false, false, false)]
        public void ControlsFollowTheSessionStatus(string status, bool start, bool pause, bool resume, bool archive)
        {
            var controls = TrainingPresenter.Controls(WithStatus(status), true, false);
            Assert.AreEqual(new[] { start, pause, resume, archive }, new[] { controls.Start, controls.Pause, controls.Resume, controls.Archive });
        }

        // -----------------------------------------------------------------------------------
        // Function: NothingIsEnabledWhileOfflineOrWithoutASession
        // -----------------------------------------------------------------------------------
        // Description:
        //     No control is enabled while offline or with no session.
        [Test]
        public void NothingIsEnabledWhileOfflineOrWithoutASession()
        {
            var offline = TrainingPresenter.Controls(WithStatus("RUNNING"), false, false);
            Assert.IsFalse(offline.Pause || offline.Archive);
            var none = TrainingPresenter.Controls(null, true, true);
            Assert.IsFalse(none.Start || none.Pause || none.Resume || none.Archive);
        }

        [TestCase("READY", true, false, false)]
        [TestCase("RUNNING", false, true, false)]
        [TestCase("PAUSED", false, false, true)]
        [TestCase("DRAFT", false, false, false)]
        [TestCase("RECOVERING", false, false, false)]
        [TestCase("COMPLETED", false, false, false)]
        [TestCase("ARCHIVED", false, false, false)]
        [TestCase("FAILED", false, false, false)]
        public void RelevantControlsStayVisibleWhenOffline(string state, bool start, bool pause, bool resume)
        {
            var s = WithStatus(state);
            foreach (var available in new[] { true, false })
            {
                var controls = TrainingPresenter.Controls(s, available, true);
                CollectionAssert.AreEqual(new[] { start, pause, resume },
                    new[] { controls.StartVisible, controls.PauseVisible, controls.ResumeVisible });
                Assert.AreEqual(start || pause || resume, controls.PlaybackVisible);
                Assert.AreEqual(available && start, controls.Start);
                Assert.AreEqual(available && pause, controls.Pause);
                Assert.AreEqual(available && resume, controls.Resume);
            }
            Assert.IsFalse(TrainingPresenter.Controls(null, true, true).PlaybackVisible);
        }

        [TestCase("DRAFT", "Draft", "session-badge-draft")]
        [TestCase("READY", "Ready", "session-badge-ready")]
        [TestCase("RUNNING", "Running", "session-badge-running")]
        [TestCase("PAUSED", "Paused", "session-badge-paused")]
        [TestCase("RECOVERING", "Recovering", "session-badge-recovering")]
        [TestCase("COMPLETED", "Completed", "session-badge-completed")]
        [TestCase("ARCHIVED", "Archived", "session-badge-archived")]
        [TestCase("FAILED", "Failed", "session-badge-failed")]
        public void BadgesDescribeEverySessionState(string state, string text, string style)
        {
            var view = TrainingPresenter.SessionStatus(WithStatus(state), true);
            Assert.AreEqual(text, view.BadgeText);
            Assert.AreEqual(style, view.BadgeClass);
            Assert.AreEqual("Session: session-001 · Connected", view.Summary);
            Assert.IsFalse(view.Offline);
        }

        [TestCase("unexpected-status")]
        [TestCase("")]
        [TestCase(null)]
        public void UnknownStatesHaveASafeBadgeAndOfflineExplanation(string state)
        {
            var s = WithStatus("RUNNING"); s.Session.Status = state;
            var view = TrainingPresenter.SessionStatus(s, true);
            Assert.AreEqual("Unknown", view.BadgeText);
            Assert.AreEqual("session-badge-unknown", view.BadgeClass);
            Assert.AreEqual("Current simulation state: unknown.", view.Tooltip);
            var offline = TrainingPresenter.SessionStatus(s, false);
            Assert.AreEqual("Offline", offline.BadgeText);
            Assert.AreEqual("session-badge-offline", offline.BadgeClass);
            Assert.IsTrue(offline.Offline);
            Assert.AreEqual("Live updates unavailable. Last received simulation state: unknown.", offline.Tooltip);
            StringAssert.Contains("showing last received state", offline.Summary);
        }

        [TestCase("ARCHIVED")]
        [TestCase("COMPLETED")]
        [TestCase("FAILED")]
        public void FinishedSessionsDefaultToVisibleSetup(string state)
        {
            foreach (var charging in new[] { true, false })
            {
                var setup = TrainingPresenter.SessionSetup(WithStatus(state), charging, null);
                Assert.IsTrue(setup.ToolsVisible);
                Assert.IsTrue(setup.ToggleVisible);
                Assert.IsFalse(setup.Compact);
            }
        }

        [Test]
        public void ChargingIsCompactUntilSetupIsExplicitlyOpened()
        {
            var s = WithStatus("RUNNING");
            var compact = TrainingPresenter.SessionSetup(s, true, null);
            Assert.IsTrue(compact.Compact);
            Assert.IsFalse(compact.ToolsVisible);
            var open = TrainingPresenter.SessionSetup(s, true, true);
            Assert.IsTrue(open.ToolsVisible);
            Assert.IsFalse(open.Compact);
            Assert.IsFalse(TrainingPresenter.SessionSetup(s, false, null).Compact);
            var empty = TrainingPresenter.SessionSetup(null, true, false);
            Assert.IsTrue(empty.ToolsVisible);
            Assert.IsFalse(empty.ToggleVisible);
            Assert.IsFalse(empty.Compact);
        }

        [Test]
        public void StatusTextIsReusedUntilItsInputsChange()
        {
            var presenter = new SessionStatusPresenter();
            var s = WithStatus("RUNNING");
            var initial = presenter.Present(s, true);
            s.Tick++; s.SnapshotId = "a-new-snapshot";
            var next = presenter.Present(s, true);
            Assert.AreSame(initial.Summary, next.Summary);
            Assert.AreSame(initial.Tooltip, next.Tooltip);
            var offline = presenter.Present(s, false);
            Assert.AreEqual("Offline", offline.BadgeText);
            StringAssert.Contains("running", offline.Tooltip);
            s.Session.Status = "PAUSED";
            StringAssert.Contains("paused", presenter.Present(s, false).Tooltip);
            Assert.AreEqual("Paused", presenter.Present(s, true).BadgeText);
            s.SessionId = "another-session";
            StringAssert.Contains("another-session", presenter.Present(s, true).Summary);
            Assert.AreEqual(TrainingPresenter.NoSession, presenter.Present(null, false).Summary);
        }

        // -----------------------------------------------------------------------------------
        // Function: StartWaitsWhileAnAssignedRouteCrossesAnActiveClosure
        // -----------------------------------------------------------------------------------
        // Description:
        //     Start stays disabled, with a hint, while an assigned route crosses a closure that is
        //     active now, and not before or after it.
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

        // -----------------------------------------------------------------------------------
        // Function: FleetRowsAreSortedAndReadable
        // -----------------------------------------------------------------------------------
        // Description:
        //     Fleet rows are sorted by shuttle ID and show readable status, route and battery.
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

        // -----------------------------------------------------------------------------------
        // Function: OutcomeMessagesMatchTheQtClient
        // -----------------------------------------------------------------------------------
        // Description:
        //     Every command outcome shows the Qt client's wording.
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

        // -----------------------------------------------------------------------------------
        // Function: OnlyTheSingaporeExerciseIsOffered
        // -----------------------------------------------------------------------------------
        // Description:
        //     New sessions offer only the Singapore exercise.
        [Test]
        public void OnlyTheSingaporeExerciseIsOffered()
        {
            Assert.AreEqual(1, TrainingPresenter.Scenarios.Count);
            Assert.AreEqual(SessionController.IntegratedScenario, TrainingPresenter.Scenarios[0].Id);
            StringAssert.StartsWith("Singapore", TrainingPresenter.ScenarioTitle(SessionController.IntegratedScenario));
        }

        // -----------------------------------------------------------------------------------
        // Function: OnlyFinishedSessionsCanBeDeleted
        // -----------------------------------------------------------------------------------
        // Description:
        //     Only archived, completed or failed sessions that no longer occupy capacity can be
        //     deleted.
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

        // -----------------------------------------------------------------------------------
        // Function: CapacityCountsAllStoredSessionsAndSaysWhenTheListIsPartial
        // -----------------------------------------------------------------------------------
        // Description:
        //     The capacity line counts every stored session and says when only some are shown.
        [Test]
        public void CapacityCountsAllStoredSessionsAndSaysWhenTheListIsPartial()
        {
            var list = new SessionListResponse { LiveCount = 1, Capacity = 16, Total = 2, Sessions = new System.Collections.Generic.List<SessionListEntry> { new SessionListEntry(), new SessionListEntry() } };
            Assert.AreEqual("1/16 live sessions · 2 total", TrainingPresenter.Capacity(list));
            list.Total = 120;
            Assert.AreEqual("1/16 live sessions · 120 total · showing 2", TrainingPresenter.Capacity(list));
        }

        // -----------------------------------------------------------------------------------
        // Function: ArchiveAllSaysHowManyAreNoLongerLive
        // -----------------------------------------------------------------------------------
        // Description:
        //     Archive all's message counts what is no longer live, singular for one.
        [Test]
        public void ArchiveAllSaysHowManyAreNoLongerLive()
        {
            Assert.AreEqual("No live sessions to archive.", TrainingPresenter.ArchivedAll(0, 0));
            Assert.AreEqual("Archived 1 live session.", TrainingPresenter.ArchivedAll(1, 1));
            Assert.AreEqual("Archived 3 live sessions.", TrainingPresenter.ArchivedAll(3, 3));
            Assert.AreEqual("Archived 2 of 3 live sessions; 1 session is still live. Refresh the list and try again.",
                TrainingPresenter.ArchivedAll(3, 2));
        }

        // -----------------------------------------------------------------------------------
        // Function: PurgeKeepsTheNewestAndSaysHowManyGo
        // -----------------------------------------------------------------------------------
        // Description:
        //     A purge keeps the newest finished sessions and every live one, and the question
        //     says exactly how many go; with none to go it says so instead.
        [Test]
        public void PurgeKeepsTheNewestAndSaysHowManyGo()
        {
            var list = new SessionListResponse { LiveCount = 16, Capacity = 16, Total = 37, Sessions = new System.Collections.Generic.List<SessionListEntry>() };
            Assert.AreEqual(1, TrainingPresenter.PurgeCount(list, 20), "21 finished, 20 kept");
            Assert.AreEqual("Permanently delete the 1 oldest finished session? The 20 newest finished sessions and every live session are kept. " +
                            "Their history is removed for everyone. This cannot be undone.", TrainingPresenter.PurgeQuestion(list, 20));
            list.Total = 50;
            StringAssert.StartsWith("Permanently delete the 14 oldest finished sessions?", TrainingPresenter.PurgeQuestion(list, 20));
            list.Total = 30;
            Assert.AreEqual(0, TrainingPresenter.PurgeCount(list, 20));
            Assert.AreEqual("Nothing to delete: there are 14 finished sessions, and the 20 newest are always kept.",
                TrainingPresenter.PurgeQuestion(list, 20));
            Assert.AreEqual("Deleted 1 old training session.", TrainingPresenter.Purged(1));
            Assert.AreEqual("No old sessions to delete.", TrainingPresenter.Purged(0));
        }

        // -----------------------------------------------------------------------------------
        // Function: ReclaimedSpaceIsShownInMegabytesAndMentionsAHeldWal
        // -----------------------------------------------------------------------------------
        // Description:
        //     Reclaimed space is shown in megabytes, with a note when the write-ahead log was held.
        [Test]
        public void ReclaimedSpaceIsShownInMegabytesAndMentionsAHeldWal()
        {
            var result = new SessionCompaction { DatabaseBytesBefore = 3145728, DatabaseBytesAfter = 1048576, WalTruncated = true };
            Assert.AreEqual("Training database reduced from 3.0 MB to 1.0 MB.", TrainingPresenter.Reclaimed(result));
            result.WalTruncated = false;
            StringAssert.Contains("try again", TrainingPresenter.Reclaimed(result));
        }

        // -----------------------------------------------------------------------------------
        // Function: AShuttleShowsAnIncidentItWillReachAndWhen
        // -----------------------------------------------------------------------------------
        // Description:
        //     A world event that names no shuttle still shows on the cards of the shuttles it
        //     will reach, with their ETAs, and in its description with who reaches it.
        [Test]
        public void AShuttleShowsAnIncidentItWillReachAndWhen()
        {
            var snapshot = (FullSnapshot)ContractParser.ParseMessage(File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/world_event_snapshot.json")));
            var rows = TrainingPresenter.Fleet(snapshot).ToDictionary(r => r.ShuttleId);
            Assert.AreEqual("! Incident scheduled-collision-1 · Detected · reaches it in ~85 s", rows["shuttle-03"].Incident);
            Assert.AreEqual("! Incident scheduled-collision-1 · Detected · reaches it in ~110 s", rows["shuttle-04"].Incident);
            Assert.IsNull(rows["shuttle-05"].Incident);
            var world = snapshot.Incidents.Single(i => i.IncidentId == "scheduled-collision-1");
            var summary = TrainingPresenter.IncidentDescription(world);
            StringAssert.StartsWith("COLLISION on Lorong 6 Toa Payoh | DETECTED", summary);
            StringAssert.Contains("It belongs to no shuttle.", summary);
            StringAssert.Contains("Shuttles reaching it: shuttle-03 in ~85 s, shuttle-04 in ~110 s.", summary);
            StringAssert.Contains("No shuttle reaches it before it clears.", TrainingPresenter.IncidentDescription(new Incident
            {
                IncidentId = "random-road_block-9", State = "DETECTED", VehicleId = "shuttle-01", CandidateIds = new List<string>(),
            }));
        }

        // -----------------------------------------------------------------------------------
        // Function: TheDescriptionSaysWhatWasDecidedForEachShuttle
        // -----------------------------------------------------------------------------------
        // Description:
        //     Each shuttle's decision is listed, a late one marked, and the options are named
        //     for people.
        [Test]
        public void TheDescriptionSaysWhatWasDecidedForEachShuttle()
        {
            var snapshot = (FullSnapshot)ContractParser.ParseMessage(File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/incident_decisions_snapshot.json")));
            var incident = snapshot.Incidents.Single(i => i.IncidentId == "scheduled-collision-1");
            Assert.AreEqual("shuttle-03 request assistance", TrainingPresenter.Responses(incident));
            var summary = TrainingPresenter.IncidentDescription(incident);
            StringAssert.Contains("Decided: shuttle-03 request assistance.", summary);
            incident.Responses.Add(new IncidentResponse { VehicleId = "shuttle-04", Action = "KEEP_ROUTE", DecidedTick = 230, BeforeArrival = false });
            Assert.AreEqual("shuttle-03 request assistance, shuttle-04 keep route (late)", TrainingPresenter.Responses(incident));
            Assert.IsNull(TrainingPresenter.Responses(new Incident { IncidentId = "none" }));
            Assert.AreEqual("Hold at stop", TrainingPresenter.ActionLabel("HOLD_AT_STOP"));
            Assert.AreEqual("Keep route", TrainingPresenter.ActionLabel("WAIT"));
        }

        // -----------------------------------------------------------------------------------
        // Function: TheReportsDescribeAnIncidentWithoutAskingForADecision
        // -----------------------------------------------------------------------------------
        // Description:
        //     The Reports page describes an incident and what was decided, but asks nothing of
        //     the operator.
        [Test]
        public void TheReportsDescribeAnIncidentWithoutAskingForADecision()
        {
            var snapshot = (FullSnapshot)ContractParser.ParseMessage(File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/incident_decisions_snapshot.json")));
            var incident = snapshot.Incidents.Single(i => i.IncidentId == "scheduled-collision-1");
            var description = TrainingPresenter.IncidentDescription(incident);
            StringAssert.Contains("Decided: shuttle-03 request assistance.", description);
            StringAssert.DoesNotContain("Decide for each shuttle", description);
            StringAssert.DoesNotContain("No operator decision is required", description);
        }

        // -----------------------------------------------------------------------------------
        // Function: OnlyAnIncidentAShuttleWillReachIsAnnounced
        // -----------------------------------------------------------------------------------
        // Description:
        //     The backend's headline is shown when an incident a shuttle will reach is detected,
        //     or another shuttle comes to be exposed; not for one nobody reaches.
        [Test]
        public void OnlyAnIncidentAShuttleWillReachIsAnnounced()
        {
            Contracts.Event Incident(string type, JObject data) => new Contracts.Event { EntityType = "INCIDENT", EventType = type, Data = data };
            const string headline = "Shuttle 03 reaches a collision on Lorong 6 Toa Payoh in about 85 s";
            Assert.AreEqual(headline, TrainingPresenter.IncidentHeadline(Incident("INCIDENT_DETECTED", new JObject { ["alert"] = true, ["headline"] = headline })));
            Assert.IsNull(TrainingPresenter.IncidentHeadline(Incident("INCIDENT_DETECTED", new JObject { ["alert"] = false, ["headline"] = "A breakdown; no shuttle reaches it before it clears" })));
            Assert.AreEqual(headline, TrainingPresenter.IncidentHeadline(Incident("INCIDENT_EXPOSURE_CHANGED", new JObject { ["added_vehicle_ids"] = new JArray("shuttle-03"), ["headline"] = headline })));
            Assert.IsNull(TrainingPresenter.IncidentHeadline(Incident("INCIDENT_EXPOSURE_CHANGED", new JObject { ["added_vehicle_ids"] = new JArray(), ["headline"] = headline })));
            Assert.IsNull(TrainingPresenter.IncidentHeadline(Incident("INCIDENT_RESOLVED", new JObject { ["headline"] = headline })));
        }
    }
}
