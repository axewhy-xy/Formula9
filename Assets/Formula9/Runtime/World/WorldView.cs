using System;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Contracts;
using UnityEngine;
using UnityEngine.UIElements;

namespace Formula9.World
{
    // The operational 3D view. It renders the selected session's verified scene into a UI Toolkit
    // element and moves shuttles to each accepted snapshot. It owns no clock or commands: like the
    // Qt TrainingWorldViewport it only shows what the backend says.
    public sealed class WorldView : MonoBehaviour
    {
        public const string NoWorld = "This 2D exercise has no 3D world; the fleet list shows its shuttles.";
        private const float MinDistance = 5, MaxDistance = 300, MinPitch = 0.12f, MaxPitch = 1.45f;

        private VisualElement surface;
        private SceneLoader loader;
        private Camera viewCamera;
        private RenderTexture texture;
        private WorldScene world;
        private SpatialReference shownPins;
        private FullSnapshot latest;
        private CancellationTokenSource loading;
        private Vector3 target;
        private float yaw, pitch = 0.6f, distance = 90;
        private int dragButton = -1;

        public bool Ready { get; private set; }
        public string Diagnostic { get; private set; } = "Join a training session to see its world.";
        public event Action<bool, string> ReadinessChanged;

        public void Attach(VisualElement view, SceneLoader sceneLoader)
        {
            surface = view;
            loader = sceneLoader;
            var cameraObject = new GameObject("World Camera");
            cameraObject.transform.SetParent(transform, false);
            viewCamera = cameraObject.AddComponent<Camera>();
            viewCamera.clearFlags = CameraClearFlags.SolidColor;
            viewCamera.backgroundColor = new Color(0.10f, 0.10f, 0.12f);
            viewCamera.nearClipPlane = 0.1f;
            viewCamera.farClipPlane = 1200;
            viewCamera.fieldOfView = 50;
            viewCamera.enabled = false;
            WorldLighting.Apply(transform);

            surface.RegisterCallback<GeometryChangedEvent>(_ => Resize());
            surface.RegisterCallback<PointerDownEvent>(OnPointerDown);
            surface.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            surface.RegisterCallback<PointerUpEvent>(OnPointerUp);
            surface.RegisterCallback<WheelEvent>(OnWheel);
        }

        private void OnDestroy()
        {
            loading?.Cancel();
            if (texture != null) texture.Release();
        }

        // Called for every accepted snapshot of the selected session.
        public void Show(FullSnapshot snapshot)
        {
            latest = snapshot;
            if (snapshot?.Spatial == null)
            {
                Clear();
                SetReady(false, snapshot == null ? "Join a training session to see its world." : NoWorld);
                return;
            }
            if (!SamePins(shownPins, snapshot.Spatial))
            {
                _ = LoadAsync(snapshot.Spatial);
                return;
            }
            if (world == null) return; // still loading; the latest snapshot is applied when it finishes
            var mismatch = world.ApplyPoses(snapshot);
            if (mismatch != null) SetReady(false, $"3D unavailable · scene/vehicle binding mismatch: {mismatch}");
            else if (!Ready) SetReady(true, $"3D ready · {world.Entities.Count} objects · {world.Package.Manifest.SceneRevision}");
        }

        public void Retry()
        {
            if (latest?.Spatial == null) return;
            shownPins = null;
            Show(latest);
        }

        private async Task LoadAsync(SpatialReference pins)
        {
            loading?.Cancel();
            var cancellation = loading = new CancellationTokenSource();
            Clear();
            shownPins = pins;
            SetReady(false, "Loading mandatory 3D geometry…");
            try
            {
                var package = await loader.LoadAsync(pins, cancellation.Token);
                var built = await WorldBuilder.BuildAsync(package, transform, cancellation.Token);
                if (cancellation.IsCancellationRequested || !SamePins(shownPins, pins))
                {
                    built.Destroy();
                    return;
                }
                world = built;
                Frame(package.Manifest);
                viewCamera.enabled = true;
                Show(latest);
            }
            catch (OperationCanceledException) { }
            catch (SceneException error)
            {
                if (cancellation.IsCancellationRequested) return;
                SetReady(false, $"3D unavailable · {error.Message}");
            }
        }

        private void Clear()
        {
            world?.Destroy();
            world = null;
            shownPins = null;
            if (viewCamera != null) viewCamera.enabled = false;
        }

        private void SetReady(bool ready, string diagnostic)
        {
            if (Ready == ready && Diagnostic == diagnostic) return;
            Ready = ready;
            Diagnostic = diagnostic;
            ReadinessChanged?.Invoke(ready, diagnostic);
        }

        private static bool SamePins(SpatialReference a, SpatialReference b) =>
            a != null && b != null && a.ManifestSha256 == b.ManifestSha256 && a.SceneRevision == b.SceneRevision;

        // Starts from the manifest's first camera bookmark, like the Qt overview camera.
        private void Frame(TrainingSceneRevision manifest)
        {
            if (manifest.CameraBookmarks.Count == 0) return;
            var bookmark = manifest.CameraBookmarks[0];
            target = WorldMath.ToUnity(bookmark.TargetM);
            var offset = WorldMath.ToUnity(bookmark.PositionM) - target;
            distance = Mathf.Clamp(offset.magnitude, MinDistance, MaxDistance);
            pitch = Mathf.Clamp(Mathf.Asin(offset.y / offset.magnitude), MinPitch, MaxPitch);
            yaw = Mathf.Atan2(offset.x, offset.z);
            PlaceCamera();
        }

        private void PlaceCamera()
        {
            if (viewCamera == null) return;
            var offset = new Vector3(Mathf.Sin(yaw) * Mathf.Cos(pitch), Mathf.Sin(pitch), Mathf.Cos(yaw) * Mathf.Cos(pitch)) * distance;
            viewCamera.transform.position = target + offset;
            viewCamera.transform.LookAt(target, Vector3.up);
        }

        private void Resize()
        {
            var rect = surface.contentRect;
            if (rect.width < 1 || rect.height < 1) return;
            // UI Toolkit lays out in points; the texture needs physical pixels to stay sharp.
            var root = surface.panel.visualTree.layout;
            var scale = root.width > 0 ? Screen.width / root.width : 1;
            var width = Mathf.Clamp(Mathf.RoundToInt(rect.width * scale), 16, 4096);
            var height = Mathf.Clamp(Mathf.RoundToInt(rect.height * scale), 16, 4096);
            if (texture != null && texture.width == width && texture.height == height) return;
            if (texture != null) texture.Release();
            texture = new RenderTexture(width, height, 24) { name = "World View", antiAliasing = 4 };
            viewCamera.targetTexture = texture;
            viewCamera.aspect = (float)width / height;
            surface.style.backgroundImage = new StyleBackground(Background.FromRenderTexture(texture));
        }

        // Right-drag orbits, middle-drag pans and the wheel zooms, as in the Qt client.
        private void OnPointerDown(PointerDownEvent e)
        {
            if (e.button != 1 && e.button != 2) return;
            dragButton = e.button;
            surface.CapturePointer(e.pointerId);
            e.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent e)
        {
            if (dragButton < 0 || !surface.HasPointerCapture(e.pointerId)) return;
            var delta = e.deltaPosition;
            if (dragButton == 1)
            {
                yaw += delta.x * 0.006f;
                pitch = Mathf.Clamp(pitch + delta.y * 0.006f, MinPitch, MaxPitch);
            }
            else
            {
                var scale = 2 * distance * Mathf.Tan(25 * Mathf.Deg2Rad) / Mathf.Max(1, surface.contentRect.height);
                var right = viewCamera.transform.right;
                var forward = Vector3.ProjectOnPlane(viewCamera.transform.forward, Vector3.up).normalized;
                target += (-right * delta.x + forward * delta.y) * scale;
            }
            PlaceCamera();
        }

        private void OnPointerUp(PointerUpEvent e)
        {
            if (e.button != dragButton) return;
            dragButton = -1;
            surface.ReleasePointer(e.pointerId);
        }

        private void OnWheel(WheelEvent e)
        {
            distance = Mathf.Clamp(distance * Mathf.Exp(e.delta.y * 0.03f), MinDistance, MaxDistance);
            PlaceCamera();
            e.StopPropagation();
        }
    }
}
