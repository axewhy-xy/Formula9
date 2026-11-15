using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.Contracts;
using Formula9.Session;
using Formula9.World;

namespace Formula9.UI
{
    // One line of feedback: what is wrong, and the roads to show for it.
    public sealed class RouteIssue
    {
        public string Text;
        public List<string> EdgeIds;
        public bool Blocking;
    }

    // One row of the comparison, with its change from the route being replaced.
    public sealed class RouteMetricRow
    {
        public string Label, Value, Change;
        public bool Worse;
    }

    // The routing exercise in the operator's words. Names people know, no IDs; every number here
    // came from the backend.
    public static class RoutePresenter
    {
        public static readonly string[] Stages = { "Problem", "Detour", "Check", "Approve", "Outcome" };

        // Where the exercise has got to for this shuttle, as an index into Stages. Running, it is back at
        // the detour while the shuttle has one on offer, and at the outcome otherwise.
        public static int Stage(FullSnapshot snapshot, string vehicleId, bool routeClosed, bool hasDraft, RouteEditPreview preview)
        {
            if (snapshot == null) return 0;
            if (snapshot.Session.Status != "READY" && DecisionRules.DetourCandidate(snapshot, vehicleId) == null) return 4;
            if (!routeClosed) return 3;
            if (preview != null) return preview.Approvable ? 3 : 2;
            return hasDraft ? 2 : 1;
        }

        // What the shuttle is up against. Running, that is the incidents it has options for, soonest
        // first; else those it was decided for, or will reach, that are still open. Without any, and
        // before the start, it is the closure on its route. Every figure is the backend's estimate.
        public static string Problem(RouteEditorContext context, FullSnapshot snapshot, string vehicleId)
        {
            var incidents = ProblemIncidents(snapshot, vehicleId);
            var name = ShuttleName(vehicleId);
            if (incidents.Count > 0)
            {
                var pending = DecisionRules.OptionsFor(snapshot, vehicleId).Count > 0;
                var head = pending && incidents.Count > 1 ? $"{name} has {incidents.Count} incidents ahead, soonest first.\n\n" : "";
                return head + string.Join("\n\n", incidents.Select(i => Describe(i, vehicleId, name, pending)));
            }
            if (context == null) return "Loading this route's roads…";
            var committed = new HashSet<string>(context.OriginalDirectedEdgeIds);
            var closed = context.Edges.Where(e => e.Closed && committed.Contains(e.EdgeId)).ToList();
            var shuttles = vehicleId != null ? name : snapshot == null ? "" :
                Names(snapshot.Vehicles.Where(v => v.RouteId == context.RouteId).Select(v => ShuttleName(v.VehicleId)));
            if (closed.Count == 0)
            {
                // The road is still closed once a route goes round it
                var elsewhere = Names(context.Edges.Where(e => e.Closed).Select(e => e.RoadName ?? "A nearby road").Distinct());
                if (!string.IsNullOrEmpty(elsewhere) && !string.IsNullOrEmpty(shuttles)) return $"{elsewhere} is closed. {shuttles}'s route goes round it.";
                if (Live(snapshot) && !string.IsNullOrEmpty(shuttles)) return $"Nothing on {shuttles}'s route needs a decision now.";
                return string.IsNullOrEmpty(shuttles) ? "No closure affects this route." : $"No closure affects {shuttles}'s route.";
            }
            var roads = Names(closed.Select(e => e.RoadName ?? "A road on this route").Distinct());
            var stops = Names(context.Stops.Select(s => s.Name));
            return $"{roads} is closed. {shuttles} can't run its loop" +
                   (string.IsNullOrEmpty(stops) ? "." : $" to {stops}.") + " Find a way round it.";
        }

        // The incidents the Problem card is about, the one to show on the map first. Those the shuttle
        // has options for, soonest arrival first; without any, those still open that it was decided for
        // and has not yet got through, or that it will reach. None before the start.
        public static List<Incident> ProblemIncidents(FullSnapshot snapshot, string vehicleId)
        {
            if (!Live(snapshot) || vehicleId == null || snapshot.Incidents == null) return new List<Incident>();
            var pending = DecisionRules.OptionsFor(snapshot, vehicleId).Select(d => d.Incident).Distinct().ToList();
            if (pending.Count > 0) return pending;
            return snapshot.Incidents
                .Where(i => i.State != "RESOLVED" && i.State != "UNRESOLVED" && (i.Involves(vehicleId) || i.ResponseOf(vehicleId) != null) &&
                            i.ResponseOf(vehicleId)?.CompletedTick == null)
                .OrderBy(i => i.ExposureOf(vehicleId)?.EtaS ?? -1).ThenBy(i => i.DetectedTick).ThenBy(i => i.IncidentId, StringComparer.Ordinal)
                .ToList();
        }

        // How long the shuttle's first options stand before the backend reissues them, or null when it
        // has none. Ticks are turned into seconds at the session's own pace.
        public static string OptionsExpiry(FullSnapshot snapshot, string vehicleId)
        {
            var first = DecisionRules.OptionsFor(snapshot, vehicleId).FirstOrDefault();
            if (first == null || first.Candidates.Count == 0) return null;
            var ticks = first.Candidates.Values.Min(c => c.ExpiresAtTick) - snapshot.Tick;
            if (ticks <= 0) return "Options are being renewed";
            return "Options for " + Rough(Math.Max(1, ticks * (double)IncidentSimulation.SecondsPerTick(snapshot)), 1);
        }

        // One incident as the shuttle meets it: what and where, when it gets there and what keeping its
        // route costs, or what was decided for it.
        private static string Describe(Incident incident, string vehicleId, string name, bool pending)
        {
            var road = incident.Location?.RoadName;
            var text = TrainingPresenter.IncidentName(incident) + (string.IsNullOrEmpty(road) ? "." : $" on {road}.");
            var response = incident.ResponseOf(vehicleId);
            if (response != null) return text + "\n" + Decided(response, name);
            var exposure = incident.ExposureOf(vehicleId);
            if (exposure != null)
                text += "\n" + (exposure.EtaS < 0.5 ? $"{name} is at it now." : $"{name} reaches it in about {Rough(exposure.EtaS)} ({Distance(exposure.DistanceM)}).");
            if (!pending) return text + "\nNo decision is asked for it now.";
            if (exposure?.KeepRouteDelayS is double delay)
                text += "\n" + (delay < 1 ? "Keeping its route costs it next to no time." : $"Keeping its route costs about {Rough(delay)}.");
            return text;
        }

        private static string Decided(IncidentResponse response, string name)
        {
            if (response.Action == "NO_DECISION") return $"{name} got there before a decision was made.";
            var what = response.Action == "HOLD_AT_STOP" && response.HoldStopId != null
                ? "hold at " + RouteDraftView.StopName(response.HoldStopId)
                : TrainingPresenter.ActionLabel(response.Action).ToLowerInvariant();
            return $"Decided for {name}: {what}" + (response.BeforeArrival ? "" : ", after it got there") + ". Waiting to see how it turns out.";
        }

        private static bool Live(FullSnapshot snapshot) =>
            snapshot?.Session != null && (snapshot.Session.Status == "RUNNING" || snapshot.Session.Status == "PAUSED");

        // A time for people, to the nearest step past a minute (10 s for an estimate): "40 s", "1 min 50 s", "3 min".
        private static string Rough(double seconds, int step = 10)
        {
            var whole = (int)Math.Round(Math.Abs(seconds));
            if (whole < 60) return $"{whole} s";
            whole = (int)Math.Round(whole / (double)step) * step;
            return whole % 60 == 0 ? $"{whole / 60} min" : $"{whole / 60} min {whole % 60} s";
        }

        public static string Verdict(RouteEditPreview preview, bool working, bool hasDraft)
        {
            if (working) return "Checking this route…";
            if (preview == null) return hasDraft ? "Waiting to check this route…" : "Click a road on the map to route the shuttle along it.";
            if (preview.Validation == "PASS") return "This route works.";
            var problems = Issues(preview).Count;
            return preview.Validation == "FAIL"
                ? (problems == 1 ? "This route can't be approved: 1 problem." : $"This route can't be approved: {problems} problems.")
                : "This route can't be cleared: something it needs isn't on record.";
        }

        public static List<RouteIssue> Issues(RouteEditPreview preview)
        {
            if (preview == null) return new List<RouteIssue>();
            return preview.Checks.Where(c => c.Status != "PASS").Select(c => new RouteIssue
            {
                Text = c.Message, EdgeIds = c.EdgeIds ?? new List<string>(), Blocking = c.Mandatory && c.Status != "WARN",
            }).ToList();
        }

        // Every road the feedback is about, for both views to highlight.
        public static HashSet<string> Highlighted(RouteEditPreview preview) =>
            new HashSet<string>(Issues(preview).Where(i => i.Blocking).SelectMany(i => i.EdgeIds));

        public static List<RouteMetricRow> Metrics(RouteEditPreview preview)
        {
            var rows = new List<RouteMetricRow>();
            var edited = preview?.Edited;
            if (edited == null) return rows;
            var original = preview.Original;
            rows.Add(new RouteMetricRow
            {
                Label = "Travel time per loop", Value = Duration(edited.DurationS),
                Change = original == null ? null : Change(edited.DurationS - original.DurationS, Duration, 1),
                Worse = original != null && edited.DurationS > original.DurationS,
            });
            rows.Add(new RouteMetricRow
            {
                Label = "Energy per loop", Value = Energy(edited.EnergyKwh),
                Change = original == null ? null : Change(edited.EnergyKwh - original.EnergyKwh, Energy, 0.005),
                Worse = original != null && edited.EnergyKwh > original.EnergyKwh,
            });
            rows.Add(new RouteMetricRow
            {
                Label = "Distance per loop", Value = Distance(edited.DistanceM),
                Change = original == null ? null : Change(edited.DistanceM - original.DistanceM, Distance, 1),
                Worse = original != null && edited.DistanceM > original.DistanceM,
            });
            var served = edited.StopCoverage.Count(s => s.Value);
            rows.Add(new RouteMetricRow
            {
                Label = "Required stops served", Value = $"{served} of {edited.StopCoverage.Count}",
                Worse = served < edited.StopCoverage.Count,
            });
            var impact = preview.ServiceImpact?.FirstOrDefault();
            if (impact?.LoopsOnEdited != null)
                rows.Add(new RouteMetricRow
                {
                    Label = $"Loops {impact.VehicleId} has charge for", Value = impact.LoopsOnEdited.Value.ToString(),
                    Change = impact.LoopsOnOriginal == null || impact.LoopsOnOriginal == impact.LoopsOnEdited ? null : Signed(impact.LoopsOnEdited.Value - impact.LoopsOnOriginal.Value),
                    Worse = impact.LoopsOnOriginal != null && impact.LoopsOnEdited < impact.LoopsOnOriginal,
                });
            return rows;
        }

        public static string ApprovalNote(FullSnapshot snapshot, RouteEditPreview preview, string reason)
        {
            if (preview == null) return "Check a route before approving it.";
            if (!preview.Approvable) return "Fix the problems above to approve this route.";
            if (string.IsNullOrWhiteSpace(reason)) return "Say why you chose this route, then approve it.";
            var shuttles = snapshot == null ? "" : Names(snapshot.Vehicles.Where(v => v.RouteId == preview.RouteId).Select(v => v.VehicleId));
            return $"{shuttles} will drive exactly this route.";
        }

        public static string OutcomeHeadline(RouteOutcome outcome)
        {
            if (outcome == null || outcome.State == "NOT_APPROVED") return "Nothing to report yet: approve a route and start the exercise.";
            if (outcome.State == "APPROVED") return "Route approved. Start the exercise to see what it does.";
            if (outcome.State == "RUNNING")
                return outcome.HeldForIncident ? $"{outcome.VehicleId} is held at an incident." : $"{outcome.VehicleId} is on its first loop of the new route.";
            return outcome.ServicePreserved == true
                ? $"Service preserved: every required stop served over {Loops(outcome.Loops.Count)}."
                : $"Service not preserved: a required stop was missed in {Loops(outcome.Loops.Count)}.";
        }

        public static List<RouteMetricRow> OutcomeRows(RouteOutcome outcome)
        {
            var rows = new List<RouteMetricRow>();
            if (outcome == null) return rows;
            var last = outcome.Loops.LastOrDefault();
            if (last != null)
            {
                rows.Add(new RouteMetricRow
                {
                    Label = "Last loop took", Value = Duration(last.DurationS),
                    Change = outcome.DelayVsOriginalS == null ? null : Change(outcome.DelayVsOriginalS.Value, Duration, 1) + " on the closed route's plan",
                    Worse = outcome.DelayVsOriginalS > 0,
                });
                rows.Add(new RouteMetricRow
                {
                    Label = "Last loop used", Value = Energy(last.EnergyKwh),
                    Change = outcome.ExtraEnergyVsOriginalKwh == null ? null : Change(outcome.ExtraEnergyVsOriginalKwh.Value, Energy, 0.005) + " on the closed route's plan",
                    Worse = outcome.ExtraEnergyVsOriginalKwh > 0,
                });
            }
            else if (outcome.Planned != null)
            {
                rows.Add(new RouteMetricRow { Label = "Planned loop", Value = Duration(outcome.Planned.DurationS) });
                rows.Add(new RouteMetricRow { Label = "Planned energy", Value = Energy(outcome.Planned.EnergyKwh) });
            }
            if (outcome.BatteryKwh != null)
                rows.Add(new RouteMetricRow
                {
                    Label = "Battery now", Value = Energy(outcome.BatteryKwh.Value),
                    Change = outcome.LoopsLeftOnCharge == null ? null : $"{Loops((int)outcome.LoopsLeftOnCharge.Value)} left above reserve",
                    Worse = outcome.LoopsLeftOnCharge < 2,
                });
            foreach (var stop in outcome.Stops)
                rows.Add(new RouteMetricRow
                {
                    Label = stop.Name,
                    Value = stop.TimesServed == 0 ? "Not yet" : stop.TimesServed == 1 ? "Served once" : $"Served {stop.TimesServed} times",
                });
            return rows;
        }

        // The line over the 3D view while the shuttle runs the route.
        public static string ExecutionLine(FullSnapshot snapshot, RouteOutcome outcome)
        {
            var vehicle = snapshot?.Vehicles.FirstOrDefault(v => v.VehicleId == outcome?.VehicleId);
            if (vehicle == null || outcome.State == "NOT_APPROVED" || snapshot.Session.Status == "READY") return null;
            var state = vehicle.ServiceState == "DWELLING" ? "at a stop" : vehicle.ServiceState == "AWAITING_GUIDANCE" ? "held at an incident" :
                vehicle.ServiceState == "BLOCKED" ? "held in traffic" : "on its approved route";
            var served = outcome.Stops.Count(s => s.ServedThisLoop);
            return $"{vehicle.VehicleId} · {state} · stops this loop {served} of {outcome.Stops.Count} · loops finished {outcome.Loops.Count}";
        }

        // "shuttle-03" reads "Shuttle 03"; any other ID is shown as it is.
        public static string ShuttleName(string vehicleId)
        {
            const string prefix = "shuttle-";
            return vehicleId != null && vehicleId.Length > prefix.Length && vehicleId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? "Shuttle " + vehicleId.Substring(prefix.Length) : vehicleId;
        }

        // One entry of the shuttle picker: the shuttle, the route it drives now (a detour it was given,
        // too), and whether it is waiting for a decision.
        public static string ShuttleChoice(FleetVehicle vehicle, bool pending) =>
            $"{ShuttleName(vehicle.VehicleId)} · {vehicle.RouteId ?? "no route"}" + (pending ? "   ● needs a decision" : "");

        public static string PendingTitle(int shuttles) => $"Needs a decision ({shuttles})";

        // A shuttle on the Needs a decision strip, and about how soon it reaches its first incident.
        public static string PendingChip(PendingDecision decision) =>
            "● " + ShuttleName(decision.VehicleId) +
            (decision.EtaS is double eta ? " · " + (eta < 0.5 ? "now" : "~" + Duration(eta)) : "");

        public static string Duration(double seconds)
        {
            var whole = (int)Math.Round(Math.Abs(seconds));
            return whole >= 60 ? $"{whole / 60} min {whole % 60} s" : $"{whole} s";
        }

        public static string Energy(double kwh) => $"{Math.Abs(kwh):0.00} kWh";
        public static string Distance(double metres) => Math.Abs(metres) >= 1000 ? $"{Math.Abs(metres) / 1000:0.00} km" : $"{Math.Abs(metres):0} m";

        private static string Change(double difference, Func<double, string> format, double smallest) =>
            Math.Abs(difference) < smallest ? "No change" : (difference > 0 ? "+" : "−") + format(difference);

        private static string Signed(long difference) => (difference > 0 ? "+" : "−") + Math.Abs(difference);

        private static string Loops(int count) => count == 1 ? "1 loop" : $"{count} loops";

        private static string Names(IEnumerable<string> names)
        {
            var list = names.ToList();
            if (list.Count <= 1) return list.FirstOrDefault() ?? "";
            return string.Join(", ", list.Take(list.Count - 1)) + " and " + list[list.Count - 1];
        }
    }
}
