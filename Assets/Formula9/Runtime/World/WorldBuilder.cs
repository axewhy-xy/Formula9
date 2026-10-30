using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Contracts;
using GLTFast;
using UnityEngine;

namespace Formula9.World
{
    // The instantiated 3D world: one GameObject per semantic entity, keyed by entity_id.
    public sealed class WorldScene
    {
        public ScenePackage Package;
        public Transform Root;
        public readonly Dictionary<string, Transform> Entities = new Dictionary<string, Transform>();
        public readonly Dictionary<string, SceneEntity> Definitions = new Dictionary<string, SceneEntity>();

        public int ShuttleCount => Definitions.Values.Count(e => e.Kind == "SHUTTLE");

        // Moves each shuttle to its snapshot pose. Returns null, or why the snapshot does not fit
        // this scene (the Qt client's "scene/vehicle binding mismatch").
        public string ApplyPoses(FullSnapshot snapshot)
        {
            if (snapshot.Spatial == null || snapshot.Spatial.ManifestSha256 == null) return "snapshot has no spatial pins";
            if (snapshot.Vehicles.Count != ShuttleCount) return "shuttle count differs from the scene";
            foreach (var vehicle in snapshot.Vehicles)
            {
                var pose = vehicle.Spatial;
                if (pose == null || !Definitions.TryGetValue(vehicle.VehicleId, out var entity) || entity.Kind != "SHUTTLE")
                    return $"{vehicle.VehicleId} is not a shuttle in this scene";
                if (pose.RenderAssetId != entity.RenderAssetId || !pose.DimensionsM.SequenceEqual(entity.DimensionsM)
                    || !pose.AssetToBody.SequenceEqual(entity.AssetToBody))
                    return $"{vehicle.VehicleId} does not match its scene binding";
                if (!WorldMath.Apply(Entities[vehicle.VehicleId], WorldMath.PlacedAsset(pose.PositionM, pose.OrientationXyzw, pose.AssetToBody)))
                    return $"{vehicle.VehicleId} has a non-rigid pose";
            }
            return null;
        }

        public void Destroy()
        {
            if (Root != null) WorldBuilder.Remove(Root.gameObject);
        }
    }

    // Decodes a verified ScenePackage with glTFast and places every entity. Each placed asset is
    // checked against the entity's declared dimensions, so a wrong axis or transform convention
    // is reported instead of drawn.
    public static class WorldBuilder
    {
        public const float DimensionTolerance = 1e-3f;

        // Destroy is deferred and only allowed while playing; edit-mode tools and tests need it now.
        public static void Remove(GameObject target)
        {
            if (Application.isPlaying) Object.Destroy(target);
            else Object.DestroyImmediate(target);
        }

        public static async Task<WorldScene> BuildAsync(ScenePackage package, Transform parent, CancellationToken cancellation = default)
        {
            var root = new GameObject($"World {package.Manifest.SceneRevision}").transform;
            root.SetParent(parent, false);
            var world = new WorldScene { Package = package, Root = root };
            try
            {
                var imports = new Dictionary<string, GltfImport>();
                foreach (var asset in package.Assets)
                {
                    // Spreading decode over frames needs play mode; edit-mode tools load in one go.
                    var import = Application.isPlaying ? new GltfImport() : new GltfImport(deferAgent: new UninterruptedDeferAgent());
                    if (!await import.Load(asset.Value, null, new ImportSettings(), cancellation))
                        throw new SceneException("SCENE_ASSET_DECODE", asset.Key);
                    imports[asset.Key] = import;
                }
                foreach (var entity in package.Manifest.Entities)
                {
                    var node = new GameObject(entity.EntityId).transform;
                    node.SetParent(root, false);
                    if (!await imports[SceneLoader.RenderedAsset(package, entity)].InstantiateMainSceneAsync(node, cancellation))
                        throw new SceneException("SCENE_ASSET_INSTANTIATE", entity.EntityId);
                    if (!WorldMath.Apply(node, WorldMath.PlacedAsset(entity.PositionM, entity.OrientationXyzw, entity.AssetToBody)))
                        throw new SceneException("SCENE_BODY_POSE", entity.EntityId);
                    CheckDimensions(node, entity);
                    foreach (var filter in node.GetComponentsInChildren<MeshFilter>())
                        filter.gameObject.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
                    world.Entities[entity.EntityId] = node;
                    world.Definitions[entity.EntityId] = entity;
                }
                return world;
            }
            catch
            {
                Remove(root.gameObject);
                throw;
            }
        }

        // The rendered geometry, seen from the body frame, must fill exactly ±dimensions/2.
        private static void CheckDimensions(Transform node, SceneEntity entity)
        {
            var bodyFromUnity = (WorldMath.CanonicalToUnity * WorldMath.BodyPose(entity.PositionM, entity.OrientationXyzw)).inverse;
            var corners = new List<Vector3>();
            foreach (var filter in node.GetComponentsInChildren<MeshFilter>())
            {
                var bounds = filter.sharedMesh.bounds;
                for (var i = 0; i < 8; i++)
                {
                    var local = bounds.center + Vector3.Scale(bounds.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    corners.Add(bodyFromUnity.MultiplyPoint3x4(filter.transform.localToWorldMatrix.MultiplyPoint3x4(local)));
                }
            }
            if (corners.Count == 0) throw new SceneException("SCENE_REQUIRED_GEOMETRY", entity.EntityId);
            for (var axis = 0; axis < 3; axis++)
            {
                var half = (float)entity.DimensionsM[axis] / 2;
                if (Mathf.Abs(corners.Min(c => c[axis]) + half) > DimensionTolerance || Mathf.Abs(corners.Max(c => c[axis]) - half) > DimensionTolerance)
                    throw new SceneException("SCENE_BODY_DIMENSIONS", entity.EntityId);
            }
        }
    }
}
