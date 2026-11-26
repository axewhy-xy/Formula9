/*
 * Unit tests for the emergency dispatch rules in DecisionRules.cs.

 * Author: Ang Yu Xuan
 * Date Created: 19/11/2026
 * Last Modified: 24/11/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Formula9.Contracts;
using Formula9.Session;
using NUnit.Framework;
using UnityEngine;

namespace Formula9.Tests
{
    public sealed class EmergencyDispatchTests
    {
        private static FullSnapshot Low()
        {
            var text = File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/low_reserve_snapshot.json"));
            var s = ContractParser.Parse<FullSnapshot>(text);
            s.Session.Status = "RUNNING";
            return s;
        }

        // shuttle-02 has broken down and the backend has an assistance option for it
        private static FullSnapshot Broken(string action = "ESCALATE", string validation = "PASS", string incidentState = "OPTIONS_READY")
        {
            var s = Low();
            s.Vehicles.First(v => v.VehicleId == "shuttle-02").ServiceState = "OUT_OF_SERVICE";
            s.Incidents = new List<Incident>
            {
                new Incident { IncidentId = "inc-1", VehicleId = "shuttle-02", EventType = "BREAKDOWN", State = incidentState,
                               AggregateRevision = 3, DetectedTick = s.Tick, CandidateIds = new List<string> { "cand-1" } },
            };
            s.Candidates = new List<Candidate>
            {
                new Candidate { CandidateId = "cand-1", IncidentId = "inc-1", VehicleId = "shuttle-02", Action = action,
                                Validation = validation, ExpiresAtTick = s.Tick + 50, Revision = 1 },
            };
            return s;
        }

        [Test]
        public void NonOperational_ListsStrandedAndBrokenShuttles_NotHealthyOnes()
        {
            var s = Broken();
            s.Incidents.Add(new Incident { IncidentId = "inc-2", VehicleId = "shuttle-03", EventType = "BREAKDOWN", State = "EXECUTING", CandidateIds = new List<string>() });
            s.Incidents.Add(new Incident { IncidentId = "inc-3", VehicleId = "shuttle-04", EventType = "BREAKDOWN", State = "RESOLVED", CandidateIds = new List<string>() });
            var ids = DecisionRules.NonOperational(s).Select(v => v.VehicleId).ToList();
            CollectionAssert.IsSubsetOf(new[] { "shuttle-02", "shuttle-03" }, ids);
            Assert.IsFalse(ids.Contains("shuttle-04"), "a breakdown that is over does not count");
        }

        [Test]
        public void NonOperational_IsEmptyWithoutASnapshot() => Assert.IsEmpty(DecisionRules.NonOperational(null));

        [Test]
        public void AssistanceOption_IsFound_ForTheBrokenShuttle()
        {
            Assert.AreEqual("cand-1", DecisionRules.AssistanceOption(Broken(), "shuttle-02")?.CandidateId);
        }

        [Test]
        public void AssistanceOption_IsNull_WhenItCannotBeApprovedNow()
        {
            Assert.IsNull(DecisionRules.AssistanceOption(Broken(), "shuttle-01"), "another shuttle");
            Assert.IsNull(DecisionRules.AssistanceOption(Broken(action: "WAIT"), "shuttle-02"), "not an assistance option");
            Assert.IsNull(DecisionRules.AssistanceOption(Broken(validation: "FAIL"), "shuttle-02"), "failed validation");
            Assert.IsNull(DecisionRules.AssistanceOption(Broken(incidentState: "ESCALATED"), "shuttle-02"), "already requested");
            var expired = Broken(); expired.Tick += 100;
            Assert.IsNull(DecisionRules.AssistanceOption(expired, "shuttle-02"), "expired");
            var paused = Broken(); paused.Session.Status = "PAUSED";
            Assert.IsNull(DecisionRules.AssistanceOption(paused, "shuttle-02"), "session not running");
        }

        [Test]
        public void Command_IsTheIncidentApproval_OfTheAssistanceOption()
        {
            var command = DecisionRules.EmergencyDispatchCommand(Broken(), "shuttle-02", "Shuttle dead at the stop, passengers aboard");
            Assert.AreEqual("APPROVE_INCIDENT_OPTION", command.CommandType);
            Assert.AreEqual("inc-1", command.TargetId);
            Assert.AreEqual("cand-1", (string)command.Payload["candidate_id"]);
        }

        [Test]
        public void Command_NeedsAReason_AndAnOption()
        {
            Assert.Throws<ArgumentException>(() => DecisionRules.EmergencyDispatchCommand(Broken(), "shuttle-02", "  "));
            Assert.Throws<ArgumentException>(() => DecisionRules.EmergencyDispatchCommand(Broken(validation: "FAIL"), "shuttle-02", "reason"));
        }
    }
}
