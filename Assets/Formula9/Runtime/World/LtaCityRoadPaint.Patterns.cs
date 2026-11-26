using System;
using System.Collections.Generic;
using UnityEngine;

namespace Formula9.World
{
    public sealed partial class LtaCityRoadPaint
    {
        private static readonly HashSet<string> LaneBoundaryTypes = new HashSet<string>(StringComparer.Ordinal)
            { "A", "A1", "A3", "B", "B1", "B2", "C", "E", "F", "H", "I", "L", "M" };

        private static readonly HashSet<string> CrossingApproachTypes = new HashSet<string>(StringComparer.Ordinal)
            { "A", "A1", "A3", "B", "C", "D", "K" };

        private static readonly Dictionary<string, Pattern> Patterns =
            new Dictionary<string, Pattern>(StringComparer.Ordinal)
            {
                ["A"] = new Pattern(0.10f, 1f, 1f),
                ["A1"] = new Pattern(0.10f, 1f, 1f, PaintColour.Yellow),
                ["A2"] = new Pattern(0.20f, 1f, 1f),
                ["A3"] = new Pattern(0.30f, 1f, 1f, PaintColour.Yellow),
                ["A4"] = new Pattern(0.20f, 0.20f, 0.30f),
                ["A5"] = new Pattern(0.10f, 1f, 3f),
                ["A8"] = new Pattern(0.40f, 0.40f, 0.40f),
                ["B"] = new Pattern(0.10f, 2f, 4f),
                ["B1"] = new Pattern(0.10f, 2f, 10f),
                ["B2"] = new Pattern(0.25f, 2f, 4f),
                ["C"] = new Pattern(0.10f, 4f, 2f),
                ["C1"] = new Pattern(0.20f, 4f, 2f),
                ["D"] = new Pattern(0.10f, 1f, 1f, separation: 0.25f),
                ["D1"] = new Pattern(0.10f, 0.5f, 0.5f, separation: 0.25f),
                ["E"] = new Pattern(0.15f, 2.75f, 2.75f),
                ["F"] = new Pattern(0.15f),
                ["G"] = new Pattern(0.15f, colour: PaintColour.Yellow),
                ["H"] = new Pattern(0.10f, separation: 0.25f),
                ["I"] = new Pattern(0.10f, colour: PaintColour.Yellow, separation: 0.20f),
                ["J"] = new Pattern(0.30f),
                ["K"] = new Pattern(0.10f, zigzag: true),
                ["L"] = new Pattern(0.30f, colour: PaintColour.Yellow),
                ["M"] = new Pattern(0.20f),
                ["O"] = new Pattern(0.10f, colour: PaintColour.Yellow, zigzag: true),
                ["R"] = new Pattern(0.30f),
                ["S"] = new Pattern(0.15f, colour: PaintColour.Yellow),
                ["T"] = new Pattern(0.15f),
                ["U"] = new Pattern(0.20f),
                ["V"] = new Pattern(0.20f),
                ["Y"] = new Pattern(0.20f),
                ["Z"] = new Pattern(0.20f),
                ["SURVEY_WORD"] = new Pattern(0.21f),
                ["SURVEY_HUMP"] = new Pattern(0.25f),
                ["SURVEY_BAY"] = new Pattern(0.15f),
                ["SURVEY_STRIP"] = new Pattern(0.30f),
                ["SURVEY_FILL"] = new Pattern(0.24f)
            };

        private static readonly Vector2[] StraightArrow = { Vector2.zero, new Vector2(0, 3.4f) };
        private static readonly Vector2[] TurnArrow =
            { Vector2.zero, new Vector2(0, 1.85f), new Vector2(0.13f, 2.22f),
              new Vector2(0.42f, 2.48f), new Vector2(1.50f, 2.48f) };
        private static readonly Vector2[] SharedTurn =
            { new Vector2(0, 1.2f), new Vector2(0.12f, 1.7f),
              new Vector2(0.42f, 2.10f), new Vector2(1.50f, 2.43f) };
        private static readonly Vector2[] MergeArrow = SampleCurve(
            new Vector2(0.58f, 0.90f), new Vector2(1.21f, 2.12f), new Vector2(1.33f, 3.74f));
        private static readonly Dictionary<string, Vector2[][]> ArrowPaths =
            new Dictionary<string, Vector2[][]>(StringComparer.Ordinal)
            {
                ["A"] = new[] { TurnArrow },
                ["B"] = new[] { StraightArrow, Mirror(SharedTurn) },
                ["C"] = new[] { StraightArrow, SharedTurn },
                ["D"] = new[] { StraightArrow },
                ["E"] = new[] { Mirror(TurnArrow) },
                ["F"] = new[] { Mirror(SharedTurn), TurnArrow },
                ["G"] = new[] { Mirror(MergeArrow) },
                ["H"] = new[] { MergeArrow },
                ["I"] = new[] { StraightArrow, Mirror(SharedTurn), SharedTurn }
            };

        private static Vector2[] Mirror(Vector2[] path) => Array.ConvertAll(path, p => new Vector2(-p.x, p.y));

        private static Vector2[] SampleCurve(Vector2 a, Vector2 b, Vector2 end)
        {
            var path = new Vector2[13];
            for (int i = 0; i < path.Length; i++)
            {
                float t = i / 12f, u = 1f - t;
                path[i] = 3 * u * u * t * a + 3 * u * t * t * b + t * t * t * end;
            }
            return path;
        }

        private readonly struct Pattern
        {
            public readonly float Width, Dash, Gap, Separation;
            public readonly PaintColour Colour;
            public readonly bool Zigzag;

            public Pattern(float width, float dash = 0, float gap = 0,
                PaintColour colour = PaintColour.White, float separation = 0, bool zigzag = false)
            {
                Width = width;
                Dash = dash;
                Gap = gap;
                Colour = colour;
                Separation = separation;
                Zigzag = zigzag;
            }
        }

        public enum PaintColour { White, Yellow }
    }
}
