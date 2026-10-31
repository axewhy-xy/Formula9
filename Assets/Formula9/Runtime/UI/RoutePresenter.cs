using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.Contracts;

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

        // Where the exercise has got to, as an index into Stages.
        public static int Stage(FullSnapshot snapshot, bool routeClosed, bool hasDraft, RouteEditPreview preview)
        {
            if (snapshot == null) return 0;
            if (snapshot.Session.Status != "READY") return 4;
            if (!routeClosed) return 3;
            if (preview != null) return preview.Approvable ? 3 : 2;
            return hasDraft ? 2 : 1;
        }

        public static string Problem(RouteEditorContext context, FullSnapshot snapshot)
        {
            if (context == null) return "Loading this route's roads…";
            var committed = new HashSet<string>(context.OriginalDirectedEdgeIds);
            var closed = context.Edges.Where(e => e.Closed && committed.Contains(e.EdgeId)).ToList();
            var shuttles = snapshot == null ? "" : Names(snapshot.Vehicles.Where(v => v.RouteId == context.RouteId).Select(v => v.VehicleId));
            if (closed.Count == 0)
            {
                // The road is still closed once a route goes round it
                var elsewhere = Names(context.Edges.Where(e => e.Closed).Select(e => e.RoadName ?? "A nearby road").Distinct());
                if (!string.IsNullOrEmpty(elsewhere) && !string.IsNullOrEmpty(shuttles)) return $"{elsewhere} is closed. {shuttles}'s route goes round it.";
                return string.IsNullOrEmpty(shuttles) ? "No closure affects this route." : $"No closure affects {shuttles}'s route.";
            }
            var roads = Names(closed.Select(e => e.RoadName ?? "A road on this route").Distinct());
            var stops = Names(context.Stops.Select(s => s.Name));
            return $"{roads} is closed. {shuttles} can't run its loop" +
                   (string.IsNullOrEmpty(stops) ? "." : $" to {stops}.") + " Find a way round it.";
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
