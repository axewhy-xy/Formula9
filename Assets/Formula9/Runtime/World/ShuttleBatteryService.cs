/*
 * Runs the shuttle battery monitor for the app and tells the operator what it finds.
 *
 * Author: Ang Yu Xuan
 * Date Created: 23/11/2026
 * Last Modified: 24/11/2026
 *
 * Description:
 *     The monitor needs the road network of the city area the session is in. It is fetched with the
 *     first snapshot (the user is signed in by then) and again if a later snapshot is in another area.
 *     Nothing in a snapshot names its area yet, so the caller says how to find it; today every
 *     session is Toa Payoh. A failed fetch is tried again after a pause, not on every snapshot.
 *     Its warnings go to the message bar; what it cannot do (closures that match no road) goes to
 *     the Console. The backend names closed roads by its own edge IDs, so when a closure appears this
 *     class fetches those edges' geometry from the route editor and hands it to the monitor, which
 *     finds the city roads along it. Formula9App only has to create this class.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.Session;
using UnityEngine;

namespace Formula9.World
{
    // -----------------------------------------------------------------------------------
    // Class: ShuttleBatteryService
    // -----------------------------------------------------------------------------------
    public sealed class ShuttleBatteryService
    {
        // How long to wait before asking again after a failed request (the roads, or the closed edges' geometry)
        private const float RetrySeconds = 30f;

        private readonly SessionController session;
        private readonly TrainingApi api;
        private readonly CityClient cityClient;
        private readonly Func<FullSnapshot, string> areaFor;
        private readonly Action<string> notify;
        private ShuttleBatteryMonitor monitor;
        private string loadedArea;
        private bool loading;
        private float nextAttempt;
        private float retryClosuresAt;   // when to ask again for closed edges whose lookup failed; 0 for never

        // Null until the roads have loaded
        public ShuttleBatteryMonitor Monitor => monitor;

        // -----------------------------------------------------------------------------------
        // Function: ShuttleBatteryService (constructor)
        // -----------------------------------------------------------------------------------
        // Args:
        //     session (SessionController): Whose accepted snapshots are checked.
        //     api (TrainingApi): Where the geometry of the backend's edges comes from.
        //     cityClient (CityClient): Where the roads come from.
        //     areaFor (Func<FullSnapshot, string>): The city area a snapshot's session is in, as the 3D city
        //     names it. Its roads are loaded when it differs from the one loaded.
        //     notify (Action<string>): Shows a message to the operator.
        public ShuttleBatteryService(SessionController session, TrainingApi api, CityClient cityClient, Func<FullSnapshot, string> areaFor, Action<string> notify)
        {
            this.session = session; this.api = api; this.cityClient = cityClient; this.areaFor = areaFor; this.notify = notify;
            session.SnapshotAccepted += (snapshot, _) => OnSnapshot(snapshot);
        }

        private void OnSnapshot(FullSnapshot snapshot)
        {
            // The roads of another area replace these once loaded; until then the monitor keeps watching
            var area = areaFor(snapshot);
            if (!loading && area != null && area != loadedArea && Time.realtimeSinceStartup >= nextAttempt) _ = Start(area);
            monitor?.OnSnapshot(snapshot);
            // A closure lookup that failed is tried again after a pause
            if (retryClosuresAt > 0 && Time.realtimeSinceStartup >= retryClosuresAt)
            {
                retryClosuresAt = 0;
                monitor?.RetryClosures();
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: ReturnMessage
        // -----------------------------------------------------------------------------------
        // Description:
        //     The warning for a shuttle that should head for a depot, worded for why it does: its own
        //     return point was reached, its gauge turned red first (so there is no "needs" to quote),
        //     or the distance is not known.
        public static string ReturnMessage(BatteryAssessment a)
        {
            if (!a.DistanceKnown) return $"{a.VehicleId}: return to the depot now. {a.BatteryKwh:0.0} kWh left.";
            if (a.BatteryKwh <= a.ThresholdKwh)
                return $"{a.VehicleId}: return to the depot now. {a.DistanceToDepotM:0} m away, {a.BatteryKwh:0.0} kWh left, needs {a.ThresholdKwh:0.0}.";
            return $"{a.VehicleId}: battery low. Return to the depot, {a.DistanceToDepotM:0} m away.";
        }

        // The geometry of closed backend edges: the route editor lists each route's edges with their paths.
        // Edges no route lists get an empty answer, which the monitor takes as final and reports. A lookup
        // that fails (a network error, say) is a different thing: those edges are asked for again later.
        private async Task LoadEdgePaths(ShuttleBatteryMonitor target, IReadOnlyCollection<string> ids)
        {
            var found = new Dictionary<string, Vector2[]>();
            var failed = false;
            try
            {
                var snapshot = session.Store.Snapshot;
                var wanted = new HashSet<string>(ids);
                foreach (var route in snapshot?.Routes.Select(r => r.RouteId).ToList() ?? new List<string>())
                {
                    try
                    {
                        var context = await api.GetRouteEditorAsync(snapshot.SessionId, route);
                        foreach (var edge in context.Edges)
                            if (wanted.Contains(edge.EdgeId) && edge.Path != null && edge.Path.Count >= 2 && !found.ContainsKey(edge.EdgeId))
                                found[edge.EdgeId] = edge.Path.Select(p => new Vector2((float)p.X, (float)p.Y)).ToArray();
                    }
                    // Not every route has roads to edit in every scenario: that is an answer, not a failure
                    catch (BackendErrorException error) when (error.Error.Code == "INVALID_REFERENCE") { }
                    catch (Exception error)
                    {
                        failed = true;
                        Debug.LogWarning("[BatteryMonitor] Could not load the geometry of closed roads: " + error.Message);
                    }
                }
            }
            catch (Exception error)
            {
                failed = true;
                Debug.LogWarning("[BatteryMonitor] Could not load the geometry of closed roads: " + error.Message);
            }
            // What was not found because a request failed, as opposed to not being listed
            var unanswered = failed ? ids.Where(id => !found.ContainsKey(id)).ToList() : null;
            target.SetEdgePaths(found, unanswered);
            if (unanswered != null && unanswered.Count > 0) retryClosuresAt = Time.realtimeSinceStartup + RetrySeconds;
        }

        private async Task Start(string area)
        {
            loading = true;
            try
            {
                var roads = await cityClient.GetRoadsAsync(area);
                var created = new ShuttleBatteryMonitor(new DepotRoadGraph(roads));
                created.ReturnRequired += a => notify(ReturnMessage(a));
                created.Critical += a => notify($"{a.VehicleId}: cannot reach a depot on its charge.");
                created.Diagnostic += message => Debug.LogWarning("[BatteryMonitor] " + message);
                created.EdgePathsNeeded += ids => _ = LoadEdgePaths(created, ids);
                monitor = created;
                loadedArea = area;
            }
            catch (Exception error)
            {
                Debug.LogWarning("Battery monitor is off, no road network: " + error.Message);
                nextAttempt = Time.realtimeSinceStartup + RetrySeconds;
            }
            finally { loading = false; }
        }
    }
}
