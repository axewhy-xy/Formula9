/*
 * Incident notifications: one card per shuttle and incident, each with a Decide button.
 *
 * Author: Lau Xiang Yang
 * Date Created: 15/11/2026
 * Last Modified: 24/11/2026
 *
 * Description:
 *     Part of the incident handling reform (phase 5). NotificationQueue is the logic: it takes the
 *     live events the alerts let through (TrainingAlerts.Fresh, so muting and the replay rules stay
 *     the same) and the accepted snapshots, and keeps one notification per (incident, shuttle). A
 *     notification stays until its shuttle is decided for or reaches the incident undecided, its
 *     options are withdrawn, the incident ends, or the operator closes it. NotificationStack draws
 *     the queue over the top-right corner of the map, at most three at a time. The Needs a decision
 *     strip on Route Planning comes from the snapshot, so closing a notification never hides a
 *     decision still to be made.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.Contracts;
using Newtonsoft.Json.Linq;
using UnityEngine.UIElements;
using Event = Formula9.Contracts.Event;

namespace Formula9.UI
{
    // What opened a notification: the incident was detected, another shuttle will now reach it, or a
    // shuttle was newly offered options.
    public enum NotificationKind { Detected, AlsoReaches, OptionsReady }

    // -----------------------------------------------------------------------------------
    // Class: IncidentNotification
    // -----------------------------------------------------------------------------------
    // Description:
    //     One notification: a shuttle and the incident it is about. Its text is worked out again
    //     from each snapshot, so the time to the incident counts down; until the snapshot has the
    //     incident, from the event that opened it.
    public sealed class IncidentNotification
    {
        public string IncidentId { get; internal set; }
        public string VehicleId { get; internal set; }
        public NotificationKind Kind { get; internal set; }
        // The event sequence of the event that opened it, or last renewed it
        public long Sequence { get; internal set; }

        // "New incident", "Another shuttle will reach it" or "Options ready"
        public string Tag { get; internal set; }
        // "Collision · Lorong 6 Toa Payoh"
        public string Title { get; internal set; }
        // "Shuttle 03 reaches it in about 40 s. Shuttle 05 in about 2 min."
        public string Body { get; internal set; }
        // What it does to the road, when it clears and who it is slowing, or null when nothing is known:
        // "Lane blocked · clears in 42 s · Shuttle 02 slowed to 3.1 m/s"
        public string Detail { get; internal set; }
        // "Decide for Shuttle 03"
        public string Decide { get; internal set; }
        // How soon the shuttle reaches the incident, in seconds, or null when no figure is known
        public double? EtaS { get; internal set; }

        public string Key => NotificationQueue.KeyOf(IncidentId, VehicleId);

        // What the opening event said, for while the snapshot does not have the incident yet; its
        // impact and when it said the incident ends are only ever in the event
        internal string EventType, IncidentType, RoadName, Impact;
        internal long? ActiveUntilTick;
        internal List<IncidentExposure> Exposures = new List<IncidentExposure>();
    }

    // -----------------------------------------------------------------------------------
    // Class: NotificationQueue
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class decides which incident notifications are open. It has no view, so it is tested
    //     on its own. Events open notifications; snapshots and OPTIONS_WITHDRAWN close them. A
    //     snapshot only closes a notification it is at least as new as, so a snapshot fetched before
    //     an incident was detected does not close that incident's notifications. A notification the
    //     operator closed is not opened again for the same shuttle and incident until the backend has
    //     settled that shuttle's part in it.
    public sealed class NotificationQueue
    {
        private readonly Dictionary<string, IncidentNotification> open = new Dictionary<string, IncidentNotification>();
        // Closed by the operator: (incident, shuttle) by key, with the sequence it was closed at
        private readonly Dictionary<string, (string IncidentId, string VehicleId, long Sequence)> dismissed =
            new Dictionary<string, (string, string, long)>();
        // A detection that named no shuttle the queue could see yet: opened from the next snapshot
        private readonly Dictionary<string, IncidentNotification> deferred = new Dictionary<string, IncidentNotification>();
        private FullSnapshot snapshot;
        private string sessionId;

        // Raised whenever the open notifications or their text change.
        public event Action Changed;

        // The shuttle an incident is slowing right now, or null. Only the 3D city knows which roads an
        // incident slows, so Formula9App points this at it; without it no card says a shuttle is slowed.
        public Func<string, FleetVehicle> SlowedBy { get; set; }

        public static string KeyOf(string incidentId, string vehicleId) => incidentId + "|" + vehicleId;

        // -----------------------------------------------------------------------------------
        // Property: Items
        // -----------------------------------------------------------------------------------
        // Description:
        //     The open notifications, the shuttle that reaches its incident soonest first; those with
        //     no figure after them, and the newest first among equals.
        public List<IncidentNotification> Items => open.Values
            .OrderBy(n => n.EtaS.HasValue ? 0 : 1).ThenBy(n => n.EtaS ?? 0).ThenByDescending(n => n.Sequence)
            .ThenBy(n => n.IncidentId, StringComparer.Ordinal).ThenBy(n => n.VehicleId, StringComparer.Ordinal).ToList();

        // -----------------------------------------------------------------------------------
        // Function: OnEvent
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function takes one live event the alerts let through, each once. A detection with
        //     "alert" opens one notification per shuttle that will reach the incident (or that it
        //     names); an exposure change opens one for each shuttle added to it; an OPTIONS_READY with
        //     "alert" opens one for each shuttle newly offered options. OPTIONS_WITHDRAWN closes the
        //     incident's notifications. Other events are left alone.
        //
        // Args:
        //     e (Event): The training event.
        //
        // Returns:
        //     bool: True when the event is about incident notifications (even if none was opened),
        //         false for any other event.
        public bool OnEvent(Event e)
        {
            if (e?.EntityType != "INCIDENT" || e.Data == null || string.IsNullOrEmpty(e.EntityId)) return false;
            switch (e.EventType)
            {
                case "INCIDENT_DETECTED":
                    if (e.Data.Value<bool?>("alert") != true) return false;
                    var reached = ReadExposures(e.Data["exposures"]).Select(x => x.VehicleId);
                    var owner = IncidentOf(snapshot, e.EntityId)?.VehicleId;
                    var vehicles = reached.Concat(owner == null ? Enumerable.Empty<string>() : new[] { owner }).ToList();
                    if (vehicles.Count == 0) deferred[e.EntityId] = From(e, null, NotificationKind.Detected);
                    else Open(e, NotificationKind.Detected, vehicles);
                    return true;
                case "INCIDENT_EXPOSURE_CHANGED":
                    var added = Ids(e.Data["added_vehicle_ids"]);
                    if (added.Count == 0) return false;
                    Open(e, NotificationKind.AlsoReaches, added);
                    return true;
                case "OPTIONS_READY":
                    if (e.Data.Value<bool?>("alert") != true) return false;
                    Open(e, NotificationKind.OptionsReady, Ids(e.Data["added_vehicle_ids"] ?? e.Data["vehicle_ids"]));
                    return true;
                case "OPTIONS_WITHDRAWN":
                    var withdrawn = Ids(e.Data["vehicle_ids"]);
                    deferred.Remove(e.EntityId);
                    var closed = open.Values.Where(n => n.IncidentId == e.EntityId && (withdrawn.Count == 0 || withdrawn.Contains(n.VehicleId))).ToList();
                    foreach (var n in closed) Settle(n.IncidentId, n.VehicleId);
                    if (closed.Count > 0) Changed?.Invoke();
                    return true;
            }
            return false;
        }

        // -----------------------------------------------------------------------------------
        // Function: OnSnapshot
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function takes an accepted snapshot. Another session starts the queue afresh. A
        //     deferred detection opens for the shuttles the snapshot says it concerns, notifications
        //     whose shuttle the snapshot shows settled close, and every text is worked out again.
        //
        // Args:
        //     s (FullSnapshot): The accepted snapshot.
        public void OnSnapshot(FullSnapshot s)
        {
            if (s == null) return;
            // The first snapshot keeps what came before it; a later one of another session does not
            if (sessionId != null && s.SessionId != sessionId) Reset();
            sessionId = s.SessionId;
            snapshot = s;
            foreach (var waiting in deferred.Values.ToList())
            {
                var incident = IncidentOf(s, waiting.IncidentId);
                if (incident == null || s.EventSequence < waiting.Sequence) continue;
                deferred.Remove(waiting.IncidentId);
                var vehicles = new[] { incident.VehicleId }.Concat(incident.ExposedVehicleIds ?? new List<string>())
                    .Concat(s.Candidates.Where(c => c.IncidentId == incident.IncidentId).Select(c => c.VehicleId ?? incident.VehicleId));
                foreach (var vehicle in vehicles.Where(v => v != null).Distinct()) Add(waiting, vehicle, waiting.Kind);
            }
            Prune();
            Describe();
            Changed?.Invoke();
        }

        // -----------------------------------------------------------------------------------
        // Function: Dismiss
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function closes a notification at the operator's request. The shuttle stays on
        //     the Needs a decision strip while it still has a decision to make.
        //
        // Args:
        //     incidentId (string): The incident.
        //     vehicleId (string): The shuttle.
        public void Dismiss(string incidentId, string vehicleId)
        {
            var key = KeyOf(incidentId, vehicleId);
            if (!open.TryGetValue(key, out var notification)) return;
            open.Remove(key);
            dismissed[key] = (incidentId, vehicleId, notification.Sequence);
            Changed?.Invoke();
        }

        // -----------------------------------------------------------------------------------
        // Function: Clear
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function forgets everything, as when the operator leaves the session.
        public void Clear()
        {
            Reset();
            snapshot = null; sessionId = null;
            Changed?.Invoke();
        }

        private void Reset() { open.Clear(); dismissed.Clear(); deferred.Clear(); }

        // Opens (or renews) the notification of each shuttle, then lets the latest snapshot close any it
        // already shows settled.
        private void Open(Event e, NotificationKind kind, IEnumerable<string> vehicles)
        {
            var template = From(e, null, kind);
            foreach (var vehicle in vehicles.Where(v => !string.IsNullOrEmpty(v)).Distinct()) Add(template, vehicle, kind);
            Prune();
            Describe();
            Changed?.Invoke();
        }

        private void Add(IncidentNotification template, string vehicleId, NotificationKind kind)
        {
            var key = KeyOf(template.IncidentId, vehicleId);
            if (dismissed.ContainsKey(key)) return;
            if (!open.TryGetValue(key, out var notification))
                open[key] = notification = new IncidentNotification { IncidentId = template.IncidentId, VehicleId = vehicleId };
            notification.Kind = kind;
            notification.Sequence = Math.Max(notification.Sequence, template.Sequence);
            notification.EventType = template.EventType ?? notification.EventType;
            notification.IncidentType = template.IncidentType ?? notification.IncidentType;
            notification.RoadName = template.RoadName ?? notification.RoadName;
            notification.Impact = template.Impact ?? notification.Impact;
            notification.ActiveUntilTick = template.ActiveUntilTick ?? notification.ActiveUntilTick;
            if (template.Exposures.Count > 0) notification.Exposures = template.Exposures;
        }

        // Closes the notifications the latest snapshot shows settled, and forgets the operator's own
        // closing of those it shows settled, so a new situation for that shuttle can notify again.
        private void Prune()
        {
            if (snapshot == null) return;
            foreach (var n in open.Values.Where(n => snapshot.EventSequence >= n.Sequence && Settled(snapshot, n.IncidentId, n.VehicleId)).ToList())
                Settle(n.IncidentId, n.VehicleId);
            foreach (var d in dismissed.Where(d => snapshot.EventSequence >= d.Value.Sequence && Settled(snapshot, d.Value.IncidentId, d.Value.VehicleId)).ToList())
                dismissed.Remove(d.Key);
        }

        private void Settle(string incidentId, string vehicleId)
        {
            var key = KeyOf(incidentId, vehicleId);
            open.Remove(key);
            dismissed.Remove(key);
        }

        // -----------------------------------------------------------------------------------
        // Function: Settled
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function says whether a snapshot leaves a shuttle nothing to be told about an
        //     incident: the incident is gone or over, the shuttle was decided for (or reached it
        //     undecided), or it no longer reaches the incident and has no options for it.
        //
        // Args:
        //     s (FullSnapshot): The snapshot.
        //     incidentId (string): The incident.
        //     vehicleId (string): The shuttle.
        //
        // Returns:
        //     bool: True when the notification should close.
        public static bool Settled(FullSnapshot s, string incidentId, string vehicleId)
        {
            var incident = IncidentOf(s, incidentId);
            if (incident == null || incident.State == "RESOLVED" || incident.State == "UNRESOLVED") return true;
            if (incident.ResponseOf(vehicleId) != null) return true;
            return !incident.Involves(vehicleId) &&
                   !s.Candidates.Any(c => c.IncidentId == incidentId && (c.VehicleId ?? incident.VehicleId) == vehicleId);
        }

        // Works out every open notification's text from the latest snapshot.
        private void Describe()
        {
            foreach (var n in open.Values)
            {
                Describe(n, IncidentOf(snapshot, n.IncidentId));
                n.Detail = Detail(n, IncidentOf(snapshot, n.IncidentId), snapshot, SlowedBy?.Invoke(n.IncidentId));
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: Detail
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function words the card's last line, what the 3D view's status box used to say:
        //     what the incident does to the road, how long until it clears, and which shuttle it is
        //     slowing. A road closure clears when its closure ends; otherwise the incident's own
        //     clearing tick, else the end the opening event gave. Figures are the backend's.
        //
        // Args:
        //     n (IncidentNotification): The notification.
        //     incident (Incident): The snapshot's incident, or null.
        //     s (FullSnapshot): The snapshot, or null.
        //     slowed (FleetVehicle): The shuttle the incident is slowing, or null.
        //
        // Returns:
        //     string: The line, or null when there is nothing to say.
        internal static string Detail(IncidentNotification n, Incident incident, FullSnapshot s, FleetVehicle slowed)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(n.Impact)) parts.Add(n.Impact);
            if (incident != null && s != null)
            {
                var edge = incident.Location?.EdgeId;
                var closure = edge == null ? null : s.Disruptions?.FirstOrDefault(d => d.AffectedEdgeIds.Contains(edge) && d.ActiveFromTick >= incident.DetectedTick);
                if ((closure?.ActiveUntilTick ?? incident.ClearsAtTick ?? n.ActiveUntilTick) is long until)
                {
                    var seconds = Math.Ceiling(Math.Max(0, until - s.Tick) * World.IncidentSimulation.SecondsPerTick(s));
                    parts.Add("clears in " + RoutePresenter.Rough(seconds));
                }
            }
            if (slowed != null) parts.Add($"{RoutePresenter.ShuttleName(slowed.VehicleId)} slowed to {slowed.SpeedMps:0.#} m/s");
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }

        // -----------------------------------------------------------------------------------
        // Function: Describe
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function words a notification: what and where the incident is, when the shuttle
        //     reaches it and, for a new incident, who else does. Figures are the backend's estimates
        //     and come from the snapshot's incident when it has it, else from the opening event.
        //
        // Args:
        //     n (IncidentNotification): The notification, whose text is set.
        //     incident (Incident): The snapshot's incident, or null.
        internal static void Describe(IncidentNotification n, Incident incident)
        {
            var name = incident != null && incident.EventType == null ? Capitalised(TrainingPresenter.IncidentName(incident))
                : World.IncidentRecord.Name(incident?.EventType ?? n.EventType, n.IncidentType);
            var road = incident?.Location?.RoadName ?? n.RoadName;
            var exposures = (incident != null ? incident.Exposures : n.Exposures) ?? new List<IncidentExposure>();
            var own = exposures.FirstOrDefault(x => x.VehicleId == n.VehicleId);
            var shuttle = RoutePresenter.ShuttleName(n.VehicleId);

            n.EtaS = own?.EtaS;
            n.Title = string.IsNullOrEmpty(road) ? name : $"{name} · {road}";
            n.Decide = "Decide for " + shuttle;
            switch (n.Kind)
            {
                case NotificationKind.AlsoReaches:
                    n.Tag = "Another shuttle will reach it";
                    n.Body = $"{shuttle} will also reach it" + (own == null ? "." : $" {When(own.EtaS)}.");
                    break;
                case NotificationKind.OptionsReady:
                    n.Tag = "Options ready";
                    n.Body = $"Options are ready for {shuttle}" + (own == null ? "." : $", which reaches it {When(own.EtaS)}.");
                    break;
                default:
                    n.Tag = "New incident";
                    n.Body = own == null ? $"It concerns {shuttle}." : $"{shuttle} reaches it {When(own.EtaS)}.";
                    var others = exposures.Where(x => x.VehicleId != n.VehicleId).OrderBy(x => x.EtaS).ToList();
                    foreach (var other in others.Take(2)) n.Body += $" {RoutePresenter.ShuttleName(other.VehicleId)} {When(other.EtaS)}.";
                    if (others.Count > 2) n.Body += $" +{others.Count - 2} more.";
                    break;
            }
        }

        private static string When(double etaS) => etaS < 0.5 ? "now" : "in about " + RoutePresenter.Rough(etaS);

        private static string Capitalised(string text)
        {
            var lower = (text ?? "").ToLowerInvariant();
            return lower.Length == 0 ? "Incident" : char.ToUpperInvariant(lower[0]) + lower.Substring(1);
        }

        private static Incident IncidentOf(FullSnapshot s, string incidentId) => s?.Incidents?.FirstOrDefault(i => i.IncidentId == incidentId);

        // A notification as the event describes it, for any shuttle.
        private static IncidentNotification From(Event e, string vehicleId, NotificationKind kind) => new IncidentNotification
        {
            IncidentId = e.EntityId, VehicleId = vehicleId, Kind = kind, Sequence = e.EventSequence,
            EventType = e.Data.Value<string>("event_type"),
            IncidentType = e.Data.Value<string>("incident_type"),
            RoadName = (e.Data["location"] as JObject)?.Value<string>("road_name"),
            Impact = e.Data.Value<string>("operational_impact"),
            ActiveUntilTick = e.Data.Value<long?>("active_until_tick"),
            Exposures = ReadExposures(e.Data["exposures"]),
        };

        private static List<string> Ids(JToken value) =>
            value is JArray items ? items.Select(t => t.Type == JTokenType.String ? (string)t : null).Where(v => !string.IsNullOrEmpty(v)).ToList() : new List<string>();

        private static List<IncidentExposure> ReadExposures(JToken value)
        {
            var found = new List<IncidentExposure>();
            if (!(value is JArray items)) return found;
            foreach (var item in items.OfType<JObject>())
            {
                var vehicle = item.Value<string>("vehicle_id");
                var eta = item.Value<double?>("eta_s");
                if (string.IsNullOrEmpty(vehicle) || !eta.HasValue) continue;
                found.Add(new IncidentExposure { VehicleId = vehicle, EtaS = eta.Value, DistanceM = item.Value<double?>("distance_m") ?? 0 });
            }
            return found;
        }
    }

    // -----------------------------------------------------------------------------------
    // Class: NotificationStack
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class draws the queue: a stack of cards over the top-right corner of the map (or of
    //     the page, when the page takes the full width), at most MaxVisible, with "+N more" under
    //     them. Cards keep their elements while the same notifications are shown, so the one-second
    //     poll never splits a click; only their text is updated.
    public sealed class NotificationStack
    {
        public const int MaxVisible = 3;

        private sealed class Card
        {
            public VisualElement Root;
            public Label Tag, Title, Body, Detail;
            public Button Decide;
        }

        private readonly VisualElement host, list;
        private readonly Button more;
        private readonly NotificationQueue queue;
        private readonly Action<string, string> decide;
        private readonly Dictionary<string, Card> cards = new Dictionary<string, Card>();
        private List<string> shown = new List<string>();

        // -----------------------------------------------------------------------------------
        // Function: NotificationStack (constructor)
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function builds the stack in its host and follows the queue.
        //
        // Args:
        //     host (VisualElement): The element to draw in, positioned over the workspace.
        //     queue (NotificationQueue): The notifications.
        //     decide (Action<string, string>): Opens Route Planning for (shuttle, incident).
        //     showAll (Action): What "+N more" does: open Route Planning, whose strip lists them all.
        public NotificationStack(VisualElement host, NotificationQueue queue, Action<string, string> decide, Action showAll)
        {
            this.host = host; this.queue = queue; this.decide = decide;
            // Only the cards take clicks; around them the map does
            host.pickingMode = PickingMode.Ignore;
            list = new VisualElement { pickingMode = PickingMode.Ignore };
            list.AddToClassList("notifications-list");
            host.Add(list);
            more = new Button(() => showAll?.Invoke()) { tooltip = "Route Planning lists every shuttle that needs a decision." };
            more.AddToClassList("notification-more");
            host.Add(more);
            queue.Changed += Render;
            Render();
        }

        // -----------------------------------------------------------------------------------
        // Function: Anchor
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function keeps the stack at the right edge of an element beside it (the map),
        //     and at the right edge of its own parent while that element is hidden.
        //
        // Args:
        //     over (VisualElement): The element whose top-right corner the stack sits in.
        public void Anchor(VisualElement over)
        {
            var container = host.parent;
            if (over == null || container == null) return;
            void Place()
            {
                var visible = over.resolvedStyle.display != DisplayStyle.None && over.layout.width > 0;
                var right = visible ? container.layout.width - over.layout.xMax + 10 : 10;
                if (float.IsNaN(right) || right < 0) right = 10;
                host.style.right = right;
            }
            over.RegisterCallback<GeometryChangedEvent>(_ => Place());
            container.RegisterCallback<GeometryChangedEvent>(_ => Place());
        }

        // -----------------------------------------------------------------------------------
        // Function: Render
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function shows the queue's first MaxVisible notifications and how many more
        //     there are. The cards are rebuilt only when which notifications show changes.
        public void Render()
        {
            var items = queue.Items;
            var visible = items.Take(MaxVisible).ToList();
            var keys = visible.Select(n => n.Key).ToList();
            if (!keys.SequenceEqual(shown))
            {
                shown = keys;
                list.Clear();
                foreach (var stale in cards.Keys.Except(keys).ToList()) cards.Remove(stale);
                foreach (var n in visible)
                {
                    if (!cards.TryGetValue(n.Key, out var card)) cards[n.Key] = card = Build(n.IncidentId, n.VehicleId);
                    list.Add(card.Root);
                }
            }
            foreach (var n in visible)
            {
                var card = cards[n.Key];
                Set(card.Tag, n.Tag);
                Set(card.Title, n.Title);
                Set(card.Body, n.Body);
                Set(card.Detail, n.Detail);
                card.Detail.style.display = string.IsNullOrEmpty(n.Detail) ? DisplayStyle.None : DisplayStyle.Flex;
                if (card.Decide.text != n.Decide) card.Decide.text = n.Decide;
                card.Root.EnableInClassList("notification-options", n.Kind == NotificationKind.OptionsReady);
            }
            var hidden = items.Count - visible.Count;
            more.text = $"+{hidden} more";
            more.style.display = hidden > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            host.style.display = items.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // One card: what it is, the incident, the shuttle's part in it, what it does to the road, and
        // its two buttons.
        private Card Build(string incidentId, string vehicleId)
        {
            var card = new Card { Root = new VisualElement() };
            card.Root.AddToClassList("notification");
            card.Tag = Text(card.Root, "notification-tag");
            card.Title = Text(card.Root, "notification-title");
            card.Body = Text(card.Root, "notification-body");
            card.Detail = Text(card.Root, "notification-detail");
            var actions = new VisualElement();
            actions.AddToClassList("notification-actions");
            card.Decide = new Button(() => decide?.Invoke(vehicleId, incidentId)) { tooltip = "Open Route Planning for this shuttle and show the incident." };
            card.Decide.AddToClassList("notification-decide");
            actions.Add(card.Decide);
            var close = new Button(() => queue.Dismiss(incidentId, vehicleId)) { text = "×", tooltip = "Close this notification. The shuttle stays on Route Planning's Needs a decision list." };
            close.AddToClassList("notification-close");
            actions.Add(close);
            card.Root.Add(actions);
            return card;
        }

        private static Label Text(VisualElement parent, string className)
        {
            var label = new Label();
            label.AddToClassList(className);
            parent.Add(label);
            return label;
        }

        private static void Set(Label label, string text) { if (label.text != (text ?? "")) label.text = text ?? ""; }
    }
}
