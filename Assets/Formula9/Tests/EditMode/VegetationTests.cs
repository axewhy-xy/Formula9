/*
 * Regression tests for optional city vegetation and ground detail.
 *
 * Author: Nur Syazliyarni Binte Zazali
 * Date Created: 26/11/2026
 * Last Modified: 26/11/2026
 *
 * Description:
 *     Checks pending-load disposal, malformed tree models, unsupported hardware,
 *     nearby off-screen shadows and the grass mask's map-colour boundaries.
 */
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Formula9.Tests
{
    public class VegetationTests
    {
        private sealed class DelayedCity : IHttpTransport
        {
            internal string DelayedPath;
            internal int Occurrence = 1;
            internal bool Reached;
            internal readonly TaskCompletionSource<HttpResponse> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly List<string> Requests = new();

            internal static string Body(string path) => path.Contains("/buildings/")
                ? "{\"buildings\":[{\"id\":\"way/1\",\"type\":\"retail\",\"name\":\"\",\"height_m\":30,\"rings_m\":[[[0,0],[10,0],[10,10],[0,10],[0,0]]]}]}"
                : path.Contains("/roads/") ? "{\"roads\":[]}"
                : path.Contains("/traffic/") ? "{\"models\":{},\"vehicles\":[]}"
                : "{\"signals\":[]}";

            public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellation = default)
            {
                var path = request.Url.AbsolutePath;
                Requests.Add(path);
                if (path == DelayedPath && Requests.Count(p => p == path) == Occurrence) { Reached = true; return Pending.Task; }
                return Task.FromResult(new HttpResponse { Status = 200, Body = Body(path) });
            }
        }

        [TestCase("/api/buildings/toa-payoh", 1)]
        [TestCase("/api/roads/toa-payoh", 1)]
        [TestCase("/api/traffic/toa-payoh", 1)]
        [TestCase("/api/road-features/toa-payoh/signals", 1)]
        [TestCase("/api/road-features/toa-payoh/signals", 2)]
        public async Task DisposingDuringCityLoadDoesNotCreateMoreObjectsOrMaterials(string path, int occurrence)
        {
            var rig = new GameObject("Disposed city load");
            var view = new CityView(rig.transform, null);
            var transport = new DelayedCity { DelayedPath = path, Occurrence = occurrence };
            try
            {
                var load = view.LoadAsync(new CityClient(transport, new Uri("http://localhost")), "toa-payoh", CancellationToken.None);
                Assert.IsTrue(transport.Reached, "The requested load stage must be pending.");
                view.Dispose();
                var count = Resources.FindObjectsOfTypeAll<Material>().Length;
                var requests = transport.Requests.Count;
                transport.Pending.SetResult(new HttpResponse { Status = 200, Body = DelayedCity.Body(path) });
                await load;
                Assert.IsNull(view.Vegetation);
                Assert.AreEqual(0, rig.transform.childCount);
                Assert.AreEqual(count, Resources.FindObjectsOfTypeAll<Material>().Length);
                Assert.AreEqual(requests, transport.Requests.Count, "Closing the city must stop later requests.");
            }
            finally
            {
                transport.Pending.TrySetResult(new HttpResponse { Status = 200, Body = DelayedCity.Body(path) });
                view.Dispose(); Object.DestroyImmediate(rig);
            }
        }

        [TestCase("missing-renderer")]
        [TestCase("missing-mesh")]
        [TestCase("no-submeshes")]
        [TestCase("missing-slots")]
        [TestCase("null-material")]
        [TestCase("uninstanced-template")]
        public void InvalidTreeLayoutsFailBeforeAllocatingOwnedMaterials(string problem)
        {
            var near = Resources.Load<GameObject>("Formula9/Vegetation/RoadsideTree").GetComponentInChildren<MeshFilter>();
            var rig = new GameObject("Invalid tree", typeof(MeshFilter));
            var far = rig.GetComponent<MeshFilter>();
            far.sharedMesh = near.sharedMesh;
            Mesh empty = null;
            var template = new Material(Resources.Load<Material>("Formula9/TrafficVehicle"));
            try
            {
                if (problem != "missing-renderer") rig.AddComponent<MeshRenderer>().sharedMaterials = near.GetComponent<MeshRenderer>().sharedMaterials;
                if (problem == "missing-mesh") far.sharedMesh = null;
                if (problem == "no-submeshes") { empty = new Mesh { subMeshCount = 0 }; far.sharedMesh = empty; }
                if (problem == "missing-slots") rig.GetComponent<MeshRenderer>().sharedMaterials = Array.Empty<Material>();
                if (problem == "null-material") rig.GetComponent<MeshRenderer>().sharedMaterials = new Material[near.sharedMesh.subMeshCount];
                if (problem == "uninstanced-template") template.enableInstancing = false;
                var placements = Resources.Load<TextAsset>("Formula9/Vegetation/ToaPayohTrees");
                var count = Resources.FindObjectsOfTypeAll<Material>().Length;
                LogAssert.Expect(LogType.Warning, "[CityView] Roadside tree mesh, renderer or materials are invalid; skipping vegetation.");
                using var trees = CityVegetation.Create(near, far, placements, template, Matrix4x4.identity);
                Assert.IsNull(trees);
                Assert.AreEqual(count, Resources.FindObjectsOfTypeAll<Material>().Length);
            }
            finally { Object.DestroyImmediate(rig); Object.DestroyImmediate(template); if (empty != null) Object.DestroyImmediate(empty); }
        }

        [Test]
        public void UnsupportedHardwareSkipsOptionalTrees()
        {
            var count = Resources.FindObjectsOfTypeAll<Material>().Length;
            LogAssert.Expect(LogType.Warning, "[CityView] GPU instancing is unavailable; roadside trees are disabled.");
            Assert.IsNull(CityVegetation.Load(Matrix4x4.identity, instancingSupported: false));
            Assert.AreEqual(count, Resources.FindObjectsOfTypeAll<Material>().Length);
        }

        // OSM Carto landcover colours, also sampled from cached Toa Payoh zoom-17 tiles.
        [TestCase("#cdebb0", true)] // grass
        [TestCase("#add19e", true)] // woodland
        [TestCase("#c8facc", true)] // park
        [TestCase("#88e0be", false)] // sports surface
        [TestCase("#0f8519", false)] // green map symbol
        [TestCase("#46673b", false)] // vegetation label
        [TestCase("#00b436", false)] // saturated green line or roof
        [TestCase("#dddddd", false)] // paving
        public void GrassMaskKeepsPaleVegetationAndRejectsPaintedOrLabelGreens(string hex, bool changes)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Needs a GPU");
            var material = new Material(Shader.Find("Formula9/CityGround"));
            var source = new Texture2D(2, 2, TextureFormat.RGB24, false);
            source.SetPixels(Enumerable.Repeat(WorldLook.Color(hex), 4).ToArray()); source.Apply();
            var target = new RenderTexture(16, 16, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var readback = new Texture2D(16, 16, TextureFormat.RGB24, false, true);
            var previous = RenderTexture.active;
            var dim = Shader.GetGlobalVector("_Formula9GroundDim");
            var shadow = Shader.GetGlobalFloat("_Formula9ShadowDarkness");
            var fog = RenderSettings.fog;
            try
            {
                Shader.SetGlobalVector("_Formula9GroundDim", Vector4.zero);
                Shader.SetGlobalFloat("_Formula9ShadowDarkness", 0); RenderSettings.fog = false;
                material.SetFloat("_HasMapTile", 1);
                material.SetTexture("_GrassTex", Resources.Load<Texture2D>("Formula9/Textures/grass_albedo"));
                Color Sample(float strength)
                {
                    material.SetFloat("_GrassStrength", strength); Graphics.Blit(source, target, material, 0);
                    RenderTexture.active = target; readback.ReadPixels(new Rect(0, 0, 16, 16), 0, 0); readback.Apply();
                    return readback.GetPixel(8, 8);
                }
                var before = Sample(0); var after = Sample(1);
                var delta = Mathf.Max(Mathf.Abs(before.r - after.r), Mathf.Abs(before.g - after.g), Mathf.Abs(before.b - after.b));
                if (changes) Assert.Greater(delta, 0.03f); else Assert.Less(delta, 0.01f);
            }
            finally
            {
                Shader.SetGlobalVector("_Formula9GroundDim", dim); Shader.SetGlobalFloat("_Formula9ShadowDarkness", shadow);
                RenderSettings.fog = fog; RenderTexture.active = previous; target.Release();
                Object.DestroyImmediate(source); Object.DestroyImmediate(readback); Object.DestroyImmediate(target); Object.DestroyImmediate(material);
            }
        }

        [UnityTest]
        public IEnumerator TreeDrawingDoesNotAllocateManagedMemoryPerFrame()
        {
            if (!SystemInfo.supportsInstancing) Assert.Ignore("Needs GPU instancing");
            var rig = new GameObject("Tree allocation check", typeof(Camera));
            var camera = rig.GetComponent<Camera>();
            var target = new RenderTexture(640, 360, 24); camera.targetTexture = target;
            camera.transform.position = new Vector3(2988, 20, -2664); camera.transform.LookAt(new Vector3(2988, 4, -2604));
            using var trees = CityVegetation.Load(Matrix4x4.identity);
            var cameras = new[] { camera };
            try
            {
                for (var frame = 0; frame < 11; frame++)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    trees.Draw(cameras);
                    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    if (frame >= 3) Assert.AreEqual(0, allocated, "Tree drawing must reuse its buffers.");
                    camera.Render(); yield return null;
                }
            }
            finally { camera.targetTexture = null; target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(rig); }
        }

        [UnityTest]
        public IEnumerator NearbyOffscreenTreesStillCastIntoTheView()
        {
            if (!SystemInfo.supportsInstancing || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Needs GPU instancing");
            var rig = new GameObject("Offscreen shadow check");
            var camera = new GameObject("Shadow camera", typeof(Camera)).GetComponent<Camera>(); camera.transform.SetParent(rig.transform);
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane); ground.transform.SetParent(rig.transform); ground.transform.localScale = Vector3.one * 4;
            var material = new Material(Shader.Find("Formula9/CityGround")); ground.GetComponent<MeshRenderer>().sharedMaterial = material;
            var light = new GameObject("Low sun", typeof(Light)).GetComponent<Light>(); light.transform.SetParent(rig.transform);
            light.type = LightType.Directional; light.shadows = LightShadows.Hard; light.shadowStrength = 1; light.transform.rotation = Quaternion.Euler(30, 270, 0);
            var target = new RenderTexture(256, 256, 24); camera.targetTexture = target;
            camera.orthographic = true; camera.orthographicSize = 10; camera.nearClipPlane = 0.1f; camera.farClipPlane = 100;
            camera.transform.position = new Vector3(0, 30, 0); camera.transform.LookAt(Vector3.zero, Vector3.forward);
            var readback = new Texture2D(256, 256, TextureFormat.RGB24, false);
            var previous = RenderTexture.active; var oldSun = RenderSettings.sun; var shadows = QualitySettings.shadows;
            var distance = QualitySettings.shadowDistance; var oldDim = Shader.GetGlobalVector("_Formula9GroundDim"); var oldDarkness = Shader.GetGlobalFloat("_Formula9ShadowDarkness");
            var placements = new TextAsset("east_m,north_m,height_scale,width_scale,yaw_deg\n14,0,1,1,0\n");
            var near = Resources.Load<GameObject>("Formula9/Vegetation/RoadsideTree").GetComponentInChildren<MeshFilter>();
            var far = Resources.Load<GameObject>("Formula9/Vegetation/RoadsideTreeLow").GetComponentInChildren<MeshFilter>();
            using var trees = CityVegetation.Create(near, far, placements, Resources.Load<Material>("Formula9/TrafficVehicle"), Matrix4x4.identity);
            try
            {
                RenderSettings.sun = light; QualitySettings.shadows = ShadowQuality.All; QualitySettings.shadowDistance = 80;
                Shader.SetGlobalVector("_Formula9GroundDim", Vector4.zero); Shader.SetGlobalFloat("_Formula9ShadowDarkness", 1);
                var bounds = near.sharedMesh.bounds; bounds.center += new Vector3(14, 0, 0);
                Assert.IsFalse(GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(camera), bounds));
                camera.Render(); RenderTexture.active = target; readback.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); readback.Apply();
                var before = readback.GetPixels32(); yield return null;
                trees.Draw(new[] { camera }); camera.Render(); RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); readback.Apply();
                var after = readback.GetPixels32();
                var folder = Environment.GetEnvironmentVariable("FORMULA9_VEGETATION_RENDER_OUT");
                if (!string.IsNullOrEmpty(folder)) File.WriteAllBytes(Path.Combine(folder, "Vegetation-offscreen-shadow.png"), readback.EncodeToPNG());
                Assert.Greater(Enumerable.Range(0, before.Length).Count(i => before[i].r - after[i].r > 10), 50,
                    "The off-screen crown should still cast a visible shadow onto the ground.");
                yield return null;
            }
            finally
            {
                RenderSettings.sun = oldSun; QualitySettings.shadows = shadows; QualitySettings.shadowDistance = distance;
                Shader.SetGlobalVector("_Formula9GroundDim", oldDim); Shader.SetGlobalFloat("_Formula9ShadowDarkness", oldDarkness);
                camera.targetTexture = null; RenderTexture.active = previous; target.Release();
                Object.DestroyImmediate(placements); Object.DestroyImmediate(material); Object.DestroyImmediate(readback); Object.DestroyImmediate(target); Object.DestroyImmediate(rig);
            }
        }
    }
}
