/*
 * Cross-field rules of the v2 contracts that the schemas cannot express.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Part of the Unity client's contract layer. Each rule mirrors a pydantic validator in
 *     Formula_9 contracts/v2 (models.py, spatial.py), and the messages match those, so the client
 *     rejects exactly the documents the backend would. Keep the two in step when a rule changes.
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Formula9.Contracts
{
    // -----------------------------------------------------------------------------------
    // Class: ContractRules
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class applies the cross-field rules to a parsed contract document.
    public static class ContractRules
    {
        private static readonly HashSet<string> DecisionCommands =
            new HashSet<string> { "APPROVE_ROUTE", "APPROVE_INCIDENT_OPTION", "RESERVE_CHARGER", "CONTINUE_SERVICE" };

        // -----------------------------------------------------------------------------------
        // Function: Check
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function applies the rules that belong to a document's type. Types without rules
        //     pass.
        //
        // Args:
        //     value (object): The parsed document.
        //
        // Returns:
        //     IReadOnlyList<string>: The problems found; empty when the document is valid.
        public static IReadOnlyList<string> Check(object value)
        {
            var problems = new List<string>();
            switch (value)
            {
                case FullSnapshot snapshot: CheckSnapshot(snapshot, problems); break;
                case EventReplay replay: CheckReplay(replay, problems); break;
                case CommandStatus status: CheckEnvelope(status.ContractVersion, status.Source, problems); CheckStatus(status, problems); break;
                case ErrorResponse error: CheckEnvelope(error.ContractVersion, error.Source, problems); break;
                case DispatcherCommand command: CheckCommand(command, problems); break;
            }
            return problems;
        }

        // -----------------------------------------------------------------------------------
        // Function: SupportedVersion
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function tells whether a contract version is supported: major 2, any numeric
        //     minor, as models.supported_version decides.
        //
        // Args:
        //     version (string): The contract_version value.
        //
        // Returns:
        //     bool: True when supported.
        public static bool SupportedVersion(string version)
        {
            var parts = (version ?? "").Split('.');
            return parts.Length == 2 && parts[0] == "2" && parts[1].Length > 0 && parts[1].All(char.IsDigit);
        }

        // -----------------------------------------------------------------------------------
        // Function: CheckEnvelope
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function checks a message's contract version and source.
        //
        // Args:
        //     version (string): The contract version.
        //     source (Provenance): The message source.
        //     problems (List<string>): Where problems are added.
        private static void CheckEnvelope(string version, Provenance source, List<string> problems)
        {
            if (!SupportedVersion(version)) problems.Add("unsupported contract major version");
            CheckProvenance(source, problems);
        }

        // -----------------------------------------------------------------------------------
        // Function: CheckProvenance
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function checks a provenance record: recorded sensor data must name its
        //     recording, sequence, frame and timestamp, and only recorded sensor data may name a
        //     frame.
        //
        // Args:
        //     source (Provenance): The provenance, or null.
        //     problems (List<string>): Where problems are added.
        private static void CheckProvenance(Provenance source, List<string> problems)
        {
            if (source == null) return;
            if (source.Kind == "RECORDED_SENSOR" && (string.IsNullOrEmpty(source.RecordingId) || string.IsNullOrEmpty(source.SequenceId)
                    || source.SourceFrame == null || string.IsNullOrEmpty(source.SourceTimestamp)))
                problems.Add("RECORDED_SENSOR requires recording_id, sequence_id, source_frame and source_timestamp");
            if (source.Kind != "RECORDED_SENSOR" && source.SourceFrame != null)
                problems.Add("source_frame belongs only to recorded sensor data");
        }

        // -----------------------------------------------------------------------------------
        // Function: CheckCommand
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function checks an outgoing command: a supported version, and a reason for every
        //     decision command.
        //
        // Args:
        //     command (DispatcherCommand): The command.
        //     problems (List<string>): Where problems are added.
        private static void CheckCommand(DispatcherCommand command, List<string> problems)
        {
            if (!SupportedVersion(command.ContractVersion)) problems.Add("unsupported contract major version");
            if (DecisionCommands.Contains(command.CommandType) && string.IsNullOrEmpty(command.Reason))
                problems.Add("decision command requires reason");
        }

        // -----------------------------------------------------------------------------------
        // Function: CheckStatus
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function checks a command status: APPLIED needs its tick and result event,
        //     REJECTED a failure code, and QUEUED cannot claim to be applied.
        //
        // Args:
        //     status (CommandStatus): The status.
        //     problems (List<string>): Where problems are added.
        private static void CheckStatus(CommandStatus status, List<string> problems)
        {
            if (status.Status == "APPLIED" && (status.AppliedTick == null || status.ResultEventId == null))
                problems.Add("APPLIED requires applied_tick and result_event_id");
            if (status.Status == "REJECTED" && status.FailureCode == null) problems.Add("REJECTED requires failure_code");
            if (status.Status == "QUEUED" && (status.AppliedTick != null || status.ResultEventId != null))
                problems.Add("QUEUED cannot claim application");
        }

        // -----------------------------------------------------------------------------------
        // Function: CheckReplay
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function checks an event replay: event sequence numbers follow the requested
        //     cursor without gaps, and the next cursor is the last event returned and not beyond
        //     the session.
        //
        // Args:
        //     replay (EventReplay): The replay.
        //     problems (List<string>): Where problems are added.
        private static void CheckReplay(EventReplay replay, List<string> problems)
        {
            CheckEnvelope(replay.ContractVersion, replay.Source, problems);
            foreach (var e in replay.Events) CheckProvenance(e.Source, problems);
            for (var i = 0; i < replay.Events.Count; i++)
                if (replay.Events[i].EventSequence != replay.AfterSequence + 1 + i)
                {
                    problems.Add("event replay has a sequence gap");
                    return;
                }
            var last = replay.Events.Count > 0 ? replay.Events[replay.Events.Count - 1].EventSequence : replay.AfterSequence;
            if (replay.NextSequence != last || replay.NextSequence > replay.EventSequence) problems.Add("invalid replay cursor");
        }

        // -----------------------------------------------------------------------------------
        // Function: CheckSnapshot
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function checks each entity in a snapshot on its own (batteries, routes,
        //     forecasts, charge options, provenance), then, if all of those pass, the references
        //     between entities.
        //
        // Args:
        //     s (FullSnapshot): The snapshot.
        //     problems (List<string>): Where problems are added.
        private static void CheckSnapshot(FullSnapshot s, List<string> problems)
        {
            CheckEnvelope(s.ContractVersion, s.Source, problems);
            CheckProvenance(s.Scenario.Provenance, problems);
            foreach (var v in s.Vehicles)
            {
                CheckProvenance(v.Provenance, problems);
                if (v.BatteryKwh > v.BatteryCapacityKwh) problems.Add("battery_kwh exceeds capacity");
                if ((v.RouteId == null) != (v.RouteRevision == null)) problems.Add("route_id and route_revision must be supplied together");
                if (v.Spatial != null && Math.Abs(v.Spatial.OrientationXyzw.Sum(q => q * q) - 1) > 1e-6) problems.Add("NON_UNIT_QUATERNION");
            }
            foreach (var r in s.Routes)
            {
                if (r.EdgePathsM.Count > 0 && r.EdgePathsM.Count != r.DirectedEdgeIds.Count) problems.Add("edge_paths_m must have one path per directed edge");
                if (r.StopsM.Any(stop => !r.RequiredStopIds.Contains(stop.StopId))) problems.Add("stops_m may only place required stops");
            }
            foreach (var d in s.Disruptions) CheckProvenance(d.Provenance, problems);
            foreach (var i in s.Incidents) CheckProvenance(i.Provenance, problems);
            foreach (var f in s.EnergyForecasts)
            {
                if (f.NextDepotId == null && (f.NextDepotEtaS != null || f.ReachesDepotAboveReserve != null))
                    problems.Add("next_depot_eta_s and reaches_depot_above_reserve need next_depot_id");
                if (f.NextDepotId != null && f.NextDepotEtaS == null) problems.Add("next_depot_id needs next_depot_eta_s");
            }
            foreach (var o in s.ChargeOptions)
            {
                if (o.Action == "CHARGE_NOW" && o.ChargerId == null) problems.Add("CHARGE_NOW needs a charger_id");
                if (o.Action == "CONTINUE" && (o.ChargerId != null || o.OutOfServiceS != 0))
                    problems.Add("CONTINUE cannot use a charger or take the shuttle out of service");
                if (o.OfferedAtTick != null && o.OfferedAtTick > o.ExpiresAtTick) problems.Add("charge option cannot be offered after it expires");
            }
            if (problems.Count == 0) CheckReferences(s, problems);
        }

        // -----------------------------------------------------------------------------------
        // Function: CheckReferences
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function checks the references between snapshot entities, in the same order as
        //     FullSnapshot.references in models.py, stopping at the first problem as it does. It
        //     also drops zero charge deferrals, which models.py drops rather than rejects.
        //
        // Args:
        //     s (FullSnapshot): The snapshot.
        //     problems (List<string>): Where problems are added.
        private static void CheckReferences(FullSnapshot s, List<string> problems)
        {
            void Fail(string message) => problems.Add(message);
            if (s.Spatial != null)
            {
                if (!SameSpatial(s.Scenario.Spatial, s.Spatial) || s.Spatial.SceneRevision != s.SceneRevision
                        || s.Spatial.TransformRevision != s.TransformRevision) { Fail("SPATIAL_REVISION_MISMATCH"); return; }
                if (s.Vehicles.Any(v => v.Spatial == null)) { Fail("MISSING_MANDATORY_SPATIAL_POSE"); return; }
                foreach (var v in s.Vehicles)
                    if (v.Spatial.PositionM.Count != 3 || v.Spatial.PositionM[0] != v.Position.XM || v.Spatial.PositionM[1] != v.Position.YM
                            || v.Spatial.PositionM[2] != v.Position.ZM) { Fail("BODY_CENTRE_POSITION_MISMATCH"); return; }
            }
            else if (s.Scenario.Spatial != null || s.Vehicles.Any(v => v.Spatial != null)) { Fail("MISSING_SPATIAL_HANDSHAKE"); return; }
            if (s.SessionId != s.Session.SessionId || s.SessionEpoch != s.Session.SessionEpoch) { Fail("snapshot envelope/session mismatch"); return; }
            if (s.Scenario.RevisionId != s.Session.ScenarioRevisionId) { Fail("missing scenario revision reference"); return; }
            if (s.SceneRevision != s.Scenario.SceneRevision || s.TransformRevision != s.Scenario.TransformRevision)
            { Fail("snapshot scene/transform revision mismatch"); return; }

            var vehicles = new HashSet<string>(s.Vehicles.Select(v => v.VehicleId));
            var routes = new HashSet<(string, long)>(s.Routes.Select(r => (r.RouteId, r.Revision)));
            var incidents = new HashSet<string>(s.Incidents.Select(i => i.IncidentId));
            var candidates = new HashSet<string>(s.Candidates.Select(c => c.CandidateId));
            foreach (var (name, ids) in new (string, IEnumerable<string>)[] {
                         ("vehicle", s.Vehicles.Select(v => v.VehicleId)), ("route", s.Routes.Select(r => r.RouteId)),
                         ("incident", s.Incidents.Select(i => i.IncidentId)), ("candidate", s.Candidates.Select(c => c.CandidateId)),
                         ("charger", s.Chargers.Select(c => c.ChargerId)) })
                if (Duplicates(ids)) { Fail($"duplicate {name} ID"); return; }
            foreach (var v in s.Vehicles)
            {
                if (v.RouteId != null && !routes.Contains((v.RouteId, v.RouteRevision.Value))) { Fail("missing vehicle route revision reference"); return; }
                if (v.ServiceState == "CHARGING" && !s.Chargers.Any(c => c.OccupiedVehicleIds.Contains(v.VehicleId)))
                { Fail("charging vehicle has no occupied charger"); return; }
            }
            if (s.Incidents.Any(i => !vehicles.Contains(i.VehicleId) || i.CandidateIds.Any(id => !candidates.Contains(id)))) { Fail("missing incident reference"); return; }
            if (s.Candidates.Any(c => !incidents.Contains(c.IncidentId))) { Fail("missing candidate incident reference"); return; }
            if (s.Chargers.Any(c => c.OccupiedVehicleIds.Count > c.PortCount || c.OccupiedVehicleIds.Any(id => !vehicles.Contains(id))))
            { Fail("invalid charger occupancy"); return; }
            if (Duplicates(s.Chargers.SelectMany(c => c.OccupiedVehicleIds))) { Fail("vehicle occupies multiple chargers"); return; }
            if (s.Routes.Any(r => r.SceneRevision != s.SceneRevision)) { Fail("route scene revision mismatch"); return; }
            if (Duplicates(s.Depots.Select(d => d.DepotId))) { Fail("duplicate depot ID"); return; }
            var chargerIds = new HashSet<string>(s.Chargers.Select(c => c.ChargerId));
            if (s.Depots.Any(d => d.ChargerIds.Any(id => !chargerIds.Contains(id)))) { Fail("depot references an unknown charger"); return; }
            if (Duplicates(s.Depots.SelectMany(d => d.ChargerIds))) { Fail("charger belongs to more than one depot"); return; }
            if (Duplicates(s.EnergyForecasts.Select(f => f.VehicleId))) { Fail("more than one energy forecast for a vehicle"); return; }
            var depots = new HashSet<string>(s.Depots.Select(d => d.DepotId));
            foreach (var f in s.EnergyForecasts)
            {
                if (!vehicles.Contains(f.VehicleId)) { Fail("energy forecast for an unknown vehicle"); return; }
                if (f.EnergyModelRevision != s.Scenario.EnergyModelRevision) { Fail("forecast uses a different energy model than the scenario"); return; }
                if (f.NextDepotId != null && !depots.Contains(f.NextDepotId)) { Fail("forecast references an unknown depot"); return; }
            }
            if (Duplicates(s.ChargeOptions.Select(o => o.OptionId))) { Fail("duplicate charge option ID"); return; }
            // A missing history entry is allowed: the active offer's own revision is its last issued one.
            if (s.ChargeOptions.Any(o => s.ChargeOptionRevisions.TryGetValue(o.OptionId, out var issued) && issued != o.Revision))
            { Fail("charge option revision differs from its last issued revision"); return; }
            if (s.ChargeDecisionDeferrals.Keys.Any(id => !vehicles.Contains(id))) { Fail("charge decision deferral for an unknown vehicle"); return; }
            var byId = s.Vehicles.ToDictionary(v => v.VehicleId);
            foreach (var o in s.ChargeOptions)
            {
                if (!vehicles.Contains(o.VehicleId)) { Fail("charge option for an unknown vehicle"); return; }
                if (o.ChargerId != null && !chargerIds.Contains(o.ChargerId)) { Fail("charge option references an unknown charger"); return; }
                var vehicle = byId[o.VehicleId];
                if (o.BatteryAfterKwh > vehicle.BatteryCapacityKwh) { Fail("charge option battery_after_kwh exceeds the vehicle's capacity"); return; }
                // Without a charger the battery can only drain, never gain.
                if (o.ChargerId == null && o.BatteryAfterKwh > vehicle.BatteryKwh) { Fail("charge option without a charger cannot gain battery"); return; }
                if (o.MinInServiceVehicles != null && o.MinInServiceVehicles > s.Vehicles.Count)
                { Fail("charge option min_in_service_vehicles exceeds the fleet size"); return; }
            }
            // models.py drops zero deferrals rather than rejecting them.
            foreach (var id in s.ChargeDecisionDeferrals.Where(entry => entry.Value <= 0).Select(entry => entry.Key).ToList())
                s.ChargeDecisionDeferrals.Remove(id);
        }

        // -----------------------------------------------------------------------------------
        // Function: SameSpatial
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function tells whether two spatial references name the same scene, revisions,
        //     manifest and capabilities.
        //
        // Args:
        //     a (SpatialReference): The first reference.
        //     b (SpatialReference): The second reference.
        //
        // Returns:
        //     bool: True when both are present and identical.
        private static bool SameSpatial(SpatialReference a, SpatialReference b) =>
            a != null && b != null && a.SceneId == b.SceneId && a.SceneRevision == b.SceneRevision && a.SpatialRevision == b.SpatialRevision
            && a.TransformRevision == b.TransformRevision && a.ManifestSha256 == b.ManifestSha256
            && a.SpatialSchemaVersion == b.SpatialSchemaVersion && a.RequiredCapabilities.SequenceEqual(b.RequiredCapabilities);

        // -----------------------------------------------------------------------------------
        // Function: Duplicates
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function tells whether any ID appears twice.
        //
        // Args:
        //     ids (IEnumerable<string>): The IDs.
        //
        // Returns:
        //     bool: True when an ID repeats.
        private static bool Duplicates(IEnumerable<string> ids)
        {
            var seen = new HashSet<string>();
            return ids.Any(id => !seen.Add(id));
        }
    }
}
