using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Formula9.World
{
    internal sealed class LtaMappedDividerTile
    {
        private const float SurfaceHeightM = RoadMesh.SurfaceY;
        private const float KerbHeightM = 0.25f;
        private readonly Transform parent;

        internal LtaMappedDividerTile(Transform parent) { this.parent = parent; }

        private int[] ReadSurface(JToken layer, float height, List<Vector3> vertices)
        {
            if (!(layer?["vertices_m"] is JArray points) ||
                !(layer["triangles"] is JArray indices) || indices.Count % 3 != 0)
                throw new InvalidOperationException("The junction has invalid mesh data.");

            int first = vertices.Count;
            foreach (JToken point in points)
                vertices.Add(ReadPoint(point, height));

            var triangles = new int[indices.Count];
            for (int i = 0; i < indices.Count; i++)
            {
                if (indices[i].Type != JTokenType.Integer)
                    throw new FormatException("Expected an integer triangle index.");
                int vertex = (int)indices[i];
                if (vertex < 0 || vertex >= points.Count)
                    throw new InvalidOperationException($"Triangle index {i} is outside the vertex array.");
                triangles[i] = first + vertex;
            }
            return triangles;
        }

        private Vector3 ReadPoint(JToken value, float height)
        {
            if (!(value is JArray point) || point.Count != 2)
                throw new InvalidOperationException("A junction vertex must contain X and Z.");

            // Subtract the city-space origin before converting to floats. The
            // small kerb-rim triangles need more precision than absolute X/Z.
            Vector3 origin = parent.localPosition;
            float x = (float)((double)point[0] - origin.x);
            float z = (float)((double)point[1] - origin.z);
            if (float.IsNaN(x) || float.IsNaN(z) || float.IsInfinity(x) || float.IsInfinity(z))
                throw new FormatException("Non-finite road vertex.");
            return new Vector3(x, height, z);
        }

        internal void Build(JObject kerbs, JArray barriers, Material concrete, Material islandFill, Material medianFill, Material metal, Action<Mesh> ownMesh)
        {
            if (!(kerbs["walls_m"] is JArray walls))
                throw new InvalidOperationException("The junction has no kerb edges.");

            float top = SurfaceHeightM + KerbHeightM;
            var vertices = new List<Vector3>();
            var kerbTriangles = new List<int>(ReadSurface(kerbs["rim"], top, vertices));
            int[] fillTriangles = ReadSurface(kerbs["fill"], top, vertices);
            int[] medianTriangles = kerbs["median_fill"] == null
                ? Array.Empty<int>() : ReadSurface(kerbs["median_fill"], top, vertices);
            var barrierTriangles = new List<int>();

            var paintUv = new List<Vector2>(new Vector2[vertices.Count]);
            foreach (JToken edge in walls)
            {
                if (!(edge is JArray pair) || pair.Count != 2)
                    throw new InvalidOperationException("A kerb edge must contain two points.");

                int first = vertices.Count;
                vertices.Add(ReadPoint(pair[0], top));
                vertices.Add(ReadPoint(pair[1], top));
                vertices.Add(ReadPoint(pair[1], SurfaceHeightM));
                vertices.Add(ReadPoint(pair[0], SurfaceHeightM));
                var a = vertices[first];
                var b = vertices[first + 1];
                var direction = b - a;
                if (direction.x < 0f || (direction.x == 0f && direction.z < 0f))
                    direction = -direction;
                direction.Normalize();
                float u0 = Vector3.Dot(a + parent.localPosition, direction);
                float u1 = Vector3.Dot(b + parent.localPosition, direction);
                paintUv.Add(new Vector2(u0, 1));
                paintUv.Add(new Vector2(u1, 1));
                paintUv.Add(new Vector2(u1, 1));
                paintUv.Add(new Vector2(u0, 1));
                kerbTriangles.Add(first);
                kerbTriangles.Add(first + 1);
                kerbTriangles.Add(first + 2);
                kerbTriangles.Add(first);
                kerbTriangles.Add(first + 2);
                kerbTriangles.Add(first + 3);
            }

            if (metal != null && barriers != null)
                AddBarriers(barriers, top, vertices, barrierTriangles);

            var generatedKerbs = new Mesh { name = "LTA junction kerbs and islands", hideFlags = HideFlags.DontSave };
            ownMesh(generatedKerbs);
            if (vertices.Count > 65535)
                generatedKerbs.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            while (paintUv.Count < vertices.Count) paintUv.Add(Vector2.zero);
            generatedKerbs.SetVertices(vertices);
            generatedKerbs.SetUVs(0, paintUv);
            generatedKerbs.subMeshCount = 4;
            generatedKerbs.SetTriangles(kerbTriangles, 0);
            generatedKerbs.SetTriangles(fillTriangles, 1);
            generatedKerbs.SetTriangles(medianTriangles, 2);
            generatedKerbs.SetTriangles(barrierTriangles, 3);
            generatedKerbs.RecalculateNormals();
            generatedKerbs.RecalculateBounds();

            var kerbObject = new GameObject("Mapped divider kerbs and barriers", typeof(MeshFilter), typeof(MeshRenderer));
            kerbObject.transform.SetParent(parent, false);
            kerbObject.GetComponent<MeshFilter>().sharedMesh = generatedKerbs;
            kerbObject.GetComponent<MeshRenderer>().sharedMaterials = new[]
            {
                concrete, islandFill, medianFill, metal
            };
        }

        private void AddBarriers(JArray barriers, float baseHeight, List<Vector3> vertices, List<int> triangles)
        {
            Vector3 up = Vector3.up;
            foreach (JToken record in barriers)
            {
                string kind = (string)record["kind"];
                if ((kind != "guard_rail" && kind != "railing") ||
                    !(record["points_m"] is JArray points) || points.Count < 2)
                    continue;

                bool guard = kind == "guard_rail";
                float postSpacing = guard ? 2f : 1.5f;
                float railHeight = guard ? 0.63f : 0.90f;
                float nextPost = 0.15f;
                Vector3 a = ReadPoint(points[0], baseHeight);
                for (int i = 1; i < points.Count; i++)
                {
                    Vector3 b = ReadPoint(points[i], baseHeight);
                    float length = Vector3.Distance(a, b);
                    if (length < 0.001f)
                    {
                        a = b;
                        continue;
                    }

                    Vector3 direction = (b - a) / length;
                    AddBeam(a + up * railHeight, b + up * railHeight,
                        guard ? 0.10f : 0.045f, guard ? 0.20f : 0.045f);
                    if (!guard)
                        AddBeam(a + up * 0.35f, b + up * 0.35f, 0.04f, 0.04f);

                    for (; nextPost < length; nextPost += postSpacing)
                    {
                        Vector3 post = a + direction * nextPost;
                        AddBeam(post - direction * 0.03f, post + direction * 0.03f,
                            0.06f, railHeight + 0.05f);
                    }
                    nextPost -= length;
                    a = b;
                }
            }

            void AddBeam(Vector3 start, Vector3 end, float width, float height)
            {
                Vector3 side = Vector3.Cross(up, end - start).normalized * (width * 0.5f);
                Vector3 rise = up * height;
                int first = vertices.Count;
                vertices.Add(start - side);
                vertices.Add(start + side);
                vertices.Add(end + side);
                vertices.Add(end - side);
                vertices.Add(start - side + rise);
                vertices.Add(start + side + rise);
                vertices.Add(end + side + rise);
                vertices.Add(end - side + rise);
                Face(0, 3, 2, 1);
                Face(4, 5, 6, 7);
                Face(0, 1, 5, 4);
                Face(1, 2, 6, 5);
                Face(2, 3, 7, 6);
                Face(3, 0, 4, 7);

                void Face(int a, int b, int c, int d)
                {
                    triangles.Add(first + a);
                    triangles.Add(first + c);
                    triangles.Add(first + b);
                    triangles.Add(first + a);
                    triangles.Add(first + d);
                    triangles.Add(first + c);
                }
            }
        }
    }
}
