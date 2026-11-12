using System;
using System.Collections.Generic;
using Formula9.Backend;
using UnityEngine;

namespace Formula9.World
{
    // Drawing the traffic the backend drives. A session whose backend runs the traffic sends its frames
    // (TrainingApi.GetTrafficFramesAsync): where every vehicle on the roads is, and where each shuttle's
    // body is, a tenth of a second apart. While frames come, nothing here drives: the vehicles and the
    // shuttles are drawn where the frames say, at a time a little behind the newest frame kept by a
    // TrafficPlaybackClock, so there is nearly always a frame either side to draw between, and the lights
    // show their colours on the same clock. The backend's shuttles queue behind its vehicles, so what is
    // drawn never needs a shuttle to fall behind the backend. Without frames, as for a session whose
    // backend has no traffic, the traffic drives itself as before.
    //
    // Between two frames a body is drawn on a curve that leaves the first along its heading and meets the
    // second along its heading, with its heading turning smoothly through the frames before and after: so
    // at ten frames a second it rounds a bend instead of zigzagging along straight pieces, and keeps its
    // speed across frames. Where the backend puts a vehicle somewhere near in one step, as into the lane
    // of the road it turns onto, the drawn vehicle carries on from where it was and closes the gap over a
    // quarter of a second rather than jumping.
    public sealed partial class CityTrafficRenderer
    {
        // Frames kept at most: a few seconds at the backend's ten a second
        private const int FollowFrames = 64;
        // A vehicle further than this from where it is drawn, or between two frames, was put somewhere new
        private const float FollowJumpM = 25f;
        // A vehicle the frames move further along in a frame than its speed allows (by FollowStepSlackM),
        // or further sideways than FollowAsideMps does, was put somewhere near; what is drawn closes the
        // gap with a time constant of FollowSettleS
        private const float FollowStepSlackM = 0.25f, FollowAsideMps = 3f, FollowAsideSlackM = 0.05f, FollowSettleS = 0.25f;
        // Between two frames, a heading further off the way between them than this cosine (25 degrees) is
        // not followed along a curve: the body was moved sideways or turned about, not driven
        private const float FollowAlignment = 0.9f;

        private readonly List<BackendFrame> backendFrames = new List<BackendFrame>();
        private readonly Stack<BackendFrame> spareFrames = new Stack<BackendFrame>();
        private readonly TrafficPlaybackClock playback = new TrafficPlaybackClock();
        // Each drawn vehicle by its index in the traffic sample, which is how the frames name it
        private VehicleState[] vehiclesBySeed = Array.Empty<VehicleState>();
        // The frame at or before the drawn time, by index in backendFrames (-1 before any is drawn), and
        // how far the drawn time is from it to the next
        private int followIndex = -1;
        private float followShare;

        // True while the backend's frames drive what is drawn.
        public bool FollowsBackend => backendFrames.Count > 0;

        // The time on the session's clock that is drawn, while following the backend.
        public double FollowedS => playback.TimeS;

        // The clock the backend's frames are drawn on, for diagnostics.
        public TrafficPlaybackClock Playback => playback;

        // Whether the backend's frames have a junction's lights stuck on red by a signal fault at the drawn
        // time; false without frames, when the lights run their usual cycle.
        public bool SignalFaulted(string junctionId)
        {
            if (junctionId == null || followIndex < 0 || followIndex >= backendFrames.Count) return false;
            return backendFrames[followIndex].SignalFaults.TryGetValue(junctionId, out var until) && SignalClockS < until;
        }

        // Takes the backend's newest frames, as they arrive. Ones no newer than those it has are left out.
        public void FollowBackend(IReadOnlyList<TrafficFrame> frames) => FollowBackend(frames, Time.realtimeSinceStartupAsDouble);

        // As FollowBackend, given when the frames arrived in real seconds.
        public void FollowBackend(IReadOnlyList<TrafficFrame> frames, double arrivedS)
        {
            if (frames == null) return;
            foreach (var frame in frames)
            {
                if (frame == null || (backendFrames.Count > 0 && frame.SimulationTimeS <= backendFrames[backendFrames.Count - 1].TimeS)) continue;
                var made = spareFrames.Count > 0 ? spareFrames.Pop() : new BackendFrame();
                made.Fill(frame, vehiclesBySeed.Length);
                backendFrames.Add(made);
                playback.Add(made.TimeS, arrivedS);
            }
            var excess = backendFrames.Count - FollowFrames;
            if (excess <= 0) return;
            // Only frames well behind the drawn time are let go; a drawn time that far behind starts over
            if (excess > followIndex - 1)
            {
                followIndex = -1;
                playback.Restart();
            }
            DropOldestFrames(excess);
        }

        // Stops following the backend, as for another session: the traffic drives itself again from
        // where it last drove, and is put straight there.
        public void ForgetBackend()
        {
            DropOldestFrames(backendFrames.Count);
            playback.Reset();
            followIndex = -1;
            foreach (var vehicle in vehicles) vehicle.Placed = false;
        }

        // Where the backend's frames put a shuttle's body now, at this height; false without frames for it.
        public bool TryGetBackendShuttlePose(string id, float height, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;
            if (id == null || followIndex < 0 || followIndex >= backendFrames.Count) return false;
            var from = backendFrames[followIndex];
            if (!from.Shuttles.TryGetValue(id, out var b)) return false;
            var c = ShuttleIn(followIndex + 1, id, b, out var hasC) ?? b;
            var a = ShuttleIn(followIndex - 1, id, b, out var hasA);
            var d = hasC ? ShuttleIn(followIndex + 2, id, c, out _) : null;

            Between(b.At, b.Dir, c.At, c.Dir, followShare, out var at);
            var heading = HeadingBetween(b.HeadingDeg, c.HeadingDeg, hasA ? a.Value.HeadingDeg : (float?)null,
                d?.HeadingDeg, followShare) * Mathf.Deg2Rad;
            position = new Vector3(at.x, height, at.y);
            rotation = Quaternion.LookRotation(new Vector3(Mathf.Cos(heading), 0, Mathf.Sin(heading)), Vector3.up);
            return true;
        }

        // A shuttle's pose in the frame at index, if there is one and it follows on from near: null otherwise.
        private BodyPose? ShuttleIn(int index, string id, BodyPose near, out bool found)
        {
            found = index >= 0 && index < backendFrames.Count && backendFrames[index].Shuttles.TryGetValue(id, out var pose)
                    && (pose.At - near.At).sqrMagnitude <= FollowJumpM * FollowJumpM;
            return found ? backendFrames[index].Shuttles[id] : (BodyPose?)null;
        }

        // Moves the drawn time on and puts every vehicle where the frames either side of it say.
        private void Follow(float dt)
        {
            playback.Advance(dt);
            playback.HoldAtLeast(backendFrames[0].TimeS);
            var now = playback.TimeS;

            var index = 0;
            while (index + 1 < backendFrames.Count && backendFrames[index + 1].TimeS <= now) index++;
            // The drawn time never goes back: frames before the one before it are done with
            if (index > 1)
            {
                DropOldestFrames(index - 1);
                index = 1;
            }
            followIndex = index;
            var from = backendFrames[index];
            var to = index + 1 < backendFrames.Count ? backendFrames[index + 1] : from;
            var before = index > 0 ? backendFrames[index - 1] : null;
            var after = index + 2 < backendFrames.Count ? backendFrames[index + 2] : null;
            followShare = to.TimeS > from.TimeS ? Mathf.Clamp01((float)((now - from.TimeS) / (to.TimeS - from.TimeS))) : 0;
            // The lights show the colours the backend's vehicles and shuttles are stopping for
            SignalClockS = now;

            var settle = dt > 0 ? Mathf.Exp(-dt / FollowSettleS) : 1;
            var pace = Mathf.Max(1f, (float)playback.Speed);
            for (var seed = 0; seed < vehiclesBySeed.Length; seed++)
            {
                var vehicle = vehiclesBySeed[seed];
                if (vehicle == null) continue;
                if (!from.Has(seed))
                {
                    // Off the map in the backend, waiting to come in
                    vehicle.Placed = false;
                    vehicle.Speed = 0;
                    continue;
                }
                var b = from.Vehicle(seed);
                var continues = to.Has(seed) && (to.Vehicle(seed).At - b.At).sqrMagnitude <= FollowJumpM * FollowJumpM;
                var c = continues ? to.Vehicle(seed) : b;
                float? headingA = before != null && before.Has(seed) && (before.Vehicle(seed).At - b.At).sqrMagnitude <= FollowJumpM * FollowJumpM
                    ? before.Vehicle(seed).HeadingDeg : (float?)null;
                float? headingD = continues && after != null && after.Has(seed) && (after.Vehicle(seed).At - c.At).sqrMagnitude <= FollowJumpM * FollowJumpM
                    ? after.Vehicle(seed).HeadingDeg : (float?)null;

                Between(b.At, b.Dir, c.At, c.Dir, followShare, out var at);
                var heading = HeadingBetween(b.HeadingDeg, c.HeadingDeg, headingA, headingD, followShare) * Mathf.Deg2Rad;
                var speed = Mathf.Lerp(b.Speed, c.Speed, followShare);
                PlaceFollowed(vehicle, new Vector3(at.x, RoadMesh.SurfaceY + 0.02f, at.y),
                    new Vector3(Mathf.Cos(heading), 0, Mathf.Sin(heading)), speed, dt, settle, pace);
            }
        }

        // Draws a vehicle where the frames put it, closing smoothly on a place the backend moved it to.
        private static void PlaceFollowed(VehicleState vehicle, Vector3 target, Vector3 forward, float speed, float dt, float settle, float pace)
        {
            vehicle.Speed = speed;
            if (!vehicle.Placed || (vehicle.Position - target).sqrMagnitude > FollowJumpM * FollowJumpM)
            {
                PlaceAt(vehicle, target, forward);
                return;
            }
            if (dt <= 0) return;
            // How far the frames moved it since the last frame drawn: more than it could drive, it was put
            // there. That move is not drawn; the vehicle carries on at its speed instead, and the gap left
            // shrinks away. Only the frames' own moves are judged, so the gap shrinking is never mistaken
            // for another.
            var moved = target - vehicle.FollowTarget;
            vehicle.FollowTarget = target;
            vehicle.FollowOffset *= settle;
            var along = Vector3.Dot(moved, forward);
            var aside = (moved - forward * along).magnitude;
            if (Mathf.Abs(along) > Mathf.Max(speed, 1f) * pace * dt * 2f + FollowStepSlackM || aside > FollowAsideMps * pace * dt + FollowAsideSlackM)
                vehicle.FollowOffset -= moved - forward * (speed * pace * dt);
            if (vehicle.FollowOffset.sqrMagnitude > FollowJumpM * FollowJumpM)
            {
                PlaceAt(vehicle, target, forward);
                return;
            }
            vehicle.Position = target + vehicle.FollowOffset;
            var turnRate = Mathf.Max(speed / vehicle.TurnRadius, MinTurnRateRad) * 1.5f;
            vehicle.Forward = Vector3.RotateTowards(vehicle.Forward, forward, turnRate * dt, 0);
        }

        private static void PlaceAt(VehicleState vehicle, Vector3 target, Vector3 forward)
        {
            vehicle.Position = vehicle.FollowTarget = target;
            vehicle.Forward = forward;
            vehicle.FollowOffset = Vector3.zero;
            vehicle.Placed = true;
        }

        // -----------------------------------------------------------------------------------
        // Function: Between
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function is where a body is a share of the way from one frame's pose to the next:
        //     on a cubic curve leaving the first along its heading and meeting the second along its
        //     heading, each as long as the way between them, so a straight run stays straight and
        //     evenly paced and a bend is rounded. A heading well off the way between them, as for a
        //     body moved sideways or turned about, is not followed: it goes straight.
        //
        // Args:
        //     a, b (Vector2): The poses' places.
        //     aDir, bDir (Vector2): The poses' headings, of unit length.
        //     share (float): From 0 at a to 1 at b.
        //     at (Vector2): Where the body is.
        internal static void Between(Vector2 a, Vector2 aDir, Vector2 b, Vector2 bDir, float share, out Vector2 at)
        {
            var chord = b - a;
            var length = chord.magnitude;
            if (length < 0.01f || Vector2.Dot(aDir, chord) < FollowAlignment * length || Vector2.Dot(bDir, chord) < FollowAlignment * length)
            {
                at = Vector2.Lerp(a, b, share);
                return;
            }
            var s = share;
            var s2 = s * s;
            var s3 = s2 * s;
            at = (2 * s3 - 3 * s2 + 1) * a + (s3 - 2 * s2 + s) * length * aDir + (3 * s2 - 2 * s3) * b + (s3 - s2) * length * bDir;
        }

        // -----------------------------------------------------------------------------------
        // Function: HeadingBetween
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function is a heading a share of the way from one frame's to the next, on a
        //     Catmull-Rom curve through the headings of the frames before and after, where there are
        //     any: so it turns smoothly across frames, rather than at a steady rate within each and
        //     changing rate in steps between them.
        //
        // Args:
        //     b, c (float): The headings of the frames either side, in degrees.
        //     a, d (float?): The headings of the frames before b and after c, if any.
        //     share (float): From 0 at b to 1 at c.
        //
        // Returns:
        //     float: The heading in degrees.
        internal static float HeadingBetween(float b, float c, float? a, float? d, float share)
        {
            // Measured from b, the shorter way round
            var toC = Mathf.DeltaAngle(b, c);
            var toA = a.HasValue ? Mathf.DeltaAngle(b, a.Value) : -toC;
            var toD = d.HasValue ? toC + Mathf.DeltaAngle(c, d.Value) : 2 * toC;
            var tangentB = (toC - toA) / 2;
            var tangentC = toD / 2;
            var s = share;
            var s2 = s * s;
            var s3 = s2 * s;
            return b + (s3 - 2 * s2 + s) * tangentB + (3 * s2 - 2 * s3) * toC + (s3 - s2) * tangentC;
        }

        private void DropOldestFrames(int count)
        {
            count = Math.Min(count, backendFrames.Count);
            for (var i = 0; i < count; i++) spareFrames.Push(backendFrames[i]);
            backendFrames.RemoveRange(0, count);
            if (followIndex >= 0) followIndex = Math.Max(-1, followIndex - count);
        }

        private struct BodyPose
        {
            public Vector2 At, Dir;
            public float HeadingDeg, Speed;

            public static BodyPose Of(float x, float y, float headingDeg, float speed)
            {
                var heading = headingDeg * Mathf.Deg2Rad;
                return new BodyPose { At = new Vector2(x, y), Dir = new Vector2(Mathf.Cos(heading), Mathf.Sin(heading)), HeadingDeg = headingDeg, Speed = speed };
            }
        }

        // One of the backend's frames, with each vehicle by its index in the traffic sample. Frames are
        // kept and filled again, so following the backend makes no garbage a frame.
        private sealed class BackendFrame
        {
            public double TimeS;
            public readonly Dictionary<string, BodyPose> Shuttles = new Dictionary<string, BodyPose>();
            public readonly Dictionary<string, double> SignalFaults = new Dictionary<string, double>();
            private bool[] has = Array.Empty<bool>();
            private BodyPose[] poses = Array.Empty<BodyPose>();

            public bool Has(int seed) => seed < has.Length && has[seed];

            public BodyPose Vehicle(int seed) => poses[seed];

            public void Fill(TrafficFrame frame, int seeds)
            {
                TimeS = frame.SimulationTimeS;
                if (has.Length != seeds)
                {
                    has = new bool[seeds];
                    poses = new BodyPose[seeds];
                }
                else Array.Clear(has, 0, has.Length);
                var v = frame.Vehicles ?? Array.Empty<int>();
                for (var i = 0; i + TrafficFrame.Stride <= v.Length; i += TrafficFrame.Stride)
                {
                    var seed = v[i];
                    if (seed < 0 || seed >= seeds) continue;
                    has[seed] = true;
                    poses[seed] = BodyPose.Of(v[i + 1] / 100f, v[i + 2] / 100f, v[i + 3] / 10f, v[i + 4] / 100f);
                }
                SignalFaults.Clear();
                if (frame.SignalFaults != null)
                    foreach (var fault in frame.SignalFaults) SignalFaults[fault.Key] = fault.Value;
                Shuttles.Clear();
                if (frame.Shuttles == null) return;
                foreach (var shuttle in frame.Shuttles)
                    if (shuttle?.VehicleId != null)
                        Shuttles[shuttle.VehicleId] = BodyPose.Of((float)shuttle.XM, (float)shuttle.YM, (float)shuttle.HeadingDeg, (float)shuttle.SpeedMps);
            }
        }
    }
}
