using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.Contracts;
using Formula9.Session;
using Formula9.World;
using Newtonsoft.Json.Linq;

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

    // One button of the decision card: an action the shuttle could take at its first incident, whether
    // the backend offers it, and the words for it. The decision is bound to (IncidentId, the shuttle,
    // CandidateAction), never to a candidate ID, which changes whenever the options are reissued.
    public sealed class DecisionOption
    {
        public string Action;           // KEEP_ROUTE, HOLD_AT_STOP, ESCALATE or ROAD_DETOUR: the button
        public string CandidateAction;  // the action of the option to send (WAIT on an older backend), or null
        public string IncidentId;       // the incident the option decides, or null when not offered
        public bool Shown, Enabled;
        public string Label, Tooltip;
        public string Confirm;          // the sentence the confirm row shows; null for the re-route
        public string Reason;           // the reason the confirm row (or the route's) starts with
    }

    // The routing exercise in the operator's words. Names people know, no IDs; every number here
    // came from the backend.
    public static class RoutePresenter
    {
        // The exercise's steps, each with its card on Route Planning numbered in this order
        public static readonly string[] Stages = { "Problem", "Detour", "Check", "Approve", "Outcome" };

        // The decision card's buttons, in order.
        public static readonly string[] OptionActions = { "KEEP_ROUTE", "HOLD_AT_STOP", "ESCALATE", "ROAD_DETOUR" };

        // Where the exercise has got to for this shuttle, as an index into Stages. Running, it is back at
        // the detour while the shuttle has one on offer (there is something to go round, closed or not),
        // and at the outcome otherwise.
        public static int Stage(FullSnapshot snapshot, string vehicleId, bool routeClosed, bool hasDraft, RouteEditPreview preview)
        {
            if (snapshot == null) return 0;
            var ready = snapshot.Session.Status == "READY";
            if (!ready && DecisionRules.DetourCandidate(snapshot, vehicleId) == null) return 4;
            if (ready && !routeClosed) return 3;
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

        // When the backend next refreshes the shuttle's first options (it reissues them with new
        // figures), as "Refreshes in 53 s", or null when it has none. Ticks are turned into seconds at
        // the session's own pace.
        public static string OptionsExpiry(FullSnapshot snapshot, string vehicleId)
        {
            var first = DecisionRules.OptionsFor(snapshot, vehicleId).FirstOrDefault();
            if (first == null || first.Candidates.Count == 0) return null;
            var ticks = first.Candidates.Values.Min(c => c.ExpiresAtTick) - snapshot.Tick;
            if (ticks <= 0) return "Refreshing now";
            return "Refreshes in " + Rough(Math.Max(1, ticks * (double)IncidentSimulation.SecondsPerTick(snapshot)), 1);
        }

        // One incident as the shuttle meets it: what and where, and when it gets there, or what was
        // decided for it. What keeping its route costs is on the Keep route option, not here.
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

        // The latest OPTIONS_READY event of an incident in the retained window, or null: the figures on
        // the option buttons come from it. The window is limited, so after a long session or a late
        // join there may be none; the decision itself still comes from the snapshot.
        public static Event OptionsEvent(IEnumerable<Event> events, string incidentId) =>
            incidentId == null || events == null ? null : events.LastOrDefault(e => e.EventType == "OPTIONS_READY" && e.EntityId == incidentId);

        // The decision card for this shuttle: a button per action, for its first incident (the one its
        // Problem card lists first). A re-route answers the latest-detected incident that offers the
        // shuttle a detour, as the backend checks a drafted route against that one. Figures come from
        // optionsEvent, and only when it describes the options the snapshot has now; without it, the
        // buttons show none. Nothing is shown when the shuttle has no decision to make.
        public static List<DecisionOption> Options(FullSnapshot snapshot, string vehicleId, Event optionsEvent)
        {
            var decision = DecisionRules.OptionsFor(snapshot, vehicleId).FirstOrDefault();
            if (decision == null) return OptionActions.Select(a => new DecisionOption { Action = a, Label = DefaultLabel(a) }).ToList();
            var incident = decision.Incident;
            var name = ShuttleName(vehicleId);
            var what = IncidentPhrase(incident);
            var paused = snapshot.Session.Status != "RUNNING";
            var evidence = optionsEvent?.EntityId == incident.IncidentId ? optionsEvent.Data?["options"] as JObject : null;
            JObject Evidence(Candidate c) => c == null ? null : evidence?[c.CandidateId] as JObject;
            double? Delay(Candidate c)
            {
                var value = Evidence(c)?["estimated_delay_s"];
                return value != null && (value.Type == JTokenType.Float || value.Type == JTokenType.Integer) ? (double?)value : null;
            }

            var options = new List<DecisionOption>();
            foreach (var action in OptionActions)
            {
                var option = new DecisionOption { Action = action, Shown = true, Label = DefaultLabel(action) };
                options.Add(option);
                if (action == "ROAD_DETOUR")
                {
                    var detour = DecisionRules.DetourCandidate(snapshot, vehicleId);
                    var answers = detour == null ? null : snapshot.Incidents.FirstOrDefault(i => i.IncidentId == detour.IncidentId);
                    option.Reason = "Go round " + IncidentPhrase(answers ?? incident);
                    if (detour == null)
                    {
                        option.Tooltip = incident.EventType == World.IncidentSimulation.SignalFault ? "Signal faults can't be routed round."
                            : incident.EventType == World.IncidentSimulation.PedestrianOnRoad ? "A pedestrian on the road is gone in moments: wait for them."
                            : $"{name}'s route can't be taken round it.";
                        continue;
                    }
                    option.CandidateAction = detour.Action; option.IncidentId = detour.IncidentId;
                    option.Tooltip = detour.IncidentId == incident.IncidentId ? "Draw a way round it on the map."
                        : $"Draw a way round {IncidentPhrase(answers)} on the map: the latest one detected, which a new route answers.";
                    Enable(option, detour, snapshot, paused);
                    continue;
                }

                var candidate = decision.Option(action) ?? (action == "KEEP_ROUTE" ? decision.Option("WAIT") : null);
                var delay = Delay(candidate);
                var figure = delay == null ? "" : delay < 1 ? " ~no delay" : " ~+" + Rough(delay.Value);
                switch (action)
                {
                    case "KEEP_ROUTE":
                        option.Label = "Keep route" + figure;
                        option.Confirm = $"{name} keeps its route through {what}" + (delay >= 1 ? $", about {Rough(delay.Value)} later." : ".");
                        option.Reason = "Keep the route; the delay is acceptable";
                        option.Tooltip = candidate == null ? $"Keeping the route isn't offered to {name}." : null;
                        break;
                    case "HOLD_AT_STOP":
                        var stopId = (string)(Evidence(candidate)?["hold_stop"] as JObject)?["stop_id"];
                        var stop = string.IsNullOrEmpty(stopId) ? null : RouteDraftView.StopName(stopId);
                        option.Label = (stop == null ? "Hold at a stop" : "Hold at " + stop) + figure;
                        option.Confirm = $"Hold {name} at {stop ?? "the stop before it"} until {what} clears.";
                        option.Reason = "Hold before the queue until it clears";
                        if (candidate == null)
                            option.Tooltip = snapshot.Vehicles.FirstOrDefault(v => v.VehicleId == vehicleId)?.ServiceState == "AWAITING_GUIDANCE"
                                ? $"{name} is already stopped at it." : "There is no stop before it to hold at.";
                        break;
                    case "ESCALATE":
                        // Assistance is for the incident, so every shuttle that reaches it gets it
                        var shuttles = Names(new[] { incident.VehicleId }.Concat(incident.ExposedVehicleIds ?? new List<string>())
                            .Where(v => v != null).Distinct().OrderBy(v => v, StringComparer.Ordinal).Select(ShuttleName));
                        option.Label = "Request assistance" + figure;
                        option.Confirm = $"Request assistance at {what}. It applies to every shuttle that reaches it" +
                                         (string.IsNullOrEmpty(shuttles) ? "." : $": {shuttles}.");
                        option.Reason = "Assistance clears the incident sooner";
                        if (candidate == null)
                            option.Tooltip = incident.Responses?.Any(r => r.Action == "ESCALATE") == true
                                ? "Assistance has already been requested." : "Assistance isn't offered for this incident.";
                        break;
                }
                if (candidate == null) continue;
                option.CandidateAction = candidate.Action; option.IncidentId = incident.IncidentId;
                Enable(option, candidate, snapshot, paused);
            }
            return options;
        }

        // An offered option can be chosen while the session runs, the backend clears it and it stands.
        private static void Enable(DecisionOption option, Candidate candidate, FullSnapshot snapshot, bool paused)
        {
            if (paused) option.Tooltip = "The session is paused. Resume it to decide.";
            else if (candidate.Validation != "PASS") option.Tooltip = "The backend can't clear this option now.";
            else if (candidate.ExpiresAtTick < snapshot.Tick) option.Tooltip = "Options are being refreshed.";
            else option.Enabled = true;
        }

        private static string DefaultLabel(string action) =>
            action == "HOLD_AT_STOP" ? "Hold at a stop" : action == "ROAD_DETOUR" ? "Re-route on the map" : TrainingPresenter.ActionLabel(action);

        // "the collision on Lorong 6 Toa Payoh", or "the collision" when it is on no named road.
        private static string IncidentPhrase(Incident incident)
        {
            var road = incident.Location?.RoadName;
            return "the " + TrainingPresenter.IncidentName(incident).ToLowerInvariant() + (string.IsNullOrEmpty(road) ? "" : " on " + road);
        }

        public static string OptionsTitle(string vehicleId) => $"What should {ShuttleName(vehicleId)} do?";

        // Why the option buttons can't be used now, or null when they can.
        public static string OptionsHint(FullSnapshot snapshot, bool available, bool sending, bool fresh)
        {
            if (snapshot?.Session.Status == "PAUSED") return "The session is paused. Resume it to decide.";
            if (!available) return "Not connected to the session. Decisions wait until it is back.";
            if (sending) return "Sending a decision…";
            if (!fresh) return "Waiting for a fresh update from the session…";
            return null;
        }

        public const string OptionsRefreshed = "Options were refreshed; choose again.";
        public const string OptionsWithdrawn = "That option is no longer offered; choose again.";

        // What the confirm row says when a decision was not applied: it stays open for another try.
        public static string Refusal(CommandOutcome outcome)
        {
            switch (outcome.Status)
            {
                case "EXPIRED": return "The option expired before it was applied. Choose again.";
                case "NEEDS_RECONFIRMATION": return "The connection dropped, so it isn't known whether this was applied. Check the problem, then try again.";
                case "REJECTED": case "NOT_ACCEPTED": return $"The backend refused it ({outcome.FailureCode ?? "no reason given"}).";
                default: return $"It wasn't applied ({outcome.Status}).";
            }
        }

        private static bool Live(FullSnapshot snapshot) =>
            snapshot?.Session != null && (snapshot.Session.Status == "RUNNING" || snapshot.Session.Status == "PAUSED");

        // A time for people, to the nearest step past a minute (10 s for an estimate): "40 s", "1 min 50 s", "3 min".
        internal static string Rough(double seconds, int step = 10)
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

        // A shuttle on the Needs a decision strip, after its name (shown in a red box): how many
        // decisions it has waiting, and about how soon it reaches the first one's incident.
        public static string PendingChip(PendingDecision decision, int decisions) =>
            $"needs a decision ({decisions})" +
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
