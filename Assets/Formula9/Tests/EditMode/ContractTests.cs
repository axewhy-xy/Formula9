/*
 * Unit tests for the contract layer (ContractParser.cs, SchemaValidator.cs, ContractRules.cs).
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Uses the same shared fixtures the backend, web and Qt clients test against, copied from
 *     Formula_9 contracts/v2/fixtures by Tools/generate_contracts.py. Valid fixtures must parse,
 *     invalid ones must be rejected for the right reason, and the strict rules (integer types,
 *     duplicate keys, provenance, command status) must hold.
 */

using System.IO;
using Formula9.Contracts;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Formula9.Tests
{
    // -----------------------------------------------------------------------------------
    // Class: ContractTests
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class tests parsing and validation against the shared fixtures.
    public class ContractTests
    {
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
        // Function: FixtureObject
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reads a shared fixture as JSON to modify.
        //
        // Args:
        //     name (string): The fixture name, without .json.
        //
        // Returns:
        //     JObject: The fixture.
        private static JObject FixtureObject(string name) => JObject.Parse(Fixture(name));

        // -----------------------------------------------------------------------------------
        // Function: SharedPositiveFixturesParse
        // -----------------------------------------------------------------------------------
        // Description:
        //     Every valid shared fixture parses as its message type.
        [TestCase("snapshot", "FULL_SNAPSHOT")]
        [TestCase("low_reserve_snapshot", "FULL_SNAPSHOT")]
        [TestCase("spatial_snapshot", "FULL_SNAPSHOT")]
        [TestCase("event_replay", "EVENT_REPLAY")]
        [TestCase("command_status", "COMMAND_STATUS")]
        [TestCase("error", "ERROR")]
        public void SharedPositiveFixturesParse(string name, string messageType)
        {
            Assert.AreEqual(messageType, ContractParser.ParseMessage(Fixture(name)).MessageType);
        }

        // -----------------------------------------------------------------------------------
        // Function: SharedNegativeFixturesAreRejectedForTheRightReason
        // -----------------------------------------------------------------------------------
        // Description:
        //     Every invalid shared fixture is rejected, and the problem names the rule it breaks.
        [TestCase("invalid_enum", "service_state")]
        [TestCase("missing_reference", "missing vehicle route revision reference")]
        [TestCase("stale_scene_revision", "snapshot scene/transform revision mismatch")]
        [TestCase("incompatible_major", "contract_version")]
        [TestCase("invalid_event_gap", "event replay has a sequence gap")]
        public void SharedNegativeFixturesAreRejectedForTheRightReason(string name, string reason)
        {
            var error = Assert.Throws<ContractException>(() => ContractParser.ParseMessage(Fixture(name)));
            StringAssert.Contains(reason, error.Message);
        }

        // -----------------------------------------------------------------------------------
        // Function: SnapshotFieldsAreTyped
        // -----------------------------------------------------------------------------------
        // Description:
        //     Snapshot fields read as typed values, and absent optional lists read as empty.
        [Test]
        public void SnapshotFieldsAreTyped()
        {
            var snapshot = (FullSnapshot)ContractParser.ParseMessage(Fixture("snapshot"));
            var vehicle = snapshot.Vehicles[0];
            Assert.AreEqual("shuttle-01", vehicle.VehicleId);
            Assert.AreEqual(28.0, vehicle.BatteryKwh);
            Assert.AreEqual(2, vehicle.RouteRevision);
            Assert.AreEqual("TRAINING_SIMULATION", vehicle.Provenance.Kind);
            Assert.IsNotNull(snapshot.ChargeOptions, "absent optional lists read as empty");
        }

        // -----------------------------------------------------------------------------------
        // Function: MinorVersionAndAdditiveFieldsAreAccepted
        // -----------------------------------------------------------------------------------
        // Description:
        //     A newer minor version and unknown extra fields are accepted, and the extra fields are
        //     kept.
        [Test]
        public void MinorVersionAndAdditiveFieldsAreAccepted()
        {
            var value = FixtureObject("snapshot");
            value["contract_version"] = "2.1";
            value["future_metadata"] = new JObject { ["ignored"] = true };
            var snapshot = (FullSnapshot)ContractParser.ParseMessage(value.ToString());
            Assert.AreEqual(true, (bool)snapshot.Extra["future_metadata"]["ignored"]);
        }

        // -----------------------------------------------------------------------------------
        // Function: RecordedProvenanceRequiresActualFrameAndTimestamp
        // -----------------------------------------------------------------------------------
        // Description:
        //     Recorded sensor provenance must name its frame and timestamp.
        [Test]
        public void RecordedProvenanceRequiresActualFrameAndTimestamp()
        {
            var value = FixtureObject("snapshot");
            value["vehicles"][0]["provenance"]["kind"] = "RECORDED_SENSOR";
            Assert.Throws<ContractException>(() => ContractParser.ParseMessage(value.ToString()));
        }

        // -----------------------------------------------------------------------------------
        // Function: QueuedCommandCannotClaimApplication
        // -----------------------------------------------------------------------------------
        // Description:
        //     A QUEUED command status cannot carry an applied tick or result event.
        [Test]
        public void QueuedCommandCannotClaimApplication()
        {
            var value = FixtureObject("command_status");
            value["status"] = "QUEUED";
            var error = Assert.Throws<ContractException>(() => ContractParser.ParseMessage(value.ToString()));
            StringAssert.Contains("QUEUED cannot claim application", error.Message);
        }

        // -----------------------------------------------------------------------------------
        // Function: IntegersMustBeJsonIntegers
        // -----------------------------------------------------------------------------------
        // Description:
        //     An integer field holding a decimal number is rejected.
        [Test]
        public void IntegersMustBeJsonIntegers()
        {
            var value = FixtureObject("snapshot");
            value["tick"] = 200.5;
            Assert.Throws<ContractException>(() => ContractParser.ParseMessage(value.ToString()));
        }

        // -----------------------------------------------------------------------------------
        // Function: DuplicateKeysAreRejected
        // -----------------------------------------------------------------------------------
        // Description:
        //     A document with a repeated key is rejected.
        [Test]
        public void DuplicateKeysAreRejected()
        {
            var json = Fixture("command_status").TrimEnd().TrimEnd('}') + ", \"status\": \"APPLIED\"}";
            Assert.Throws<ContractException>(() => ContractParser.ParseMessage(json));
        }

        // -----------------------------------------------------------------------------------
        // Function: CommandFixtureParsesAndDecisionsNeedAReason
        // -----------------------------------------------------------------------------------
        // Description:
        //     The command fixture parses, and a decision command without a reason is rejected.
        [Test]
        public void CommandFixtureParsesAndDecisionsNeedAReason()
        {
            Assert.AreEqual("APPROVE_INCIDENT_OPTION", ContractParser.Parse<DispatcherCommand>(Fixture("command")).CommandType);
            var value = FixtureObject("command");
            value["reason"] = null;
            var error = Assert.Throws<ContractException>(() => ContractParser.Parse<DispatcherCommand>(value.ToString()));
            StringAssert.Contains("decision command requires reason", error.Message);
        }

        // -----------------------------------------------------------------------------------
        // Function: SessionListAndCreateRequestFixturesParse
        // -----------------------------------------------------------------------------------
        // Description:
        //     The session list and create-session fixtures parse.
        [Test]
        public void SessionListAndCreateRequestFixturesParse()
        {
            var listed = ContractParser.Parse<SessionListResponse>(Fixture("session_list"));
            Assert.Greater(listed.Sessions.Count, 0);
            Assert.AreEqual(16, listed.Capacity);
            Assert.IsNotNull(ContractParser.Parse<CreateSessionRequest>(Fixture("create_session")).ScenarioRevisionId);
        }

        // -----------------------------------------------------------------------------------
        // Function: OutgoingCreateRequestIsValidatedAndSerialised
        // -----------------------------------------------------------------------------------
        // Description:
        //     An outgoing create request is serialised in contract form, and an invalid one is
        //     refused.
        [Test]
        public void OutgoingCreateRequestIsValidatedAndSerialised()
        {
            var json = ContractParser.Serialize(new CreateSessionRequest { ScenarioRevisionId = "scenario-m2-r1", Seed = 42 });
            Assert.AreEqual("scenario-m2-r1", (string)JObject.Parse(json)["scenario_revision_id"]);
            Assert.Throws<ContractException>(() => ContractParser.Serialize(new CreateSessionRequest { ScenarioRevisionId = "bad id!" }));
        }
    }
}
