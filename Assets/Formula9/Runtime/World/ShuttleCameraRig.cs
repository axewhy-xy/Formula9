using System.Collections.Generic;
using UnityEngine;

namespace Formula9.World
{
    public sealed class ShuttleCameraRig
    {
        private struct Mount
        {
            public string Name;
            public Vector3 Offset;
            public float Yaw, Pitch;
        }
        private static readonly Mount[] Mounts =
        {
            new Mount { Name = "Front", Offset = new Vector3(0, 2.5f, 6.2f), Yaw = 0, Pitch = 0 },
            // Rear, Left, Right, Cabin, Bird's-eye...
            new Mount {Name = "Rear", Offset = new Vector3(0, 2.5f, -6.2f), Yaw = 180, Pitch = 0},
            new Mount {Name = "Left", Offset = new Vector3(-2.6f, 2.5f, 0), Yaw = -90, Pitch = 0},
            new Mount {Name = "Right", Offset = new Vector3(2.6f, 2.5f, 0), Yaw = 90, Pitch = 0},
            new Mount {Name = "Cabin", Offset = new Vector3(0, 4, 5), Yaw = 180, Pitch = 20},
            new Mount {Name = "Bird's eye", Offset = new Vector3(0, 25, 0), Yaw = 0, Pitch = 90}
        };
    }
}