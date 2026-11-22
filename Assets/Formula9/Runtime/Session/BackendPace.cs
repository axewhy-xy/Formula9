/*
 * How fast the backend runs the joined session against the wall clock.
 *
 * Author: Lau Xiang Yang
 * Date Created: 17/11/2026
 * Last Modified: 17/11/2026
 *
 * Description:
 *     Part of the Unity client's session layer. A backend with more running sessions than its one
 *     core can tick falls behind the wall clock, and the session's simulation time then advances
 *     slower than real time; the 3D view, which follows the frames it receives, draws that as slow
 *     motion. This meter compares the simulation time of the snapshots the client receives with
 *     the time they arrived, over the last several seconds, so the screen can say "backend behind
 *     (0.6x)" instead of leaving the slow motion unexplained (docs/performance.md, section 5).
 */

using System.Collections.Generic;
using Formula9.Contracts;

namespace Formula9.Session
{
    // -----------------------------------------------------------------------------------
    // Class: BackendPace
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class measures the joined session's real-time factor: simulated seconds over wall
    //     seconds, from the snapshots received while it runs. It is known only once the snapshots
    //     span MinSpanS of wall time, and starts over whenever the session, its epoch or its
    //     running state changes, or its clock goes back.
    //
    //     Attributes: Factor, the real-time factor, or null while unknown; Behind, true when
    //     Factor is known and below BehindBelow.
    public sealed class BackendPace
    {
        // The window measured: long enough that a poll's network delay (some tens of
        // milliseconds) moves the factor by a percent or so, short enough to show a change soon.
        public const double WindowS = 10;
        public const double MinSpanS = 4;
        // Below this the backend is reported behind. Polling jitter alone stays well above it.
        public const double BehindBelow = 0.95;

        private readonly Queue<(double Wall, double Simulation)> samples = new Queue<(double, double)>();
        private (double Wall, double Simulation) last;
        private string sessionId;
        private long epoch = -1;

        public double? Factor { get; private set; }
        public bool Behind => Factor < BehindBelow;

        // -----------------------------------------------------------------------------------
        // Function: Observe
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function records an accepted snapshot.
        //
        // Args:
        //     snapshot (FullSnapshot): The snapshot; null when no session is joined.
        //     wallS (double): When it arrived, in seconds on any steady clock.
        public void Observe(FullSnapshot snapshot, double wallS)
        {
            if (snapshot == null || snapshot.Session?.Status != "RUNNING" || snapshot.SessionId != sessionId
                || snapshot.SessionEpoch != epoch)
            {
                Reset();
                if (snapshot?.Session?.Status != "RUNNING") return;
                sessionId = snapshot.SessionId;
                epoch = snapshot.SessionEpoch;
            }
            // A clock going back is a different run of the session, not a pace. A simulation time
            // that has not moved since the last poll counts: a stalled backend is the slowest.
            if (samples.Count > 0 && (snapshot.SimulationTimeS < last.Simulation || wallS < last.Wall)) Reset(keepSession: true);
            last = (wallS, snapshot.SimulationTimeS);
            samples.Enqueue(last);
            while (samples.Count > 2 && wallS - samples.Peek().Wall > WindowS) samples.Dequeue();
            var first = samples.Peek();
            var span = wallS - first.Wall;
            Factor = span >= MinSpanS ? (snapshot.SimulationTimeS - first.Simulation) / span : (double?)null;
        }

        // -----------------------------------------------------------------------------------
        // Function: Reset
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function forgets the measurement, as when the session is left or paused.
        //
        // Args:
        //     keepSession (bool): Keep the session and epoch measured.
        public void Reset(bool keepSession = false)
        {
            samples.Clear();
            Factor = null;
            if (keepSession) return;
            sessionId = null;
            epoch = -1;
        }
    }
}
