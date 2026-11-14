/*
 * The text and state the Unity client's screens show.
 *
 * Author: Choo Jian Wei
 * Contributors: Rishika (fleet cards, next stop and ETA), Lau Xiang Yang (session list and cleanup, exit)
 * Date Created: 30/10/2026
 * Last Modified: 10/11/2026
 *
 * Description:
 *     Part of the Unity client's UI. Pure functions that turn login results, snapshots, session
 *     lists and command outcomes into what the screens show, so the wording and rules are tested
 *     without building any UI. The wording follows the Qt client's LoginPage and TrainingWorkspace.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.Session;

namespace Formula9.UI
{
    // -----------------------------------------------------------------------------------
    // Class: LoginMessages
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class holds what the login screen says for each outcome, with the same wording as
    //     the Qt LoginPage.
    //
    //     Attributes: SignUpHint explains the account rules; Expired is shown when a login runs
    //     out. ConfirmExit asks before the application closes.
    public static class LoginMessages
    {
        public const string SignUpHint = "Create an account to get started. Usernames are 3 to 32 letters, numbers, dots, " +
                                         "dashes or underscores; passwords need at least 8 characters.";
        public const string Expired = "Your login has expired. Log in again to continue; your training session is kept.";

        // -----------------------------------------------------------------------------------
        // Function: Validate
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function checks the login form before anything is sent.
        //
        // Args:
        //     username (string): The username typed.
        //     password (string): The password typed.
        //     signUp (bool): True when creating an account.
        //     confirmation (string): The repeated password, for sign-up.
        //
        // Returns:
        //     string: Null when the form may be submitted, otherwise what to fix.
        public static string Validate(string username, string password, bool signUp, string confirmation)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) return "Enter your username and password.";
            if (signUp && password != confirmation) return "Passwords do not match.";
            return null;
        }

        // -----------------------------------------------------------------------------------
        // Function: ConfirmExit
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function asks whether to close the application. The login is held in memory
        //     only, so a logged-in trainee is told they will have to log in again.
        //
        // Args:
        //     loggedIn (bool): True when a trainee is logged in.
        //
        // Returns:
        //     string: The question to show.
        public static string ConfirmExit(bool loggedIn) =>
            loggedIn ? "Exit Formula 9? You will need to log in again next time." : "Exit Formula 9?";

        // -----------------------------------------------------------------------------------
        // Function: For
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function says what went wrong with a login or sign-up in words the trainee can
        //     act on: a missing login endpoint, an unreachable server, a server error, wrong
        //     details, or the server's own explanation (invalid details, username taken, accounts
        //     unavailable).
        //
        // Args:
        //     error (Exception): What the login attempt threw.
        //
        // Returns:
        //     string: The message to show.
        public static string For(Exception error)
        {
            switch (error)
            {
                case NoReplyException noReply when noReply.Status == 404:
                case BackendErrorException backend404 when backend404.Error.Status == 404:
                    return "Login isn't available on this server yet. Restart the backend and try again.";
                case NoReplyException noReply when noReply.Status == 0:
                    return "Can't reach the server. Start the backend (run.bat) and try again.";
                case NoReplyException noReply:
                    return $"Something went wrong on the server (error {noReply.Status}). Try again.";
                case BackendErrorException backend when backend.Error.Status == 401:
                    return "Incorrect username or password.";
                case BackendErrorException backend:
                    // 400 invalid details, 409 username taken, 503 accounts unavailable: the server explains.
                    return string.IsNullOrWhiteSpace(backend.Error.Message) || backend.Error.Message == backend.Error.Code
                        ? $"Something went wrong on the server (error {backend.Error.Status}). Try again."
                        : backend.Error.Message;
                case ArgumentException invalid:
                    return invalid.Message;
                default:
                    return "The server sent an unexpected reply. Try again.";
            }
        }
    }

    // -----------------------------------------------------------------------------------
    // Class: ScenarioChoice
    // -----------------------------------------------------------------------------------
    // Description:
    //     This struct is one scenario the trainee can create: its label, scenario revision ID and
    //     description.
    public readonly struct ScenarioChoice
    {
        public readonly string Label, Id, Description;
        public ScenarioChoice(string label, string id, string description) { Label = label; Id = id; Description = description; }
    }

    // -----------------------------------------------------------------------------------
    // Class: SessionControls
    // -----------------------------------------------------------------------------------
    // Description:
    //     This struct controls visibility and enablement for Start, Pause, Resume and Archive,
    //     including why Start is unavailable.
    public readonly struct SessionControls
    {
        public readonly bool Start, Pause, Resume, Archive;
        public readonly bool StartVisible, PauseVisible, ResumeVisible, PlaybackVisible;
        public readonly string StartHint;
        public SessionControls(bool ready, bool running, bool paused, bool available, bool startAllowed, string startHint)
        {
            StartVisible = ready; PauseVisible = running; ResumeVisible = paused;
            PlaybackVisible = ready || running || paused;
            Start = available && ready && startAllowed;
            Pause = available && running; Resume = available && paused;
            Archive = available && PlaybackVisible; StartHint = startHint;
        }
    }

    public readonly struct SessionSetupView
    {
        public readonly bool ToolsVisible, ToggleVisible, Compact;
        public SessionSetupView(bool toolsVisible, bool toggleVisible, bool compact)
        { ToolsVisible = toolsVisible; ToggleVisible = toggleVisible; Compact = compact; }
    }

    public readonly struct SessionStatusView
    {
        public readonly string BadgeText, BadgeClass, Tooltip, Summary;
        public readonly bool Offline;
        public SessionStatusView(string badgeText, string badgeClass, string tooltip, string summary, bool offline)
        { BadgeText = badgeText; BadgeClass = badgeClass; Tooltip = tooltip; Summary = summary; Offline = offline; }
    }

    // Snapshot ticks change every poll; the operator's summary usually does not.
    public sealed class SessionStatusPresenter
    {
        private (bool Joined, string Session, string State, bool Available)? statusKey;
        private SessionStatusView status;

        public SessionStatusView Present(FullSnapshot snapshot, bool available)
        {
            var key = (snapshot != null, snapshot?.SessionId, snapshot?.Session?.Status, snapshot != null && available);
            if (statusKey != key)
            {
                status = TrainingPresenter.SessionStatus(snapshot, available);
                statusKey = key;
            }
            return status;
        }
    }

    // -----------------------------------------------------------------------------------
    // Class: FleetRow
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class is one shuttle's card in the fleet panel.
    //
    //     Attributes: the shuttle's ID, state and status text, route, battery (as text and
    //     percent), provenance, next stop and ETA lines; Incident, a line for its open incident or
    //     null; Index, its position among the sorted shuttles, which picks its colour on the map
    //     and the card.
    public sealed class FleetRow
    {
        public string ShuttleId, State, Status, Route, RouteLine, Battery, Provenance, NextStop, Eta;
        // A line for the shuttle's open incident; null when it has none.
        public string Incident;
        public double BatteryPercent;
        // Its position among the sorted shuttles, which picks its colour on the map and the card.
        public int Index;
    }

    // -----------------------------------------------------------------------------------
    // Class: TrainingPresenter
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class derives everything the training screen shows from the accepted snapshot. A
    //     port of the Qt TrainingWorkspace's refreshStatus, refreshViews and showCommandResult.
    //
    //     Attributes: Scenarios lists what can be created; NoSession is shown when nothing is
    //     joined.
    public static class TrainingPresenter
    {
        public static readonly IReadOnlyList<ScenarioChoice> Scenarios = new[]
        {
            new ScenarioChoice("Singapore, real streets (2D)", SessionController.IntegratedScenario, "Real streets and LTA bus stops, shuttles running from a bus interchange, with incidents, detours and charging."),
        };

        // -----------------------------------------------------------------------------------
        // Function: ScenarioTitle
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function names a scenario for the screen header.
        //
        // Args:
        //     scenarioId (string): The scenario revision ID.
        //
        // Returns:
        //     string: The title.
        public static string ScenarioTitle(string scenarioId) => scenarioId == SessionController.IntegratedScenario
            ? "Singapore, real streets · incidents, detours, charging"
            : "3D fleet exercise · road, ramp and obstruction";

        public const string NoSession = "Create or join a training session to begin.";

        public static bool IsTerminalSession(string status) => status == "ARCHIVED" || status == "COMPLETED" || status == "FAILED";

        public static SessionSetupView SessionSetup(FullSnapshot snapshot, bool charging, bool? toolsOpen)
        {
            var joined = snapshot != null;
            var showTools = !joined || (toolsOpen ?? IsTerminalSession(snapshot.Session?.Status));
            return new SessionSetupView(showTools, joined, charging && !showTools);
        }

        private static (string Text, string Class) SessionBadge(string state) => state switch
        {
            "DRAFT" => ("Draft", "session-badge-draft"),
            "READY" => ("Ready", "session-badge-ready"),
            "RUNNING" => ("Running", "session-badge-running"),
            "PAUSED" => ("Paused", "session-badge-paused"),
            "RECOVERING" => ("Recovering", "session-badge-recovering"),
            "COMPLETED" => ("Completed", "session-badge-completed"),
            "ARCHIVED" => ("Archived", "session-badge-archived"),
            "FAILED" => ("Failed", "session-badge-failed"),
            _ => ("Unknown", "session-badge-unknown"),
        };

        public static SessionStatusView SessionStatus(FullSnapshot snapshot, bool available)
        {
            if (snapshot == null) return new SessionStatusView("No session", "session-badge-none", NoSession, NoSession, false);
            var known = SessionBadge(snapshot.Session?.Status);
            var offline = !available;
            return new SessionStatusView(offline ? "Offline" : known.Text, offline ? "session-badge-offline" : known.Class,
                (offline ? "Live updates unavailable. Last received simulation state: " : "Current simulation state: ") + known.Text.ToLowerInvariant() + ".",
                $"Session: {snapshot.SessionId} · " + (offline ? "Offline · showing last received state" : "Connected"), offline);
        }

        // -----------------------------------------------------------------------------------
        // Function: Title
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function names the joined session's scenario: spatial sessions are the 3D
        //     exercise.
        //
        // Args:
        //     snapshot (FullSnapshot): The accepted snapshot.
        //
        // Returns:
        //     string: The title.
        public static string Title(FullSnapshot snapshot) => snapshot.Spatial != null
            ? "3D fleet exercise · road, ramp and obstruction"
            : "Singapore, real streets · incidents, detours, charging";

        // -----------------------------------------------------------------------------------
        // Function: Controls
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function decides which session controls are enabled. Start needs a READY
        //     session, its 3D geometry drawn when it has a world, and no assigned route across a
        //     closed road.
        //
        // Args:
        //     snapshot (FullSnapshot): The accepted snapshot, or null.
        //     available (bool): True when the session can take commands.
        //     spatialReady (bool): True when the 3D world is drawn.
        //
        // Returns:
        //     SessionControls: The enabled controls and Start's hint.
        public static SessionControls Controls(FullSnapshot snapshot, bool available, bool spatialReady)
        {
            var status = snapshot?.Session?.Status;
            if (snapshot == null) return new SessionControls(false, false, false, false, false, null);
            var routeClosed = AssignedRouteIsClosed(snapshot);
            var needsGeometry = snapshot.Spatial != null && !spatialReady;
            string hint = null;
            if (needsGeometry) hint = "Required 3D geometry must render before starting this scenario.";
            else if (routeClosed && status == "READY") hint = "A shuttle's route crosses a closed road. Approve a detour first.";
            return new SessionControls(status == "READY", status == "RUNNING", status == "PAUSED", available,
                !needsGeometry && !routeClosed, hint);
        }

        // -----------------------------------------------------------------------------------
        // Function: AssignedRouteIsClosed
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function tells whether any shuttle's route crosses a road closed now. The
        //     backend refuses START in that case.
        //
        // Args:
        //     snapshot (FullSnapshot): The accepted snapshot.
        //
        // Returns:
        //     bool: True when a detour must be approved first.
        public static bool AssignedRouteIsClosed(FullSnapshot snapshot)
        {
            var closed = new HashSet<string>(snapshot.Disruptions
                .Where(d => d.ActiveFromTick <= snapshot.Tick && (d.ActiveUntilTick == null || snapshot.Tick < d.ActiveUntilTick))
                .SelectMany(d => d.AffectedEdgeIds));
            var routes = snapshot.Routes.ToDictionary(r => r.RouteId);
            return snapshot.Vehicles.Any(v => v.RouteId != null && routes.TryGetValue(v.RouteId, out var route)
                                              && route.DirectedEdgeIds.Any(closed.Contains));
        }

        // -----------------------------------------------------------------------------------
        // Function: Fleet
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function builds the fleet cards, sorted by shuttle ID.
        //
        // Args:
        //     snapshot (FullSnapshot): The accepted snapshot.
        //     selectedIncident (string): The incident selected on the incidents page, or null.
        //
        // Returns:
        //     IReadOnlyList<FleetRow>: One card per shuttle.
        public static IReadOnlyList<FleetRow> Fleet(FullSnapshot snapshot, string selectedIncident = null) => snapshot.Vehicles
            .OrderBy(v => v.VehicleId, StringComparer.Ordinal)
            .Select((v, index) =>
            {
                var route = snapshot.Routes.FirstOrDefault(r => r.RouteId == v.RouteId);
                return new FleetRow
                {
                    ShuttleId = v.VehicleId,
                    State = v.ServiceState,
                    Status = StatusText(v.ServiceState),
                    Route = v.RouteId == null ? "—" : $"{v.RouteId} / {v.RouteRevision}",
                    RouteLine = v.RouteId == null ? "No route assigned" : $"Route {v.RouteId} · rev {v.RouteRevision}",
                    Battery = $"{Number(v.BatteryKwh)} / {Number(v.BatteryCapacityKwh)} kWh",
                    BatteryPercent = v.BatteryCapacityKwh > 0 ? Math.Max(0, Math.Min(100, 100 * v.BatteryKwh / v.BatteryCapacityKwh)) : 0,
                    Provenance = v.Provenance.Kind,
                    NextStop = NextStopLine(v, route),
                    Eta = EtaLine(snapshot.EnergyForecasts?.FirstOrDefault(f => f.VehicleId == v.VehicleId)),
                    Incident = IncidentLine(snapshot, v.VehicleId, selectedIncident),
                    Index = index,
                };
            })
            .ToList();

        // -----------------------------------------------------------------------------------
        // Function: StatusText
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function turns a service state into words. An unknown state is shown as sent,
        //     not guessed.
        //
        // Args:
        //     state (string): The service_state value.
        //
        // Returns:
        //     string: The status text.
        public static string StatusText(string state)
        {
            switch (state)
            {
                case "IN_SERVICE": return "Moving";
                case "DETOURING": return "Detouring";
                case "RETURNING_TO_DEPOT": return "Returning to depot";
                case "BLOCKED": return "Stopped";
                case "AWAITING_GUIDANCE": return "Awaiting guidance";
                case "DWELLING": return "At stop";
                case "QUEUED_FOR_CHARGE": return "Queued to charge";
                case "CHARGING": return "Charging";
                case "STANDBY": return "Standby";
                case "OUT_OF_SERVICE": return "Out of service";
                default: return state;
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: NextStopLine
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function writes a shuttle's next-stop line.
        //
        // Args:
        //     vehicle (FleetVehicle): The shuttle.
        //     route (RouteRevision): Its route, or null.
        //
        // Returns:
        //     string: The line.
        private static string NextStopLine(FleetVehicle vehicle, RouteRevision route)
        {
            if (vehicle.RouteId == null) return "Next stop —";
            if (route == null) return "Next stop unknown";
            if (route.StopsM.Count == 0) return "No stops on this route";
            var next = NextStop(route, vehicle.Position.XM, vehicle.Position.YM);
            return next == null ? "Next stop unknown" : $"Next stop {next}";
        }

        // -----------------------------------------------------------------------------------
        // Function: NextStop
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function finds the first stop ahead along the route, or the route's first stop
        //     once past them all. A stop up to 1 m behind still counts as next, since the shuttle
        //     is there.
        //
        // Args:
        //     route (RouteRevision): The route.
        //     x (double): The shuttle's x in metres.
        //     y (double): The shuttle's y in metres.
        //
        // Returns:
        //     string: The stop ID, or null when the route has no path or stops.
        public static string NextStop(RouteRevision route, double x, double y)
        {
            var path = route.EdgePathsM.SelectMany(p => p).ToList();
            if (path.Count < 2) return null;
            // Within this distance behind the shuttle, a stop still counts as reached next (it is there)
            const double reachedM = 1;
            var shuttleAt = DistanceAlong(path, x, y);
            var ordered = route.StopsM.Select(s => (s.StopId, At: DistanceAlong(path, s.Position.XM, s.Position.YM))).OrderBy(s => s.At).ToList();
            if (ordered.Count == 0) return null;
            foreach (var stop in ordered)
                if (stop.At + reachedM >= shuttleAt) return stop.StopId;
            return ordered[0].StopId;
        }

        // -----------------------------------------------------------------------------------
        // Function: DistanceAlong
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function measures how far along a path the point nearest to (x, y) lies.
        //
        // Args:
        //     path (List<Position>): The path's points.
        //     x (double): The x in metres.
        //     y (double): The y in metres.
        //
        // Returns:
        //     double: The distance along the path in metres.
        private static double DistanceAlong(List<Position> path, double x, double y)
        {
            double travelled = 0, best = double.MaxValue, bestAt = 0;
            for (var i = 0; i + 1 < path.Count; i++)
            {
                double dx = path[i + 1].XM - path[i].XM, dy = path[i + 1].YM - path[i].YM;
                var length = Math.Sqrt(dx * dx + dy * dy);
                var share = length > 0 ? Math.Max(0, Math.Min(1, ((x - path[i].XM) * dx + (y - path[i].YM) * dy) / (length * length))) : 0;
                double offX = path[i].XM + dx * share - x, offY = path[i].YM + dy * share - y;
                var distance = Math.Sqrt(offX * offX + offY * offY);
                if (distance < best) { best = distance; bestAt = travelled + length * share; }
                travelled += length;
            }
            return bestAt;
        }

        // -----------------------------------------------------------------------------------
        // Function: EtaLine
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function writes the ETA to the shuttle's next depot, in seconds under a minute
        //     and minutes after.
        //
        // Args:
        //     forecast (EnergyForecast): The shuttle's energy forecast, or null.
        //
        // Returns:
        //     string: The line.
        private static string EtaLine(EnergyForecast forecast)
        {
            if (forecast?.NextDepotEtaS == null) return "ETA —";
            var seconds = forecast.NextDepotEtaS.Value;
            return $"Depot {forecast.NextDepotId} ETA {(seconds < 60 ? $"{Math.Round(seconds)} s" : $"{Math.Round(seconds / 60)} min")}";
        }

        // -----------------------------------------------------------------------------------
        // Function: IncidentLine
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function writes a line for a shuttle's open incidents, those that name it or
        //     that it will reach: the selected incident when it is one of them, otherwise the
        //     most recent one, with a count of the others. One it will reach says when.
        //
        // Args:
        //     snapshot (FullSnapshot): The accepted snapshot.
        //     vehicleId (string): The shuttle.
        //     selected (string): The selected incident, or null.
        //
        // Returns:
        //     string: The line, or null when the shuttle has no open incident.
        private static string IncidentLine(FullSnapshot snapshot, string vehicleId, string selected)
        {
            var open = snapshot.Incidents.Where(i => i.Involves(vehicleId) && i.State != "RESOLVED").ToList();
            if (open.Count == 0) return null;
            var shown = open.FirstOrDefault(i => i.IncidentId == selected) ?? open.OrderByDescending(i => i.DetectedTick).First();
            var state = shown.State.ToLowerInvariant().Replace('_', ' ');
            var text = $"! Incident {shown.IncidentId} · {char.ToUpperInvariant(state[0])}{state.Substring(1)}";
            var reach = shown.ExposureOf(vehicleId);
            if (reach != null) text += reach.EtaS < 0.5 ? " · reaching it now" : $" · reaches it in ~{Math.Round(reach.EtaS)} s";
            if (open.Count > 1) text += $" (+{open.Count - 1} more)";
            return shown.IncidentId == selected ? text + " · selected" : text;
        }

        // -----------------------------------------------------------------------------------
        // Function: IncidentDescription
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function describes an incident: what and where it is, its state, which
        //     shuttles will reach it and when, and what was decided. The Reports page shows it
        //     as it is; the Incident Monitor adds what is asked of the operator.
        //
        // Args:
        //     incident (Incident): The incident.
        //
        // Returns:
        //     string: The text.
        public static string IncidentDescription(Incident incident)
        {
            var name = World.IncidentRecord.Name(incident.EventType,
                incident.IncidentId.Contains("road_accident") ? World.IncidentSimulation.Accident : World.IncidentSimulation.Block).ToUpperInvariant();
            var road = incident.Location?.RoadName != null ? $" on {incident.Location.RoadName}" : "";
            var reach = World.IncidentRecord.Reach(incident.Exposures, 5);
            var exposure = reach == null ? "No shuttle reaches it before it clears." : $"Shuttles reaching it: {reach}.";
            var owner = incident.VehicleId == null ? "It belongs to no shuttle. " : "";
            var decided = Responses(incident);
            return $"{name}{road} | {incident.State}\n{owner}{exposure}{(decided == null ? "" : $"\nDecided: {decided}.")}";
        }

        // -----------------------------------------------------------------------------------
        // Function: IncidentSummary
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function describes the selected incident on the Incident Monitor, followed by
        //     what is asked of the operator.
        //
        // Args:
        //     incident (Incident): The incident.
        //     hasOptions (bool): Whether it offers options to decide on.
        //
        // Returns:
        //     string: The text.
        public static string IncidentSummary(Incident incident, bool hasOptions)
        {
            var ask = hasOptions
                ? "Decide for each shuttle that will reach it: keep its route, re-route it on the map, hold it at a stop before it, or request assistance (once, for every shuttle). Preview an option's consequences before approval; deciding before the shuttle gets there scores best."
                : "No operator decision is required for this incident now. Shuttles that reach it meet its queue.";
            return IncidentDescription(incident) + "\n" + ask;
        }

        // -----------------------------------------------------------------------------------
        // Function: ActionLabel
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function names an incident option or decision for people.
        //
        // Args:
        //     action (string): The contract's action.
        //
        // Returns:
        //     string: Its name.
        public static string ActionLabel(string action)
        {
            switch (action)
            {
                case "KEEP_ROUTE": case "WAIT": return "Keep route";
                case "ROAD_DETOUR": return "Road detour";
                case "ESCALATE": return "Request assistance";
                case "HOLD_AT_STOP": return "Hold at stop";
                case "NO_DECISION": return "No decision";
                default: return action;
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: Responses
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function lists what was decided for each shuttle about an incident: "shuttle-03
        //     request assistance, shuttle-01 no decision (late)".
        //
        // Args:
        //     incident (Incident): The incident.
        //
        // Returns:
        //     string: The list, or null when nothing has been decided.
        public static string Responses(Incident incident)
        {
            var responses = incident?.Responses ?? new List<IncidentResponse>();
            if (responses.Count == 0) return null;
            return string.Join(", ", responses.Select(r =>
                $"{r.VehicleId} {ActionLabel(r.Action).ToLowerInvariant()}{(r.BeforeArrival || r.Action == "NO_DECISION" ? "" : " (late)")}"));
        }

        // -----------------------------------------------------------------------------------
        // Function: IncidentHeadline
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function words the message shown when an incident a shuttle will reach is
        //     detected, or when another shuttle comes to be exposed to it. The backend writes the
        //     headline; other events, and an incident nobody reaches, show nothing.
        //
        // Args:
        //     e (Event): A live training event.
        //
        // Returns:
        //     string: The message, or null for none.
        public static string IncidentHeadline(Event e)
        {
            if (e?.EntityType != "INCIDENT" || e.Data == null) return null;
            var headline = e.Data.Value<string>("headline");
            if (string.IsNullOrEmpty(headline)) return null;
            if (e.EventType == "INCIDENT_DETECTED" && e.Data.Value<bool?>("alert") == true) return headline;
            if (e.EventType == "INCIDENT_EXPOSURE_CHANGED" && (e.Data["added_vehicle_ids"] as Newtonsoft.Json.Linq.JArray)?.Count > 0) return headline;
            return null;
        }

        // -----------------------------------------------------------------------------------
        // Function: SessionLabel
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function labels a session in the session list.
        //
        // Args:
        //     entry (SessionListEntry): The session.
        //
        // Returns:
        //     string: Its status and ID.
        public static string SessionLabel(SessionListEntry entry) => $"{entry.Status} · {entry.SessionId}";

        // -----------------------------------------------------------------------------------
        // Function: Capacity
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function summarises the session list: live sessions against capacity, the total,
        //     and how many are shown.
        //
        // Args:
        //     list (SessionListResponse): The session list.
        //
        // Returns:
        //     string: The summary.
        public static string Capacity(SessionListResponse list) =>
            $"{list.LiveCount}/{list.Capacity} live sessions · {list.Total} total" +
            (list.Sessions.Count < list.Total ? $" · showing {list.Sessions.Count}" : "");

        // -----------------------------------------------------------------------------------
        // Function: CanDelete
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function tells whether a session may be deleted: only an attempt that can never
        //     run again. The backend checks this too.
        //
        // Args:
        //     entry (SessionListEntry): The session, or null.
        //
        // Returns:
        //     bool: True when it may be deleted.
        public static bool CanDelete(SessionListEntry entry) =>
            entry != null && !entry.OccupiesCapacity && (entry.Status == "ARCHIVED" || entry.Status == "COMPLETED" || entry.Status == "FAILED");

        // -----------------------------------------------------------------------------------
        // Function: Reclaimed
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reports how much a database compaction reclaimed.
        //
        // Args:
        //     result (SessionCompaction): The compaction reply.
        //
        // Returns:
        //     string: The message.
        public static string Reclaimed(SessionCompaction result)
        {
            var text = $"Training database reduced from {Megabytes(result.DatabaseBytesBefore)} to {Megabytes(result.DatabaseBytesAfter)}.";
            return result.WalTruncated ? text : text + " Another reader held some space; try again shortly to release it.";
        }

        // -----------------------------------------------------------------------------------
        // Function: Megabytes
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function formats a size in megabytes.
        //
        // Args:
        //     bytes (long): The size in bytes.
        //
        // Returns:
        //     string: For example 12.5 MB.
        // -----------------------------------------------------------------------------------
        // Function: ArchivedAll
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reports what Archive all did: how many of the live sessions it found
        //     are no longer live.
        //
        // Args:
        //     live (int): How many sessions were live.
        //     archived (int): How many of those are no longer live.
        //
        // Returns:
        //     string: The message.
        public static string ArchivedAll(int live, int archived)
        {
            if (live == 0) return "No live sessions to archive.";
            if (archived == live) return $"Archived {Count(live, "live session")}.";
            return $"Archived {archived} of {Count(live, "live session")}; {Count(live - archived, "session")} " +
                   $"{(live - archived == 1 ? "is" : "are")} still live. Refresh the list and try again.";
        }

        // -----------------------------------------------------------------------------------
        // Function: PurgeCount
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function works out how many finished sessions a purge keeping the keep newest
        //     would delete: every session that is not live, less those kept.
        //
        // Args:
        //     list (SessionListResponse): The session list, for its counts.
        //     keep (int): How many of the newest finished sessions are kept.
        //
        // Returns:
        //     long: How many would be deleted.
        public static long PurgeCount(SessionListResponse list, int keep) => Math.Max(0, list.Total - list.LiveCount - keep);

        // -----------------------------------------------------------------------------------
        // Function: PurgeQuestion
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function asks before a purge, saying exactly how many sessions go and what is
        //     kept, or says there is nothing to delete.
        //
        // Args:
        //     list (SessionListResponse): The session list, for its counts.
        //     keep (int): How many of the newest finished sessions are kept.
        //
        // Returns:
        //     string: The question, or why there is nothing to delete.
        public static string PurgeQuestion(SessionListResponse list, int keep)
        {
            var deleted = PurgeCount(list, keep);
            var finished = list.Total - list.LiveCount;
            if (deleted == 0)
                return $"Nothing to delete: there {(finished == 1 ? "is" : "are")} {Count(finished, "finished session")}, " +
                       $"and the {keep} newest are always kept.";
            return $"Permanently delete the {Count(deleted, "oldest finished session")}? The {keep} newest finished sessions " +
                   "and every live session are kept. Their history is removed for everyone. This cannot be undone.";
        }

        // -----------------------------------------------------------------------------------
        // Function: Purged
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reports a purge.
        //
        // Args:
        //     deleted (long): How many sessions it deleted.
        //
        // Returns:
        //     string: The message.
        public static string Purged(long deleted) =>
            deleted == 0 ? "No old sessions to delete." : $"Deleted {Count(deleted, "old training session")}.";

        // A count with its noun, singular for one: "1 live session", "3 live sessions".
        private static string Count(long count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

        private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

        // -----------------------------------------------------------------------------------
        // Function: Outcome
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function says what happened to a command, in the Qt client's wording, with
        //     plainer messages for a kept-in-service shuttle, a reserved charger, and a Start
        //     refused because a route crosses a closed road.
        //
        // Args:
        //     outcome (CommandOutcome): The outcome.
        //
        // Returns:
        //     string: The message.
        public static string Outcome(CommandOutcome outcome)
        {
            switch (outcome.Status)
            {
                case "QUEUED":
                    return $"Decision {outcome.CommandId} for {outcome.TargetId} is QUEUED and durably accepted; awaiting authoritative outcome.";
                case "APPLIED":
                    if (outcome.CommandType == "SCHEDULE_CHARGE") return $"Charging slot booked on {outcome.TargetId}";
                    if (outcome.CommandType == "CANCEL_CHARGE_BOOKING") return $"Charging booking on {outcome.TargetId} cancelled";
                    if (outcome.TargetType == "VEHICLE") return $"Shuttle {outcome.TargetId} kept in service";
                    if (outcome.TargetType == "CHARGER") return $"Charger {outcome.TargetId} reserved";
                    return $"Decision {outcome.CommandId} APPLIED for {outcome.TargetId} at tick {outcome.AppliedTick?.ToString() ?? "unknown"}.";
                case "REJECTED":
                    if (outcome.TargetType == "SESSION" && outcome.FailureCode == "UNKNOWN_CONSTRAINT")
                        return "Can't start yet: a shuttle's route crosses a closed road. Approve a detour, then start again.";
                    if (outcome.CommandType == "SCHEDULE_CHARGE" || outcome.CommandType == "CANCEL_CHARGE_BOOKING")
                        return outcome.FailureCode == "STALE_REVISION" ? $"{outcome.TargetId} changed just now. Check the slot again."
                            : outcome.FailureCode == "CHARGER_CONFLICT" ? $"That slot on {outcome.TargetId} is already taken."
                            : $"The charging change was refused: {outcome.FailureCode ?? "no code"}.";
                    return $"Decision {outcome.CommandId} REJECTED for {outcome.TargetId}: {outcome.FailureCode ?? "no code"}.";
                case "NEEDS_RECONFIRMATION":
                    return $"Decision {outcome.CommandId} for {outcome.TargetId} needs reconfirmation after recovery; review and submit again.";
                case "EXPIRED":
                    return $"Decision {outcome.CommandId} for {outcome.TargetId} expired; refresh and choose a current option.";
                default:
                    return $"Decision {outcome.CommandId} returned status {outcome.Status} for {outcome.TargetId}.";
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: Number
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function formats a number with at most two decimals.
        //
        // Args:
        //     value (double): The number.
        //
        // Returns:
        //     string: The text.
        private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
