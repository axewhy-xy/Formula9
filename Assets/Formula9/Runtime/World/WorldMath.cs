using System.Collections.Generic;
using UnityEngine;

namespace Formula9.World
{
    // Coordinate conversions between the shared contract and Unity.
    //
    // Canonical world: right-handed, X east, Y north, Z up, metres (RH_X_EAST_Y_NORTH_Z_UP).
    // Unity: left-handed, Y up. Swapping Y and Z maps one onto the other (the reflection also
    // turns right-handed into left-handed).
    // Assets: raw glTF coordinates; asset_to_body takes them into the body frame. glTFast negates X
    // when it imports glTF, so a placed asset first undoes that flip.
    public static class WorldMath
    {
        // glTFast's import conversion; it is its own inverse.
        private static readonly Matrix4x4 GltfFlip = Matrix4x4.Scale(new Vector3(-1, 1, 1));

        // Canonical (x, y, z) -> Unity (x, z, y).
        public static readonly Matrix4x4 CanonicalToUnity = new Matrix4x4(
            new Vector4(1, 0, 0, 0), new Vector4(0, 0, 1, 0), new Vector4(0, 1, 0, 0), new Vector4(0, 0, 0, 1));

        public static Vector3 ToUnity(IReadOnlyList<double> canonical) =>
            new Vector3((float)canonical[0], (float)canonical[2], (float)canonical[1]);

        // World-from-body as a canonical rigid transform.
        public static Matrix4x4 BodyPose(IReadOnlyList<double> position, IReadOnlyList<double> orientationXyzw)
        {
            var rotation = new Quaternion((float)orientationXyzw[0], (float)orientationXyzw[1], (float)orientationXyzw[2], (float)orientationXyzw[3]);
            return Matrix4x4.TRS(new Vector3((float)position[0], (float)position[1], (float)position[2]), rotation, Vector3.one);
        }

        // asset_to_body is row-major, as in the manifest and snapshot.
        public static Matrix4x4 AssetToBody(IReadOnlyList<double> rowMajor)
        {
            var m = new Matrix4x4();
            for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
                m[r, c] = (float)rowMajor[r * 4 + c];
            return m;
        }

        // Unity world-from-imported-asset: undo glTFast's flip, take the asset into the body frame,
        // place the body in the canonical world, then change basis to Unity. The two reflections
        // cancel, so the result is a proper rigid transform a Unity Transform can hold.
        public static Matrix4x4 PlacedAsset(IReadOnlyList<double> position, IReadOnlyList<double> orientationXyzw,
            IReadOnlyList<double> assetToBody) =>
            CanonicalToUnity * BodyPose(position, orientationXyzw) * AssetToBody(assetToBody) * GltfFlip;

        // The eight corners of the body's operational box (centred, dimensions_m) in Unity space.
        public static Bounds BodyBounds(IReadOnlyList<double> position, IReadOnlyList<double> orientationXyzw, IReadOnlyList<double> dimensions)
        {
            var world = CanonicalToUnity * BodyPose(position, orientationXyzw);
            var half = new Vector3((float)dimensions[0], (float)dimensions[1], (float)dimensions[2]) / 2;
            var bounds = new Bounds(world.MultiplyPoint3x4(-half), Vector3.zero);
            for (var i = 0; i < 8; i++)
                bounds.Encapsulate(world.MultiplyPoint3x4(new Vector3(
                    (i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z)));
            return bounds;
        }

        // Applies a rigid transform to a Transform. Returns false when the matrix is not rigid
        // (scaled, sheared or reflected), which a contract-valid pose never produces.
        public static bool Apply(Transform target, Matrix4x4 matrix)
        {
            var scale = matrix.lossyScale;
            if (Mathf.Abs(scale.x - 1) > 1e-3f || Mathf.Abs(scale.y - 1) > 1e-3f || Mathf.Abs(scale.z - 1) > 1e-3f
                || matrix.determinant < 0) return false;
            target.SetPositionAndRotation(matrix.GetPosition(), matrix.rotation);
            return true;
        }
    }
}
