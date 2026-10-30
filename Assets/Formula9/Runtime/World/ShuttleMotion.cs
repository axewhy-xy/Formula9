/*
 * Moves the 3D view's shuttles smoothly between the backend's snapshots.
 *
 * Author: Lau Xiang Yang
 * Date Created: 31/10/2026
 * Last Modified: 31/10/2026
 *
 * Description:
 *     Part of the Unity client's 3D world. Snapshots arrive far less often than frames are drawn,
 *     so each one starts a glide from where the shuttle is shown to where the backend says it is,
 *     lasting about as long as the gap between snapshots. Between stops that gives a steady speed
 *     at any snapshot rate. It only ever moves towards an authoritative pose: it never predicts past
 *     one, follows the route around a bend where it can, and jumps across a discontinuity.
 */

using System.Collections.Generic;
using System.Linq;
using Formula9.Contracts;
using UnityEngine;

namespace Formula9.World
{
    // -----------------------------------------------------------------------------------
    // Class: ShuttleMotion
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class holds one glide per shuttle, sampled once per rendered frame.
    //
    //     Attributes: Interval, the smoothed gap between snapshots in seconds.
    public sealed class ShuttleMotion
    {
        private const float DefaultInterval = 1, MinInterval = 0.02f, MaxInterval = 2, IntervalBlend = 0.3f;
        // Slightly longer than the gap, so a late snapshot does not stop the shuttle. It settles at
        // the true speed, about a tenth of a gap behind.
        private const float GlideSlack = 1.1f;
        // Further than a shuttle could have driven: it was moved, so do not sweep it across the map
        private const float SnapDistanceM = 200;
        // A pose this far from the route is not on it, and a route this much longer than the straight
        // line is the wrong part of it (it passes here twice)
        private const float MaxOffRouteM = 8, MaxPathStretch = 3, PathStretchAllowanceM = 2;

        private sealed class Track
        {
            public Vector3[] Path;
            public Quaternion FromRotation, TargetRotation;
            public Vector3 Target;
            public float Start, Duration;
        }

        private readonly Dictionary<string, Track> tracks = new Dictionary<string, Track>();
        private long? lastTick;
        private float lastSnapshotAt;

        public float Interval { get; private set; } = DefaultInterval;

        // -----------------------------------------------------------------------------------
        // Function: BeginSnapshot
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function notes when a snapshot arrived, to learn the gap between them. The same
        //     tick shown again and the long gap of a pause or a lost window focus teach it nothing.
        //
        // Args:
        //     tick (long): The snapshot's tick.
        //     now (float): The current time in seconds.
        public void BeginSnapshot(long tick, float now)
        {
            if (tick == lastTick) return;
            var gap = now - lastSnapshotAt;
            if (lastTick != null && gap >= MinInterval && gap <= MaxInterval) Interval = Mathf.Lerp(Interval, gap, IntervalBlend);
            lastTick = tick;
            lastSnapshotAt = now;
        }

        // -----------------------------------------------------------------------------------
        // Function: SetTarget
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function starts a shuttle's glide to its new pose from where it is shown now. A
        //     new shuttle, a discontinuity or a jump too long to have been driven goes there at once.
        //     The same pose again leaves the current glide running.
        //
        // Args:
        //     id (string): The shuttle.
        //     position (Vector3): The pose's position in Unity space.
        //     rotation (Quaternion): The pose's rotation in Unity space.
        //     now (float): The current time in seconds.
        //     route (IReadOnlyList<Vector2>): The shuttle's route as Unity (x, z), or null.
        //     discontinuity (bool): True when the backend says the shuttle did not drive here.
        public void SetTarget(string id, Vector3 position, Quaternion rotation, float now, IReadOnlyList<Vector2> route = null, bool discontinuity = false)
        {
            if (tracks.TryGetValue(id, out var track) && track.Target == position && track.TargetRotation == rotation) return;
            var shown = position;
            var shownRotation = rotation;
            var glide = track != null && !discontinuity && Sample(id, now, out shown, out shownRotation)
                && Vector3.Distance(shown, position) <= SnapDistanceM;
            if (track == null) tracks[id] = track = new Track();
            track.Path = glide ? PathBetween(route, shown, position) : new[] { position };
            track.FromRotation = glide ? shownRotation : rotation;
            track.Target = position;
            track.TargetRotation = rotation;
            track.Start = now;
            track.Duration = glide ? Interval * GlideSlack : 0;
        }

        // -----------------------------------------------------------------------------------
        // Function: Sample
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function gives the pose to draw a shuttle with at a moment. Once the glide ends
        //     the shuttle holds its last authoritative pose until another arrives.
        //
        // Args:
        //     id (string): The shuttle.
        //     now (float): The current time in seconds.
        //     position (Vector3): The position to draw.
        //     rotation (Quaternion): The rotation to draw.
        //
        // Returns:
        //     bool: False when the shuttle has no pose yet.
        public bool Sample(string id, float now, out Vector3 position, out Quaternion rotation)
        {
            position = default; rotation = Quaternion.identity;
            if (!tracks.TryGetValue(id, out var track)) return false;
            var progress = track.Duration <= 0 ? 1 : Mathf.Clamp01((now - track.Start) / track.Duration);
            position = PointAlong(track.Path, progress);
            rotation = Quaternion.Slerp(track.FromRotation, track.TargetRotation, progress);
            return true;
        }

        // -----------------------------------------------------------------------------------
        // Function: Clear
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function forgets every glide, so the next pose of each shuttle is placed at once.
        //     For a new session or epoch, a rebuilt world or a view that was not being updated.
        public void Clear() => tracks.Clear();

        // -----------------------------------------------------------------------------------
        // Function: Routes
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reads the snapshot's route lines as Unity (x, z), for SetTarget.
        //
        // Args:
        //     snapshot (FullSnapshot): The snapshot.
        //
        // Returns:
        //     Dictionary<string, List<Vector2>>: The points of each route, by route ID.
        public static Dictionary<string, List<Vector2>> Routes(FullSnapshot snapshot)
        {
            var routes = new Dictionary<string, List<Vector2>>();
            foreach (var route in snapshot.Routes)
                routes[route.RouteId] = route.EdgePathsM.SelectMany(p => p).Select(p => new Vector2((float)p.XM, (float)p.YM)).ToList();
            return routes;
        }

        // -----------------------------------------------------------------------------------
        // Function: PathBetween
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function finds the way from one pose to the next: along the route through any
        //     corners between them, in either direction, since a shuttle may be coming back along
        //     the same road. Where the route does not explain the move it is a straight line.
        //
        // Args:
        //     route (IReadOnlyList<Vector2>): The route as Unity (x, z), or null.
        //     from (Vector3): The starting position.
        //     to (Vector3): The final position.
        //
        // Returns:
        //     Vector3[]: The points to travel through, starting at from and ending at to.
        public static Vector3[] PathBetween(IReadOnlyList<Vector2> route, Vector3 from, Vector3 to)
        {
            var direct = new[] { from, to };
            if (route == null || route.Count < 2) return direct;
            var start = Nearest(route, new Vector2(from.x, from.z));
            var end = Nearest(route, new Vector2(to.x, to.z));
            if (start.Distance > MaxOffRouteM || end.Distance > MaxOffRouteM || start.Segment == end.Segment) return direct;
            var points = new List<Vector3> { from };
            if (start.Segment < end.Segment) for (var i = start.Segment + 1; i <= end.Segment; i++) points.Add(new Vector3(route[i].x, 0, route[i].y));
            else for (var i = start.Segment; i > end.Segment; i--) points.Add(new Vector3(route[i].x, 0, route[i].y));
            points.Add(to);
            var length = 0f;
            for (var i = 1; i < points.Count; i++) length += Vector3.Distance(Flat(points[i - 1]), Flat(points[i]));
            if (length > Vector3.Distance(Flat(from), Flat(to)) * MaxPathStretch + PathStretchAllowanceM) return direct;
            // The route has no heights, so the height changes evenly over the distance travelled
            var travelled = 0f;
            for (var i = 1; i < points.Count - 1; i++)
            {
                travelled += Vector3.Distance(Flat(points[i - 1]), Flat(points[i]));
                points[i] = new Vector3(points[i].x, Mathf.Lerp(from.y, to.y, length > 0 ? travelled / length : 1), points[i].z);
            }
            return points.ToArray();
        }

        private static Vector3 Flat(Vector3 point) => new Vector3(point.x, 0, point.z);

        private static (int Segment, float Distance) Nearest(IReadOnlyList<Vector2> route, Vector2 point)
        {
            var best = (Segment: 0, Distance: float.MaxValue);
            for (var i = 0; i + 1 < route.Count; i++)
            {
                var along = route[i + 1] - route[i];
                var squared = along.sqrMagnitude;
                if (squared <= 0) continue;
                var distance = Vector2.Distance(point, route[i] + along * Mathf.Clamp01(Vector2.Dot(point - route[i], along) / squared));
                if (distance < best.Distance) best = (i, distance);
            }
            return best;
        }

        private static Vector3 PointAlong(Vector3[] path, float progress)
        {
            if (path.Length == 1 || progress >= 1) return path[path.Length - 1];
            var total = 0f;
            for (var i = 1; i < path.Length; i++) total += Vector3.Distance(path[i - 1], path[i]);
            var remaining = total * progress;
            for (var i = 1; i < path.Length; i++)
            {
                var step = Vector3.Distance(path[i - 1], path[i]);
                if (remaining <= step && step > 0) return Vector3.Lerp(path[i - 1], path[i], remaining / step);
                remaining -= step;
            }
            return path[path.Length - 1];
        }
    }
}
