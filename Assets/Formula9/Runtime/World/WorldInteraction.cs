using System;
using System.Linq;
using Formula9.Contracts;
using UnityEngine;

namespace Formula9.World
{
    public static class WorldInteraction
    {
        public static bool TryPick(WorldScene world, Ray ray, out string entityId, out Vector3 canonical)
        {
            entityId = null; canonical = default;
            var nearest = float.PositiveInfinity;
            Physics.SyncTransforms();
            foreach (var entity in world.Entities)
                foreach (var collider in entity.Value.GetComponentsInChildren<MeshCollider>())
                    if (collider.Raycast(ray, out var hit, nearest))
                    {
                        nearest = hit.distance; entityId = entity.Key;
                        canonical = new Vector3(hit.point.x, hit.point.z, hit.point.y);
                    }
            return entityId != null;
        }

        public static bool OnSurface(RoadSurface surface, Vector3 canonical, float tolerance = 0.02f)
        {
            var normal = V(surface.Normal).normalized;
            for (var i = 1; i < surface.PathSamplesM.Count; i++)
            {
                var a = V(surface.PathSamplesM[i - 1]); var delta = V(surface.PathSamplesM[i]) - a;
                if (delta.sqrMagnitude < 1e-10f) continue;
                var u = Vector3.Dot(canonical - a, delta) / delta.sqrMagnitude;
                if (u < -1e-6f || u > 1 + 1e-6f) continue;
                var offset = canonical - (a + Mathf.Clamp01(u) * delta);
                if (Mathf.Abs(Vector3.Dot(offset, normal)) <= tolerance && offset.magnitude <= surface.WidthM / 2 + tolerance) return true;
            }
            return false;
        }

        public static bool NodeOnSurface(TrainingSceneRevision manifest, RouteEditorContext context, RouteEditorNode node, string surfaceId)
        {
            if (manifest == null || context == null || context.SceneRevision != manifest.SceneRevision) return false;
            var surface = manifest.RoadSurfaces.FirstOrDefault(s => s.SurfaceId == surfaceId);
            return surface != null && context.Edges.Any(e => e.EdgeId == surface.DirectedEdgeId && (e.Source == node.NodeId || e.Target == node.NodeId)) &&
                OnSurface(surface, new Vector3((float)node.X, (float)node.Y, (float)node.Z));
        }

        private static Vector3 V(System.Collections.Generic.List<double> p) => new Vector3((float)p[0], (float)p[1], (float)p[2]);
    }
}
