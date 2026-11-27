using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Formula9.World
{
    // One anchor per mapped feature. Adjacent export tiles may repeat the same survey records.
    internal static class LtaOverviewSymbols
    {
        private sealed class Batch
        {
            internal readonly List<Vector3> Positions = new List<Vector3>();
            internal readonly List<Vector4> Shape = new List<Vector4>();
            internal readonly List<Vector4> Placement = new List<Vector4>();
            internal readonly List<int> Indices = new List<int>();

            internal void Add(Vector2 at, Vector2 heading, int kind, float width, float length)
            {
                int start = Positions.Count;
                for (int i = 0; i < 4; i++)
                {
                    Positions.Add(new Vector3(at.x, RoadMesh.SurfaceY + .05f, at.y));
                    Shape.Add(new Vector4(i == 0 || i == 3 ? -1 : 1, i < 2 ? -1 : 1, kind, 0));
                    Placement.Add(new Vector4(heading.x, heading.y, width, length));
                }
                Indices.Add(start); Indices.Add(start + 1); Indices.Add(start + 2);
                Indices.Add(start); Indices.Add(start + 2); Indices.Add(start + 3);
            }
        }

        internal static void Build(JArray tiles, Transform parent, Material material, Action<Mesh> own)
        {
            var batch = new Batch();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int arrowCount = 0;
            foreach (var tile in tiles)
            {
                foreach (var row in (tile["markings"]?["arrows"] as JArray ?? new JArray()))
                {
                    var code = (string)row["type_code"];
                    if ((float?)row["level"] != 2f || row["x_m"] == null || row["y_m"] == null || code == null || code.Length != 1 || code[0] < 'A' || code[0] > 'I' ||
                        row["heading_deg"] == null || !seen.Add((string)row["id"])) continue;
                    float angle = (float)row["heading_deg"] * Mathf.Deg2Rad;
                    batch.Add(new Vector2((float)row["x_m"], (float)row["y_m"]),
                        new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)), code[0] - 'A', 2.7f, 4f);
                    arrowCount++;
                }
            }
            if (batch.Indices.Count == 0) return;
            Debug.Log($"[CityView] Mapped directional arrows active at every zoom: {arrowCount}.");
            var mesh = new Mesh { name = "Surveyed directional arrows", indexFormat = IndexFormat.UInt32 };
            own(mesh);
            mesh.SetVertices(batch.Positions);
            mesh.SetUVs(0, batch.Shape);
            mesh.SetUVs(1, batch.Placement);
            mesh.SetTriangles(batch.Indices, 0);
            var bounds = mesh.bounds;
            bounds.Expand(128f);
            mesh.bounds = bounds;
            var go = new GameObject(mesh.name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

    }
}
