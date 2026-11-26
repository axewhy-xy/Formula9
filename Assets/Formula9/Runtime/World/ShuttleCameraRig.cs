/*
 * Cameras that ride along with a shuttle, one per blind spot.
 *
 * Author: Choo Jian Wei
 * Date Created: 10/11/2026
 * Last Modified: 26/11/2026
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
        // The camera shown large draws into one image the size of the view it fills (SetLargeSize),
        // so it is never stretched and blurred and a wider view shows more to the sides; the
        // thumbnails, shown at 192x108, draw into small ones. Edges are smoothed (MSAA).
        private const int SmallWidth = 320, SmallHeight = 180, Antialiasing = 4, MaxLargeWidth = 1920;
        private int largeWidth = 960, largeHeight = 540;
        // One camera and one small image per mount, in the same order as Mounts, and the large
        // image. The images exist only while the cameras are on.
        private readonly Camera[] cameras;
        private readonly RenderTexture[] textures;
        private RenderTexture large;
        // The camera shown large, whether the cameras are in use, and which thumbnail draws on
        // the next frame.
        private int main;
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
        //     far it sees, so the camera pictures show the same sky and distant buildings. The
        //     planning overlays are left out, as a real camera on the shuttle would not see them.
        //
        // Args:
        //     view (Camera): The 3D view's camera.
        public void Match(Camera view)
        {
            foreach (var camera in cameras)
            {
                camera.clearFlags = view.clearFlags;
                camera.backgroundColor = view.backgroundColor;
                // Everything the 3D view draws except the planning aids
                camera.cullingMask = view.cullingMask & ~(1 << CityView.PlanningLayer);
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
        // The image the camera view shows large; null while the cameras are closed.
        public RenderTexture Large => large;
        // The image for camera i's thumbnail: the large one for the camera shown large, as it
        // stops drawing into its own small one.
        public RenderTexture Thumbnail(int i) => i == main ? large : textures[i];
        // The name of camera i, for its label in the panel.
        public string NameOf(int i) => Mounts[i].Name;
        // Whether the thumbnails draw. While the panel's thumbnails are folded away nobody sees
        // them, so only the camera shown large draws.
        public bool Thumbnails { get; set; } = true;

        // -----------------------------------------------------------------------------------
        // Function: SetMain
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function picks the camera shown large. It draws into the large image and
        //     the others into their small ones. Before the images are made, every camera
        //     just gets null.
        //
        // Args:
        //     index (int): The camera to show large, in the rig's order.
        public void SetMain(int index)
        {
            main = index;
            for (var i = 0; i < cameras.Length; i++)
            {
                var target = i == main ? large : textures[i];
                cameras[i].targetTexture = target;
                // The picture's shape, so a wider view shows more rather than stretching
                if (target != null) cameras[i].aspect = (float)target.width / target.height;
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: SetLargeSize
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function sizes the large image to the view it fills, in physical pixels, so it
        //     is drawn sharp rather than stretched. While the cameras are open it is remade at once.
        //
        // Returns:
        //     bool: True if the large image was remade, so whoever shows it must show the new one.
        public bool SetLargeSize(int width, int height)
        {
            // No wider than MaxLargeWidth, keeping the shape: on a 1440p or 4K screen a full-size image
            // with MSAA would take a few hundred MB of graphics memory for little visible gain
            if (width > MaxLargeWidth && width > 0)
            {
                height = Mathf.RoundToInt(height * (float)MaxLargeWidth / width);
                width = MaxLargeWidth;
            }
            width = Mathf.Clamp(width, 16, 4096);
            height = Mathf.Clamp(height, 16, 4096);
            if (width == largeWidth && height == largeHeight) return false;
            largeWidth = width;
            largeHeight = height;
            if (large == null) return false;
            var old = large;
            if (cameras[main] != null) cameras[main].targetTexture = null;
            large = MakeLarge();
            SetMain(main);
            Free(old);
            return true;
        }

        private RenderTexture MakeLarge() =>
            new RenderTexture(largeWidth, largeHeight, 24) { name = "Shuttle camera (large)", antiAliasing = Antialiasing };

        // Free one image: Release it, then Destroy (playing) or DestroyImmediate (EditMode)
        private static void Free(RenderTexture t)
        {
            if (t == null) return;
            t.Release();
            if (Application.isPlaying) Object.Destroy(t);
            else Object.DestroyImmediate(t);
        }

        // -----------------------------------------------------------------------------------
        // Function: SetActive
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function switches every camera on or off. A camera that is off draws
        //     nothing and costs nothing, so they stay off while no one is watching them. The
        //     images are made when the cameras switch on and freed when they switch off.
        //
        // Args:
        //     active (bool): True to switch the cameras on, false to switch them off.
        public void SetActive(bool active)
        {
            this.active = active;
            if (active && large == null)
            {
                large = MakeLarge();
                for (var i = 0; i < textures.Length; i++)
                    textures[i] = new RenderTexture(SmallWidth, SmallHeight, 24) { antiAliasing = Antialiasing };
                // Points every camera at its image
                SetMain(main);
            }
            if (!active) FreeTextures();
            for (var i = 0; i < cameras.Length; i++)
                cameras[i].enabled = active;
        }

        // -----------------------------------------------------------------------------------
        // Function: FreeTextures
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function releases and destroys the large image and every small one, and
        //     forgets them, so the cameras use no image memory while they are closed. Outside
        //     Play mode, as in EditMode tests, they are destroyed at once.
        private void FreeTextures()
        {
            // The cameras let go of their images first: Unity will not release one a camera
            // still draws into
            foreach (var camera in cameras)
                if (camera != null) camera.targetTexture = null;
            Free(large);
            large = null;
            for (var i = 0; i < textures.Length; i++) { Free(textures[i]); textures[i] = null; }
        }

        // -----------------------------------------------------------------------------------
        // Function: Tick
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function picks which cameras draw this frame. The one shown large draws every
        //     frame, so it moves smoothly; the thumbnails take turns, one a frame, so the view
        //     costs two cameras instead of four. At 60 frames a second each thumbnail still
        //     refreshes twenty times a second. With the thumbnails folded away (Thumbnails false)
        //     only the large one draws. It does nothing while the cameras are off.
        public void Tick()
        {
            if (!active) return;
            // The large camera draws anyway, so its turn as a thumbnail goes to the next one
            if (next == main) next = (next + 1) % cameras.Length;
            for (var i = 0; i < cameras.Length; i++)
                cameras[i].enabled = i == main || (Thumbnails && i == next);
            next = (next + 1) % cameras.Length;
        }

        // -----------------------------------------------------------------------------------
        // Function: Dispose
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function frees the images through FreeTextures and removes the cameras'
        //     objects. Outside Play mode, as in EditMode tests, they are destroyed at once.
        public void Dispose()
        {
            FreeTextures();
            for (var i = 0; i < cameras.Length; i++)
            {
                if (cameras[i] == null) continue;
                if (Application.isPlaying) Object.Destroy(cameras[i].gameObject);
                else Object.DestroyImmediate(cameras[i].gameObject);
            }
        }
    }

}