using System;
using System.Collections.Generic;
using Formula9.Backend;
using UnityEngine;

namespace Formula9.World
{
    // Drawing the pedestrians the backend walks (backend src/traffic/city_traffic.py and its native engine,
    // unless F9_PEDESTRIANS is 0). The backend's traffic frames then carry every walker as well as every vehicle, and the vehicles
    // there give way to them, so nothing walks here: walkers are drawn where the frames say, at the time the
    // traffic is drawn (CityTrafficRenderer.FollowedS, its TrafficPlaybackClock). A car is therefore never
    // drawn driving through someone the backend had it stop for.
    //
    // Between two frames a walker is drawn a share of the way from one to the next, turning the shorter way
    // round. One that moved further than JumpM between them was put somewhere new (as by a checkpoint) and
    // is drawn at the frame before until the next. Its walk cycle runs on with the distance it is drawn
    // walking. Its feet stand on the road's surface while on a crossing's road part, and otherwise on the
    // sidewalk under it (ramps included), as the sidewalk surface gives it.
    //
    // CityView draws these while the newest frame has walkers in it; otherwise the walkers of
    // PedestrianLocalSim walk the walkways file here, as for a backend that walks none.
    public sealed class PedestrianFollow
    {
        // Frames kept at most: a few seconds at the backend's ten a second
        public const int MaxFrames = 64;
        // A walker this far from where it was a frame before was put there, not walked there
        public const float JumpM = 3f;
        // Slower than this a walker stands, in its idle pose
        public const float StandingMps = 0.1f;
        // A seed with nothing known of it: this tall
        private const float DefaultHeightM = 1.7f;

        private readonly List<Frame> frames = new List<Frame>();
        private readonly Stack<Frame> spare = new Stack<Frame>();
        private readonly List<PedestrianPose> poses = new List<PedestrianPose>();
        // By seed index: its character and height from the walkways file, and how it was last drawn
        private int[] models = Array.Empty<int>();
        private float[] heights = Array.Empty<float>();
        private float[] walkPhase = Array.Empty<float>();
        private Vector2[] lastAt = Array.Empty<Vector2>();
        private bool[] drawn = Array.Empty<bool>();
        private Func<Vector2, float> groundHeight;

        // True while the newest frame carries walkers: then they, not PedestrianLocalSim's, are drawn.
        public bool Active => frames.Count > 0 && frames[frames.Count - 1].Count > 0;

        // How many frames are kept, for diagnostics and tests.
        public int FrameCount => frames.Count;

        // Where everyone is after the last Advance, for PedestrianCrowd to draw.
        public IReadOnlyList<PedestrianPose> Poses => poses;

        // -----------------------------------------------------------------------------------
        // Function: SetSeeds
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function takes the walkways file's seeds, which the frames name walkers by: each
        //     one's character model and height.
        //
        // Args:
        //     data (CityWalkwayData): The walkways file; null forgets them.
        public void SetSeeds(CityWalkwayData data)
        {
            var seeds = data?.Seeds;
            var count = seeds?.Count ?? 0;
            Grow(count);
            for (var i = 0; i < count; i++)
            {
                models[i] = seeds[i].Model;
                heights[i] = seeds[i].HeightM > 0 ? seeds[i].HeightM : DefaultHeightM;
            }
        }

        // The ground's height under a point (x east, y north), once the sidewalks are in; until then
        // walkers stand at the road's height.
        public void SetGroundHeight(Func<Vector2, float> height) => groundHeight = height;

        // -----------------------------------------------------------------------------------
        // Function: Add
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function takes the backend's newest traffic frames, as they arrive, as
        //     CityTrafficRenderer.FollowBackend does. Ones no newer than those it has are left out.
        //
        // Args:
        //     incoming (IReadOnlyList<TrafficFrame>): The frames, oldest first.
        public void Add(IReadOnlyList<TrafficFrame> incoming)
        {
            if (incoming == null) return;
            foreach (var frame in incoming)
            {
                if (frame == null || (frames.Count > 0 && frame.SimulationTimeS <= frames[frames.Count - 1].TimeS)) continue;
                var made = spare.Count > 0 ? spare.Pop() : new Frame();
                made.TimeS = frame.SimulationTimeS;
                made.Walkers = frame.Pedestrians ?? Array.Empty<int>();
                frames.Add(made);
            }
            if (frames.Count > MaxFrames) Drop(frames.Count - MaxFrames);
        }

        // Forgets every frame, as for another session: until more come, nobody is drawn from here.
        public void Forget()
        {
            Drop(frames.Count);
            poses.Clear();
            Array.Clear(drawn, 0, drawn.Length);
        }

        // -----------------------------------------------------------------------------------
        // Function: Advance
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function puts every walker of the frames where they are at a time on the
        //     session's clock: the time the traffic is drawn at, so walkers and vehicles show the
        //     same moment. Frames well before it are let go.
        //
        // Args:
        //     timeS (double): The time to draw, on the session's clock; NaN for the newest frame.
        public void Advance(double timeS)
        {
            poses.Clear();
            if (frames.Count == 0) return;
            if (double.IsNaN(timeS)) timeS = frames[frames.Count - 1].TimeS;
            // The drawn time never goes back: frames before the one at or before it are done with
            var index = 0;
            while (index + 1 < frames.Count && frames[index + 1].TimeS <= timeS) index++;
            if (index > 0) Drop(index);
            var from = frames[0];
            var to = frames.Count > 1 ? frames[1] : from;
            var share = to.TimeS > from.TimeS ? Mathf.Clamp01((float)((timeS - from.TimeS) / (to.TimeS - from.TimeS))) : 0f;

            const int stride = TrafficFrame.PedestrianStride;
            var a = from.Walkers;
            var b = to.Walkers;
            for (var i = 0; i + stride <= a.Length; i += stride)
            {
                var seed = a[i];
                if (seed < 0) continue;
                Grow(seed + 1);
                var at = new Vector2(a[i + 1] / 100f, a[i + 2] / 100f);
                var heading = a[i + 3] / 10f;
                var speed = a[i + 4] * 0.02f;
                var flags = a[i + 5];
                // The backend sends its walkers in the same order every frame; one not found there stands
                var j = FindSeed(b, seed, i);
                if (j >= 0)
                {
                    var next = new Vector2(b[j + 1] / 100f, b[j + 2] / 100f);
                    if ((next - at).sqrMagnitude <= JumpM * JumpM)
                    {
                        at = Vector2.Lerp(at, next, share);
                        heading = Mathf.LerpAngle(heading, b[j + 3] / 10f, share);
                        speed = Mathf.Lerp(speed, b[j + 4] * 0.02f, share);
                        if (share >= 0.5f) flags = b[j + 5];
                    }
                }

                // The walk cycle runs on with the distance walked, as PedestrianLocalSim's does
                if (drawn[seed])
                {
                    var moved = (at - lastAt[seed]).magnitude;
                    if (moved <= JumpM) walkPhase[seed] += moved / PedestrianLocalSim.StrideM;
                }
                else walkPhase[seed] = (seed * 0.618034f) % 1f;
                drawn[seed] = true;
                lastAt[seed] = at;

                var radians = heading * Mathf.Deg2Rad;
                var standing = speed < StandingMps || (flags & (TrafficFrame.PedestrianWaiting | TrafficFrame.PedestrianPaused)) != 0;
                poses.Add(new PedestrianPose
                {
                    Position = new Vector3(at.x, HeightAt(at, flags), at.y),
                    Rotation = Quaternion.LookRotation(new Vector3(Mathf.Cos(radians), 0, Mathf.Sin(radians)), Vector3.up),
                    ScaleM = heights[seed] > 0 ? heights[seed] : DefaultHeightM,
                    Model = models[seed],
                    WalkPhase = walkPhase[seed],
                    Moving = !standing,
                });
            }
        }

        // The height of a walker's feet: the road's on a crossing's road part, else the sidewalk's (which
        // slopes down a ramp) where there is one
        private float HeightAt(Vector2 at, int flags)
        {
            if ((flags & TrafficFrame.PedestrianOnRoad) != 0 || groundHeight == null) return RoadMesh.SurfaceY;
            return groundHeight(at);
        }

        private static int FindSeed(int[] walkers, int seed, int hint)
        {
            const int stride = TrafficFrame.PedestrianStride;
            if (hint + stride <= walkers.Length && walkers[hint] == seed) return hint;
            for (var i = 0; i + stride <= walkers.Length; i += stride)
                if (walkers[i] == seed) return i;
            return -1;
        }

        private void Grow(int count)
        {
            if (count <= models.Length) return;
            var size = Math.Max(count, models.Length * 2);
            var oldCount = models.Length;
            Array.Resize(ref models, size);
            Array.Resize(ref heights, size);
            Array.Resize(ref walkPhase, size);
            Array.Resize(ref lastAt, size);
            Array.Resize(ref drawn, size);
            // Seeds the walkways file has not named yet: a model by their number, and an everyday height
            for (var i = oldCount; i < size; i++)
            {
                models[i] = i;
                heights[i] = DefaultHeightM;
            }
        }

        private void Drop(int count)
        {
            count = Math.Min(count, frames.Count);
            for (var i = 0; i < count; i++)
            {
                frames[i].Walkers = Array.Empty<int>();
                spare.Push(frames[i]);
            }
            frames.RemoveRange(0, count);
        }

        private sealed class Frame
        {
            public double TimeS;
            public int[] Walkers = Array.Empty<int>();
            public int Count => Walkers.Length / TrafficFrame.PedestrianStride;
        }
    }
}
