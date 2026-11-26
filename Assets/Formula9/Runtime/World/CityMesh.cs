using System.Collections.Generic;
using System.Linq;
using Formula9.Backend;
using UnityEngine;
using UnityEngine.Rendering;

namespace Formula9.World
{
    // A strip of ground no building may stand on: a rectangle, anticlockwise seen from above.
    public sealed class Corridor
    {
        public readonly Vector2[] Corners;
        public readonly Rect Box;

        public Corridor(Vector2[] corners)
        {
            Corners = corners;
            Box = CityMesh.BoxAround(corners);
        }
    }

    // Extrudes building outlines into one mesh: walls straight up from the outline, a flat roof on top.
    // OSM outlines ignore what passes under a building, so corridors are cut out from ground to roof.
    public static class CityMesh
    {
        // Pieces smaller than this only come from rounding, and are not drawn
        private const float MinWallM = 1e-3f, MinRoofM2 = 1e-4f;
        // A wall exactly on a corridor's edge stays, so corridors that share an edge keep their wall
        private const float EdgeToleranceM = 1e-3f;
        // Less of an outline than this left over is only thin strips, like the sides of a canopy over the road
        private const float MinLeftShare = 0.2f;
        // Each vertex's first texture coordinate is (metres along the wall, metres up, style, 1 on a roof), so
        // CityBuilding.shader can tile walls and facades by real size and leave roofs plain. The style is its
        // index in BuildingStyles; the vertex colour is the building's body colour and the fourth texture
        // coordinate its accent colour.
        // With details, roofs also get their style's parapet and rooftop structures (lift rooms, tanks, plant)
        public static Mesh Build(IEnumerable<CityBuilding> buildings, IReadOnlyList<Corridor> corridors = null, BuildingStyles styles = null, bool details = false, Cache cache = null)
        {
            var mesh = new Mesh { name = "City buildings" };
            Fill(mesh, buildings, corridors, styles, details, cache);
            return mesh;
        }

        // Each building outline's finished geometry, kept between rebuilds of the same buildings: an
        // outline is only built again when the corridors over it change, so a route that moves redoes the
        // few buildings it passes rather than the whole detailed city. It is for one list of buildings; a
        // different list (or styles, or details) starts it afresh.
        public sealed class Cache
        {
            internal object Buildings;
            internal BuildingStyles Styles;
            internal bool Details;
            internal readonly List<Piece> Pieces = new List<Piece>();
            // The mesh last filled from these pieces
            internal Mesh Filled;

            // How many outlines the last Fill built rather than reused
            public int LastBuilt { get; internal set; }
        }

        internal sealed class Piece
        {
            // The cleaned-up outline, anticlockwise; null when there is nothing to draw
            public List<Vector2> Points;
            public Rect Box;
            // The corridors over it when Geometry was built
            public List<Corridor> Cuts;
            public Lists Geometry;
        }

        // Rebuilds a mesh in place, for when the corridors change. With a cache, only outlines whose
        // corridors changed since the last fill are built again.
        public static void Fill(Mesh mesh, IEnumerable<CityBuilding> buildings, IReadOnlyList<Corridor> corridors = null, BuildingStyles styles = null, bool details = false, Cache cache = null)
        {
            styles = styles ?? BuildingStyles.Default;
            cache = cache ?? new Cache();
            var changed = false;
            if (!ReferenceEquals(cache.Buildings, buildings) || cache.Styles != styles || cache.Details != details)
            {
                changed = true;
                cache.Pieces.Clear();
                cache.Buildings = buildings; cache.Styles = styles; cache.Details = details;
            }
            cache.LastBuilt = 0;
            var next = 0;
            foreach (var building in buildings)
            {
                Paint paint = null;
                var height = (float)building.HeightM;
                foreach (var ring in building.Rings)
                {
                    Piece piece;
                    if (next < cache.Pieces.Count) piece = cache.Pieces[next];
                    else
                    {
                        piece = new Piece { Points = Prepare(ring.Select(p => new Vector2((float)p.X, (float)p.Y)).ToList(), height, styles.SimplifyM) };
                        if (piece.Points != null) piece.Box = BoxAround(piece.Points);
                        cache.Pieces.Add(piece);
                    }
                    next++;
                    if (piece.Points == null) continue;
                    var cuts = corridors == null ? new List<Corridor>() : corridors.Where(c => c.Box.Overlaps(piece.Box)).ToList();
                    if (piece.Geometry != null && SameCorridors(piece.Cuts, cuts)) continue;
                    paint = paint ?? PaintFor(building, styles, details);
                    piece.Geometry = new Lists();
                    Extrude(piece.Points, height, paint, cuts, piece.Geometry);
                    Shade(piece.Geometry);
                    piece.Cuts = cuts;
                    cache.LastBuilt++;
                }
            }
            if (cache.Pieces.Count > next) { cache.Pieces.RemoveRange(next, cache.Pieces.Count - next); changed = true; }
            // Nothing new since this mesh was last filled
            if (!changed && cache.LastBuilt == 0 && ReferenceEquals(cache.Filled, mesh)) return;
            cache.Filled = mesh;
            var all = new Lists();
            foreach (var piece in cache.Pieces)
            {
                var geometry = piece.Geometry;
                if (geometry == null) continue;
                var offset = all.Vertices.Count;
                all.Vertices.AddRange(geometry.Vertices); all.Normals.AddRange(geometry.Normals); all.Tangents.AddRange(geometry.Tangents);
                all.Colors.AddRange(geometry.Colors); all.Uvs.AddRange(geometry.Uvs); all.Accents.AddRange(geometry.Accents);
                foreach (var index in geometry.Triangles) all.Triangles.Add(offset + index);
            }
            mesh.Clear();
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(all.Vertices); mesh.SetNormals(all.Normals); mesh.SetTangents(all.Tangents);
            mesh.SetColors(all.Colors); mesh.SetUVs(0, all.Uvs); mesh.SetUVs(3, all.Accents);
            mesh.SetTriangles(all.Triangles, 0);
            mesh.RecalculateBounds();
        }

        private static Paint PaintFor(CityBuilding building, BuildingStyles styles, bool details)
        {
            var style = StyleFor(building, styles);
            var saturation = styles.ColorSaturation;
            var paint = new Paint
            {
                Style = style,
                Saturation = saturation,
                Body = PaintColor(BuildingStyles.Pick(styles.Styles[style].BodyColors, building.Id, 1), saturation),
                Accent = PaintColor(BuildingStyles.Pick(styles.Styles[style].AccentColors, building.Id, 2), saturation),
            };
            var roofs = styles.Styles[style].RoofColors;
            paint.Roof = roofs.Count > 0 ? PaintColor(BuildingStyles.Pick(roofs, building.Id, 3), saturation) : paint.Body;
            paint.Spec = details ? styles.Styles[style] : null;
            paint.Id = building.Id ?? "";
            return paint;
        }

        // Corridors are made afresh for each fill, so they are compared by their corners
        private static bool SameCorridors(List<Corridor> before, List<Corridor> now)
        {
            if (before == null || before.Count != now.Count) return false;
            for (var i = 0; i < now.Count; i++)
            {
                if (ReferenceEquals(before[i], now[i])) continue;
                Vector2[] a = before[i].Corners, b = now[i].Corners;
                if (a.Length != b.Length) return false;
                for (var k = 0; k < a.Length; k++) if (a[k] != b[k]) return false;
            }
            return true;
        }

        // Normals and tangents for one outline's geometry, as RecalculateNormals and RecalculateTangents
        // would give them (every surface here is flat, so area weighting changes nothing), but per outline
        // so a cached outline keeps its own. Tangents run along the first texture coordinate, for the
        // facades' normal maps.
        private static void Shade(Lists lists)
        {
            var vertices = lists.Vertices; var uvs = lists.Uvs; var triangles = lists.Triangles;
            var count = vertices.Count;
            var normals = new Vector3[count]; var alongU = new Vector3[count]; var alongV = new Vector3[count];
            for (var i = 0; i + 2 < triangles.Count; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                Vector3 e1 = vertices[b] - vertices[a], e2 = vertices[c] - vertices[a];
                // Facing whoever sees the triangle clockwise, weighted by its area
                var face = Vector3.Cross(e1, e2);
                normals[a] += face; normals[b] += face; normals[c] += face;
                float s1 = uvs[b].x - uvs[a].x, t1 = uvs[b].y - uvs[a].y, s2 = uvs[c].x - uvs[a].x, t2 = uvs[c].y - uvs[a].y;
                var det = s1 * t2 - s2 * t1;
                if (Mathf.Abs(det) < 1e-12f) continue;
                var r = 1 / det;
                var u = (e1 * t2 - e2 * t1) * r;
                var v = (e2 * s1 - e1 * s2) * r;
                alongU[a] += u; alongU[b] += u; alongU[c] += u;
                alongV[a] += v; alongV[b] += v; alongV[c] += v;
            }
            for (var i = 0; i < count; i++)
            {
                var n = normals[i].sqrMagnitude > 0 ? normals[i].normalized : Vector3.up;
                var t = alongU[i] - n * Vector3.Dot(n, alongU[i]);
                // Plain surfaces have no texture to follow: any direction across the surface will do
                if (t.sqrMagnitude < 1e-12f) t = Vector3.Cross(n, Mathf.Abs(n.y) < 0.99f ? Vector3.up : Vector3.forward);
                t.Normalize();
                var w = Vector3.Dot(Vector3.Cross(n, t), alongV[i]) < 0 ? -1f : 1f;
                lists.Normals.Add(n);
                lists.Tangents.Add(new Vector4(t.x, t.y, t.z, w));
            }
        }

        // Which style a building takes, from the styles file's rules
        public static int StyleFor(CityBuilding building, BuildingStyles styles = null) =>
            (styles ?? BuildingStyles.Default).StyleFor(building.Type, building.HeightM, building.Id);

        // A styles-file colour as the mesh carries it: its saturation scaled, then, since the file's colours
        // are sRGB and vertex colours reach the shader as they are, converted for a linear project
        public static Color PaintColor(string hex, float saturation = 1)
        {
            var (r, g, b) = BuildingStyles.Rgb(hex);
            var color = new Color(r, g, b);
            if (saturation != 1)
            {
                Color.RGBToHSV(color, out var h, out var s, out var v);
                color = Color.HSVToRGB(h, Mathf.Clamp01(s * saturation), v);
            }
            return QualitySettings.activeColorSpace == ColorSpace.Linear ? color.linear : color;
        }

        private sealed class Paint
        {
            public int Style;
            public float Saturation = 1;
            public Color Body, Accent, Roof;
            // The style, when the roof gets its details; null for none
            public BuildingStyle Spec;
            public string Id;
        }

        internal sealed class Lists
        {
            public readonly List<Vector3> Vertices = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<Vector4> Tangents = new List<Vector4>();
            public readonly List<Color> Colors = new List<Color>();
            public readonly List<Vector4> Uvs = new List<Vector4>();
            public readonly List<Vector4> Accents = new List<Vector4>();
            public readonly List<int> Triangles = new List<int>();
        }

        // One rectangle per segment of a path. Each runs half its width past its ends, so the
        // rectangles still overlap on the outside of a bend.
        public static List<Corridor> Corridors(IEnumerable<IReadOnlyList<Vector2>> paths, float halfWidth)
        {
            var corridors = new List<Corridor>();
            foreach (var path in paths)
                for (var i = 0; i + 1 < path.Count; i++)
                {
                    var along = path[i + 1] - path[i];
                    if (along.magnitude < MinWallM) continue;
                    along = along.normalized * halfWidth;
                    var left = new Vector2(-along.y, along.x);
                    Vector2 start = path[i] - along, end = path[i + 1] + along;
                    corridors.Add(new Corridor(new[] { start - left, end - left, end + left, start + left }));
                }
            return corridors;
        }

        public static Rect BoxAround(IReadOnlyList<Vector2> points)
        {
            Vector2 min = points[0], max = points[0];
            foreach (var point in points) { min = Vector2.Min(min, point); max = Vector2.Max(max, point); }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        // An outline as Extrude takes it: unclosed, anticlockwise and simplified; null if nothing is left
        private static List<Vector2> Prepare(List<Vector2> outline, float height, float simplifyM)
        {
            var points = new List<Vector2>(outline);
            if (points.Count > 1 && points[0] == points[points.Count - 1]) points.RemoveAt(points.Count - 1);
            if (points.Count < 3 || height <= 0) return null;
            if (SignedArea(points) < 0) points.Reverse();
            if (simplifyM > 0) points = Simplify(points, simplifyM);
            return points.Count < 3 ? null : points;
        }

        // A wall along the outline or a cut, with the building on its left; alongA and alongB are how far
        // a and b are along the wall in metres, so windows keep their width
        private struct WallPiece
        {
            public Vector2 A, B;
            public float AlongA, AlongB;
        }

        // Canonical (x, y) is Unity (x, z). Front faces are clockwise seen from outside.
        private static void Extrude(List<Vector2> points, float height, Paint paint, List<Corridor> cuts, Lists lists)
        {
            var vertices = lists.Vertices; var colors = lists.Colors; var uvs = lists.Uvs; var triangles = lists.Triangles;
            var color = paint.Body; float style = paint.Style;
            var accent = new Vector4(paint.Accent.r, paint.Accent.g, paint.Accent.b, 1);
            var whole = Triangulate(points);
            var roofPieces = new List<List<Vector2>>();
            for (var i = 0; cuts.Count > 0 && i < whole.Count; i += 3)
            {
                var pieces = new List<List<Vector2>> { new List<Vector2> { points[whole[i]], points[whole[i + 2]], points[whole[i + 1]] } };
                foreach (var cut in cuts) pieces = pieces.SelectMany(piece => Subtract(piece, cut.Corners)).ToList();
                roofPieces.AddRange(pieces);
            }
            if (cuts.Count > 0 && roofPieces.Sum(piece => Mathf.Abs(SignedArea(piece))) < MinLeftShare * SignedArea(points)) return;
            var walls = new List<WallPiece>();
            // An open ground floor: the walls start above it, and VoidDeck builds what is under them
            var deck = paint.Spec?.VoidDeck;
            var groundM = paint.Spec?.GroundFloorM ?? 0;
            if (deck != null && (height < deck.MinHeightM || height < groundM + 2)) deck = null;
            var bottom = deck != null ? groundM : 0;
            void Wall(Vector2 a, Vector2 b, float alongA, float alongB)
            {
                walls.Add(new WallPiece { A = a, B = b, AlongA = alongA, AlongB = alongB });
                var start = vertices.Count;
                vertices.Add(new Vector3(a.x, bottom, a.y)); vertices.Add(new Vector3(a.x, height, a.y));
                vertices.Add(new Vector3(b.x, height, b.y)); vertices.Add(new Vector3(b.x, bottom, b.y));
                // Walls a little darker low down, so buildings read as solid
                var low = color * Mathf.Lerp(0.8f, 1, bottom / height);
                colors.Add(low); colors.Add(color); colors.Add(color); colors.Add(low);
                uvs.Add(new Vector4(alongA, bottom, style, 0)); uvs.Add(new Vector4(alongA, height, style, 0));
                uvs.Add(new Vector4(alongB, height, style, 0)); uvs.Add(new Vector4(alongB, bottom, style, 0));
                for (var k = 0; k < 4; k++) lists.Accents.Add(accent);
                triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
            }
            void RoofPoint(Vector2 point)
            {
                vertices.Add(new Vector3(point.x, height, point.y)); colors.Add(paint.Roof);
                uvs.Add(new Vector4(point.x, point.y, style, 1));
                lists.Accents.Add(accent);
            }
            // Measured round the whole outline, so the window columns carry on round each corner
            var outlineSoFar = 0f;
            for (var i = 0; i < points.Count; i++)
            {
                var corner = points[i];
                foreach (var (p, q) in Outside(corner, points[(i + 1) % points.Count], cuts))
                    Wall(p, q, outlineSoFar + Vector2.Distance(corner, p), outlineSoFar + Vector2.Distance(corner, q));
                outlineSoFar += Vector2.Distance(corner, points[(i + 1) % points.Count]);
            }
            // The sides of a cut are new walls, facing into the corridor: its inside is left of each edge
            foreach (var cut in cuts)
            {
                var others = cuts.Where(c => c != cut).ToList();
                for (var i = 0; i < cut.Corners.Length; i++)
                    foreach (var (enter, leave) in InsideOutline(cut.Corners[i], cut.Corners[(i + 1) % cut.Corners.Length], points))
                        foreach (var (p, q) in Outside(enter, leave, others))
                            Wall(q, p, Vector2.Distance(cut.Corners[i], q), Vector2.Distance(cut.Corners[i], p));
            }
            if (cuts.Count == 0)
            {
                var roof = vertices.Count;
                foreach (var point in points) RoofPoint(point);
                foreach (var triangle in whole) triangles.Add(roof + triangle);
            }
            else
                foreach (var piece in roofPieces)
                {
                    if (SignedArea(piece) < 0) piece.Reverse();
                    var roof = vertices.Count;
                    foreach (var point in piece) RoofPoint(point);
                    for (var j = 1; j + 1 < piece.Count; j++) triangles.AddRange(new[] { roof, roof + j + 1, roof + j });
                }
            if (paint.Spec == null) return;
            var mitres = walls.Select((_, i) => Mitres(walls, i)).ToList();
            if (deck != null)
                for (var i = 0; i < walls.Count; i++) VoidDeck(walls[i], mitres[i], groundM, deck, paint, lists);
            RoofDetails(walls, mitres, points, cuts, height, paint, lists);
        }

        // Inset strips (a void deck's back wall and ceiling, a parapet) stop short of each end of their
        // wall by this much per metre of depth, so that neighbouring strips meet on the line halving the
        // corner between them instead of poking out through the facade at a sharp corner: 1 at a square
        // corner, more at a sharper one, below 0 (running on) at an inside corner, 0 at an open end
        private static (float Start, float End) Mitres(List<WallPiece> walls, int index)
        {
            var wall = walls[index];
            var dir = (wall.B - wall.A).normalized;
            float start = 0, end = 0;
            for (var j = 0; j < walls.Count; j++)
            {
                if (j == index) continue;
                var other = walls[j];
                if ((other.B - other.A).sqrMagnitude < 1e-6f) continue;
                var otherDir = (other.B - other.A).normalized;
                if ((other.B - wall.A).sqrMagnitude < JoinM * JoinM) start = Mitre(otherDir, dir);
                if ((other.A - wall.B).sqrMagnitude < JoinM * JoinM) end = Mitre(dir, otherDir);
            }
            return (start, end);
        }

        // Wall ends closer than this meet at a corner
        private const float JoinM = 0.01f;
        // A corner sharper than about 2 degrees is treated as that sharp
        private const float MaxMitre = 60;

        // The tangent of half the turn from one wall to the next, left turns (outside corners) positive
        private static float Mitre(Vector2 incoming, Vector2 outgoing)
        {
            var sin = incoming.x * outgoing.y - incoming.y * outgoing.x;
            var cos = Vector2.Dot(incoming, outgoing);
            return 1 + cos < 1e-4f ? MaxMitre : Mathf.Clamp(sin / (1 + cos), -MaxMitre, MaxMitre);
        }

        // How far a square standing in a corner, flush with this wall, must move along it per metre of its
        // size to stay clear of the neighbouring wall: 0 at a square or blunter corner
        private static float Overhang(float mitre) => mitre > 1 ? (mitre * mitre - 1) / (2 * mitre) : 0;

        // The inner edge of a strip depthM deep along a wall, mitred at both ends; shallower if the wall
        // is too short for the mitres at full depth
        private static (Vector2 A, Vector2 B, float Depth) Inset(WallPiece wall, (float Start, float End) mitre, float depthM)
        {
            var along = wall.B - wall.A;
            var length = along.magnitude;
            var dir = along / length;
            var inward = new Vector2(-dir.y, dir.x);
            var shortening = Mathf.Max(0, mitre.Start) + Mathf.Max(0, mitre.End);
            var depth = shortening * depthM > length ? length / shortening : depthM;
            return (wall.A + inward * depth + dir * (mitre.Start * depth), wall.B + inward * depth - dir * (mitre.End * depth), depth);
        }

        // Under one wall of an open ground floor: the back wall set in from the outline (textured with the
        // style's wall), the ceiling over the gap, and columns along the outline, wide piers at its ends so
        // the corners stay closed
        private static void VoidDeck(WallPiece wall, (float Start, float End) mitre, float groundM, VoidDeckSpec deck, Paint paint, Lists lists)
        {
            Vector2 a = wall.A, b = wall.B;
            var along = b - a;
            var length = along.magnitude;
            if (length < 1e-3f) return;
            var dir = along / length;
            var inward = new Vector2(-dir.y, dir.x);
            float style = paint.Style;
            var accent = new Vector4(paint.Accent.r, paint.Accent.g, paint.Accent.b, 1);
            var plain = new Vector4(0, 0, style, 1);
            Vector3 At(Vector2 p, float y) => new Vector3(p.x, y, p.y);
            void Add(Vector3 p, Color color, Vector4 uv) { lists.Vertices.Add(p); lists.Colors.Add(color); lists.Uvs.Add(uv); lists.Accents.Add(accent); }
            var (ai, bi, depth) = Inset(wall, mitre, deck.DepthM);
            float alongA = wall.AlongA + mitre.Start * depth, alongB = wall.AlongB - mitre.End * depth;
            // The back wall, facing out, in the shade of the floor above
            var start = lists.Vertices.Count;
            var back = paint.Body * (1 - deck.Shade);
            Add(At(ai, 0), back * 0.85f, new Vector4(alongA, 0, style, 0)); Add(At(ai, groundM), back, new Vector4(alongA, groundM, style, 0));
            Add(At(bi, groundM), back, new Vector4(alongB, groundM, style, 0)); Add(At(bi, 0), back * 0.85f, new Vector4(alongB, 0, style, 0));
            lists.Triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
            // The ceiling, facing down
            var ceiling = Named(deck.CeilingColor, paint) * (1 - deck.Shade * 0.5f);
            start = lists.Vertices.Count;
            foreach (var p in new[] { a, b, bi, ai }) Add(At(p, groundM), ceiling, plain);
            lists.Triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
            // Columns: their outer faces on the outline, kept clear of the next wall at a sharp corner
            var columnColor = Named(deck.ColumnColor, paint);
            void Column(float from, float size)
            {
                var p0 = a + dir * from;
                var p1 = a + dir * (from + size);
                var corners = new[] { p0, p1, p1 + inward * size, p0 + inward * size };
                for (var c = 0; c < 4; c++)
                {
                    start = lists.Vertices.Count;
                    Vector2 p = corners[c], q = corners[(c + 1) % 4];
                    Add(At(p, 0), columnColor * 0.9f, plain); Add(At(p, groundM), columnColor, plain);
                    Add(At(q, groundM), columnColor, plain); Add(At(q, 0), columnColor * 0.9f, plain);
                    lists.Triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
                }
            }
            var corner = Mathf.Max(deck.ColumnM, deck.DepthM);
            float lo = Overhang(mitre.Start) * corner, hi = length - Overhang(mitre.End) * corner;
            corner = Mathf.Min(corner, (hi - lo) / 2);
            if (corner < 0.05f) return;
            Column(lo, corner);
            Column(hi - corner, corner);
            var gaps = Mathf.Max(1, Mathf.CeilToInt(length / Mathf.Max(deck.SpacingM, 0.5f)));
            for (var k = 1; k < gaps; k++)
            {
                var centre = length * k / gaps;
                if (centre - deck.ColumnM / 2 < lo + corner || centre + deck.ColumnM / 2 > hi - corner) continue;
                Column(centre - deck.ColumnM / 2, deck.ColumnM);
            }
        }

        // Drops corners while the outline stays within toleranceM of every original corner, so footprints
        // traced with tiny jogs read as clean blocks. Each removal is checked against the original corners
        // it skips, not just its current neighbours, so a curve cannot drift further than toleranceM.
        public static List<Vector2> Simplify(List<Vector2> points, float toleranceM)
        {
            var count = points.Count;
            // Indices of the corners kept, in order round the outline
            var kept = Enumerable.Range(0, count).ToList();
            bool Fits(int from, int to)
            {
                for (var j = (from + 1) % count; j != to; j = (j + 1) % count)
                    if (DistanceToSegment(points[j], points[from], points[to]) >= toleranceM) return false;
                return true;
            }
            for (var removed = true; removed && kept.Count > 3;)
            {
                removed = false;
                for (var i = 0; i < kept.Count && kept.Count > 3; i++)
                {
                    if (!Fits(kept[(i + kept.Count - 1) % kept.Count], kept[(i + 1) % kept.Count])) continue;
                    kept.RemoveAt(i);
                    removed = true;
                    i--;
                }
            }
            var result = kept.Select(i => points[i]).ToList();
            // Corners within the tolerance move the area little; much more means the outline folded
            float before = SignedArea(points), after = SignedArea(result);
            return Mathf.Sign(before) == Mathf.Sign(after) && Mathf.Abs(after - before) <= MaxSimplifyAreaShare * Mathf.Abs(before) ? result : points;
        }

        // Simplifying may change an outline's area by at most this share of it
        private const float MaxSimplifyAreaShare = 0.25f;

        // A colour named in the styles file: body, accent or roof (the building's own) or #RRGGBB
        private static Color Named(string name, Paint paint) =>
            name == "body" ? paint.Body : name == "accent" ? paint.Accent : name == "roof" ? paint.Roof : PaintColor(name, paint.Saturation);

        // The style's parapet round the roof and its rooftop structures, as plain-coloured surfaces
        private static void RoofDetails(List<WallPiece> walls, List<(float Start, float End)> mitres, List<Vector2> outline, List<Corridor> cuts, float height, Paint paint, Lists lists)
        {
            var spec = paint.Spec;
            var plain = new Vector4(0, 0, paint.Style, 1);
            var accent = new Vector4(paint.Accent.r, paint.Accent.g, paint.Accent.b, 1);
            // Corners in the order Wall uses (bottom of a, top of a, top of b, bottom of b), facing whoever
            // sees a on the left
            void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Color color)
            {
                var start = lists.Vertices.Count;
                foreach (var p in new[] { p0, p1, p2, p3 }) { lists.Vertices.Add(p); lists.Colors.Add(color); lists.Uvs.Add(plain); lists.Accents.Add(accent); }
                lists.Triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
            }
            // A flat, upward-facing piece with its corners anticlockwise seen from above
            void Cap(IReadOnlyList<Vector2> corners, float y, Color color)
            {
                var start = lists.Vertices.Count;
                foreach (var c in corners) { lists.Vertices.Add(new Vector3(c.x, y, c.y)); lists.Colors.Add(color); lists.Uvs.Add(plain); lists.Accents.Add(accent); }
                for (var j = 1; j + 1 < corners.Count; j++) lists.Triangles.AddRange(new[] { start, start + j + 1, start + j });
            }
            Vector3 At(Vector2 p, float y) => new Vector3(p.x, y, p.y);
            var parapet = spec.Parapet;
            var inset = 0f;
            if (parapet != null && parapet.HeightM > 0 && parapet.ThicknessM > 0)
            {
                var trim = Named(parapet.Color, paint);
                var top = height + parapet.HeightM;
                inset = parapet.ThicknessM;
                for (var i = 0; i < walls.Count; i++)
                {
                    Vector2 a = walls[i].A, b = walls[i].B;
                    if ((b - a).sqrMagnitude < 1e-6f) continue;
                    // The building is on the left of each wall; the inner face is mitred at the corners
                    var (ai, bi, _) = Inset(walls[i], mitres[i], parapet.ThicknessM);
                    Quad(At(a, height), At(a, top), At(b, top), At(b, height), trim);
                    Quad(At(bi, height), At(bi, top), At(ai, top), At(ai, height), trim * 0.9f);
                    Cap(new[] { a, b, bi, ai }, top, trim * 1.05f);
                }
            }
            if (spec.Rooftop.Count == 0) return;
            // Structures line up with the building's longest wall
            var longest = 0; var longestM = 0f;
            for (var i = 0; i < outline.Count; i++)
            {
                var m = (outline[(i + 1) % outline.Count] - outline[i]).magnitude;
                if (m > longestM) { longestM = m; longest = i; }
            }
            var u = (outline[(longest + 1) % outline.Count] - outline[longest]).normalized;
            var v = new Vector2(-u.y, u.x);
            float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
            foreach (var p in outline)
            {
                minU = Mathf.Min(minU, Vector2.Dot(p, u)); maxU = Mathf.Max(maxU, Vector2.Dot(p, u));
                minV = Mathf.Min(minV, Vector2.Dot(p, v)); maxV = Mathf.Max(maxV, Vector2.Dot(p, v));
            }
            var area = SignedArea(outline);
            var placed = new List<Rect>();
            var margin = inset + 0.4f;
            bool Clear(Vector2 p)
            {
                if (!Contains(outline, p) || cuts.Any(c => Contains(c.Corners, p))) return false;
                for (var i = 0; i < outline.Count; i++)
                    if (DistanceToSegment(p, outline[i], outline[(i + 1) % outline.Count]) < margin) return false;
                return true;
            }
            for (var item = 0; item < spec.Rooftop.Count; item++)
            {
                var structure = spec.Rooftop[item];
                if (area < structure.MinRoofM2 || structure.SizeM == null || structure.SizeM.Length != 3) continue;
                float w = structure.SizeM[0], d = structure.SizeM[1], h = structure.SizeM[2];
                var count = structure.Count + (int)(area * structure.PerThousandM2 / 1000f);
                var color = Named(structure.Color, paint);
                for (int n = 0, attempt = 0; n < count && attempt < 40 * count; attempt++)
                {
                    float cu = Mathf.Lerp(minU, maxU, Noise01(paint.Id, item * 1000 + attempt, 1));
                    float cv = Mathf.Lerp(minV, maxV, Noise01(paint.Id, item * 1000 + attempt, 2));
                    var rect = new Rect(cu - w / 2, cv - d / 2, w, d);
                    if (placed.Any(r => r.Overlaps(new Rect(rect.x - 0.6f, rect.y - 0.6f, rect.width + 1.2f, rect.height + 1.2f)))) continue;
                    var corners = new[] { u * rect.xMin + v * rect.yMin, u * rect.xMax + v * rect.yMin, u * rect.xMax + v * rect.yMax, u * rect.xMin + v * rect.yMax };
                    if (!corners.All(Clear) || !Clear(u * cu + v * cv)) continue;
                    placed.Add(rect);
                    n++;
                    for (var k = 0; k < 4; k++)
                        Quad(At(corners[k], height), At(corners[k], height + h), At(corners[(k + 1) % 4], height + h), At(corners[(k + 1) % 4], height), color * 0.92f);
                    Cap(corners, height + h, color);
                }
            }
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var t = ab.sqrMagnitude < 1e-8f ? 0 : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return (p - (a + ab * t)).magnitude;
        }

        // 0 to 1, the same for the same building and numbers
        private static float Noise01(string id, int a, int b)
        {
            unchecked
            {
                var hash = 2166136261u;
                foreach (var c in id) hash = (hash ^ c) * 16777619u;
                hash = (hash ^ (uint)a) * 16777619u;
                hash = (hash ^ (uint)b) * 16777619u;
                hash ^= hash >> 15; hash *= 2246822519u; hash ^= hash >> 13;
                return (hash & 0xffffff) / 16777216f;
            }
        }

        // The parts of a line that no corridor covers
        private static List<(Vector2 From, Vector2 To)> Outside(Vector2 a, Vector2 b, List<Corridor> cuts)
        {
            var parts = new List<(float From, float To)> { (0, 1) };
            foreach (var cut in cuts)
            {
                if (!Crosses(a, b, cut.Corners, out var enter, out var leave)) continue;
                parts = parts.SelectMany(p => new[] { (p.From, Mathf.Min(p.To, enter)), (Mathf.Max(p.From, leave), p.To) }).ToList();
            }
            return ToSegments(a, b, parts);
        }

        // Where a line runs through a corridor, as fractions of the line
        private static bool Crosses(Vector2 a, Vector2 b, Vector2[] corners, out float enter, out float leave)
        {
            enter = 0; leave = 1;
            for (var i = 0; i < corners.Length; i++)
            {
                var inward = Inward(corners[i], corners[(i + 1) % corners.Length]);
                var fromA = Vector2.Dot(a - corners[i], inward) - EdgeToleranceM;
                var fromB = Vector2.Dot(b - corners[i], inward) - EdgeToleranceM;
                if (fromA <= 0 && fromB <= 0) return false;
                if (fromA < 0) enter = Mathf.Max(enter, fromA / (fromA - fromB));
                else if (fromB < 0) leave = Mathf.Min(leave, fromA / (fromA - fromB));
            }
            return enter < leave;
        }

        // The parts of a line inside an outline
        private static List<(Vector2 From, Vector2 To)> InsideOutline(Vector2 a, Vector2 b, List<Vector2> outline)
        {
            var stops = new List<float> { 0, 1 };
            var along = b - a;
            for (var i = 0; i < outline.Count; i++)
            {
                Vector2 c = outline[i], side = outline[(i + 1) % outline.Count] - c;
                var denominator = along.x * side.y - along.y * side.x;
                if (Mathf.Approximately(denominator, 0)) continue;
                var onLine = ((c.x - a.x) * side.y - (c.y - a.y) * side.x) / denominator;
                var onSide = ((c.x - a.x) * along.y - (c.y - a.y) * along.x) / denominator;
                if (onLine > 0 && onLine < 1 && onSide >= 0 && onSide <= 1) stops.Add(onLine);
            }
            stops.Sort();
            var parts = new List<(float From, float To)>();
            for (var i = 0; i + 1 < stops.Count; i++)
                if (Contains(outline, a + along * ((stops[i] + stops[i + 1]) / 2))) parts.Add((stops[i], stops[i + 1]));
            return ToSegments(a, b, parts);
        }

        private static List<(Vector2 From, Vector2 To)> ToSegments(Vector2 a, Vector2 b, List<(float From, float To)> parts) =>
            parts.Where(p => p.To > p.From).Select(p => (From: Vector2.LerpUnclamped(a, b, p.From), To: Vector2.LerpUnclamped(a, b, p.To)))
                .Where(s => (s.To - s.From).magnitude > MinWallM).ToList();

        private static bool Contains(IReadOnlyList<Vector2> outline, Vector2 point)
        {
            var inside = false;
            for (var i = 0; i < outline.Count; i++)
            {
                Vector2 a = outline[i], b = outline[(i + 1) % outline.Count];
                if ((a.y > point.y) != (b.y > point.y) && point.x < a.x + (b.x - a.x) * (point.y - a.y) / (b.y - a.y)) inside = !inside;
            }
            return inside;
        }

        // A convex piece of roof minus a corridor, as convex pieces: what lies beyond each edge in turn
        private static List<List<Vector2>> Subtract(List<Vector2> piece, Vector2[] corners)
        {
            var kept = new List<List<Vector2>>();
            var rest = piece;
            for (var i = 0; i < corners.Length && rest.Count >= 3; i++)
            {
                var inward = Inward(corners[i], corners[(i + 1) % corners.Length]);
                var inside = new List<Vector2>(); var beyond = new List<Vector2>();
                for (var j = 0; j < rest.Count; j++)
                {
                    Vector2 a = rest[j], b = rest[(j + 1) % rest.Count];
                    float fromA = Vector2.Dot(a - corners[i], inward), fromB = Vector2.Dot(b - corners[i], inward);
                    if (fromA >= 0) inside.Add(a);
                    if (fromA <= 0) beyond.Add(a);
                    if (fromA * fromB >= 0) continue;
                    var crossing = Vector2.Lerp(a, b, fromA / (fromA - fromB));
                    inside.Add(crossing); beyond.Add(crossing);
                }
                if (beyond.Count >= 3 && Mathf.Abs(SignedArea(beyond)) > MinRoofM2) kept.Add(beyond);
                rest = inside;
            }
            // It never reached the corridor, so leave it whole rather than in needless pieces
            if (rest.Count < 3 || Mathf.Abs(SignedArea(rest)) <= MinRoofM2) return new List<List<Vector2>> { piece };
            return kept;
        }

        private static Vector2 Inward(Vector2 from, Vector2 to)
        {
            var along = (to - from).normalized;
            return new Vector2(-along.y, along.x);
        }

        public static float SignedArea(IReadOnlyList<Vector2> points)
        {
            var area = 0f;
            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i]; var b = points[(i + 1) % points.Count];
                area += a.x * b.y - b.x * a.y;
            }
            return area / 2;
        }

        // Ear clipping for an anticlockwise outline; triangle indices come out clockwise, for a roof
        // seen from above. Real outlines can touch themselves, so anything left over is fanned.
        public static List<int> Triangulate(IReadOnlyList<Vector2> points)
        {
            var result = new List<int>();
            var remaining = Enumerable.Range(0, points.Count).ToList();
            var guard = points.Count * points.Count;
            var at = 0;
            while (remaining.Count > 3 && guard-- > 0)
            {
                var previous = remaining[(at + remaining.Count - 1) % remaining.Count];
                var current = remaining[at % remaining.Count];
                var next = remaining[(at + 1) % remaining.Count];
                if (IsEar(points, remaining, previous, current, next))
                {
                    result.AddRange(new[] { previous, next, current });
                    remaining.RemoveAt(at % remaining.Count);
                    at = 0;
                }
                else at = (at + 1) % remaining.Count;
            }
            for (var i = 1; i + 1 < remaining.Count; i++) result.AddRange(new[] { remaining[0], remaining[i + 1], remaining[i] });
            return result;
        }

        private static bool IsEar(IReadOnlyList<Vector2> points, List<int> remaining, int previous, int current, int next)
        {
            Vector2 a = points[previous], b = points[current], c = points[next];
            if ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) <= 0) return false;
            foreach (var index in remaining)
            {
                if (index == previous || index == current || index == next) continue;
                if (Inside(points[index], a, b, c)) return false;
            }
            return true;
        }

        private static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float Side(Vector2 u, Vector2 v) => (v.x - u.x) * (p.y - u.y) - (v.y - u.y) * (p.x - u.x);
            return Side(a, b) >= 0 && Side(b, c) >= 0 && Side(c, a) >= 0;
        }
    }
}
