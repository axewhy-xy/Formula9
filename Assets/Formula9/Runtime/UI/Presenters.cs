using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.Session;

namespace Formula9.UI
{
    // What the login screen says for each outcome. Same wording as the Qt LoginPage.
    public static class LoginMessages
    {
        public const string SignUpHint = "Create an account to get started. Usernames are 3 to 32 letters, numbers, dots, " +
                                         "dashes or underscores; passwords need at least 8 characters.";
        public const string Expired = "Your login has expired. Log in again to continue; your training session is kept.";

        // Checks the form before anything is sent. Returns null when it may be submitted.
        public static string Validate(string username, string password, bool signUp, string confirmation)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) return "Enter your username and password.";
            if (signUp && password != confirmation) return "Passwords do not match.";
            return null;
        }

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

    public readonly struct ScenarioChoice
    {
        public readonly string Label, Id, Description;
        public ScenarioChoice(string label, string id, string description) { Label = label; Id = id; Description = description; }
    }

    public readonly struct SessionControls
    {
        public readonly bool Start, Pause, Resume, Archive;
        public readonly string StartHint;
        public SessionControls(bool start, bool pause, bool resume, bool archive, string startHint)
        {
            Start = start; Pause = pause; Resume = resume; Archive = archive; StartHint = startHint;
        }
    }

    // One shuttle's card in the fleet panel.
    public sealed class FleetRow
    {
        public string ShuttleId, State, Status, Route, RouteLine, Battery, Provenance, NextStop, Eta;
        // A line for the shuttle's open incident; null when it has none.
        public string Incident;
        public double BatteryPercent;
        // Its position among the sorted shuttles, which picks its colour on the map and the card.
        public int Index;
    }

    // Everything the training screen shows, derived from the accepted snapshot. Port of the Qt
    // TrainingWorkspace's refreshStatus, refreshViews and showCommandResult.
    public static class TrainingPresenter
    {
        // The 3D exercise is first, as in the Qt client.
        public static readonly IReadOnlyList<ScenarioChoice> Scenarios = new[]
        {
            new ScenarioChoice("3D fleet exercise", "scenario-m2-road-3d-r1", "Five shuttles on a road and ramp."),
            new ScenarioChoice("Singapore, real streets (2D)", SessionController.IntegratedScenario, "Real streets and LTA bus stops, shuttles running from a bus interchange, with incidents, detours and charging."),
        };

        public static string ScenarioTitle(string scenarioId) => scenarioId == SessionController.IntegratedScenario
            ? "Singapore, real streets · incidents, detours, charging"
            : "3D fleet exercise · road, ramp and obstruction";

        public const string NoSession = "No training session selected";

        public static string Title(FullSnapshot snapshot) => snapshot.Spatial != null
            ? "3D fleet exercise · road, ramp and obstruction"
            : "Singapore, real streets · incidents, detours, charging";

        public static string StatusLine(FullSnapshot snapshot, bool available) =>
            $"Session {snapshot.Session.SessionId} · epoch {snapshot.Session.SessionEpoch} · snapshot {snapshot.SnapshotId} · " +
            $"tick {snapshot.Tick} · {snapshot.Session.Status} · {(available ? "current" : "stale/offline")}";

        public static SessionControls Controls(FullSnapshot snapshot, bool available, bool spatialReady)
        {
            if (snapshot == null) return new SessionControls(false, false, false, false, null);
            var status = snapshot.Session.Status;
            var routeClosed = AssignedRouteIsClosed(snapshot);
            var needsGeometry = snapshot.Spatial != null && !spatialReady;
            string hint = null;
            if (needsGeometry) hint = "Required 3D geometry must render before starting this scenario.";
            else if (routeClosed && status == "READY") hint = "A shuttle's route crosses a closed road. Approve a detour first.";
            return new SessionControls(
                available && !needsGeometry && status == "READY" && !routeClosed,
                available && status == "RUNNING",
                available && status == "PAUSED",
                available && (status == "READY" || status == "RUNNING" || status == "PAUSED"),
                hint);
        }

        // The backend refuses START while an assigned route crosses an active closure.
        public static bool AssignedRouteIsClosed(FullSnapshot snapshot)
        {
            var closed = new HashSet<string>(snapshot.Disruptions
                .Where(d => d.ActiveFromTick <= snapshot.Tick && (d.ActiveUntilTick == null || snapshot.Tick < d.ActiveUntilTick))
                .SelectMany(d => d.AffectedEdgeIds));
            var routes = snapshot.Routes.ToDictionary(r => r.RouteId);
            return snapshot.Vehicles.Any(v => v.RouteId != null && routes.TryGetValue(v.RouteId, out var route)
                                              && route.DirectedEdgeIds.Any(closed.Contains));
        }

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

        // An unknown state is shown as sent, not guessed.
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

        private static string NextStopLine(FleetVehicle vehicle, RouteRevision route)
        {
            if (vehicle.RouteId == null) return "Next stop —";
            if (route == null) return "Next stop unknown";
            if (route.StopsM.Count == 0) return "No stops on this route";
            var next = NextStop(route, vehicle.Position.XM, vehicle.Position.YM);
            return next == null ? "Next stop unknown" : $"Next stop {next}";
        }

        // The first stop ahead along the route, or the route's first stop once past them all.
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

        private static string EtaLine(EnergyForecast forecast)
        {
            if (forecast?.NextDepotEtaS == null) return "ETA —";
            var seconds = forecast.NextDepotEtaS.Value;
            return $"Depot {forecast.NextDepotId} ETA {(seconds < 60 ? $"{Math.Round(seconds)} s" : $"{Math.Round(seconds / 60)} min")}";
        }

        // The selected incident when it is on this shuttle, otherwise the most recent one.
        private static string IncidentLine(FullSnapshot snapshot, string vehicleId, string selected)
        {
            var open = snapshot.Incidents.Where(i => i.VehicleId == vehicleId && i.State != "RESOLVED").ToList();
            if (open.Count == 0) return null;
            var shown = open.FirstOrDefault(i => i.IncidentId == selected) ?? open.OrderByDescending(i => i.DetectedTick).First();
            var state = shown.State.ToLowerInvariant().Replace('_', ' ');
            var text = $"! Incident {shown.IncidentId} · {char.ToUpperInvariant(state[0])}{state.Substring(1)}";
            if (open.Count > 1) text += $" (+{open.Count - 1} more)";
            return shown.IncidentId == selected ? text + " · selected" : text;
        }

        public static string SessionLabel(SessionListEntry entry) => $"{entry.Status} · {entry.SessionId}";

        public static string Capacity(SessionListResponse list) =>
            $"{list.LiveCount}/{list.Capacity} live sessions · {list.Sessions.Count} total";

        public static string Outcome(CommandOutcome outcome)
        {
            switch (outcome.Status)
            {
                case "QUEUED":
                    return $"Decision {outcome.CommandId} for {outcome.TargetId} is QUEUED and durably accepted; awaiting authoritative outcome.";
                case "APPLIED":
                    if (outcome.TargetType == "VEHICLE") return $"Shuttle {outcome.TargetId} kept in service";
                    if (outcome.TargetType == "CHARGER") return $"Charger {outcome.TargetId} reserved";
                    return $"Decision {outcome.CommandId} APPLIED for {outcome.TargetId} at tick {outcome.AppliedTick?.ToString() ?? "unknown"}.";
                case "REJECTED":
                    if (outcome.TargetType == "SESSION" && outcome.FailureCode == "UNKNOWN_CONSTRAINT")
                        return "Can't start yet: a shuttle's route crosses a closed road. Approve a detour, then start again.";
                    return $"Decision {outcome.CommandId} REJECTED for {outcome.TargetId}: {outcome.FailureCode ?? "no code"}.";
                case "NEEDS_RECONFIRMATION":
                    return $"Decision {outcome.CommandId} for {outcome.TargetId} needs reconfirmation after recovery; review and submit again.";
                case "EXPIRED":
                    return $"Decision {outcome.CommandId} for {outcome.TargetId} expired; refresh and choose a current option.";
                default:
                    return $"Decision {outcome.CommandId} returned status {outcome.Status} for {outcome.TargetId}.";
            }
        }

        private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
