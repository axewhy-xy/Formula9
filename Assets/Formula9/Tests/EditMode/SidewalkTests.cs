using System.Collections.Generic;
using System.Linq;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.World;
using NUnit.Framework;
using UnityEngine;

namespace Formula9.Tests
{
    public class SidewalkTests
    {
        // A 10 m stretch of sidewalk 3 m deep north of a road along y = 0, with a crossing's ramp in its
        // middle third: the kerb runs along y = 0, the ramp from x 3.5 to 6.5 and 1.5 m deep.
        //
        //   8 ---------------------------- 9      y = 3
        //   4 ------- 5 ======== 6 ------- 7      y = 1.5
        //   |  path   |   ramp   |  path   |
        //   0 ------- 1 ======== 2 ------- 3      y = 0, the kerb
        private const string OneTile = @"{
            ""area"": ""test"", ""version"": 1, ""source_hash"": ""abc"",
            ""constants"": {""surface_y"": 0.1, ""kerb_rise_m"": 0.15, ""kerb_stone_m"": 0.15, ""ramp_depth_m"": 1.5},
            ""render"": {""tiles"": [{
                ""key"": [0, 0],
                ""vertices_cm"": [0,0, 350,0, 650,0, 1000,0, 0,150, 350,150, 650,150, 1000,150, 0,300, 1000,300],
                ""zones"": {""footpath"": [0,5,1, 0,4,5, 2,7,3, 2,6,7, 4,9,7, 4,8,9], ""kerb_stone"": [], ""verge"": []},
                ""ramps"": [[500, 0, 0, 10000]],
                ""ramp_triangles"": [1,6,2,0, 1,5,6,0],
                ""kerb_edges"": [0,1,-1, 1,2,0, 2,3,-1],
                ""outer_edges"": [3,7,-1, 7,9,-1, 9,8,-1, 8,4,-1, 4,0,-1],
                ""cheek_edges"": [1,5,0, 6,2,0]
            }]},
            ""report"": {""ramps"": 1}
        }";

        private static Vector3 Normal(Vector3[] vertices, int[] triangles, int i) =>
            Vector3.Cross(vertices[triangles[i + 1]] - vertices[triangles[i]], vertices[triangles[i + 2]] - vertices[triangles[i]]);

        [Test]
        public void TheWalkwaysFileIsReadIntoFlatLists()
        {
            var data = CityWalkwayData.Parse(OneTile);
            Assert.AreEqual("test", data.Area);
            Assert.AreEqual("abc", data.SourceHash);
            Assert.AreEqual(0.15f, data.KerbRiseM, 1e-6f);
            var tile = data.Tiles.Single();
            Assert.AreEqual(20, tile.VerticesCm.Length);
            Assert.AreEqual(18, tile.Footpath.Length);
            Assert.AreEqual(new[] { 500, 0, 0, 10000 }, tile.Ramps, "a ramp's list is flattened");
            Assert.AreEqual(8, tile.RampTriangles.Length);
            Assert.AreEqual(9, tile.KerbEdges.Length);
        }

        [TestCase("\"footpath\": [0,5,1,", "\"footpath\": [0,5,99,")]
        [TestCase("[1,6,2,0,", "[1,6,2,3,")]
        [TestCase("\"cheek_edges\": [1,5,0,", "\"cheek_edges\": [1,5,")]
        [TestCase("\"render\"", "\"rendering\"")]
        public void ABrokenWalkwaysFileIsRefused(string from, string to)
        {
            Assert.Throws<ContractException>(() => CityWalkwayData.Parse(OneTile.Replace(from, to)));
        }

        [Test]
        public void ASidewalkStandsAKerbAboveTheRoadAndFacesUp()
        {
            var mesh = SidewalkMesh.Build(CityWalkwayData.Parse(OneTile), 0);
            try
            {
                Assert.AreEqual(SidewalkMesh.LayerCount, mesh.subMeshCount);
                Assert.AreEqual(RoadMesh.SurfaceY + SidewalkMesh.KerbRiseM, SidewalkMesh.TopY, 1e-6f);
                var vertices = mesh.vertices;
                var path = mesh.GetTriangles(SidewalkMesh.FootpathLayer);
                Assert.AreEqual((6 + 2) * 3, path.Length, "six flat triangles and the ramp's two");
                for (var i = 0; i < path.Length; i += 3)
                    Assert.Greater(Normal(vertices, path, i).normalized.y, 0.99f, "faces up, the ramp too");
                foreach (var index in path.Take(18))
                    Assert.AreEqual(SidewalkMesh.TopY, vertices[index].y, 1e-5f, "flat paving is at the top, even beside the ramp");
                var uvs = new List<Vector2>(); mesh.GetUVs(0, uvs);
                for (var i = 0; i < vertices.Length; i++)
                    Assert.AreEqual(new Vector2(vertices[i].x, vertices[i].z), uvs[i], "textures are laid in world metres");
                foreach (var layer in Enumerable.Range(0, SidewalkMesh.LayerCount))
                {
                    var triangles = mesh.GetTriangles(layer);
                    for (var i = 0; i < triangles.Length; i += 3)
                        Assert.Greater(Normal(vertices, triangles, i).magnitude, 1e-6f, $"no degenerate triangle in layer {layer}");
                }
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void ARampSlopesFromTheRoadAtTheKerbToTheTopAtItsBack()
        {
            var mesh = SidewalkMesh.Build(CityWalkwayData.Parse(OneTile), 0);
            try
            {
                var vertices = mesh.vertices;
                var path = mesh.GetTriangles(SidewalkMesh.FootpathLayer);
                var ramp = path.Skip(18).Select(index => vertices[index]).ToList();
                Assert.IsTrue(ramp.Where(v => v.z < 1e-3f).All(v => Mathf.Abs(v.y - RoadMesh.SurfaceY) < 1e-5f), "level with the road at the kerb");
                Assert.IsTrue(ramp.Where(v => v.z > 1.5f - 1e-3f).All(v => Mathf.Abs(v.y - SidewalkMesh.TopY) < 1e-5f), "level with the paving at its back");
                Assert.AreEqual(RoadMesh.SurfaceY + SidewalkMesh.KerbRiseM / 2,
                    SidewalkMesh.RampHeight(new[] { 500, 0, 0, 10000 }, 0, new Vector2(5, 0.75f)), 1e-5f, "halfway up halfway in");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void KerbFacesStandUpright()
        {
            var mesh = SidewalkMesh.Build(CityWalkwayData.Parse(OneTile), 0);
            try
            {
                var vertices = mesh.vertices;
                var kerb = mesh.GetTriangles(SidewalkMesh.KerbFaceLayer);
                Assert.IsNotEmpty(kerb);
                for (var i = 0; i < kerb.Length; i += 3)
                    Assert.Less(Mathf.Abs(Normal(vertices, kerb, i).normalized.y), 1e-4f, "vertical");
                var atKerb = Enumerable.Range(0, kerb.Length / 3).Select(t => t * 3)
                    .Where(i => kerb.Skip(i).Take(3).All(index => vertices[index].z < 1e-3f)).ToList();
                Assert.AreEqual(4, atKerb.Count, "a quad each side of the ramp, none where the ramp meets the road");
                foreach (var i in atKerb)
                {
                    Assert.Less(Normal(vertices, kerb, i).z, 0, "faces the road");
                    Assert.IsTrue(kerb.Skip(i).Take(3).All(index => vertices[index].x <= 3.5f + 1e-3f || vertices[index].x >= 6.5f - 1e-3f));
                    Assert.AreEqual(RoadMesh.SurfaceY, kerb.Skip(i).Take(3).Min(index => vertices[index].y), 1e-5f, "from the road");
                    Assert.AreEqual(SidewalkMesh.TopY, kerb.Skip(i).Take(3).Max(index => vertices[index].y), 1e-5f, "up to the top");
                }
                var cheeks = Enumerable.Range(0, kerb.Length / 3).Select(t => t * 3).Except(atKerb).ToList();
                Assert.AreEqual(2, cheeks.Count, "one tapering triangle each side of the ramp");
                foreach (var i in cheeks)
                {
                    var middle = (vertices[kerb[i]] + vertices[kerb[i + 1]] + vertices[kerb[i + 2]]) / 3;
                    var into = middle.x < 5 ? 1 : -1;
                    Assert.Greater(Normal(vertices, kerb, i).x * into, 0, "a ramp's side looks into the ramp");
                }

                var outer = mesh.GetTriangles(SidewalkMesh.OuterFaceLayer);
                Assert.AreEqual(5 * 2 * 3, outer.Length, "a quad on each outer edge");
                Assert.AreEqual(0, outer.Min(index => vertices[index].y), 1e-6f, "down to the ground");
                for (var i = 0; i < outer.Length; i += 3)
                    Assert.Less(Mathf.Abs(Normal(vertices, outer, i).normalized.y), 1e-4f, "vertical");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void ThingsOnTheSidewalkStandOnItsTop()
        {
            var surface = SidewalkSurface.Build(CityWalkwayData.Parse(OneTile));
            Assert.AreEqual(8, surface.TriangleCount);
            Assert.IsTrue(surface.TryHeight(new Vector2(1, 2), out var top));
            Assert.AreEqual(SidewalkMesh.TopY, top, 1e-5f);
            Assert.IsTrue(surface.TryHeight(new Vector2(5, 0.75f), out var ramp));
            Assert.AreEqual(RoadMesh.SurfaceY + SidewalkMesh.KerbRiseM / 2, ramp, 1e-4f, "on the ramp's slope");
            Assert.IsFalse(surface.TryHeight(new Vector2(5, -2), out _), "the road is not sidewalk");
            Assert.AreEqual(0.5f, surface.HeightOr(new Vector2(50, 50), 0.5f));
        }

        [Test]
        public void ATrafficLightPoleStandsOnTheGroundItIsGiven()
        {
            var signal = new CitySignalTrafficLight { Id = "L1", Junction = "J1", TrafficHeadingDeg = 90, FacingHeadingDeg = 270, XM = -8, YM = 0 };
            var road = new CityRoad { Id = "way/1/1", Class = "primary", Oneway = false, Lanes = 1, Points = new List<(double X, double Y)> { (0, -50), (0, 50) } };
            Assert.AreEqual(0, TrafficLightPlacement.Place(signal, new[] { road }).Position.y, "on the ground without sidewalks");
            Assert.AreEqual(SidewalkMesh.TopY, TrafficLightPlacement.Place(signal, new[] { road }, _ => SidewalkMesh.TopY).Position.y, 1e-6f);
        }

        // The copy bundled for offline use is the backend's file; every tile of it must build
        [Test]
        public void TheBundledToaPayohSidewalksBuildEveryTile()
        {
            var asset = Resources.Load<TextAsset>("Formula9/RoadData/toa-payoh-walkways");
            Assert.IsNotNull(asset, "Resources/Formula9/RoadData/toa-payoh-walkways.json");
            var data = CityWalkwayData.Parse(asset.text);
            Assert.AreEqual("toa-payoh", data.Area);
            Assert.AreEqual(SidewalkMesh.KerbRiseM, data.KerbRiseM, 1e-6f, "the importer and the view agree on the kerb");
            Assert.AreEqual(SidewalkMesh.RampDepthM, data.RampDepthM, 1e-6f);
            Assert.AreEqual(RoadMesh.SurfaceY, data.SurfaceY, 1e-6f);
            Assert.Greater(data.Tiles.Count, 20);
            var triangles = 0;
            foreach (var tile in data.Tiles)
            {
                var mesh = SidewalkMesh.Build(tile);
                try
                {
                    Assert.LessOrEqual(mesh.bounds.max.y, SidewalkMesh.TopY + 1e-4f);
                    Assert.GreaterOrEqual(mesh.bounds.min.y, -1e-4f);
                    for (var layer = 0; layer < SidewalkMesh.LayerCount; layer++) triangles += mesh.GetTriangles(layer).Length / 3;
                }
                finally { Object.DestroyImmediate(mesh); }
            }
            Assert.Greater(triangles, 20000);
            Assert.Greater(data.Tiles.Sum(tile => tile.Ramps.Length / 4), 100, "ramps where crossings meet the sidewalks");
        }
    }
}
