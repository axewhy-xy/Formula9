using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.Contracts;
using Newtonsoft.Json.Linq;

namespace Formula9.Session
{
    // Shared by UI actions and tests. The backend remains the authority for all consequences.
    public static class DecisionRules
    {
        public static string RouteDependencies(FullSnapshot snapshot, string route) => snapshot == null ? null :
            $"{snapshot.SessionId}/{snapshot.SessionEpoch}/{snapshot.SceneRevision}/{snapshot.Session.Status}/" +
            string.Join(";", snapshot.Routes.Where(r => r.RouteId == route).Select(r => r.Revision)) + "/" +
            string.Join(";", snapshot.Disruptions.OrderBy(d => d.DisruptionId).Select(d => $"{d.DisruptionId}:{d.AggregateRevision}:{d.ActiveFromTick <= snapshot.Tick && (d.ActiveUntilTick == null || snapshot.Tick < d.ActiveUntilTick)}")) + "/" +
            string.Join(";", snapshot.Vehicles.Where(v => v.RouteId == route).OrderBy(v => v.VehicleId).Select(v => $"{v.VehicleId}:{v.RouteRevision}"));

        public static bool RoutePreviewMatches(FullSnapshot snapshot, RouteEditPreview preview) =>
            snapshot != null && preview != null && snapshot.Session.Status == "READY" &&
            snapshot.SessionId == preview.SessionId && snapshot.SessionEpoch == preview.SessionEpoch && snapshot.SceneRevision == preview.SceneRevision &&
            snapshot.Routes.Any(r => r.RouteId == preview.RouteId && r.Revision == preview.RouteRevision) &&
            snapshot.Vehicles.Any(v => v.RouteId == preview.RouteId && v.RouteRevision == preview.RouteRevision) &&
            snapshot.Vehicles.Where(v => v.RouteId == preview.RouteId).All(v => v.RouteRevision == preview.RouteRevision) &&
            preview.Checks.All(c => !c.Mandatory || c.Status == "PASS") &&
            preview.ClosureRevisions.Count == snapshot.Disruptions.Count &&
            snapshot.Disruptions.All(d => preview.ClosureRevisions.TryGetValue(d.DisruptionId, out var revision) && revision == d.AggregateRevision);

        public static List<long> ParseViaNodes(string text)
        {
            var nodes = new List<long>();
            foreach (var part in (text ?? "").Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!long.TryParse(part, out var id) || id < -9007199254740991L || id > 9007199254740991L)
                    throw new ArgumentException("Via nodes must be integer node IDs from the route graph.");
                nodes.Add(id);
            }
            if (nodes.Count > 16) throw new ArgumentException("A draft can contain at most 16 via nodes.");
            return nodes;
        }

        public static DispatcherCommand ChargeCommand(FullSnapshot snapshot, string optionId, string reason)
        {
            var option = snapshot.ChargeOptions.SingleOrDefault(o => o.OptionId == optionId);
            if (snapshot.Session.Status != "RUNNING" || option == null || option.Validation != "PASS" || option.ExpiresAtTick < snapshot.Tick)
                throw new ArgumentException("Refresh and choose a current passing charging option.");
            var keep = option.Action == "CONTINUE";
            if (!keep && option.Action != "CHARGE_NOW") throw new ArgumentException("Unknown charging action.");
            var revision = keep ? snapshot.Vehicles.Single(v => v.VehicleId == option.VehicleId).AggregateRevision
                : snapshot.Chargers.Single(c => c.ChargerId == option.ChargerId).AggregateRevision;
            return Command(snapshot, keep ? "CONTINUE_SERVICE" : "RESERVE_CHARGER", keep ? "VEHICLE" : "CHARGER",
                keep ? option.VehicleId : option.ChargerId, revision, reason,
                new JObject { ["option_id"] = option.OptionId, ["option_revision"] = option.Revision });
        }

        public static DispatcherCommand IncidentCommand(FullSnapshot snapshot, string incidentId, string candidateId, string reason)
        {
            var incident = snapshot.Incidents.Single(i => i.IncidentId == incidentId);
            var candidate = snapshot.Candidates.Single(c => c.CandidateId == candidateId && c.IncidentId == incidentId);
            if (snapshot.Session.Status != "RUNNING" || incident.State != "OPTIONS_READY" || !incident.CandidateIds.Contains(candidateId) ||
                candidate.Validation != "PASS" || candidate.ExpiresAtTick < snapshot.Tick)
                throw new ArgumentException("Refresh and choose a current passing incident option.");
            return Command(snapshot, "APPROVE_INCIDENT_OPTION", "INCIDENT", incidentId, incident.AggregateRevision, reason,
                new JObject { ["candidate_id"] = candidate.CandidateId });
        }

        private static DispatcherCommand Command(FullSnapshot snapshot, string kind, string targetType, string id, long revision, string reason, JObject payload)
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Enter a reason for this decision.");
            return new DispatcherCommand
            {
                ContractVersion = "2.0", CommandId = Guid.NewGuid().ToString(), IdempotencyKey = Guid.NewGuid().ToString(),
                SessionId = snapshot.SessionId, ExpectedEpoch = snapshot.SessionEpoch, ExpectedRevision = revision,
                CommandType = kind, TargetType = targetType, TargetId = id, Reason = reason.Trim(), Payload = payload,
            };
        }
    }
}
