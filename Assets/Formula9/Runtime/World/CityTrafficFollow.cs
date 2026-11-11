using System;
using System.Collections.Generic;
using Formula9.Backend;
using UnityEngine;

namespace Formula9.World
{
    // Drawing the traffic the backend drives. A session whose backend runs the traffic sends its frames
    // (TrainingApi.GetTrafficFramesAsync): where every vehicle on the roads is, and where each shuttle's
    // body is, a tenth of a second apart. While frames come, nothing here drives: the vehicles and the
    // shuttles are drawn where the frames say, a little behind the newest so there is nearly always a
    // frame either side to draw between, and the lights show their colours on the same clock. The backend's
    // shuttles queue behind its vehicles, so what is drawn never needs a shuttle to fall behind the backend.
    // Without frames, as for a session whose backend has no traffic, the traffic drives itself as before.
    public sealed partial class CityTrafficRenderer
    {
        // The view runs this far behind the newest frame
        private const double FollowDelayS = 0.35;
        // Further behind than this, or ahead of the newest frame, it is put back at the delay
        private const double FollowMaxBehindS = 1.5;
        // How quickly it closes on the delay when it has drifted, as a share a second
        private const double FollowCatchUpRate = 0.5;
        // Frames kept: a few seconds at the backend's ten a second
        private const int FollowFrames = 40;
        // A vehicle further than this from where it is drawn, or between two frames, was put somewhere new
        private const float FollowJumpM = 25f;

        private readonly List<BackendFrame> backendFrames = new List<BackendFrame>();
        // Each drawn vehicle by its index in the traffic sample, which is how the frames name it
        private readonly Dictionary<int, VehicleState> vehiclesBySeed = new Dictionary<int, VehicleState>();
        private double followS = double.NaN;
        private BackendFrame followFrom, followTo;
        private float followShare;

        // True while the backend's frames drive what is drawn.
        public bool FollowsBackend => backendFrames.Count > 0;

        // The time on the session's clock that is drawn, while following the backend.
        public double FollowedS => followS;

        // Takes the backend's newest frames. Ones no newer than those it has are left out.
        public void FollowBackend(IReadOnlyList<TrafficFrame> frames)
        {
            if (frames == null) return;
            foreach (var frame in frames)
            {
                if (frame == null || (backendFrames.Count > 0 && frame.SimulationTimeS <= backendFrames[backendFrames.Count - 1].TimeS)) continue;
                var made = new BackendFrame { TimeS = frame.SimulationTimeS };
                var v = frame.Vehicles ?? Array.Empty<int>();
                for (var i = 0; i + TrafficFrame.Stride <= v.Length; i += TrafficFrame.Stride)
                    made.Vehicles[v[i]] = new BodyPose { At = new Vector2(v[i + 1] / 100f, v[i + 2] / 100f), HeadingDeg = v[i + 3] / 10f, Speed = v[i + 4] / 100f };
                foreach (var shuttle in frame.Shuttles ?? new List<TrafficShuttlePose>())
                    if (shuttle?.VehicleId != null)
                        made.Shuttles[shuttle.VehicleId] = new BodyPose { At = new Vector2((float)shuttle.XM, (float)shuttle.YM), HeadingDeg = (float)shuttle.HeadingDeg, Speed = (float)shuttle.SpeedMps };
                backendFrames.Add(made);
            }
            while (backendFrames.Count > FollowFrames) backendFrames.RemoveAt(0);
        }

        // Stops following the backend, as for another session: the traffic drives itself again from
        // where it last drove, and is put straight there.
        public void ForgetBackend()
        {
            backendFrames.Clear();
            followS = double.NaN;
            followFrom = followTo = null;
            foreach (var vehicle in vehicles) vehicle.Placed = false;
        }

        // Where the backend's frames put a shuttle's body now, at this height; false without frames for it.
        public bool TryGetBackendShuttlePose(string id, float height, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;
            if (followFrom == null || id == null || !followFrom.Shuttles.TryGetValue(id, out var from)) return false;
            var to = followTo != null && followTo.Shuttles.TryGetValue(id, out var next) && (next.At - from.At).sqrMagnitude <= FollowJumpM * FollowJumpM ? next : from;
            var at = Vector2.Lerp(from.At, to.At, followShare);
            var heading = Mathf.LerpAngle(from.HeadingDeg, to.HeadingDeg, followShare) * Mathf.Deg2Rad;
            position = new Vector3(at.x, height, at.y);
            rotation = Quaternion.LookRotation(new Vector3(Mathf.Cos(heading), 0, Mathf.Sin(heading)), Vector3.up);
            return true;
        }

        // Moves the drawn time on and puts every vehicle where the frames either side of it say. A
        // vehicle closes on that place as UpdatePose does, so a jump between lanes at a junction is
        // steered across rather than snapped; one put somewhere new is drawn there at once.
        private void Follow(float dt)
        {
            var latest = backendFrames[backendFrames.Count - 1].TimeS;
            var wanted = latest - FollowDelayS;
            if (double.IsNaN(followS) || followS > latest || followS < latest - FollowMaxBehindS)
                followS = Math.Max(backendFrames[0].TimeS, wanted);
            else
            {
                followS += dt;
                followS += (wanted - followS) * Math.Min(1.0, dt * FollowCatchUpRate);
                followS = Math.Min(followS, latest);
            }
            // The lights show the colours the backend's vehicles and shuttles are stopping for
            SignalClockS = followS;

            var after = backendFrames.FindIndex(f => f.TimeS > followS);
            if (after < 0) followFrom = followTo = backendFrames[backendFrames.Count - 1];
            else if (after == 0) followFrom = followTo = backendFrames[0];
            else
            {
                followFrom = backendFrames[after - 1];
                followTo = backendFrames[after];
                // Older frames than the one drawn from are done with
                if (after > 1) backendFrames.RemoveRange(0, after - 1);
            }
            followShare = followTo.TimeS > followFrom.TimeS ? (float)((followS - followFrom.TimeS) / (followTo.TimeS - followFrom.TimeS)) : 0;

            foreach (var pair in vehiclesBySeed)
            {
                var vehicle = pair.Value;
                if (!followFrom.Vehicles.TryGetValue(pair.Key, out var from))
                {
                    // Off the map in the backend, waiting to come in
                    vehicle.Placed = false;
                    vehicle.Speed = 0;
                    continue;
                }
                var to = followTo.Vehicles.TryGetValue(pair.Key, out var next) && (next.At - from.At).sqrMagnitude <= FollowJumpM * FollowJumpM ? next : from;
                var at = Vector2.Lerp(from.At, to.At, followShare);
                var heading = Mathf.LerpAngle(from.HeadingDeg, to.HeadingDeg, followShare) * Mathf.Deg2Rad;
                var target = new Vector3(at.x, RoadMesh.SurfaceY + 0.02f, at.y);
                var forward = new Vector3(Mathf.Cos(heading), 0, Mathf.Sin(heading));
                vehicle.Speed = Mathf.Lerp(from.Speed, to.Speed, followShare);
                if (!vehicle.Placed || (vehicle.Position - target).sqrMagnitude > FollowJumpM * FollowJumpM)
                {
                    vehicle.Position = target;
                    vehicle.Forward = forward;
                    vehicle.Placed = true;
                    continue;
                }
                if (dt <= 0) continue;
                vehicle.Position = Vector3.MoveTowards(vehicle.Position, target, Mathf.Max(vehicle.Speed * CatchUpFactor, MinCatchUpMps) * dt);
                var turnRate = Mathf.Max(vehicle.Speed / vehicle.TurnRadius, MinTurnRateRad);
                vehicle.Forward = Vector3.RotateTowards(vehicle.Forward, forward, turnRate * dt, 0);
            }
        }

        private struct BodyPose
        {
            public Vector2 At;
            public float HeadingDeg, Speed;
        }

        private sealed class BackendFrame
        {
            public double TimeS;
            public readonly Dictionary<int, BodyPose> Vehicles = new Dictionary<int, BodyPose>();
            public readonly Dictionary<string, BodyPose> Shuttles = new Dictionary<string, BodyPose>();
        }
    }
}
