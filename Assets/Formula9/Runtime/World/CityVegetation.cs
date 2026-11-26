using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Formula9.World
{
    internal sealed class CityVegetation : IDisposable
    {
        private const string ResourceFolder = "Formula9/Vegetation/";
        private const float CellSizeM = 100f;
        // Standard instancing supplies two matrices per instance and supports 511 per draw.
        private const int BatchSize = 500;
        private const float DetailSizePx = 35f, MinimumSizePx = 2f, ShadowDistanceM = 45f;

        private readonly Mesh nearMesh, farMesh;
        private readonly Material[] nearMaterials, farMaterials;
        private readonly List<(Bounds Bounds, float TreeSize, Matrix4x4[] Transforms)> batches = new();
        private readonly Plane[] frustum = new Plane[6];
        private readonly Matrix4x4[] distantTransforms = new Matrix4x4[BatchSize];

        internal int TreeCount { get; }
        internal int BatchCount => batches.Count;

        private CityVegetation(MeshFilter near, MeshFilter far, Material instancingMaterial,
            TextAsset placements, Matrix4x4 cityToWorld)
        {
            nearMesh = near.sharedMesh;
            farMesh = far.sharedMesh;
            nearMaterials = CreateMaterials(near, instancingMaterial);
            farMaterials = CreateMaterials(far, instancingMaterial);
            var meshBounds = nearMesh.bounds;

            // Both OBJ assets use the same ground-level origin and local axes.
            foreach (var cell in ReadPlacements(placements).GroupBy(tree => new Vector2Int(
                Mathf.FloorToInt(tree.Position.x / CellSizeM), Mathf.FloorToInt(tree.Position.z / CellSizeM))))
            {
                var trees = cell.ToArray();
                TreeCount += trees.Length;
                for (var start = 0; start < trees.Length; start += BatchSize)
                {
                    var matrices = new Matrix4x4[Math.Min(BatchSize, trees.Length - start)];
                    var bounds = new Bounds();
                    var treeSize = 0f;
                    for (var i = 0; i < matrices.Length; i++)
                    {
                        var tree = trees[start + i];
                        matrices[i] = cityToWorld * Matrix4x4.TRS(tree.Position,
                            Quaternion.Euler(0, tree.Yaw, 0), Vector3.one * tree.Scale);
                        var treeBounds = TransformBounds(meshBounds, matrices[i]);
                        if (i == 0) bounds = treeBounds;
                        else bounds.Encapsulate(treeBounds);
                        var size = treeBounds.size;
                        treeSize = Mathf.Max(treeSize, Mathf.Max(size.y, Mathf.Max(size.x, size.z)));
                    }
                    batches.Add((bounds, treeSize, matrices));
                }
            }
        }

        internal static CityVegetation Load(Matrix4x4 cityToWorld)
        {
            var near = Resources.Load<GameObject>(ResourceFolder + "RoadsideTree")?.GetComponentInChildren<MeshFilter>();
            var far = Resources.Load<GameObject>(ResourceFolder + "RoadsideTreeLow")?.GetComponentInChildren<MeshFilter>();
            var placements = Resources.Load<TextAsset>(ResourceFolder + "ToaPayohTrees");
            // Use the instancing shader already shipped for traffic and pedestrians.
            var instancingMaterial = Resources.Load<Material>("Formula9/TrafficVehicle");
            if (near == null || far == null || placements == null || instancingMaterial == null)
            {
                Debug.LogWarning("[CityView] Roadside tree assets are missing.");
                return null;
            }
            return new CityVegetation(near, far, instancingMaterial, placements, cityToWorld);
        }

        /// <summary>Keep the model's proportions while fitting each placement's height and canopy limits.</summary>
        internal static IEnumerable<(Vector3 Position, float Scale, float Yaw)> ReadPlacements(TextAsset asset)
        {
            using var reader = new StringReader(asset.text);
            reader.ReadLine(); // Header: east_m,north_m,height_scale,width_scale,yaw_deg.
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var columns = line.Split(',');
                if (columns.Length < 4 || columns.Length > 5 || !TryRead(columns[0], out var east)
                    || !TryRead(columns[1], out var north) || !TryRead(columns[2], out var height)
                    || height <= 0 || !TryRead(columns[^1], out var yaw)) continue;

                var width = height; // The original four-column file uses a uniform scale.
                if (columns.Length == 5 && (!TryRead(columns[3], out width) || width <= 0)) continue;
                // Scaling all axes together preserves the trunk and crown. Taking the smaller limit
                // also keeps the canopy inside its existing footprint beside roads and buildings.
                yield return (new Vector3(east, 0, north), Mathf.Min(height, width), yaw);
            }
        }

        private static bool TryRead(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

        private static Material[] CreateMaterials(MeshFilter model, Material template) => model.GetComponent<MeshRenderer>()
            .sharedMaterials.Select(source => new Material(template) { name = source.name, color = source.color }).ToArray();

        private static Vector3 Absolute(Vector3 value) => new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));

        private static Bounds TransformBounds(Bounds bounds, Matrix4x4 matrix)
        {
            var extents = bounds.extents;
            var worldExtents = Absolute(matrix.MultiplyVector(new Vector3(extents.x, 0, 0)))
                + Absolute(matrix.MultiplyVector(new Vector3(0, extents.y, 0)))
                + Absolute(matrix.MultiplyVector(new Vector3(0, 0, extents.z)));
            return new Bounds(matrix.MultiplyPoint3x4(bounds.center), worldExtents * 2);
        }

        internal void Draw(IReadOnlyList<Camera> cameras)
        {
            for (var i = 0; i < cameras.Count; i++)
            {
                var camera = cameras[i];
                if (camera == null || !camera.enabled || (camera.cullingMask & 1) == 0) continue;
                GeometryUtility.CalculateFrustumPlanes(camera, frustum);
                var eye = camera.transform.position;
                var forward = camera.transform.forward;
                var absoluteForward = Absolute(forward);
                var orthographic = camera.orthographic;
                var pixelsPerMetre = camera.pixelHeight / (orthographic
                    ? 2 * camera.orthographicSize : 2 * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f));
                var nearClip = camera.nearClipPlane;
                var distantCount = 0;
                var distantBounds = new Bounds();
                foreach (var batch in batches)
                {
                    if (!GeometryUtility.TestPlanesAABB(frustum, batch.Bounds)) continue;
                    // The nearest edge keeps the full silhouette on close trees at cell boundaries.
                    var depth = Vector3.Dot(batch.Bounds.center - eye, forward)
                        - Vector3.Dot(batch.Bounds.extents, absoluteForward);
                    var size = batch.TreeSize * pixelsPerMetre / (orthographic ? 1 : Mathf.Max(nearClip, depth));
                    if (size < MinimumSizePx) continue;
                    if (size >= DetailSizePx)
                    {
                        var castsShadows = batch.Bounds.SqrDistance(eye) < ShadowDistanceM * ShadowDistanceM;
                        Submit(nearMesh, nearMaterials, batch.Bounds, batch.Transforms, batch.Transforms.Length, camera, castsShadows);
                        continue;
                    }

                    // Distant cells already passed camera culling. Combine them to keep the overview's draw count low.
                    if (distantCount + batch.Transforms.Length > BatchSize)
                    {
                        Submit(farMesh, farMaterials, distantBounds, distantTransforms, distantCount, camera, false);
                        distantCount = 0;
                    }
                    Array.Copy(batch.Transforms, 0, distantTransforms, distantCount, batch.Transforms.Length);
                    if (distantCount == 0) distantBounds = batch.Bounds;
                    else distantBounds.Encapsulate(batch.Bounds);
                    distantCount += batch.Transforms.Length;
                }
                if (distantCount > 0)
                    Submit(farMesh, farMaterials, distantBounds, distantTransforms, distantCount, camera, false);
            }
        }

        private static void Submit(Mesh mesh, Material[] materials, Bounds bounds, Matrix4x4[] transforms,
            int count, Camera camera, bool castsShadows)
        {
            for (var submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                var parameters = new RenderParams(materials[submesh])
                {
                    camera = camera,
                    worldBounds = bounds,
                    shadowCastingMode = castsShadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
                    receiveShadows = true,
                    lightProbeUsage = LightProbeUsage.Off,
                };
                if (SystemInfo.supportsInstancing)
                    Graphics.RenderMeshInstanced(parameters, mesh, submesh, transforms, count);
                else
                    for (var i = 0; i < count; i++) Graphics.RenderMesh(parameters, mesh, submesh, transforms[i]);
            }
        }

        public void Dispose()
        {
            foreach (var material in nearMaterials.Concat(farMaterials))
            {
                if (Application.isPlaying) Object.Destroy(material);
                else Object.DestroyImmediate(material);
            }
            batches.Clear();
        }
    }
}
