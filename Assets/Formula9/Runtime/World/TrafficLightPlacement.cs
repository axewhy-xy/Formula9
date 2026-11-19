using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.Backend;
using UnityEngine;

namespace Formula9.World
{
    // Where the TrafficLights prefab stands for an LTA signal head. Its pivot is the foot of the pole, its
    // +X the mast arm reaching out to the head, and its +Z the way the traffic it controls is travelling.
    public static class TrafficLightPlacement
    {
        // The prefab's reach from the foot of its pole to the head
        public const float ArmM = 4.58f;
        // LTA lists a pole's heads separately, and one pole is enough for those on one approach
        private const float SamePoleM = 5, SameDirectionDeg = 30;
        // Roads are drawn wider than some of LTA's kerbs are, so a pole can land on the road. It moves no further than this.
        private const float MaxShiftM = 24, ShiftStepM = 1;
        private const int ShiftDirections = 16;
        // The pole's half width, kept clear of the asphalt: twice the kerb stone, so a pole moved off the
        // road stands just behind the kerb stone, in the verge or on the footpath
        private const float PoleClearanceM = 0.3f;

        public readonly struct Pose
        {
            public readonly Vector3 Position;
            public readonly Quaternion Rotation;
            public readonly bool Mirrored;

            public Pose(Vector3 position, Quaternion rotation, bool mirrored)
            {
                Position = position;
                Rotation = rotation;
                Mirrored = mirrored;
            }
        }

        public static List<CitySignalTrafficLight> OnePerPole(IEnumerable<CitySignalTrafficLight> signals)
        {
            var kept = new List<CitySignalTrafficLight>();
            foreach (var signal in signals)
                if (!kept.Any(existing => existing.Junction == signal.Junction
                    && Vector2.Distance(new Vector2((float)existing.XM, (float)existing.YM), new Vector2((float)signal.XM, (float)signal.YM)) < SamePoleM
                    && Mathf.Abs(Mathf.DeltaAngle((float)existing.FacingHeadingDeg, (float)signal.FacingHeadingDeg)) < SameDirectionDeg))
                    kept.Add(signal);
            return kept;
        }

        // groundHeight gives the height under a point (x east, y north), such as the sidewalk's top; without
        // it the pole stands on the ground.
        public static Pose Place(CitySignalTrafficLight signal, IReadOnlyList<CityRoad> roads, Func<Vector2, float> groundHeight = null)
        {
            var heading = (float)signal.TrafficHeadingDeg * Mathf.Deg2Rad;
            var travel = new Vector2(Mathf.Cos(heading), Mathf.Sin(heading));
            var foot = new Vector2((float)signal.XM, (float)signal.YM);
            var nearby = Near(roads, foot);
            foot = OffTheRoad(foot, nearby);

            // Traffic keeps left, so the arm reaches to the driver's right, unless this pole stands across the road from them
            var right = new Vector2(travel.y, -travel.x);
            var mirrored = !OnAsphalt(foot + right * ArmM, nearby, 0) && OnAsphalt(foot - right * ArmM, nearby, 0);
            var height = groundHeight != null ? groundHeight(foot) : 0;
            return new Pose(new Vector3(foot.x, height, foot.y), Quaternion.LookRotation(new Vector3(travel.x, 0, travel.y)), mirrored);
        }

        // Only the sections that could matter, as a city has thousands of them and hundreds of lights
        private static List<(Vector2 A, Vector2 B, float HalfWidth)> Near(IReadOnlyList<CityRoad> roads, Vector2 point)
        {
            var reach = MaxShiftM + ArmM;
            var sections = new List<(Vector2, Vector2, float)>();
            foreach (var road in roads)
            {
                var halfWidth = RoadMesh.HalfWidth(road);
                for (var i = 0; i + 1 < road.Points.Count; i++)
                {
                    var a = new Vector2((float)road.Points[i].X, (float)road.Points[i].Y);
                    var b = new Vector2((float)road.Points[i + 1].X, (float)road.Points[i + 1].Y);
                    if (DistanceToSection(point, a, b) <= reach + halfWidth) sections.Add((a, b, halfWidth));
                }
            }
            return sections;
        }

        // The nearest ground clear of every road, as moving off one road can land on another at a junction
        private static Vector2 OffTheRoad(Vector2 foot, List<(Vector2 A, Vector2 B, float HalfWidth)> sections)
        {
            if (!OnAsphalt(foot, sections, PoleClearanceM)) return foot;
            for (var shift = ShiftStepM; shift <= MaxShiftM; shift += ShiftStepM)
                for (var i = 0; i < ShiftDirections; i++)
                {
                    var angle = i * Mathf.PI * 2 / ShiftDirections;
                    var candidate = foot + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * shift;
                    if (!OnAsphalt(candidate, sections, PoleClearanceM)) return candidate;
                }
            return foot;
        }

        private static bool OnAsphalt(Vector2 point, List<(Vector2 A, Vector2 B, float HalfWidth)> sections, float clearance)
        {
            foreach (var (a, b, halfWidth) in sections)
                if (DistanceToSection(point, a, b) < halfWidth + clearance) return true;
            return false;
        }

        private static float DistanceToSection(Vector2 point, Vector2 a, Vector2 b)
        {
            var along = b - a;
            var lengthSquared = along.sqrMagnitude;
            var share = lengthSquared <= 0 ? 0 : Mathf.Clamp01(Vector2.Dot(point - a, along) / lengthSquared);
            return Vector2.Distance(point, a + along * share);
        }
    }
}

