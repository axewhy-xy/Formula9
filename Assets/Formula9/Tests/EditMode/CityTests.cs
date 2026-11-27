using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Formula9.Tests
{
    public class CityTests
    {
        private static CityBuilding Square(string id, double height, bool reversed = false)
        {
            var ring = new List<(double X, double Y)> { (0, 0), (10, 0), (10, 10), (0, 10), (0, 0) };
            if (reversed) ring.Reverse();
            return new CityBuilding { Id = id, Type = "residential", Name = "", HeightM = height, Rings = new List<List<(double X, double Y)>> { ring } };
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ASquareBecomesFourWallsAndARoofFacingOutwards(bool reversed)
        {
            var mesh = CityMesh.Build(new[] { Square("way/1", 30, reversed) });
            try
            {
                Assert.AreEqual(4 * 4 + 4, mesh.vertexCount, "four separate walls and a roof");
                Assert.AreEqual((4 * 2 + 2) * 3, mesh.triangles.Length);
                Assert.AreEqual(30, mesh.bounds.max.y, 1e-3f);
                Assert.AreEqual(0, mesh.bounds.min.y, 1e-3f);
                var normals = mesh.normals; var vertices = mesh.vertices;
                var center = new Vector3(5, 0, 5);
                for (var i = 0; i < 16; i++)
                {
                    var away = new Vector3(vertices[i].x, 0, vertices[i].z) - center;
                    Assert.Greater(Vector3.Dot(normals[i], away), 0, "walls face out");
                }
                for (var i = 16; i < 20; i++) Assert.Greater(normals[i].y, 0.9f, "the roof faces up");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void WallsAreMeasuredInMetresRoundTheOutlineAndRoofsAreMarked()
        {
            var mesh = CityMesh.Build(new[] { Square("way/1", 30) });
            try
            {
                var uvs = new List<Vector4>();
                mesh.GetUVs(0, uvs);
                Assert.AreEqual(mesh.vertexCount, uvs.Count, "one entry per vertex");
                // Each wall is (start, bottom), (start, top), (end, top), (end, bottom), and the four
                // walls carry on from each other round the 40 m outline
                for (var wall = 0; wall < 4; wall++)
                {
                    float start = wall * 10, end = start + 10;
                    // A 30 m residential block is an HDB tower, so its walls carry the HDB style
                    float set = CityMesh.StyleFor(Square("way/1", 30));
                    StringAssert.StartsWith("hdb", BuildingStyles.Default.Styles[(int)set].Name);
                    var expected = new[] { new Vector4(start, 0, set, 0), new Vector4(start, 30, set, 0), new Vector4(end, 30, set, 0), new Vector4(end, 0, set, 0) };
                    for (var corner = 0; corner < 4; corner++)
                        Assert.Less(Vector4.Distance(expected[corner], uvs[wall * 4 + corner]), 1e-3f, $"wall {wall}, corner {corner}");
                }
                for (var i = 16; i < 20; i++) Assert.AreEqual(1, uvs[i].w, "the roof is marked to stay plain");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [TestCase("residential", 40, "hdb")]
        [TestCase("residential", 12, "lowrise")]
        [TestCase("apartments", 40, "home")]
        [TestCase("apartments", 12, "lowrise")]
        [TestCase("house", 8, "lowrise")]
        [TestCase("terrace", 8, "lowrise")]
        [TestCase("office", 12, "office")]
        [TestCase("commercial", 20, "office")]
        [TestCase("yes", 60, "office")]
        [TestCase("yes", 12, "other")]
        [TestCase("retail", 6, "other")]
        [TestCase("garage", 4, "other")]
        public void EachKindOfBuildingUsesItsOwnTextures(string type, float heightM, string style)
        {
            var building = Square("way/1", heightM);
            building.Type = type;
            var mesh = CityMesh.Build(new[] { building });
            try
            {
                var uvs = new List<Vector4>();
                mesh.GetUVs(0, uvs);
                // A rule may offer several styles of the kind, such as two HDB designs
                var index = CityMesh.StyleFor(building);
                StringAssert.StartsWith(style, BuildingStyles.Default.Styles[index].Name);
                Assert.IsTrue(uvs.All(uv => uv.z == index));
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void ABuildingIsPaintedFromItsStylesColoursAndHasTangentsForTheNormalMaps()
        {
            var styles = BuildingStyles.Default;
            var hdb = styles.Styles[CityMesh.StyleFor(Square("way/1", 30))];
            var mesh = CityMesh.Build(new[] { Square("way/1", 30) });
            try
            {
                Color Expected(string hex) => CityMesh.PaintColor(hex, styles.ColorSaturation);
                var body = Expected(BuildingStyles.Pick(hdb.BodyColors, "way/1", 1));
                var accent = Expected(BuildingStyles.Pick(hdb.AccentColors, "way/1", 2));
                var colors = new List<Color>();
                mesh.GetColors(colors);
                // The top of a wall carries the body colour as it is; lower down it is shaded
                Assert.Less(Mathf.Abs(colors[1].r - body.r) + Mathf.Abs(colors[1].g - body.g) + Mathf.Abs(colors[1].b - body.b), 1e-3f);
                var accents = new List<Vector4>();
                mesh.GetUVs(3, accents);
                Assert.AreEqual(mesh.vertexCount, accents.Count);
                Assert.IsTrue(accents.All(a => Mathf.Abs(a.x - accent.r) + Mathf.Abs(a.y - accent.g) + Mathf.Abs(a.z - accent.b) < 1e-3f));
                Assert.AreEqual(mesh.vertexCount, mesh.tangents.Length);
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void WithDetailsAnHdbBlockGetsAParapetRooftopStructuresAndAVoidDeckInsideItsOutline()
        {
            var block = Square("way/7", 40);
            block.Rings[0] = new List<(double X, double Y)> { (0, 0), (40, 0), (40, 15), (0, 15), (0, 0) };
            var bare = CityMesh.Build(new[] { block });
            var detailed = CityMesh.Build(new[] { block }, details: true);
            try
            {
                var hdb = BuildingStyles.Default.Styles[CityMesh.StyleFor(block)];
                Assert.Greater(detailed.vertexCount, bare.vertexCount);
                Assert.AreEqual(40 + hdb.Rooftop.Max(r => r.SizeM[2]), detailed.bounds.max.y, 1e-3f, "the tallest rooftop structure stands on the roof");
                var vertices = detailed.vertices;
                foreach (var v in vertices)
                {
                    Assert.That(v.x, Is.InRange(-1e-3f, 40 + 1e-3f), "everything stays within the outline");
                    Assert.That(v.z, Is.InRange(-1e-3f, 15 + 1e-3f));
                }
                Assert.IsTrue(vertices.Any(v => Mathf.Abs(v.y - (40 + hdb.Parapet.HeightM)) < 1e-3f && v.x < 1e-3f), "a parapet stands round the edge");
                var uvs = new List<Vector4>();
                detailed.GetUVs(0, uvs);
                Assert.IsTrue(Enumerable.Range(0, vertices.Length).Where(i => vertices[i].y > 40 + 1e-3f).All(i => uvs[i].w == 1), "what stands on the roof is plain colour");
                // The void deck: walls above it start at the ground floor's height, its back wall stands
                // inside the outline, and its ceiling faces down
                var deck = hdb.VoidDeck;
                bool At(float x, float y, float z) => vertices.Any(v => (v - new Vector3(x, y, z)).magnitude < 1e-3f);
                // mitred at the corners, so it meets the short sides' back walls rather than crossing them
                Assert.IsTrue(At(deck.DepthM, 0, deck.DepthM) && At(40 - deck.DepthM, 0, deck.DepthM) && At(40 - deck.DepthM, hdb.GroundFloorM, deck.DepthM), "the back wall of the long side is set in");
                var triangles = detailed.triangles;
                var down = 0;
                for (var i = 0; i < triangles.Length; i += 3)
                    if (Vector3.Cross(vertices[triangles[i + 1]] - vertices[triangles[i]], vertices[triangles[i + 2]] - vertices[triangles[i]]).y < -1e-3f) down++;
                Assert.Greater(down, 0, "the void deck has a ceiling");
            }
            finally { UnityEngine.Object.DestroyImmediate(bare); UnityEngine.Object.DestroyImmediate(detailed); }
        }

        [Test]
        public void WithDetailsAWedgeShapedBlockKeepsItsVoidDeckAndParapetInsideItsOutline()
        {
            // Two 45 degree corners, where insets set square to each wall would poke out through the next
            var block = Square("way/8", 40);
            block.Rings[0] = new List<(double X, double Y)> { (0, 0), (60, 0), (0, 60), (0, 0) };
            var mesh = CityMesh.Build(new[] { block }, details: true);
            try
            {
                Assert.IsNotNull(BuildingStyles.Default.Styles[CityMesh.StyleFor(block)].VoidDeck, "the style has a void deck to test");
                foreach (var v in mesh.vertices)
                {
                    Assert.GreaterOrEqual(v.x, -1e-3f, "inside the outline: " + v);
                    Assert.GreaterOrEqual(v.z, -1e-3f, "inside the outline: " + v);
                    Assert.LessOrEqual(v.x + v.z, 60 + 1e-3f, "inside the outline: " + v);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void SimplifyingACurveStaysWithinTheTolerance()
        {
            // A round building traced with 32 corners: removals must not add up past the tolerance
            var circle = Enumerable.Range(0, 32).Select(k => new Vector2(10 * Mathf.Cos(k * Mathf.PI / 16), 10 * Mathf.Sin(k * Mathf.PI / 16))).ToList();
            var simple = CityMesh.Simplify(circle, 0.8f);
            Assert.Less(simple.Count, circle.Count, "small steps round the curve are dropped");
            foreach (var p in circle)
            {
                var nearest = Enumerable.Range(0, simple.Count).Min(i => DistanceToSegment(p, simple[i], simple[(i + 1) % simple.Count]));
                Assert.Less(nearest, 0.8f, "every original corner stays within the tolerance");
            }
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return (p - (a + ab * t)).magnitude;
        }

        [Test]
        public void ACachedFillOnlyRebuildsTheBuildingsWhoseCorridorsChanged()
        {
            var near = Square("way/1", 30);
            var far = Square("way/2", 30);
            far.Rings[0] = far.Rings[0].Select(p => (p.X + 500, p.Y)).ToList();
            var buildings = new List<CityBuilding> { near, far };
            var cache = new CityMesh.Cache();
            var mesh = CityMesh.Build(buildings, details: true, cache: cache);
            var corridors = CityMesh.Corridors(new[] { (IReadOnlyList<Vector2>)new[] { new Vector2(5, -20), new Vector2(5, 30) } }, 2);
            var fresh = CityMesh.Build(buildings, corridors, details: true);
            try
            {
                Assert.AreEqual(2, cache.LastBuilt);
                CityMesh.Fill(mesh, buildings, corridors, details: true, cache: cache);
                Assert.AreEqual(1, cache.LastBuilt, "only the building the corridor crosses");
                CollectionAssert.AreEqual(fresh.vertices, mesh.vertices, "the same mesh as building it all again");
                CollectionAssert.AreEqual(fresh.triangles, mesh.triangles);
                CollectionAssert.AreEqual(fresh.normals, mesh.normals);
                CityMesh.Fill(mesh, buildings, CityMesh.Corridors(new[] { (IReadOnlyList<Vector2>)new[] { new Vector2(5, -20), new Vector2(5, 30) } }, 2), details: true, cache: cache);
                Assert.AreEqual(0, cache.LastBuilt, "the same corridors again change nothing");
                CityMesh.Fill(mesh, buildings, null, details: true, cache: cache);
                Assert.AreEqual(1, cache.LastBuilt, "the corridor going gives the building back");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); UnityEngine.Object.DestroyImmediate(fresh); }
        }

        [Test]
        public void TangentsRunAlongTheWall()
        {
            var mesh = CityMesh.Build(new[] { Square("way/1", 30) });
            try
            {
                var vertices = mesh.vertices; var tangents = mesh.tangents; var normals = mesh.normals;
                Assert.AreEqual(vertices.Length, tangents.Length);
                // The first wall runs from (0, 0) to (10, 0): along +x, and up the wall is the texture's v
                for (var i = 0; i < 4; i++)
                {
                    Assert.AreEqual(1, tangents[i].x, 1e-4f);
                    var up = Vector3.Cross(normals[i], tangents[i]) * tangents[i].w;
                    Assert.Greater(up.y, 0.99f, "the bitangent points up the wall");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void ABrokenStylesFileFallsBackToOnePlainStyle()
        {
            var styles = BuildingStyles.Fallback();
            Assert.IsTrue(styles.IsFallback);
            Assert.AreEqual(1, styles.Styles.Count);
            Assert.IsFalse(BuildingStyles.Default.IsFallback, "the project's own file reads");
            var mesh = CityMesh.Build(new[] { Square("way/1", 30) }, styles: styles, details: true);
            try { Assert.AreEqual(30, mesh.bounds.max.y, 1e-3f, "buildings still stand"); }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void TheWorldsLightingIsPutBackWhenItsSunGoes()
        {
            var rig = new GameObject("Lighting rig");
            var distance = QualitySettings.shadowDistance;
            var cascades = QualitySettings.shadowCascades;
            var shadows = QualitySettings.shadows;
            var anisotropic = QualitySettings.anisotropicFiltering;
            var skybox = RenderSettings.skybox;
            var ambient = RenderSettings.ambientSkyColor;
            var fog = RenderSettings.fog;
            Material sky;
            try
            {
                WorldLighting.Apply(rig.transform);
                sky = RenderSettings.skybox;
                Assert.AreEqual(ShadowQuality.All, QualitySettings.shadows, "the sun's shadows show at any quality level");
                Assert.AreEqual(AnisotropicFiltering.Enable, QualitySettings.anisotropicFiltering, "facades stay sharp at any quality level");
            }
            finally { UnityEngine.Object.DestroyImmediate(rig); }
            Assert.AreEqual(distance, QualitySettings.shadowDistance);
            Assert.AreEqual(cascades, QualitySettings.shadowCascades);
            Assert.AreEqual(shadows, QualitySettings.shadows);
            Assert.AreEqual(anisotropic, QualitySettings.anisotropicFiltering);
            Assert.AreEqual(skybox, RenderSettings.skybox);
            Assert.AreEqual(ambient, RenderSettings.ambientSkyColor);
            Assert.AreEqual(fog, RenderSettings.fog);
            if (sky != skybox) Assert.IsTrue(sky == null, "the world's sky material is destroyed with it");
        }

        [Test]
        public void SimplifyingDropsSmallJogsButKeepsRealCorners()
        {
            var outline = new List<Vector2> { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 4.8f), new Vector2(10.4f, 5), new Vector2(10, 5.2f), new Vector2(10, 10), new Vector2(0, 10) };
            var simple = CityMesh.Simplify(outline, 0.8f);
            CollectionAssert.AreEqual(new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10) }, simple);
        }

        [Test]
        public void AListInTheStylesFileReplacesItsDefaultInsteadOfAddingToIt()
        {
            var styles = BuildingStyles.Parse("{\"defaultStyle\":\"a\",\"styles\":[{\"name\":\"a\",\"accentColors\":[\"#123456\"]}]}");
            CollectionAssert.AreEqual(new[] { "#123456" }, styles.Styles[0].AccentColors);
        }

        [Test]
        public void TheStylesFileNamesEveryStyleItsRulesUse()
        {
            var styles = BuildingStyles.Default;
            Assert.LessOrEqual(styles.Styles.Count, BuildingStyles.MaxStyles);
            foreach (var rule in styles.Rules)
                foreach (var name in rule.Choices.Count > 0 ? rule.Choices : new List<string> { rule.Style })
                    Assert.DoesNotThrow(() => styles.IndexOf(name), name);
            Assert.AreEqual(styles.IndexOf(styles.DefaultStyle), styles.StyleFor("something-new", 10), "an unknown type takes the default style");
        }

        [Test]
        public void AWallCutForARouteKeepsItsPlaceAlongTheOutline()
        {
            // The route cuts x from 4 to 6 out of the south wall, so its east piece starts 6 m along
            var mesh = CityMesh.Build(new[] { Square("way/1", 30) }, CityMesh.Corridors(new[] { new[] { new Vector2(5, -5), new Vector2(5, 15) } }, 1));
            try
            {
                var uvs = new List<Vector4>();
                mesh.GetUVs(0, uvs);
                var vertices = mesh.vertices; var normals = mesh.normals;
                Assert.AreEqual(vertices.Length, uvs.Count);
                // The south wall's pieces, facing south; the outline starts at its west end, (0, 0)
                var south = Enumerable.Range(0, vertices.Length).Where(i => uvs[i].w == 0 && Mathf.Abs(vertices[i].z) < 1e-3f && normals[i].z < -0.9f).ToList();
                Assert.IsTrue(south.Any(i => vertices[i].x > 5.9f), "the piece east of the cut is there");
                Assert.IsTrue(south.All(i => Mathf.Abs(uvs[i].x - vertices[i].x) < 1e-3f), "measured from the outline's start, not restarted after the cut");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void AnLShapedRoofStaysInsideItsOutline()
        {
            var outline = new[] { new Vector2(0, 0), new Vector2(20, 0), new Vector2(20, 10), new Vector2(10, 10), new Vector2(10, 20), new Vector2(0, 20) };
            var triangles = CityMesh.Triangulate(outline);
            Assert.AreEqual((outline.Length - 2) * 3, triangles.Count);
            var area = 0f;
            for (var i = 0; i < triangles.Count; i += 3)
                area += Mathf.Abs(CityMesh.SignedArea(new[] { outline[triangles[i]], outline[triangles[i + 1]], outline[triangles[i + 2]] }));
            Assert.AreEqual(300, area, 1e-3f, "the triangles cover the L exactly, not the notch");
        }

        [Test]
        public void ABrokenOutlineIsLeftOutRatherThanThrown()
        {
            var broken = Square("way/2", 30);
            broken.Rings[0] = new List<(double X, double Y)> { (0, 0), (1, 1) };
            var mesh = CityMesh.Build(new[] { broken });
            try { Assert.AreEqual(0, mesh.vertexCount); }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        private static List<Corridor> CutAlong(params Vector2[] path) => CityMesh.Corridors(new[] { path }, 1);

        // The roof itself: upward faces at the lowest level any face up, so parapet tops and rooftop
        // structures standing on the roof are not counted
        private static float RoofArea(Mesh mesh, bool ceilings = false)
        {
            var vertices = mesh.vertices; var triangles = mesh.triangles;
            var up = new List<(Vector3 A, Vector3 B, Vector3 C)>();
            for (var i = 0; i < triangles.Length; i += 3)
            {
                Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
                var normal = Vector3.Cross(b - a, c - a);
                // Only void deck ceilings face down
                if (!ceilings) Assert.GreaterOrEqual(normal.y, -1e-3f, "nothing faces down");
                if (normal.y > 1e-3f) up.Add((a, b, c));
            }
            if (up.Count == 0) return 0;
            var roofY = up.Min(t => t.A.y);
            return up.Where(t => t.A.y < roofY + 1e-3f).Sum(t => Vector3.Cross(t.B - t.A, t.C - t.A).magnitude / 2);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ARouteThroughABuildingIsCutOutFromGroundToRoof(bool reversed)
        {
            var mesh = CityMesh.Build(new[] { Square("way/1", 30, reversed) }, CutAlong(new Vector2(5, -5), new Vector2(5, 15)));
            try
            {
                Assert.AreEqual(80, RoofArea(mesh), 1e-2f, "a 2 m slot is gone from the 10 m square");
                var vertices = mesh.vertices; var normals = mesh.normals; var triangles = mesh.triangles;
                for (var i = 0; i < triangles.Length; i += 3)
                {
                    var middle = (vertices[triangles[i]] + vertices[triangles[i + 1]] + vertices[triangles[i + 2]]) / 3;
                    Assert.IsFalse(middle.x > 4.01f && middle.x < 5.99f, "nothing is left over the route");
                }
                int west = 0, east = 0;
                for (var i = 0; i < vertices.Length; i++)
                {
                    if (Mathf.Approximately(vertices[i].x, 4) && normals[i].x > 0.9f) west++;
                    if (Mathf.Approximately(vertices[i].x, 6) && normals[i].x < -0.9f) east++;
                }
                Assert.AreEqual(4, west, "one new wall facing the route from the west");
                Assert.AreEqual(4, east, "and one from the east");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void ARouteClearOfABuildingLeavesItWhole()
        {
            var mesh = CityMesh.Build(new[] { Square("way/1", 30) }, CutAlong(new Vector2(20, -5), new Vector2(20, 15)));
            try { Assert.AreEqual(4 * 4 + 4, mesh.vertexCount); }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void ACanopyOverARouteGoesRatherThanLeavingStrips()
        {
            var canopy = Square("way/4", 15);
            canopy.Rings[0] = new List<(double X, double Y)> { (0, 0), (2.5, 0), (2.5, 10), (0, 10), (0, 0) };
            var mesh = CityMesh.Build(new[] { canopy }, CutAlong(new Vector2(1.25f, -5), new Vector2(1.25f, 15)));
            try { Assert.AreEqual(0, mesh.vertexCount, "only 0.25 m strips would be left of it"); }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void ARouteTurningInsideABuildingLeavesNothingInTheBend()
        {
            // In from the south, out through the east, turning at the middle
            var mesh = CityMesh.Build(new[] { Square("way/1", 30) }, CutAlong(new Vector2(5, -5), new Vector2(5, 5), new Vector2(15, 5)));
            try
            {
                Assert.AreEqual(100 - 12 - 12 + 4, RoofArea(mesh), 1e-2f, "both arms are gone, their overlap counted once");
                var vertices = mesh.vertices;
                for (var i = 0; i < vertices.Length; i++)
                    Assert.IsFalse(vertices[i].x > 4.01f && vertices[i].x < 5.99f && vertices[i].z > 0.01f && vertices[i].z < 5.99f, "nothing stands in the bend");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void ARouteAcrossAConcaveBuildingOnlyWallsWhatIsBuilt()
        {
            // A U open to the north; the route runs east across both arms and the gap between them
            var building = Square("way/3", 30);
            building.Rings[0] = new List<(double X, double Y)> { (0, 0), (30, 0), (30, 30), (20, 30), (20, 10), (10, 10), (10, 30), (0, 30), (0, 0) };
            var mesh = CityMesh.Build(new[] { building }, CutAlong(new Vector2(-5, 20), new Vector2(35, 20)));
            try
            {
                Assert.AreEqual(700 - 20 - 20, RoofArea(mesh), 1e-2f);
                var vertices = mesh.vertices;
                for (var i = 0; i < vertices.Length; i++)
                    Assert.IsFalse(vertices[i].x > 10.01f && vertices[i].x < 19.99f && vertices[i].z > 10.01f, "no new wall across the open gap");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        private static CityRoad Road(string kind, bool oneway, int lanes, long from, long to, params (double X, double Y)[] points) =>
            new CityRoad { Id = $"way/{from}/{to}", Class = kind, FromNode = from, ToNode = to, Oneway = oneway, Lanes = lanes, Points = points.ToList() };

        private static Bounds BoundsOf(Mesh mesh, int layer)
        {
            var vertices = mesh.vertices; var triangles = mesh.GetTriangles(layer);
            var bounds = new Bounds(vertices[triangles[0]], Vector3.zero);
            foreach (var index in triangles) bounds.Encapsulate(vertices[index]);
            return bounds;
        }

        [Test]
        public void ARoadIsAsWideAsItsLanesWithAKerbAndFacesUp()
        {
            var mesh = RoadMesh.Build(new[] { Road("residential", false, 2, 1, 2, (0, 0), (0, 100)) });
            try
            {
                Assert.AreEqual(RoadMesh.LayerCount, mesh.subMeshCount);
                var half = 2 * RoadMesh.LaneWidthM + RoadMesh.ShoulderM;
                Assert.AreEqual(half, BoundsOf(mesh, RoadMesh.AsphaltLayer).max.x, 1e-3f, "two lanes each way");
                Assert.AreEqual(half + RoadMesh.KerbM, BoundsOf(mesh, RoadMesh.KerbLayer).max.x, 1e-3f);
                Assert.AreEqual(0, mesh.GetTriangles(RoadMesh.MarkingLayer).Length, "legacy marking triangles are removed");
                Assert.AreEqual(107, BoundsOf(mesh, RoadMesh.AsphaltLayer).max.z, 1e-3f, "asphalt reaches the last endpoint");
                var uv1 = new List<Vector4>(); mesh.GetUVs(1, uv1);
                var uv2 = new List<Vector4>(); mesh.GetUVs(2, uv2);
                Assert.AreEqual(mesh.vertexCount, uv1.Count);
                Assert.AreEqual(mesh.vertexCount, uv2.Count);
                var asphalt = mesh.GetTriangles(RoadMesh.AsphaltLayer);
                var textured = asphalt.Where(i => Mathf.Abs(uv1[i].z + 4f) < 1e-4f && uv1[i].w > 0.5f).ToList();

                Assert.IsTrue(textured.Any(), "two-way road has four textured lanes");
                Assert.IsTrue(textured.Any(i => Mathf.Abs(uv1[i].x) < 1e-4f), "one texture edge present");
                Assert.IsTrue(textured.Any(i => Mathf.Abs(uv1[i].x - 2f * half) < 1e-4f), "opposite texture edge present");

                var distance = textured.Select(i => uv1[i].y).ToList();
                Assert.AreEqual(100f / 24f, distance.Max() - distance.Min(), 1e-4f,"texture distance follows the full road");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void AOneWayRoadSitsOnItsLineAndCountsLanesFromItsEdge()
        {
            var mesh = RoadMesh.Build(new[] { Road("primary", true, 3, 1, 2, (0, 0), (100, 0)) });
            try
            {
                var bounds = BoundsOf(mesh, RoadMesh.AsphaltLayer);
                var half = 1.5f * RoadMesh.LaneWidthM + RoadMesh.ShoulderM;
                Assert.AreEqual(half, bounds.max.z, 1e-3f);
                Assert.AreEqual(-half, bounds.min.z, 1e-3f);
                Assert.AreEqual(0, mesh.GetTriangles(RoadMesh.MarkingLayer).Length, "no legacy lane-marking geometry");
                var uv1 = new List<Vector4>(); mesh.GetUVs(1, uv1);
                var asphalt = mesh.GetTriangles(RoadMesh.AsphaltLayer);

                var vertices = mesh.vertices;
                var textured = asphalt.Where(i => uv1[i].w > 0.5f && Mathf.Abs(uv1[i].z - 3f) < 1e-4f).ToList();

                Assert.IsTrue(textured.Any(i => Mathf.Abs(uv1[i].x) < 1e-4f), "first edge: three one-way lanes");
                Assert.IsTrue(textured.Any(i => Mathf.Abs(uv1[i].x - 2f * half) < 1e-4f), "other edge: three one-way lanes");
                Assert.IsTrue(textured.Any(i => Mathf.Abs(vertices[i].x) < 1e-4f), "texture starts at the first road endpoint");
                Assert.IsTrue(textured.Any(i => Mathf.Abs(vertices[i].x - 100f) < 1e-4f), "texture reaches the far road endpoint");

                var distance = textured.Select(i => uv1[i].y).ToList();
                Assert.AreEqual(100f / 24f, distance.Max() - distance.Min(), 1e-4f, "texture distance follows the full road");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void LinesStopShortOfAJunctionButCarryOnWhereOneRoadBecomesTheNext()
        {
            var mesh = RoadMesh.Build(new[]
            {
                Road("primary", true, 2, 1, 2, (-100, 0), (0, 0)),
                Road("primary", true, 2, 2, 3, (0, 0), (100, 0)),
                Road("residential", false, 1, 2, 4, (0, 0), (0, 100)),
                Road("residential", false, 1, 5, 6, (500, 0), (500, 100)),
                Road("residential", false, 1, 6, 7, (500, 100), (500, 200)),
            });
            try
            {
                Assert.AreEqual(0, mesh.GetTriangles(RoadMesh.MarkingLayer).Length, "shader handles all ordinary marking fades");
                var uv1 = new List<Vector4>(); mesh.GetUVs(1, uv1);
                var uv2 = new List<Vector4>(); mesh.GetUVs(2, uv2);
                var vertices = mesh.vertices;
                var asphalt = mesh.GetTriangles(RoadMesh.AsphaltLayer);
                // At the three-arm node, both incoming and outgoing roads have a nonzero clearance.
                Assert.IsTrue(asphalt.Any(i =>
                    vertices[i].x < -95f &&
                    Mathf.Abs(uv1[i].z) > .5f &&
                    (uv2[i].z > .5f || uv2[i].w > .5f) &&
                    uv2[i].y < 100f),
                    "incoming lane paint ends before the three-way junction");
                Assert.IsTrue(asphalt.Any(i =>
                    vertices[i].x > 0f && vertices[i].x < 20f &&
                    Mathf.Abs(uv1[i].z) > .5f &&
                    (uv1[i].y < uv2[i].x || uv1[i].y > uv2[i].y)),
                    "outgoing lane paint begins after the junction");
                // At the two-arm node (500,100) neither road requires a junction cutoff.
               float asphaltHalf = RoadMesh.LaneWidthM + RoadMesh.ShoulderM;
                const float textureRepeatLengthM = 24f;

                bool UntrimmedAtNode(int i)
                {
                    float alongM = uv1[i].y * textureRepeatLengthM;
                    return Mathf.Abs(vertices[i].z - 100f) < 1e-3f &&
                        Mathf.Abs(Mathf.Abs(vertices[i].x - 500f) - asphaltHalf) < 1e-3f &&
                        Mathf.Abs(uv1[i].z) > 0.5f &&
                        alongM >= uv2[i].x - 1e-3f &&
                        alongM <= uv2[i].y + 1e-3f &&
                        uv2[i].z < 0.5f &&
                        uv2[i].w < 0.5f;
                }

                Assert.IsTrue(asphalt.Any(UntrimmedAtNode),
                    "lane paint continues through the two-road node");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }
        [Test]
        public void AServiceRoadHasNoLinesAndABrokenRoadIsLeftOut()
        {
            var mesh = RoadMesh.Build(new[]
            {
                Road("service", false, 1, 1, 2, (0, 0), (50, 0)),
                Road("residential", false, 1, 3, 4, (7, 7), (7, 7)),
            });
            try
            {
                Assert.Greater(mesh.GetTriangles(RoadMesh.AsphaltLayer).Length, 0);
                Assert.AreEqual(0, mesh.GetTriangles(RoadMesh.MarkingLayer).Length);
                Assert.Less(mesh.bounds.max.z, 6, "nothing at the broken road's point");
                var uv1 = new List<Vector4>(); mesh.GetUVs(1, uv1);
                var asphalt = mesh.GetTriangles(RoadMesh.AsphaltLayer);
                Assert.IsTrue(asphalt.All(i => Mathf.Abs(uv1[i].z) < 1e-4f), "service asphalt must disable textured lane markings");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }
        [Test]
        public void ClippedRoadIntersectionsHaveNoTJunctionVertices()
        {
            var mesh = RoadMesh.Build(new[]
            {
                Road("primary", true, 1, 1, 2, (-20, 0), (20, 0)),
                Road("residential", true, 1, 3, 4, (0, -20), (0, 20)),
            }, out int clippedTriangles);

            try
            {
                Assert.Greater(clippedTriangles, 0, "overlapping road triangles should be clipped");

                var vertices = mesh.vertices;
                var triangles = mesh.GetTriangles(RoadMesh.AsphaltLayer);
                var junctionVertices = triangles.Select(index =>
                        new Vector2(vertices[index].x, vertices[index].z))
                    .Distinct()
                    .Where(point => Mathf.Abs(point.x) < 5f && Mathf.Abs(point.y) < 5f)
                    .ToArray();

                Assert.Greater(junctionVertices.Length, 0);

                for (int i = 0; i < triangles.Length; i += 3)
                for (int edge = 0; edge < 3; edge++)
                {
                    var a3 = vertices[triangles[i + edge]];
                    var b3 = vertices[triangles[i + (edge + 1) % 3]];
                    var a = new Vector2(a3.x, a3.z);
                    var b = new Vector2(b3.x, b3.z);
                    var delta = b - a;
                    double length2 = delta.sqrMagnitude;
                    if (length2 < 1e-12) continue;

                    foreach (var point in junctionVertices)
                    {
                        if (point == a || point == b) continue;
                        var offset = point - a;
                        double t = Vector2.Dot(offset, delta) / length2;
                        double distance =
                            Math.Abs((double)delta.x * offset.y - (double)delta.y * offset.x) /
                            Math.Sqrt(length2);

                        Assert.IsFalse(t > 1e-5 && t < 1 - 1e-5 && distance < 1e-4,
                            $"Junction vertex {point} lies inside asphalt edge {a}-{b}");
                    }
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void DashStationContinuesAcrossConnectedRoadSegments()
        {
            var mesh = RoadMesh.Build(new[]
            {
                Road("primary", true, 2, 1, 2, (0, 0), (10, 0)),
                Road("primary", true, 2, 2, 3, (10, 0), (30, 0)),
            });

            try
            {
                var vertices = mesh.vertices;
                var lane = new List<Vector4>();
                mesh.GetUVs(1, lane);

                float width = 2f * RoadMesh.HalfWidth(
                    Road("primary", true, 2, 1, 2, (0, 0), (10, 0)));

                var joinStations = mesh.GetTriangles(RoadMesh.AsphaltLayer)
                    .Where(i => Mathf.Abs(vertices[i].x - 10f) < 1e-4f &&
                                Mathf.Abs(lane[i].w - width) < 1e-4f)
                    .Select(i => lane[i].y)
                    .Distinct()
                    .ToArray();

                Assert.GreaterOrEqual(joinStations.Length, 2);
                Assert.IsTrue(joinStations.Any(a => joinStations.Any(b =>
                {
                    if (Mathf.Abs(a - b) < 1e-5f) return false;
                    float difference = Mathf.Repeat(Mathf.Abs(a - b), 6f / 24f);
                    return difference < 1e-4f || 6f / 24f - difference < 1e-4f;
                })), "dash stations at a shared node retain the same six-metre phase");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [TestCase(8.5f, false, RoadMesh.ConcreteMedianLayer)]
        [TestCase(10.5f, true, RoadMesh.GreenMedianLayer)]
        public void ParallelOneWayRoadsBuildTheMatchingMedian(
            float separation, bool planted, int layer)
        {
            var mesh = RoadMesh.Build(new[]
            {
                Road("primary", true, 2, 1, 2, (0, 0), (0, 100)),
                Road("primary", true, 2, 3, 4, (separation, 100), (separation, 0)),
            });

            try
            {
                var triangles = mesh.GetTriangles(layer);
                Assert.Greater(triangles.Length, 0, "the sustained gap produces a median");
                Assert.AreEqual(0, mesh.GetTriangles(planted
                    ? RoadMesh.ConcreteMedianLayer
                    : RoadMesh.GreenMedianLayer).Length);

                var vertices = mesh.vertices;
                Assert.IsTrue(triangles.All(i =>
                    Mathf.Abs(vertices[i].y - (RoadMesh.SurfaceY - 0.04f)) < 1e-5f),
                    "median surfaces are recessed below the road");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

       [Test]
        public void ASharpBendHasNoSpikeAndOnlyUnjoinedEndsGetCaps()
        {
            var mesh = RoadMesh.Build(new[] { Road("service", true, 1, 1, 2, (0, 0), (50, 0), (50, 50)) });
            try
            {
                var half = RoadMesh.LaneWidthM / 2 + RoadMesh.ShoulderM;
                var bend = new Vector3(50, RoadMesh.SurfaceY, 0);
                var vertices = mesh.vertices;
                var asphalt = mesh.GetTriangles(RoadMesh.AsphaltLayer);

                Assert.IsFalse(asphalt.Any(i => vertices[i] == bend), "a joined bend does not get an end cap");
                Assert.IsTrue(asphalt.Any(i => vertices[i] == new Vector3(0, RoadMesh.SurfaceY, 0)),
                    "the unjoined start gets a cap");
                Assert.IsTrue(asphalt.Any(i => vertices[i] == new Vector3(50, RoadMesh.SurfaceY, 50)),
                    "the unjoined finish gets a cap");
                Assert.IsFalse(asphalt.Any(i =>
                    vertices[i].x > 50 + half * 2.01f || vertices[i].z < -half * 2.01f),
                    "the mitre is limited");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        private sealed class OneBuilding : IHttpTransport
        {
            public string Roads = "{\"roads\":[{\"id\":\"way/9/1\",\"class\":\"primary\",\"from_node\":1,\"to_node\":2,\"oneway\":true,\"lanes\":2,\"speed_kph\":50,\"points_m\":[[0,0],[0,40]]}]}";
            public int RoadStatus = 200;

            public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellation = default) =>
                Task.FromResult(request.Url.AbsolutePath.StartsWith("/api/roads/")
                    ? new HttpResponse { Status = RoadStatus, Body = Roads }
                    : new HttpResponse { Status = 200, Body = "{\"buildings\":[{\"id\":\"way/1\",\"type\":\"retail\",\"name\":\"\",\"height_m\":30,\"rings_m\":[[[0,0],[10,0],[10,10],[0,10],[0,0]]]}]}" });
        }

        [Test]
        public void TheCityLaysItsRoadsOverTheGround()
        {
            var rig = new GameObject("City road rig");
            var view = new CityView(rig.transform, null);
            try
            {
                Assert.IsTrue(view.LoadAsync(new CityClient(new OneBuilding(), new Uri("http://localhost")), "toa-payoh", CancellationToken.None).IsCompleted);
                Assert.AreEqual(1, view.RoadCount);
                var materials = rig.GetComponentsInChildren<MeshRenderer>().First(r => r.name == "Roads").sharedMaterials;
                Assert.AreEqual(RoadMesh.LayerCount, materials.Length);
                Assert.Less(materials[RoadMesh.AsphaltLayer].renderQueue, materials[RoadMesh.MarkingLayer].renderQueue);
                Assert.Less(materials[RoadMesh.KerbLayer].renderQueue, materials[RoadMesh.AsphaltLayer].renderQueue);
               
                Assert.Greater(materials[RoadMesh.KerbLayer].renderQueue, (int)RenderQueue.Geometry, "after the ground and buildings");
            }
            finally
            {
                view.Dispose();
                UnityEngine.Object.DestroyImmediate(rig);
            }
        }
        [Test]
        public void CentreDividerMaskBoundsUseTheAreaMapExtent()
        {
            var areaBounds = new Newtonsoft.Json.Linq.JArray(100, -40, 500, 60);
            Assert.IsTrue(CityView.TryCreateSurveyMaskBounds(areaBounds, out var maskBounds));
            Assert.AreEqual(100f, maskBounds.x);
            Assert.AreEqual(-40f, maskBounds.y);
            Assert.AreEqual(1f / 400f, maskBounds.z);
            Assert.AreEqual(1f / 100f, maskBounds.w);
            Assert.IsFalse(CityView.TryCreateSurveyMaskBounds(
                new Newtonsoft.Json.Linq.JArray(100, -40, 100, 60), out _));
        }

        [Test]
        public void BuildingsGetTheirWallAndWindowTexturesAndMarkersStayPlain()
        {
            var rig = new GameObject("City texture rig");
            var view = new CityView(rig.transform, null);
            try
            {
                Assert.IsTrue(view.LoadAsync(new CityClient(new OneBuilding(), new Uri("http://localhost")), "toa-payoh", CancellationToken.None).IsCompleted);
                var buildings = rig.GetComponentsInChildren<MeshRenderer>().First(r => r.name == "Buildings").sharedMaterial;
                Assert.AreEqual(4, CityView.BuildingTextures.Length, "walls and facades, each with a normal map");
                foreach (var property in CityView.BuildingTextures.Select(t => t.Property))
                {
                    Assert.IsTrue(buildings.HasProperty(property), property + " is in the shader");
                    var texture = buildings.GetTexture(property) as Texture2DArray;
                    Assert.IsNotNull(texture, property);
                    Assert.GreaterOrEqual(texture.depth, BuildingStyles.Default.Styles.Count, property + " has a slice per style");
                    Assert.AreEqual(TextureWrapMode.Repeat, texture.wrapMode, property + " tiles along the wall");
                }
                Assert.AreEqual(0, buildings.GetFloat("_Plain"));
                foreach (var file in CityView.BuildingTextures.Select(t => t.File))
                {
                    var importer = (UnityEditor.TextureImporter)UnityEditor.AssetImporter.GetAtPath($"Assets/Formula9/Resources/Formula9/Textures/{file}.png");
                    Assert.IsTrue(importer.ignoreMipmapLimit, file + " keeps full resolution at low quality levels");
                }

                var snapshot = (FullSnapshot)ContractParser.ParseMessage(
                    File.ReadAllText(Path.Combine(Application.dataPath,
                        "Formula9/Tests/EditMode/Fixtures/snapshot.json")));

                snapshot.Routes.Single(r => r.RouteId == "route-a").StopsM.Add(new RouteStop
                {
                    StopId = "stop-1",
                    Position = new Position { XM = 10, YM = 0 }
                });

                view.Show(snapshot, new ShuttleMotion(), 0);

                Assert.IsTrue(
                    snapshot.Vehicles.Any(v => v.VehicleId == "shuttle-01"),
                    "the fixture contains shuttle-01");
                Assert.IsTrue(
                    view.TryGetShuttle("shuttle-01", out _),
                    "the snapshot's shuttle is shown");
            }
            finally
            {
                view.Dispose();
                UnityEngine.Object.DestroyImmediate(rig);
            }
        }

        [TestCase(404, "{\"detail\":\"No road data\"}")]
        [TestCase(200, "{\"roads\":[{\"id\":\"way/9/1\"}]}")]
        public void ACityWithoutRoadDataStillStands(int status, string body)
        {
            var rig = new GameObject("City road failure rig");
            var view = new CityView(rig.transform, null);
            try
            {
                var load = view.LoadAsync(new CityClient(new OneBuilding { RoadStatus = status, Roads = body }, new Uri("http://localhost")), "toa-payoh", CancellationToken.None);
                Assert.IsTrue(load.IsCompleted && !load.IsFaulted);
                Assert.AreEqual(1, view.BuildingCount);
                Assert.AreEqual(0, view.RoadCount);
                Assert.IsFalse(rig.GetComponentsInChildren<MeshRenderer>().Any(r => r.name == "Roads"));
            }
            finally
            {
                view.Dispose();
                UnityEngine.Object.DestroyImmediate(rig);
            }
        }

        [Test]
        public void EveryDepotStandsInTheCityWithAChargerPostThatShowsWhenItIsTaken()
        {
            var rig = new GameObject("City depot rig");
            var view = new CityView(rig.transform, null);
            try
            {
                var snapshot = (FullSnapshot)ContractParser.ParseMessage(File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/snapshot.json")));
                snapshot.Chargers.Add(new Charger { ChargerId = "charger-3", AggregateRevision = 1, PortCount = 1, PowerKw = 50, OccupiedVehicleIds = new List<string>() });
                snapshot.Depots.Add(new Depot { DepotId = "depot-2", Position = new Position { XM = 900, YM = -600 }, ChargerIds = new List<string> { "charger-3" } });
                view.Show(snapshot, new ShuttleMotion(), 0);
                Assert.AreEqual(2, view.DepotCount);
                var pad = rig.GetComponentsInChildren<Transform>().Single(t => t.name == "Depot depot-2");
                Assert.AreEqual(new Vector3(900, 0.2f, -600), pad.position, "the pad lies on the ground at the backend's position");
                Renderer Post(string id) => rig.GetComponentsInChildren<MeshRenderer>().Single(r => r.name == "Charger " + id);
                Assert.AreEqual(904.5f, Post("charger-3").transform.position.x, 1e-3f, "a depot's only bay is at its middle, its post beside it");
                Assert.AreEqual(-600, Post("charger-3").transform.position.z, 1e-3f, "level with the bay, not in the lane in or out");
                var first = Post("charger-1").transform.position.x - (float)snapshot.Depots[0].Position.XM;
                Assert.AreEqual(-19.5f, first, 1e-3f, "two bays sit 15 m either side, as the backend lays them out, each post on its outer side");
                var free = Post("charger-3").sharedMaterial.color;
                snapshot.Chargers.Single(c => c.ChargerId == "charger-3").OccupiedVehicleIds.Add("shuttle-01");
                view.Show(snapshot, new ShuttleMotion(), 1);
                Assert.AreNotEqual(free, Post("charger-3").sharedMaterial.color, "a taken charger changes colour");
                Assert.AreEqual(free, Post("charger-1").sharedMaterial.color, "a free one keeps its colour");
            }
            finally
            {
                view.Dispose();
                UnityEngine.Object.DestroyImmediate(rig);
            }
        }

        [Test]
        public void TheCityCutsItsBuildingsForTheRoutesItShowsAndForTheirNewRevisions()
        {
            var rig = new GameObject("City route rig");
            var view = new CityView(rig.transform, null);
            try
            {
                var load = view.LoadAsync(new CityClient(new OneBuilding(), new Uri("http://localhost")), "toa-payoh", CancellationToken.None);
                Assert.IsTrue(load.IsCompleted, "the fake replies at once");
                var mesh = rig.GetComponentsInChildren<MeshFilter>().First(f => f.name == "Buildings").sharedMesh;
                Assert.AreEqual(100, RoofArea(mesh, true), 1e-2f, "whole until a route is shown");
                var snapshot = (FullSnapshot)ContractParser.ParseMessage(File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/snapshot.json")));
                Position At(double x, double y) => new Position { XM = x, YM = y };
                snapshot.Routes[0].EdgePathsM = new List<List<Position>> { new List<Position> { At(3, -20), At(3, 30) } };
                view.Show(snapshot, new ShuttleMotion(), 0);
                Assert.AreEqual(100 - 70, RoofArea(mesh, true), 1e-2f, "the route clears 4 m each side, so only x from 7 to 10 stands");
                snapshot.Routes[0].Revision++;
                snapshot.Routes[0].EdgePathsM = new List<List<Position>> { new List<Position> { At(100, -20), At(100, 30) } };
                view.Show(snapshot, new ShuttleMotion(), 1);
                Assert.AreEqual(100, RoofArea(mesh, true), 1e-2f, "a route that moves away gives the building back");
                // The 30 m building and the parapet round its roof
                var other = BuildingStyles.Default.Styles[BuildingStyles.Default.IndexOf("other")];
                Assert.AreEqual(30 + other.Parapet.HeightM, view.Bounds.max.y, 1e-3f);
            }
            finally
            {
                view.Dispose();
                UnityEngine.Object.DestroyImmediate(rig);
            }
        }

        [Test]
        public void TheCitysShuttlesGlideBetweenSnapshotsAndFaceTheirHeading()
        {
            var rig = new GameObject("City shuttle rig");
            var view = new CityView(rig.transform, null);
            try
            {
                var snapshot = (FullSnapshot)ContractParser.ParseMessage(File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/snapshot.json")));
                var motion = new ShuttleMotion();
                var vehicle = snapshot.Vehicles[0];
                vehicle.RouteId = null;
                vehicle.HeadingDeg = 90;
                view.Show(snapshot, motion, 0);
                Assert.IsTrue(view.TryGetShuttle(vehicle.VehicleId, out var start), "placed at once");
                Assert.AreEqual((float)vehicle.Position.XM, start.x, 1e-3f);

                vehicle.Position.XM += 11;
                view.Show(snapshot, motion, 1);
                view.TryGetShuttle(vehicle.VehicleId, out var unmoved);
                Assert.Less(Vector3.Distance(unmoved, start), 1e-3f, "the snapshot does not move it by itself");
                view.Tick(motion, 1.55f);
                view.TryGetShuttle(vehicle.VehicleId, out var halfway);
                Assert.Less(Vector3.Distance(halfway, start + Vector3.right * 5.5f), 1e-2f, "halfway at halfway");
                motion.Sample(vehicle.VehicleId, 1.55f, out _, out var rotation);
                Assert.Less(Vector3.Angle(rotation * Vector3.forward, Vector3.forward), 0.1f, "90 degrees from east is north, Unity's +Z");
            }
            finally
            {
                view.Dispose();
                UnityEngine.Object.DestroyImmediate(rig);
            }
        }


        [Test]
        public void CentreDividerRoadShaderLoadsWithSurveyMaskProperties()
        {
            var shader = Resources.Load<Shader>("Formula9/Shaders/CityRoadWithCentreDividers");
            Assert.IsNotNull(shader);
            Assert.IsTrue(shader.isSupported);
            var material = new Material(shader);
            try
            {
                Assert.IsTrue(material.HasProperty("_SurveyPaintMask"));
                Assert.IsTrue(material.HasProperty("_SurveyMaskBounds"));
                Assert.IsTrue(material.HasProperty("_SurveyMaskEnabled"));
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }

        // Draws the real buildings from a live backend, to look at. Needs a GPU and the isolated
        // backend; set FORMULA9_CITY_RENDER_OUT to a .png path to keep the image.
        [UnityTest, Category("Render")]
        public IEnumerator TheRealCityRenders()
        {
            var backend = Environment.GetEnvironmentVariable("FORMULA9_E2E_BACKEND_URL");
            if (string.IsNullOrEmpty(backend)) Assert.Ignore("Needs FORMULA9_E2E_BACKEND_URL");
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Needs a GPU");
            var rig = new GameObject("City render rig");
            var transport = new UnityHttpTransport();
            var tiles = new MapTiles(transport, "https://tile.openstreetmap.org/{z}/{x}/{y}.png");
            var view = new CityView(rig.transform, tiles);
            var texture = new RenderTexture(1600, 900, 24) { antiAliasing = 4 };
            try
            {
                WorldLighting.Apply(rig.transform);
                var load = view.LoadAsync(new CityClient(transport, new Uri(backend)), "toa-payoh", CancellationToken.None);
                var deadline = Time.realtimeSinceStartup + 60;
                while (!load.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsTrue(load.IsCompleted, "buildings loaded");
                if (load.IsFaulted) throw load.Exception;
                Assert.Greater(view.BuildingCount, 1000);
                Assert.Greater(view.RoadCount, 1000);
                // Time for the ground tiles to arrive
                deadline = Time.realtimeSinceStartup + 45;
                while (Time.realtimeSinceStartup < deadline) yield return null;
                var camera = new GameObject("Camera", typeof(Camera)).GetComponent<Camera>();
                camera.transform.SetParent(rig.transform, true);
                // The sky WorldLighting set up, as the 3D view shows it
                camera.clearFlags = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.72f, 0.84f, 0.92f);
                camera.fieldOfView = 50; camera.farClipPlane = 8000; camera.nearClipPlane = 0.1f;
                var center = view.Bounds.center;
                var focus = new Vector3(center.x, 0, center.z);
                camera.targetTexture = texture;
                var output = Environment.GetEnvironmentVariable("FORMULA9_CITY_RENDER_OUT");
                void Shoot(Vector3 at, float distance, float pitch, string path)
                {
                    camera.transform.position = at + new Vector3(Mathf.Sin(Mathf.PI - 0.35f) * Mathf.Cos(pitch), Mathf.Sin(pitch), Mathf.Cos(Mathf.PI - 0.35f) * Mathf.Cos(pitch)) * distance;
                    camera.transform.LookAt(at, Vector3.up);
                    camera.Render();
                    RenderTexture.active = texture;
                    var image = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
                    image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                    image.Apply();
                    RenderTexture.active = null;
                    if (!string.IsNullOrEmpty(output)) File.WriteAllBytes(path, image.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(image);
                }
                Shoot(focus, 2200, 0.3f, output);
                // Where the view opens, then close enough on Lorong 2 Toa Payoh to see the road's lines
                Shoot(focus, 1200, 0.75f, Path.ChangeExtension(output ?? "", ".opening.png"));
                Shoot(new Vector3(2988, 0, -2604), 90, 0.7f, Path.ChangeExtension(output ?? "", ".street.png"));
                Shoot(new Vector3(2988, 0, -2604), 350, 1.1f, Path.ChangeExtension(output ?? "", ".junctions.png"));
                // Looking across the blocks at an angle, as the 3D view is usually used
                Shoot(new Vector3(2988, 0, -2604), 260, 0.5f, Path.ChangeExtension(output ?? "", ".blocks.png"));
            }
            finally
            {
                view.Dispose();
                tiles.Dispose();
                texture.Release();
                UnityEngine.Object.DestroyImmediate(rig);
            }
        }
    }
}
