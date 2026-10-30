using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Formula9.Backend;
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
                // Time for the ground tiles to arrive
                deadline = Time.realtimeSinceStartup + 45;
                while (Time.realtimeSinceStartup < deadline) yield return null;
                var camera = new GameObject("Camera", typeof(Camera)).GetComponent<Camera>();
                camera.transform.SetParent(rig.transform, true);
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.72f, 0.84f, 0.92f);
                camera.fieldOfView = 50; camera.farClipPlane = 8000; camera.nearClipPlane = 1;
                var center = view.Bounds.center;
                var focus = new Vector3(center.x, 0, center.z);
                camera.transform.position = focus + new Vector3(Mathf.Sin(Mathf.PI - 0.35f) * Mathf.Cos(0.3f), Mathf.Sin(0.3f), Mathf.Cos(Mathf.PI - 0.35f) * Mathf.Cos(0.3f)) * 2200;
                camera.transform.LookAt(focus, Vector3.up);
                camera.targetTexture = texture;
                camera.Render();
                RenderTexture.active = texture;
                var image = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                image.Apply();
                RenderTexture.active = null;
                var output = Environment.GetEnvironmentVariable("FORMULA9_CITY_RENDER_OUT");
                if (!string.IsNullOrEmpty(output)) File.WriteAllBytes(output, image.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(image);
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
