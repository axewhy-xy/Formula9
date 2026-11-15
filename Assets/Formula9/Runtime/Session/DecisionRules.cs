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
        private const string RoadDetour = "ROAD_DETOUR";

        public static string RouteDependencies(FullSnapshot snapshot, string route) => snapshot == null ? null :
            $"{snapshot.SessionId}/{snapshot.SessionEpoch}/{snapshot.SceneRevision}/{snapshot.Session.Status}/" +
            string.Join(";", snapshot.Routes.Where(r => r.RouteId == route).Select(r => r.Revision)) + "/" +
            string.Join(";", snapshot.Disruptions.OrderBy(d => d.DisruptionId).Select(d => $"{d.DisruptionId}:{d.AggregateRevision}:{d.ActiveFromTick <= snapshot.Tick && (d.ActiveUntilTick == null || snapshot.Tick < d.ActiveUntilTick)}")) + "/" +
            string.Join(";", snapshot.Vehicles.Where(v => v.RouteId == route).OrderBy(v => v.VehicleId).Select(v => $"{v.VehicleId}:{v.RouteRevision}"));

        // Decisions are open while the session runs, and stay listed while it is paused.
        private static bool Live(FullSnapshot snapshot) =>
            snapshot != null && (snapshot.Session.Status == "RUNNING" || snapshot.Session.Status == "PAUSED");

        // The shuttle an option is for: each shuttle that will reach an incident has its own, which names
        // it (an older snapshot's names none, and is for the incident's own shuttle).
        private static string VehicleOf(Candidate candidate, Incident incident) => candidate.VehicleId ?? incident.VehicleId;

        private static IEnumerable<Incident> OptionsReady(FullSnapshot snapshot) =>
            (snapshot.Incidents ?? new List<Incident>()).Where(i => i.State == "OPTIONS_READY");

        // Every decision still to be made: one per shuttle and incident the backend offers that shuttle
        // options for, soonest arrival first. A shuttle with no exposure is one the incident names and
        // that is already at it, so it comes first.
        public static List<PendingDecision> PendingDecisions(FullSnapshot snapshot)
        {
            var pending = new List<PendingDecision>();
            if (!Live(snapshot)) return pending;
            foreach (var incident in OptionsReady(snapshot))
                foreach (var options in snapshot.Candidates.Where(c => c.IncidentId == incident.IncidentId)
                             .GroupBy(c => VehicleOf(c, incident)).Where(g => g.Key != null))
                    pending.Add(new PendingDecision(options.Key, incident, incident.ExposureOf(options.Key),
                        options.GroupBy(c => c.Action).ToDictionary(g => g.Key, g => g.First())));
            return pending.OrderBy(d => d.EtaS ?? -1).ThenBy(d => d.Incident.DetectedTick)
                .ThenBy(d => d.IncidentId, StringComparer.Ordinal).ThenBy(d => d.VehicleId, StringComparer.Ordinal).ToList();
        }

        // One shuttle's decisions still to be made, soonest arrival first.
        public static List<PendingDecision> OptionsFor(FullSnapshot snapshot, string vehicleId) =>
            PendingDecisions(snapshot).Where(d => d.VehicleId == vehicleId).ToList();

        // The option for this shuttle and action at this incident as the snapshot has it now, or null.
        // Options are reissued with new IDs at each incident revision and on expiry, so a decision
        // looks its ID up here when it is sent, never before.
        public static Candidate CandidateFor(FullSnapshot snapshot, string incidentId, string vehicleId, string action)
        {
            var incident = snapshot?.Incidents?.FirstOrDefault(i => i.IncidentId == incidentId);
            return incident == null || vehicleId == null ? null : snapshot.Candidates.FirstOrDefault(c =>
                c.IncidentId == incidentId && c.Action == action && VehicleOf(c, incident) == vehicleId);
        }

        // Every road detour on offer, in the order the backend's route_edit.detour_target checks a draft
        // against them: the latest-detected incident first, ties and options in snapshot order (both
        // sorts are stable). A route has one shuttle, so the route's riders are that shuttle.
        private static IEnumerable<(Incident Incident, Candidate Candidate, string VehicleId)> Detours(FullSnapshot snapshot)
        {
            if (!Live(snapshot)) yield break;
            foreach (var incident in OptionsReady(snapshot).OrderByDescending(i => i.DetectedTick))
                foreach (var candidate in snapshot.Candidates.Where(c => c.IncidentId == incident.IncidentId && c.Action == RoadDetour))
                    yield return (incident, candidate, VehicleOf(candidate, incident));
        }

        // The road detour a route drafted for this shuttle now would answer, or null: its option at the
        // latest-detected incident that offers it one.
        public static Candidate DetourCandidate(FullSnapshot snapshot, string vehicleId) =>
            vehicleId == null ? null : Detours(snapshot).FirstOrDefault(d => d.VehicleId == vehicleId).Candidate;

        // The route the operator can edit for this shuttle while the session runs: its own, while it has
        // a detour on offer. Null otherwise, and while paused.
        public static string ReroutableRoute(FullSnapshot snapshot, string vehicleId) =>
            snapshot?.Session.Status == "RUNNING" && DetourCandidate(snapshot, vehicleId) != null
                ? snapshot.Vehicles.FirstOrDefault(v => v.VehicleId == vehicleId)?.RouteId : null;

        // The shuttles still waiting for a decision, each once, soonest arrival first.
        public static List<string> PendingVehicles(FullSnapshot snapshot) =>
            PendingDecisions(snapshot).Select(d => d.VehicleId).Distinct().ToList();

        // Whether a preview is still the one to approve: before the start, for any route; while running,
        // for the route of this shuttle with a detour on offer (vehicleId is ignored before the start).
        public static bool RoutePreviewMatches(FullSnapshot snapshot, string vehicleId, RouteEditPreview preview) =>
            snapshot != null && preview != null && (snapshot.Session.Status == "READY" || ReroutableRoute(snapshot, vehicleId) == preview.RouteId) &&
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

        // A time slot on a charger; replacing moves that booking in one step. The backend checks it again.
        public static DispatcherCommand ScheduleChargeCommand(FullSnapshot snapshot, string vehicleId, string chargerId, long startTick, string replacing, string reason)
        {
            if (snapshot.Session.Status != "RUNNING") throw new ArgumentException("Charging can only be scheduled while the session runs.");
            var charger = snapshot.Chargers.SingleOrDefault(c => c.ChargerId == chargerId);
            if (charger == null || snapshot.Vehicles.All(v => v.VehicleId != vehicleId)) throw new ArgumentException("Refresh and choose a current shuttle and charger.");
            return Command(snapshot, "SCHEDULE_CHARGE", "CHARGER", chargerId, charger.AggregateRevision, reason,
                new JObject { ["vehicle_id"] = vehicleId, ["start_tick"] = startTick, ["replaces_booking_id"] = replacing });
        }

        public static DispatcherCommand CancelChargeBookingCommand(FullSnapshot snapshot, string bookingId, string reason)
        {
            var booking = snapshot.ChargeBookings.SingleOrDefault(b => b.BookingId == bookingId);
            if (snapshot.Session.Status != "RUNNING" || booking == null || booking.Status != "SCHEDULED")
                throw new ArgumentException("That booking can no longer be cancelled.");
            var charger = snapshot.Chargers.Single(c => c.ChargerId == booking.ChargerId);
            return Command(snapshot, "CANCEL_CHARGE_BOOKING", "CHARGER", booking.ChargerId, charger.AggregateRevision, reason,
                new JObject { ["booking_id"] = bookingId });
        }

        public static DispatcherCommand IncidentCommand(FullSnapshot snapshot, string incidentId, string candidateId, string reason)
        {
            // An option reissued since it was chosen is gone from the snapshot: that is a stale choice too.
            var incident = snapshot.Incidents.SingleOrDefault(i => i.IncidentId == incidentId);
            var candidate = snapshot.Candidates.SingleOrDefault(c => c.CandidateId == candidateId && c.IncidentId == incidentId);
            if (incident == null || candidate == null ||
                snapshot.Session.Status != "RUNNING" || incident.State != "OPTIONS_READY" || !incident.CandidateIds.Contains(candidateId) ||
                candidate.Validation != "PASS" || candidate.ExpiresAtTick < snapshot.Tick)
                throw new ArgumentException("Refresh and choose a current passing incident option.");
            return Command(snapshot, "APPROVE_INCIDENT_OPTION", "INCIDENT", incidentId, incident.AggregateRevision, reason,
                new JObject { ["candidate_id"] = candidate.CandidateId });
        }

        // The operator's own detour answers the incident in place of the planned one, for this shuttle.
        public static DispatcherCommand RerouteCommand(FullSnapshot snapshot, string vehicleId, RouteEditPreview preview, string reason)
        {
            var candidate = DetourCandidate(snapshot, vehicleId);
            if (!RoutePreviewMatches(snapshot, vehicleId, preview) || !preview.Approvable || candidate == null)
                throw new ArgumentException("Validate a fresh route proposal before approving.");
            var command = IncidentCommand(snapshot, candidate.IncidentId, candidate.CandidateId, reason);
            command.Payload["directed_edge_ids"] = JArray.FromObject(preview.DirectedEdgeIds);
            return command;
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

    // One shuttle's decision still to be made about one incident: the options the backend offers it, by
    // action, and how far it is from the incident. Every figure is the backend's estimate; the exposure
    // is null when the incident names the shuttle but gives no figures for it.
    public sealed class PendingDecision
    {
        public PendingDecision(string vehicleId, Incident incident, IncidentExposure exposure, IReadOnlyDictionary<string, Candidate> candidates)
        {
            VehicleId = vehicleId; Incident = incident; Exposure = exposure; Candidates = candidates;
        }

        public string VehicleId { get; }
        public Incident Incident { get; }
        public IncidentExposure Exposure { get; }
        public IReadOnlyDictionary<string, Candidate> Candidates { get; }

        public string IncidentId => Incident.IncidentId;
        public double? EtaS => Exposure?.EtaS;
        public double? DistanceM => Exposure?.DistanceM;
        public double? KeepRouteDelayS => Exposure?.KeepRouteDelayS;

        // This shuttle's option for an action (KEEP_ROUTE, ROAD_DETOUR, ESCALATE, HOLD_AT_STOP), or null.
        public Candidate Option(string action) => Candidates.TryGetValue(action, out var candidate) ? candidate : null;
    }
}
