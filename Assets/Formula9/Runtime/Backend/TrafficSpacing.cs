using System;
using System.Collections.Generic;

namespace Formula9.Traffic
{
    // ALL distances are metres along a directed lane (s), measured from the same origin.
    // PositionMeters represents the vehicle CENTRE, not its front bumper.
    // Inputs are immutable snapshots; this module does NOT move cars or shuttles.
    public sealed class CarState
    {
        public string Id { get; private set; }
        public string LaneId { get; private set; }
        public float PositionMeters { get; private set; }
        public float SpeedMps { get; private set; }
        public float LengthMeters { get; private set; }
        public float DesiredSpeedMps { get; private set; }

        public CarState(string id, string laneId, float positionMeters,
            float speedMps, float lengthMeters, float desiredSpeedMps)
        {
            Id = id;
            LaneId = laneId;
            PositionMeters = positionMeters;
            SpeedMps = speedMps;
            LengthMeters = lengthMeters;
            DesiredSpeedMps = desiredSpeedMps;
        }
    }

    // The caller supplies shuttle snapshots from the backend. Never move them here.
    public sealed class ShuttleState
    {
        public string Id { get; private set; }
        public string LaneId { get; private set; }
        public float PositionMeters { get; private set; }
        public float SpeedMps { get; private set; }
        public float LengthMeters { get; private set; }

        public ShuttleState(string id, string laneId, float positionMeters,
            float speedMps, float lengthMeters)
        {
            Id = id;
            LaneId = laneId;
            PositionMeters = positionMeters;
            SpeedMps = speedMps;
            LengthMeters = lengthMeters;
        }
    }

    // Unknown and Yellow are treated as STOP, never guessed to be green.
    public enum SignalState { Unknown, Red, Yellow, Green }

    // One approach of a shared conflict zone. Entry and Exit are s-coordinates
    // on THIS approach lane, so crossing lanes may have different coordinates.
    public sealed class JunctionApproach
    {
        public string LaneId { get; private set; }
        public float EntryMeters { get; private set; }
        public float ExitMeters { get; private set; }
        public SignalState Signal { get; private set; }

        public JunctionApproach(string laneId, float entryMeters,
            float exitMeters, SignalState signal)
        {
            LaneId = laneId;
            EntryMeters = entryMeters;
            ExitMeters = exitMeters;
            Signal = signal;
        }
    }

    public sealed class JunctionState
    {
        public string Id { get; private set; }
        public bool Blocked { get; private set; }
        public IList<JunctionApproach> Approaches { get; private set; }

        public JunctionState(string id, bool blocked, IList<JunctionApproach> approaches)
        {
            Id = id;
            Blocked = blocked;
            Approaches = approaches;
        }
    }

    public sealed class SpacingFrame
    {
        public float DeltaTimeSeconds { get; private set; }
        public IList<CarState> Cars { get; private set; }
        public IList<ShuttleState> Shuttles { get; private set; }
        public IList<JunctionState> Junctions { get; private set; }

        public SpacingFrame(float deltaTimeSeconds, IList<CarState> cars,
            IList<ShuttleState> shuttles, IList<JunctionState> junctions)
        {
            DeltaTimeSeconds = deltaTimeSeconds;
            Cars = cars;
            Shuttles = shuttles;
            Junctions = junctions;
        }
    }

    public sealed class TrafficSpacingSettings
    {
        public float TimeHeadwaySeconds = 2.0f;
        public float StandstillGapMeters = 4.5f;  // roughly an extra car length
        public float StopLineBufferMeters = 0.5f;
        public float ComfortableDecelerationMps2 = 3.0f;
        public float ShuttleYieldDistanceMeters = 10.0f;
        public float ShuttleLookaheadSeconds = 2.0f;
    }

    /// <summary>
    /// Stateless safe-speed calculator. 
    /// For every car returns a speed limit (m/s) for THIS timestep.
    /// Apply simultaneously as sNext = sNow + allowedSpeed * dt. Never exceed it.
    /// No engine-style impulses, rigid bodies, or collision callbacks are used.
    ///
    /// Assumptions: valid non-overlapping input; no backwards vehicle motion;
    /// shuttles do not teleport or move backward into cars; and shuttles must not
    /// force entry into a crossing junction while a car is already inside it.
    /// This module alone cannot enforce constraints on backend-driven shuttles.
    /// </summary>
    public sealed class TrafficSpacing
    {
        private readonly TrafficSpacingSettings settings;
        private const float Epsilon = 0.0001f;

        private sealed class RoadObject
        {
            public string Id;
            public string LaneId;
            public float S;
            public float Speed;
            public float Length;
            public bool IsShuttle;
            public float Front { get { return S + Length * 0.5f; } }
            public float Rear { get { return S - Length * 0.5f; } }
        }

        public TrafficSpacing(TrafficSpacingSettings customSettings = null)
        {
            settings = customSettings ?? new TrafficSpacingSettings();
            CheckPositive(settings.TimeHeadwaySeconds, "TimeHeadwaySeconds");
            CheckNonnegative(settings.StandstillGapMeters, "StandstillGapMeters");
            CheckNonnegative(settings.StopLineBufferMeters, "StopLineBufferMeters");
            CheckPositive(settings.ComfortableDecelerationMps2, "ComfortableDecelerationMps2");
            CheckNonnegative(settings.ShuttleYieldDistanceMeters, "ShuttleYieldDistanceMeters");
            CheckNonnegative(settings.ShuttleLookaheadSeconds, "ShuttleLookaheadSeconds");
        }

        public Dictionary<string, float> CalculateAllowedSpeeds(SpacingFrame frame)
        {
            List<RoadObject> all = ValidateAndBuild(frame);
            List<string> overlaps = FindOverlapsInternal(all, frame.Junctions);
            if (overlaps.Count != 0)
                throw new InvalidOperationException("Unsafe input snapshot: " + string.Join("; ", overlaps.ToArray()));

            var result = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (CarState car in frame.Cars)
                result.Add(car.Id, car.DesiredSpeedMps);

            // A leader is the immediately next object by s on the same lane.
            // Treat its CURRENT position as fixed for the whole step. This is
            // deliberately conservative: the leader may move ahead, but never
            // needs to move for the follower to remain non-overlapping.
            var lanes = new Dictionary<string, List<RoadObject>>(StringComparer.Ordinal);
            foreach (RoadObject obj in all)
            {
                List<RoadObject> lane;
                if (!lanes.TryGetValue(obj.LaneId, out lane))
                {
                    lane = new List<RoadObject>();
                    lanes.Add(obj.LaneId, lane);
                }
                lane.Add(obj);
            }

            foreach (List<RoadObject> lane in lanes.Values)
            {
                lane.Sort((a, b) => a.S.CompareTo(b.S));
                for (int i = 0; i < lane.Count - 1; i++)
                {
                    RoadObject follower = lane[i];
                    if (follower.IsShuttle) continue;
                    RoadObject leader = lane[i + 1];
                    float clearGap = leader.Rear - follower.Front;
                    float headwayCap = Math.Max(0f,
                        (clearGap - settings.StandstillGapMeters) / settings.TimeHeadwaySeconds);
                    float nonOverlapCap = Math.Max(0f, clearGap / frame.DeltaTimeSeconds);
                    result[follower.Id] = Math.Min(result[follower.Id],
                        Math.Min(headwayCap, nonOverlapCap));
                }
            }

            // For a shared junction, allow at most one car to enter at a time.
            // An already occupying vehicle keeps the right to EXIT (even if
            // the light changes); cars outside stop before the entry line.
            foreach (JunctionState junction in frame.Junctions)
            {
                RoadObject occupant = FindOccupant(all, junction);
                bool shuttleApproaching = ShuttleApproaching(all, junction, frame.DeltaTimeSeconds);
                string admittedCarId = null;

                if (occupant == null && !junction.Blocked && !shuttleApproaching)
                {
                    float closest = float.PositiveInfinity;
                    foreach (RoadObject obj in all)
                    {
                        if (obj.IsShuttle) continue;
                        JunctionApproach approach = ApproachFor(junction, obj.LaneId);
                        if (approach == null || approach.Signal != SignalState.Green) continue;
                        if (obj.Front > approach.EntryMeters + Epsilon) continue;
                        float distance = approach.EntryMeters - obj.Front;
                        // Deterministic winner: smallest distance, then stable ID.
                        if (distance < closest - Epsilon ||
                            (Math.Abs(distance - closest) <= Epsilon &&
                             (admittedCarId == null ||
                              string.CompareOrdinal(obj.Id, admittedCarId) < 0)))
                        {
                            admittedCarId = obj.Id;
                            closest = distance;
                        }
                    }
                }

                foreach (RoadObject obj in all)
                {
                    if (obj.IsShuttle) continue;
                    JunctionApproach approach = ApproachFor(junction, obj.LaneId);
                    if (approach == null) continue;
                    bool inside = Occupies(obj, approach);
                    if (inside) continue; // don't trap a car already in a junction
                    if (obj.Front > approach.EntryMeters + Epsilon) continue; // fully beyond entry/exit
                    if (obj.Id == admittedCarId) continue;

                    float remaining = Math.Max(0f,
                        approach.EntryMeters - settings.StopLineBufferMeters - obj.Front);
                    float hardCap = remaining / frame.DeltaTimeSeconds;
                    float brakingCap = (float)Math.Sqrt(
                        2f * settings.ComfortableDecelerationMps2 * remaining);
                    result[obj.Id] = Math.Min(result[obj.Id], Math.Min(hardCap, brakingCap));
                }
            }

            foreach (CarState car in frame.Cars)
                result[car.Id] = Math.Max(0f, result[car.Id]);
            return result;
        }

        // Verify old or simulated NEXT states. Junction conflict checking is
        // intentionally stronger than same-lane geometric overlap checking.
        public List<string> FindOverlaps(SpacingFrame frame)
        {
            List<RoadObject> all = ValidateAndBuild(frame);
            return FindOverlapsInternal(all, frame.Junctions);
        }

        private List<RoadObject> ValidateAndBuild(SpacingFrame frame)
        {
            if (frame == null) throw new ArgumentNullException("frame");
            CheckPositive(frame.DeltaTimeSeconds, "DeltaTimeSeconds");
            if (frame.Cars == null || frame.Shuttles == null || frame.Junctions == null)
                throw new ArgumentException("Cars, Shuttles, and Junctions cannot be null.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var all = new List<RoadObject>(frame.Cars.Count + frame.Shuttles.Count);

            foreach (CarState car in frame.Cars)
            {
                if (car == null) throw new ArgumentException("Null car.");
                CheckVehicle(car.Id, car.LaneId, car.PositionMeters, car.SpeedMps,
                    car.LengthMeters, ids);
                CheckNonnegative(car.DesiredSpeedMps, "Car DesiredSpeedMps");
                all.Add(new RoadObject
                {
                    Id = car.Id,
                    LaneId = car.LaneId,
                    S = car.PositionMeters,
                    Speed = car.SpeedMps,
                    Length = car.LengthMeters
                });
            }
            foreach (ShuttleState shuttle in frame.Shuttles)
            {
                if (shuttle == null) throw new ArgumentException("Null shuttle.");
                CheckVehicle(shuttle.Id, shuttle.LaneId, shuttle.PositionMeters,
                    shuttle.SpeedMps, shuttle.LengthMeters, ids);
                all.Add(new RoadObject
                {
                    Id = shuttle.Id,
                    LaneId = shuttle.LaneId,
                    S = shuttle.PositionMeters,
                    Speed = shuttle.SpeedMps,
                    Length = shuttle.LengthMeters,
                    IsShuttle = true
                });
            }

            var junctionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (JunctionState junction in frame.Junctions)
            {
                if (junction == null || string.IsNullOrEmpty(junction.Id) ||
                    !junctionIds.Add(junction.Id) || junction.Approaches == null)
                    throw new ArgumentException("Invalid or duplicate junction ID/approaches.");
                var laneIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (JunctionApproach approach in junction.Approaches)
                {
                    if (approach == null || string.IsNullOrEmpty(approach.LaneId) ||
                        !laneIds.Add(approach.LaneId))
                        throw new ArgumentException("Invalid or duplicate approach lane.");
                    CheckFinite(approach.EntryMeters, "EntryMeters");
                    CheckFinite(approach.ExitMeters, "ExitMeters");
                    if (approach.ExitMeters <= approach.EntryMeters)
                        throw new ArgumentException("Junction exit must follow entry along the lane.");
                }
            }
            return all;
        }

        private static void CheckVehicle(string id, string laneId, float pos,
            float speed, float length, HashSet<string> ids)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(laneId) || !ids.Add(id))
                throw new ArgumentException("Missing/duplicate vehicle ID or lane ID.");
            CheckFinite(pos, "PositionMeters");
            CheckNonnegative(speed, "SpeedMps");
            CheckPositive(length, "LengthMeters");
        }

        private static void CheckFinite(float v, string name)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
                throw new ArgumentException(name + " must be finite.");
        }
        private static void CheckNonnegative(float v, string name)
        {
            CheckFinite(v, name);
            if (v < 0f) throw new ArgumentException(name + " must be >= 0.");
        }
        private static void CheckPositive(float v, string name)
        {
            CheckFinite(v, name);
            if (v <= 0f) throw new ArgumentException(name + " must be > 0.");
        }

        private static JunctionApproach ApproachFor(JunctionState junction, string laneId)
        {
            foreach (JunctionApproach approach in junction.Approaches)
                if (string.Equals(approach.LaneId, laneId, StringComparison.Ordinal))
                    return approach;
            return null;
        }

        private static bool Occupies(RoadObject obj, JunctionApproach approach)
        {
            return obj.Front > approach.EntryMeters + Epsilon &&
                   obj.Rear < approach.ExitMeters - Epsilon;
        }

        private static RoadObject FindOccupant(List<RoadObject> all, JunctionState junction)
        {
            foreach (RoadObject obj in all)
            {
                JunctionApproach approach = ApproachFor(junction, obj.LaneId);
                if (approach != null && Occupies(obj, approach)) return obj;
            }
            return null;
        }

        private bool ShuttleApproaching(List<RoadObject> all, JunctionState junction, float dt)
        {
            foreach (RoadObject obj in all)
            {
                if (!obj.IsShuttle) continue;
                JunctionApproach approach = ApproachFor(junction, obj.LaneId);
                if (approach == null) continue;
                if (Occupies(obj, approach)) return true;
                float distance = approach.EntryMeters - obj.Front;
                float yieldWindow = Math.Max(settings.ShuttleYieldDistanceMeters,
                    obj.Speed * (settings.ShuttleLookaheadSeconds + dt));
                if (distance >= 0f && distance <= yieldWindow) return true;
            }
            return false;
        }

        private static List<string> FindOverlapsInternal(List<RoadObject> all,
            IList<JunctionState> junctions)
        {
            var problems = new List<string>();
            var lanes = new Dictionary<string, List<RoadObject>>(StringComparer.Ordinal);
            foreach (RoadObject obj in all)
            {
                List<RoadObject> lane;
                if (!lanes.TryGetValue(obj.LaneId, out lane))
                {
                    lane = new List<RoadObject>();
                    lanes.Add(obj.LaneId, lane);
                }
                lane.Add(obj);
            }
            foreach (List<RoadObject> lane in lanes.Values)
            {
                lane.Sort((a, b) => a.S.CompareTo(b.S));
                for (int i = 0; i < lane.Count - 1; i++)
                {
                    RoadObject behind = lane[i];
                    RoadObject ahead = lane[i + 1];
                    if (behind.Front > ahead.Rear + Epsilon)
                        problems.Add("lane " + behind.LaneId + ": " +
                            behind.Id + " overlaps " + ahead.Id);
                }
            }
            foreach (JunctionState junction in junctions)
            {
                RoadObject first = null;
                foreach (RoadObject obj in all)
                {
                    JunctionApproach approach = ApproachFor(junction, obj.LaneId);
                    if (approach == null || !Occupies(obj, approach)) continue;
                    if (first == null) first = obj;
                    else problems.Add("junction " + junction.Id + ": " +
                        first.Id + " conflicts with " + obj.Id);
                }
            }
            return problems;
        }
    }
}
