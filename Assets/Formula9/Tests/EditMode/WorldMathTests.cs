/*
 * Unit tests for WorldMath.cs.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Checks the coordinate conventions against the backend's 3D motion fixture: placed assets land
 *     where the backend says, placements are rigid, reflections are refused, and canonical up is
 *     Unity up.
 */

using System.IO;
using System.Linq;
using Formula9.World;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Formula9.Tests
{
    // -----------------------------------------------------------------------------------
    // Class: WorldMathTests
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class tests the coordinate conversions.
    public class WorldMathTests
    {
        // -----------------------------------------------------------------------------------
        // Function: Fixture
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reads a shared fixture.
        //
        // Args:
        //     name (string): The fixture name, without .json.
        //
        // Returns:
        //     JObject: The fixture.
        private static JObject Fixture(string name) =>
            JObject.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures", name + ".json")));

        // -----------------------------------------------------------------------------------
        // Function: Doubles
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reads a JSON array of numbers.
        //
        // Args:
        //     array (JToken): The array.
        //
        // Returns:
        //     double[]: The numbers.
        private static double[] Doubles(JToken array) => array.Select(v => (double)v).ToArray();

        // -----------------------------------------------------------------------------------
        // Function: PlacedAssetsMatchTheBackendsMarkerPoints
        // -----------------------------------------------------------------------------------
        // Description:
        //     The backend's motion fixture records where an asymmetric asset vertex lands for every
        //     shuttle (render_marker_m, in the Qt/OpenGL render basis x, z, -y). Placing the same
        //     vertex through glTFast's import flip and PlacedAsset must land on the same point,
        //     within 1 mm.
        [Test]
        public void PlacedAssetsMatchTheBackendsMarkerPoints()
        {
            var checkedMarkers = 0;
            foreach (JObject sample in Fixture("m2_3d_motion")["samples"])
            {
                foreach (JObject vehicle in sample["snapshot"]["vehicles"])
                {
                    var pose = vehicle["spatial"];
                    var marker = sample["markers"][(string)vehicle["vehicle_id"]];
                    var vertex = Doubles(marker["asset_vertex_m"]);
                    var render = Doubles(marker["render_marker_m"]);
                    var placed = WorldMath.PlacedAsset(Doubles(pose["position_m"]), Doubles(pose["orientation_xyzw"]), Doubles(pose["asset_to_body"]));
                    // glTFast imports the asset vertex with X negated.
                    var unity = placed.MultiplyPoint3x4(new Vector3(-(float)vertex[0], (float)vertex[1], (float)vertex[2]));
                    var expected = new Vector3((float)render[0], (float)render[1], -(float)render[2]);
                    Assert.Less(Vector3.Distance(expected, unity), 1e-3f, $"{vehicle["vehicle_id"]} at tick {sample["snapshot"]["tick"]}");
                    checkedMarkers++;
                }
            }
            Assert.Greater(checkedMarkers, 10);
        }

        // -----------------------------------------------------------------------------------
        // Function: PlacedAssetsAreRigid
        // -----------------------------------------------------------------------------------
        // Description:
        //     A placed asset's transform is rigid, so a Transform can hold it exactly.
        [Test]
        public void PlacedAssetsAreRigid()
        {
            var pose = Fixture("m2_3d_motion")["samples"][3]["snapshot"]["vehicles"][4]["spatial"];
            var placed = WorldMath.PlacedAsset(Doubles(pose["position_m"]), Doubles(pose["orientation_xyzw"]), Doubles(pose["asset_to_body"]));
            var target = new GameObject("probe").transform;
            try
            {
                Assert.IsTrue(WorldMath.Apply(target, placed));
                Assert.Less(Vector3.Distance(target.position, placed.GetPosition()), 1e-4f);
            }
            finally
            {
                Object.DestroyImmediate(target.gameObject);
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: ReflectionsAreRefused
        // -----------------------------------------------------------------------------------
        // Description:
        //     Apply refuses a reflected matrix.
        [Test]
        public void ReflectionsAreRefused()
        {
            var target = new GameObject("probe").transform;
            try { Assert.IsFalse(WorldMath.Apply(target, Matrix4x4.Scale(new Vector3(-1, 1, 1)))); }
            finally { Object.DestroyImmediate(target.gameObject); }
        }

        // -----------------------------------------------------------------------------------
        // Function: CanonicalUpIsUnityUp
        // -----------------------------------------------------------------------------------
        // Description:
        //     Canonical (x, y, z) becomes Unity (x, z, y), for points and for a body's bounds.
        [Test]
        public void CanonicalUpIsUnityUp()
        {
            Assert.AreEqual(new Vector3(1, 3, 2), WorldMath.ToUnity(new double[] { 1, 2, 3 }));
            var bounds = WorldMath.BodyBounds(new double[] { 20, 0, -0.1 }, new double[] { 0, 0, 0, 1 }, new double[] { 40, 6, 0.2 });
            Assert.AreEqual(new Vector3(40, 0.2f, 6), bounds.size);
            Assert.AreEqual(new Vector3(20, -0.1f, 0), bounds.center);
        }
    }
}
