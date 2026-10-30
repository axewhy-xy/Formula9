/*
 * Coordinate conversions between the shared contract and Unity.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Part of the Unity client's 3D world. The canonical world is right-handed, X east, Y north, Z
 *     up, in metres (RH_X_EAST_Y_NORTH_Z_UP). Unity is left-handed and Y up; swapping Y and Z maps
 *     one onto the other, and the reflection also turns right-handed into left-handed. Assets are
 *     in raw glTF coordinates, and asset_to_body takes them into the body frame. glTFast negates X
 *     when it imports glTF, so a placed asset first undoes that flip.
 */

using System.Collections.Generic;
using UnityEngine;

namespace Formula9.World
{
    // -----------------------------------------------------------------------------------
    // Class: WorldMath
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class converts poses and transforms from the contract into Unity.
    //
    //     Attributes: CanonicalToUnity maps canonical (x, y, z) to Unity (x, z, y).
    public static class WorldMath
    {
        // glTFast's import conversion; it is its own inverse.
        private static readonly Matrix4x4 GltfFlip = Matrix4x4.Scale(new Vector3(-1, 1, 1));

        // Canonical (x, y, z) -> Unity (x, z, y).
        public static readonly Matrix4x4 CanonicalToUnity = new Matrix4x4(
            new Vector4(1, 0, 0, 0), new Vector4(0, 0, 1, 0), new Vector4(0, 1, 0, 0), new Vector4(0, 0, 0, 1));

        // -----------------------------------------------------------------------------------
        // Function: ToUnity
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function converts a canonical point to Unity coordinates.
        //
        // Args:
        //     canonical (IReadOnlyList<double>): The point (x, y, z) in canonical metres.
        //
        // Returns:
        //     Vector3: The point in Unity coordinates.
        public static Vector3 ToUnity(IReadOnlyList<double> canonical) =>
            new Vector3((float)canonical[0], (float)canonical[2], (float)canonical[1]);

        // -----------------------------------------------------------------------------------
        // Function: BodyPose
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function builds world-from-body as a canonical rigid transform.
        //
        // Args:
        //     position (IReadOnlyList<double>): The body centre in canonical metres (x, y, z).
        //     orientationXyzw (IReadOnlyList<double>): The body orientation as a unit quaternion
        //         (x, y, z, w).
        //
        // Returns:
        //     Matrix4x4: The canonical world-from-body transform.
        public static Matrix4x4 BodyPose(IReadOnlyList<double> position, IReadOnlyList<double> orientationXyzw)
        {
            var rotation = new Quaternion((float)orientationXyzw[0], (float)orientationXyzw[1], (float)orientationXyzw[2], (float)orientationXyzw[3]);
            return Matrix4x4.TRS(new Vector3((float)position[0], (float)position[1], (float)position[2]), rotation, Vector3.one);
        }

        // -----------------------------------------------------------------------------------
        // Function: AssetToBody
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reads asset_to_body, which is row-major in the manifest and snapshot.
        //
        // Args:
        //     rowMajor (IReadOnlyList<double>): The 16 matrix values, row by row.
        //
        // Returns:
        //     Matrix4x4: The body-from-asset transform.
        public static Matrix4x4 AssetToBody(IReadOnlyList<double> rowMajor)
        {
            var m = new Matrix4x4();
            for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
                m[r, c] = (float)rowMajor[r * 4 + c];
            return m;
        }

        // -----------------------------------------------------------------------------------
        // Function: PlacedAsset
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function builds Unity world-from-imported-asset: undo glTFast's flip, take the
        //     asset into the body frame, place the body in the canonical world, then change basis
        //     to Unity. The two reflections cancel, so the result is a proper rigid transform a
        //     Unity Transform can hold.
        //
        // Args:
        //     position (IReadOnlyList<double>): The body centre in canonical metres (x, y, z).
        //     orientationXyzw (IReadOnlyList<double>): The body orientation as a unit quaternion
        //         (x, y, z, w).
        //     assetToBody (IReadOnlyList<double>): asset_to_body, row-major.
        //
        // Returns:
        //     Matrix4x4: The Unity world-from-asset transform.
        public static Matrix4x4 PlacedAsset(IReadOnlyList<double> position, IReadOnlyList<double> orientationXyzw,
            IReadOnlyList<double> assetToBody) =>
            CanonicalToUnity * BodyPose(position, orientationXyzw) * AssetToBody(assetToBody) * GltfFlip;

        // -----------------------------------------------------------------------------------
        // Function: BodyBounds
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function computes the Unity-space bounds of the body's operational box, centred
        //     on the body with the given dimensions.
        //
        // Args:
        //     position (IReadOnlyList<double>): The body centre in canonical metres (x, y, z).
        //     orientationXyzw (IReadOnlyList<double>): The body orientation as a unit quaternion
        //         (x, y, z, w).
        //     dimensions (IReadOnlyList<double>): dimensions_m: length, width and height in metres.
        //
        // Returns:
        //     Bounds: The axis-aligned bounds of the box's eight corners.
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

        // -----------------------------------------------------------------------------------
        // Function: Apply
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function applies a rigid transform to a Transform. It refuses a matrix that is
        //     scaled, sheared or reflected, which a contract-valid pose never produces.
        //
        // Args:
        //     target (Transform): The transform to move.
        //     matrix (Matrix4x4): The world transform.
        //
        // Returns:
        //     bool: False when the matrix is not rigid; the target is then unchanged.
        public static bool Apply(Transform target, Matrix4x4 matrix)
        {
            if (!IsRigid(matrix)) return false;
            target.SetPositionAndRotation(matrix.GetPosition(), matrix.rotation);
            return true;
        }

        // -----------------------------------------------------------------------------------
        // Function: IsRigid
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function tells whether a matrix is free of scale, shear and reflection.
        //
        // Args:
        //     matrix (Matrix4x4): The world transform.
        //
        // Returns:
        //     bool: True when a Transform can hold it exactly.
        public static bool IsRigid(Matrix4x4 matrix)
        {
            var scale = matrix.lossyScale;
            return Mathf.Abs(scale.x - 1) <= 1e-3f && Mathf.Abs(scale.y - 1) <= 1e-3f && Mathf.Abs(scale.z - 1) <= 1e-3f
                && matrix.determinant >= 0;
        }
    }
}
