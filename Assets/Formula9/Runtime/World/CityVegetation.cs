/*
 * Instanced roadside trees for the 3D city.
 *
 * Author: Nur Syazliyarni Binte Zazali
 * Date Created: 26/11/2026
 * Last Modified: 26/11/2026
 *
 * Description:
 *     Loads tree placements with proportional size variation, groups them into spatial
 *     batches, and selects detail and shadows by camera distance and screen size.
 */
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
        private readonly Dictionary<Color, Material> materialsByColor = new();
        private readonly RenderParams drawParameters;
        private readonly List<(Bounds Bounds, float TreeSize, Matrix4x4[] Transforms)> batches = new();
        private readonly Plane[] frustum = new Plane[6];
        private readonly Matrix4x4[] distantTransforms = new Matrix4x4[BatchSize];

        internal int TreeCount { get; }
        internal int BatchCount => batches.Count;
        internal int MaterialCount => materialsByColor.Count;

        private CityVegetation((Mesh Mesh, Material[] Materials) near, (Mesh Mesh, Material[] Materials) far, Material instancingMaterial,
            TextAsset placements, Matrix4x4 cityToWorld)
        {
            nearMesh = near.Mesh;
            farMesh = far.Mesh;
            // RenderParams reads Unity's default rendering layer; do that once, not per submesh.
            drawParameters = new RenderParams(instancingMaterial) { receiveShadows = true, lightProbeUsage = LightProbeUsage.Off };
            try
            {
                nearMaterials = CreateMaterials(near.Materials, nearMesh.subMeshCount, instancingMaterial);
                farMaterials = CreateMaterials(far.Materials, farMesh.subMeshCount, instancingMaterial);
                var meshBounds = nearMesh.bounds;
                meshBounds.Encapsulate(farMesh.bounds);

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
            catch
            {
                Dispose();
                throw;
            }
        }

        internal static CityVegetation Load(Matrix4x4 cityToWorld) => Load(cityToWorld, SystemInfo.supportsInstancing);

        /// <summary>Check the reported device capability before loading optional tree resources.</summary>
        internal static CityVegetation Load(Matrix4x4 cityToWorld, bool instancingSupported)
        {
            // Trees are optional scenery. Avoid thousands of individual draws on older hardware.
            if (!instancingSupported)
            {
                Debug.LogWarning("[CityView] GPU instancing is unavailable; roadside trees are disabled.");
                return null;
            }
            var near = Resources.Load<GameObject>(ResourceFolder + "RoadsideTree")?.GetComponentInChildren<MeshFilter>();
            var far = Resources.Load<GameObject>(ResourceFolder + "RoadsideTreeLow")?.GetComponentInChildren<MeshFilter>();
            var placements = Resources.Load<TextAsset>(ResourceFolder + "ToaPayohTrees");
            // Use the instancing shader already shipped for traffic and pedestrians.
            var instancingMaterial = Resources.Load<Material>("Formula9/TrafficVehicle");
            return Create(near, far, placements, instancingMaterial, cityToWorld);
        }

        /// <summary>Validate both detail levels before creating any owned materials.</summary>
        internal static CityVegetation Create(MeshFilter near, MeshFilter far, TextAsset placements,
            Material instancingMaterial, Matrix4x4 cityToWorld)
        {
            if (placements == null || instancingMaterial == null || near == null || far == null)
            {
                Debug.LogWarning("[CityView] Roadside tree assets are missing.");
                return null;
            }
            if (!TryReadModel(near, out var nearModel) || !TryReadModel(far, out var farModel) ||
                !instancingMaterial.enableInstancing || instancingMaterial.shader == null || !instancingMaterial.shader.isSupported)
            {
                Debug.LogWarning("[CityView] Roadside tree mesh, renderer or materials are invalid; skipping vegetation.");
                return null;
            }
            try { return new CityVegetation(nearModel, farModel, instancingMaterial, placements, cityToWorld); }
            catch (Exception error)
            {
                Debug.LogWarning("[CityView] Roadside trees unavailable: " + error.Message);
                return null;
            }
        }

        private static bool TryReadModel(MeshFilter filter, out (Mesh Mesh, Material[] Materials) model)
        {
            model = default;
            var mesh = filter.sharedMesh;
            if (mesh == null || mesh.subMeshCount == 0 || !filter.TryGetComponent<MeshRenderer>(out var renderer)) return false;
            var sources = renderer.sharedMaterials;
            if (sources.Length < mesh.subMeshCount) return false;
            for (var i = 0; i < mesh.subMeshCount; i++)
                if (sources[i] == null) return false;
            model = (mesh, sources);
            return true;
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

        private Material[] CreateMaterials(Material[] sources, int submeshCount, Material template)
        {
            var result = new Material[submeshCount];
            for (var i = 0; i < result.Length; i++)
            {
                // The tree shader uses the source colour only. Both detail levels can share bark.
                var color = sources[i].color;
                if (!materialsByColor.TryGetValue(color, out var material))
                {
                    material = new Material(template) { name = sources[i].name, color = color };
                    materialsByColor.Add(color, material);
                }
                result[i] = material;
            }
            return result;
        }

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
            if (batches.Count == 0) return;
            for (var i = 0; i < cameras.Count; i++)
            {
                var camera = cameras[i];
                if (camera == null || !camera.isActiveAndEnabled || (camera.cullingMask & 1) == 0) continue;
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
                    if (!GeometryUtility.TestPlanesAABB(frustum, batch.Bounds))
                    {
                        // Nearby trees can cast into the view even when their crowns are off-screen.
                        if (batch.Bounds.SqrDistance(eye) < ShadowDistanceM * ShadowDistanceM)
                            Submit(nearMesh, nearMaterials, batch.Bounds, batch.Transforms, batch.Transforms.Length, camera, ShadowCastingMode.ShadowsOnly);
                        continue;
                    }
                    // The nearest edge keeps the full silhouette on close trees at cell boundaries.
                    var depth = Vector3.Dot(batch.Bounds.center - eye, forward)
                        - Vector3.Dot(batch.Bounds.extents, absoluteForward);
                    var size = batch.TreeSize * pixelsPerMetre / (orthographic ? 1 : Mathf.Max(nearClip, depth));
                    if (size < MinimumSizePx) continue;
                    if (size >= DetailSizePx)
                    {
                        var castsShadows = batch.Bounds.SqrDistance(eye) < ShadowDistanceM * ShadowDistanceM;
                        Submit(nearMesh, nearMaterials, batch.Bounds, batch.Transforms, batch.Transforms.Length, camera,
                            castsShadows ? ShadowCastingMode.On : ShadowCastingMode.Off);
                        continue;
                    }

                    // Distant cells already passed camera culling. Combine them to keep the overview's draw count low.
                    if (distantCount + batch.Transforms.Length > BatchSize)
                    {
                        Submit(farMesh, farMaterials, distantBounds, distantTransforms, distantCount, camera, ShadowCastingMode.Off);
                        distantCount = 0;
                    }
                    Array.Copy(batch.Transforms, 0, distantTransforms, distantCount, batch.Transforms.Length);
                    if (distantCount == 0) distantBounds = batch.Bounds;
                    else distantBounds.Encapsulate(batch.Bounds);
                    distantCount += batch.Transforms.Length;
                }
                if (distantCount > 0)
                    Submit(farMesh, farMaterials, distantBounds, distantTransforms, distantCount, camera, ShadowCastingMode.Off);
            }
        }

        private void Submit(Mesh mesh, Material[] materials, Bounds bounds, Matrix4x4[] transforms,
            int count, Camera camera, ShadowCastingMode shadows)
        {
            var parameters = drawParameters;
            parameters.camera = camera;
            parameters.worldBounds = bounds;
            parameters.shadowCastingMode = shadows;
            for (var submesh = 0; submesh < materials.Length; submesh++)
            {
                parameters.material = materials[submesh];
                Graphics.RenderMeshInstanced(parameters, mesh, submesh, transforms, count);
            }
        }

        public void Dispose()
        {
            foreach (var material in materialsByColor.Values)
            {
                if (Application.isPlaying) Object.Destroy(material);
                else Object.DestroyImmediate(material);
            }
            materialsByColor.Clear();
            batches.Clear();
        }
    }
}
