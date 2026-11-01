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
                    // A 30 m residential block is an HDB tower, so its walls carry the HDB set
                    const float set = CityMesh.HdbPair;
                    var expected = new[] { new Vector4(start, 0, set, 0), new Vector4(start, 30, set, 0), new Vector4(end, 30, set, 0), new Vector4(end, 0, set, 0) };
                    for (var corner = 0; corner < 4; corner++)
                        Assert.Less(Vector4.Distance(expected[corner], uvs[wall * 4 + corner]), 1e-3f, $"wall {wall}, corner {corner}");
                }
                for (var i = 16; i < 20; i++) Assert.AreEqual(1, uvs[i].w, "the roof is marked to stay plain");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [TestCase("residential", 40, CityMesh.HdbPair)]
        [TestCase("residential", 12, CityMesh.LowRisePair)]
        [TestCase("apartments", 40, CityMesh.HomePair)]
        [TestCase("apartments", 12, CityMesh.LowRisePair)]
        [TestCase("house", 8, CityMesh.LowRisePair)]
        [TestCase("terrace", 8, CityMesh.LowRisePair)]
        [TestCase("office", 12, CityMesh.OfficePair)]
        [TestCase("commercial", 20, CityMesh.OfficePair)]
        [TestCase("yes", 60, CityMesh.OfficePair)]
        [TestCase("yes", 12, CityMesh.OtherPair)]
        [TestCase("retail", 6, CityMesh.OtherPair)]
        [TestCase("garage", 4, CityMesh.OtherPair)]
        public void EachKindOfBuildingUsesItsOwnTextures(string type, float heightM, float pair)
        {
            var building = Square("way/1", heightM);
            building.Type = type;
            var mesh = CityMesh.Build(new[] { building });
            try
            {
                var uvs = new List<Vector4>();
                mesh.GetUVs(0, uvs);
                Assert.IsTrue(uvs.All(uv => uv.z == pair));
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
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

        private static float RoofArea(Mesh mesh)
        {
            var vertices = mesh.vertices; var triangles = mesh.triangles; var area = 0f;
            for (var i = 0; i < triangles.Length; i += 3)
            {
                Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
                var normal = Vector3.Cross(b - a, c - a);
                Assert.GreaterOrEqual(normal.y, -1e-3f, "nothing faces down");
                if (normal.y > 1e-3f) area += normal.magnitude / 2;
            }
            return area;
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
                Assert.AreEqual(half, BoundsOf(mesh, RoadMesh.MarkingLayer).max.x, 1e-3f);
                Assert.AreEqual(100, BoundsOf(mesh, RoadMesh.MarkingLayer).max.z, 1e-3f, "lines run the whole way when nothing joins");
                foreach (var normal in mesh.normals) Assert.Greater(normal.y, 0.99f);
                Assert.AreEqual(RoadMesh.SurfaceY, mesh.bounds.center.y, 1e-3f, "flat, over the ground");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void AOneWayRoadSitsOnItsLineAndCountsLanesFromItsEdge()
        {
            var mesh = RoadMesh.Build(new[] { Road("primary", true, 3, 1, 2, (0, 0), (100, 0)) });
            try
            {
                var bounds = BoundsOf(mesh, RoadMesh.MarkingLayer);
                var half = 1.5f * RoadMesh.LaneWidthM + RoadMesh.ShoulderM;
                Assert.AreEqual(half, bounds.max.z, 1e-3f);
                Assert.AreEqual(-half, bounds.min.z, 1e-3f);
                var uvs = new List<Vector4>(); mesh.GetUVs(0, uvs);
                var triangles = mesh.GetTriangles(RoadMesh.MarkingLayer);
                var lanes = triangles.Select(i => uvs[i].x).ToList();
                var overhang = RoadMesh.ShoulderM / RoadMesh.LaneWidthM;
                Assert.AreEqual(-overhang, lanes.Min(), 1e-3f, "lane 0 is one edge");
                Assert.AreEqual(3 + overhang, lanes.Max(), 1e-3f, "lane 3 the other");
                Assert.AreEqual(3, uvs[triangles[0]].z);
                Assert.AreEqual(0, uvs[triangles[0]].w, "one-way");
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
                var vertices = mesh.vertices;
                var lined = mesh.GetTriangles(RoadMesh.MarkingLayer).Select(i => vertices[i]).ToList();
                var asphaltHalf = RoadMesh.LaneWidthM + RoadMesh.ShoulderM;
                Assert.IsFalse(lined.Any(v => Mathf.Abs(v.x) < asphaltHalf && Mathf.Abs(v.z) < asphaltHalf), "the junction is plain asphalt");
                Assert.IsTrue(lined.Any(v => v.x > 490 && Mathf.Approximately(v.z, 100)), "two roads alone just meet, so their lines reach the shared point");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
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
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void ASharpBendGetsARoundCornerAndNoSpike()
        {
            var mesh = RoadMesh.Build(new[] { Road("service", true, 1, 1, 2, (0, 0), (50, 0), (50, 50)) });
            try
            {
                var half = RoadMesh.LaneWidthM / 2 + RoadMesh.ShoulderM;
                var corner = new Vector3(50, RoadMesh.SurfaceY, 0);
                var vertices = mesh.vertices; var asphalt = mesh.GetTriangles(RoadMesh.AsphaltLayer);
                Assert.IsTrue(asphalt.Any(i => vertices[i] == corner), "a cap centred on the bend");
                Assert.IsFalse(asphalt.Any(i => vertices[i].x > 50 + half * 2.01f || vertices[i].z < -half * 2.01f), "the mitre is limited");
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
                Assert.Less(materials[RoadMesh.KerbLayer].renderQueue, materials[RoadMesh.AsphaltLayer].renderQueue);
                Assert.Less(materials[RoadMesh.AsphaltLayer].renderQueue, materials[RoadMesh.MarkingLayer].renderQueue);
                Assert.Greater(materials[RoadMesh.KerbLayer].renderQueue, (int)RenderQueue.Geometry, "after the ground and buildings");
            }
            finally
            {
                view.Dispose();
                UnityEngine.Object.DestroyImmediate(rig);
            }
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
                Assert.AreEqual(10, CityView.BuildingTextures.Length, "a wall and a window for each of the five sets");
                foreach (var property in CityView.BuildingTextures.Select(t => t.Property))
                {
                    Assert.IsTrue(buildings.HasProperty(property), property + " is in the shader");
                    var texture = buildings.GetTexture(property);
                    Assert.IsNotNull(texture, property);
                    Assert.AreEqual(TextureWrapMode.Repeat, texture.wrapMode, property + " tiles along the wall");
                }
                Assert.AreEqual(0, buildings.GetFloat("_Plain"));

                var snapshot = (FullSnapshot)ContractParser.ParseMessage(File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/snapshot.json")));
                view.Show(snapshot, new ShuttleMotion(), 0);
                var markers = rig.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.name.StartsWith("Shuttle ") || r.name.StartsWith("Stop ")).ToList();
                Assert.IsNotEmpty(markers);
                Assert.IsTrue(markers.All(r => r.sharedMaterial.GetFloat("_Plain") == 1), "no windows on shuttles or stops");
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
                Assert.AreEqual(900, Post("charger-3").transform.position.x, 1e-3f, "a depot's only charger stands at its middle");
                var first = Post("charger-1").transform.position.x - (float)snapshot.Depots[0].Position.XM;
                Assert.AreEqual(-15, first, 1e-3f, "two bays sit 15 m either side, as the backend lays them out");
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
                Assert.AreEqual(100, RoofArea(mesh), 1e-2f, "whole until a route is shown");
                var snapshot = (FullSnapshot)ContractParser.ParseMessage(File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/snapshot.json")));
                Position At(double x, double y) => new Position { XM = x, YM = y };
                snapshot.Routes[0].EdgePathsM = new List<List<Position>> { new List<Position> { At(3, -20), At(3, 30) } };
                view.Show(snapshot, new ShuttleMotion(), 0);
                Assert.AreEqual(100 - 70, RoofArea(mesh), 1e-2f, "the route clears 4 m each side, so only x from 7 to 10 stands");
                snapshot.Routes[0].Revision++;
                snapshot.Routes[0].EdgePathsM = new List<List<Position>> { new List<Position> { At(100, -20), At(100, 30) } };
                view.Show(snapshot, new ShuttleMotion(), 1);
                Assert.AreEqual(100, RoofArea(mesh), 1e-2f, "a route that moves away gives the building back");
                Assert.AreEqual(30, view.Bounds.max.y, 1e-3f);
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
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.72f, 0.84f, 0.92f);
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
