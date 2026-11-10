/*
 * Cameras that ride along with a shuttle, one per blind spot.
 *
 * Author: Choo Jian Wei
 * Date Created: 10/11/2026
 * Last Modified: 10/11/2026
 *
 * Description:
 *     Mounts four cameras on the selected shuttle, the way real AV shuttles
 *     are watched remotely: front, rear, left and right, about 2 m above
 *     the road. Each camera draws into its own render texture for the
 *     camera view, which shows one of them large and the rest as
 *     thumbnails. The cameras start switched off, and their render textures
 *     are only made the first time they are switched on. They draw the same
 *     sky and distance as the 3D view, and follow the shuttle when placed
 *     each frame.
 */
using System.Collections.Generic;
using UnityEngine;

namespace Formula9.World
{
    public sealed class ShuttleCameraRig : System.IDisposable
    {
        // Where one camera sits on the shuttle and which way it looks. Offset is in metres from
        // the middle of the shuttle, 2.5 m above the road (x right, y up, z forward); Yaw turns
        // it right and Pitch tilts it down, in degrees.
        private struct Mount
        {
            public string Name;
            public Vector3 Offset;
            public float Yaw, Pitch;
        }

        // One camera per blind spot, about 2 m above the road, just outside both shapes a
        // shuttle can have. The bus model is 4.6 m wide and 12.6 m long but not centred on the
        // shuttle's position: it reaches 6.6 m ahead and 6.0 m behind, so the front and rear
        // mounts are uneven on purpose. The plain box drawn when the model is missing is 5 m
        // wide and 12 m long, so the side mounts sit 2.6 m out.
        private static readonly Mount[] Mounts =
        {
            new Mount { Name = "Front", Offset = new Vector3(0, -0.5f, 6.8f), Yaw = 0, Pitch = 0 },
            new Mount { Name = "Rear", Offset = new Vector3(0, -0.5f, -6.2f), Yaw = 180, Pitch = 0 },
            new Mount { Name = "Left", Offset = new Vector3(-2.6f, -0.5f, 0), Yaw = -90, Pitch = 0 },
            new Mount { Name = "Right", Offset = new Vector3(2.6f, -0.5f, 0), Yaw = 90, Pitch = 0 }
        };
        // Big enough to fill the 3D view when shown large. Any thumbnail can be shown large at
        // a click, so they all have this size rather than being made again on each click.
        private const int TextureWidth = 960, TextureHeight = 540;
        // One camera and one render texture per mount, in the same order as Mounts. The
        // textures stay null until the cameras are first switched on.
        private readonly Camera[] cameras;
        private readonly RenderTexture[] textures;
        // Whether the cameras are in use, and which thumbnail draws on the next frame.
        private bool active;
        private int next;

        // -----------------------------------------------------------------------------------
        // Function: ShuttleCameraRig
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function creates one camera per mount. The cameras are wide (90 degrees)
        //     and start switched off; Match gives them the 3D view's sky and view distance.
        //     Their render textures are made by SetActive, so a session in which nobody opens
        //     the cameras never allocates them.
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
                cameras[i].enabled = false;
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: Match
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function gives every camera the 3D view's background, what it draws and how
        //     far it sees, so the camera pictures show the same sky and distant buildings.
        //
        // Args:
        //     view (Camera): The 3D view's camera.
        public void Match(Camera view)
        {
            foreach (var camera in cameras)
            {
                camera.clearFlags = view.clearFlags;
                camera.backgroundColor = view.backgroundColor;
                camera.cullingMask = view.cullingMask;
                camera.farClipPlane = view.farClipPlane;
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
        //     position (Vector3): The middle of the shuttle, 2.5 m above the road.
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
        // What each camera draws into, for the camera view; same order as Cameras. Each is
        // null until the cameras are first switched on.
        public IReadOnlyList<RenderTexture> Textures => textures;
        // The name of camera i, for its label in the panel.
        public string NameOf(int i) => Mounts[i].Name;

        // -----------------------------------------------------------------------------------
        // Function: SetActive
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function switches every camera on or off. A camera that is off draws
        //     nothing and costs nothing, so they stay off while no one is watching them. The
        //     first time they are switched on, their render textures are made.
        //
        // Args:
        //     active (bool): True to switch the cameras on, false to switch them off.
        public void SetActive(bool active)
        {
            this.active = active;
            for (var i = 0; i < cameras.Length; i++)
            {
                if (active && textures[i] == null)
                {
                    textures[i] = new RenderTexture(TextureWidth, TextureHeight, 24);
                    cameras[i].targetTexture = textures[i];
                }
                cameras[i].enabled = active;
            }
        }
        // -----------------------------------------------------------------------------------
        // Function: Tick
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function picks which cameras draw this frame. The one shown large draws every
        //     frame, so it moves smoothly; the thumbnails take turns, one a frame, so the view
        //     costs two cameras instead of four. At 60 frames a second each thumbnail still
        //     refreshes twenty times a second. It does nothing while the cameras are off.
        //
        // Args:
        //     main (int): The camera shown large.
        public void Tick(int main)
        {
            if (!active) return;
            // The large camera draws anyway, so its turn as a thumbnail goes to the next one
            if (next == main) next = (next + 1) % cameras.Length;
            for (var i = 0; i < cameras.Length; i++)
                cameras[i].enabled = i == main || i == next;
            next = (next + 1) % cameras.Length;
        }

        // -----------------------------------------------------------------------------------
        // Function: Dispose
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function releases and destroys the render textures and removes the cameras'
        //     objects. Outside Play mode, as in EditMode tests, they are destroyed at once.
        public void Dispose()
        {
            for (var i = 0; i < cameras.Length; i++)
            {
                if (textures[i] != null)
                {
                    textures[i].Release();
                    if (Application.isPlaying) Object.Destroy(textures[i]);
                    else Object.DestroyImmediate(textures[i]);
                }
                if (cameras[i] == null) continue;
                if (Application.isPlaying) Object.Destroy(cameras[i].gameObject);
                else Object.DestroyImmediate(cameras[i].gameObject);
            }
        }
    }

}