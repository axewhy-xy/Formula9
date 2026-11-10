/*
 * Cameras that ride along with a shuttle, one per blind spot.
 *
 * Author: Choo Jian Wei
 * Date Created: 10/11/2026
 * Last Modified: 10/11/2026
 *
 * Description:
 *     Mounts six cameras on the selected shuttle, the way real AV shuttles
 *     are watched remotely: front, rear, left, right, one looking back into
 *     the cabin, and a top-down bird's-eye view. Each camera draws into its
 *     own small render texture for the camera panel. The cameras start
 *     switched off, see only nearby, and follow the shuttle when placed
 *     each frame.
 */
using System.Collections.Generic;
using UnityEngine;

namespace Formula9.World
{
    public sealed class ShuttleCameraRig : System.IDisposable
    {
        // Where one camera sits on the shuttle and which way it looks. Offset is in metres from
        // the shuttle's centre at ground level (x right, y up, z forward); Yaw turns it right and
        // Pitch tilts it down, in degrees.
        private struct Mount
        {
            public string Name;
            public Vector3 Offset;
            public float Yaw, Pitch;
        }

        // One camera per blind spot, for a shuttle 5 m wide, 5 m tall and 12 m long. A pitch of
        // 90 looks straight down and makes the bird's-eye camera orthographic.
        private static readonly Mount[] Mounts =
        {
            new Mount { Name = "Front", Offset = new Vector3(0, 2.5f, 6.2f), Yaw = 0, Pitch = 0 },
            new Mount {Name = "Rear", Offset = new Vector3(0, 2.5f, -6.2f), Yaw = 180, Pitch = 0},
            new Mount {Name = "Left", Offset = new Vector3(-2.6f, 2.5f, 0), Yaw = -90, Pitch = 0},
            new Mount {Name = "Right", Offset = new Vector3(2.6f, 2.5f, 0), Yaw = 90, Pitch = 0},
            new Mount {Name = "Cabin", Offset = new Vector3(0, 4, 5), Yaw = 180, Pitch = 20},
            new Mount {Name = "Bird's eye", Offset = new Vector3(0, 25, 0), Yaw = 0, Pitch = 90}
        };
        // One camera and one render texture per mount, in the same order as Mounts.
        private readonly Camera[] cameras;
        private readonly RenderTexture[] textures;

        // -----------------------------------------------------------------------------------
        // Function: ShuttleCameraRig
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function creates one camera per mount, each drawing into its own 320x180
        //     render texture. The cameras are wide (90 degrees), see only 300 m, and start
        //     switched off. The bird's-eye camera is orthographic, showing 30 m top to bottom.
        //
        // Args:
        //     parent (Transform): The object the cameras are created under.
        public ShuttleCameraRig(Transform parent)
        {
            cameras = new Camera[Mounts.Length];
            textures = new RenderTexture[Mounts.Length];
            for (var i = 0; i < Mounts.Length; i++)
            {
                GameObject obj = new GameObject("Shuttle camera " + Mounts[i].Name);
                obj.transform.SetParent(parent, false);
                cameras[i] = obj.AddComponent<Camera>();
                cameras[i].fieldOfView = 90;
                cameras[i].nearClipPlane = 0.1f;
                cameras[i].farClipPlane = 300;
                cameras[i].enabled = false;
                textures[i] = new RenderTexture(320, 180, 24);
                cameras[i].targetTexture = textures[i];
                if (Mounts[i].Pitch == 90)
                {
                    cameras[i].orthographic = true;
                    cameras[i].orthographicSize = 15;
                }
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: Place
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function moves every camera to its mount on the shuttle. Each offset and
        //     angle is turned with the shuttle, so the rear camera always faces out of the
        //     back, whichever way the shuttle is heading. It is called once a frame.
        //
        // Args:
        //     position (Vector3): The shuttle's centre at ground level.
        //     rotation (Quaternion): The way the shuttle faces.
        public void Place(Vector3 position, Quaternion rotation)
        {
            for (var i = 0; i < cameras.Length; i++)
            {
                cameras[i].transform.position = position + (rotation * Mounts[i].Offset);
                cameras[i].transform.rotation = rotation * Quaternion.Euler(Mounts[i].Pitch, Mounts[i].Yaw,0);
            }
        }

        // The cameras, so the traffic can be drawn for them too.
        public IReadOnlyList<Camera> Cameras => cameras;
        // What each camera draws into, for the camera panel; same order as Cameras.
        public IReadOnlyList<RenderTexture> Textures => textures;

        // -----------------------------------------------------------------------------------
        // Function: SetActive
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function switches every camera on or off. A camera that is off draws
        //     nothing and costs nothing, so they stay off while no one is watching them.
        //
        // Args:
        //     active (bool): True to switch the cameras on, false to switch them off.
        public void SetActive(bool active)
        {
            for (var i = 0; i < cameras.Length; i++)
            { cameras[i].enabled = active; }
        }

        // -----------------------------------------------------------------------------------
        // Function: Dispose
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function releases the render textures' GPU memory and removes the cameras'
        //     objects. Outside Play mode, as in EditMode tests, they are destroyed at once.
        public void Dispose()
        {
            for (var i = 0; i < cameras.Length; i++)
            {
                textures[i].Release();
                if (cameras[i] == null) continue;
                if (Application.isPlaying) Object.Destroy(cameras[i].gameObject);
                else Object.DestroyImmediate(cameras[i].gameObject);
            }
        }
    }
    
}