using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Backend;
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
        public const string NoWorld = "3D unavailable · this session has no spatial capability.";
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
        private FleetMap map;
        private bool preferMap;
        private MapTiles tiles;
        private TransitClient transit;
        private DropdownField surfaces;
        private Toggle drafting;
        private RouteEditorContext routeContext;
        private List<long> draftNodes = new List<long>();
        private RouteEditPreview routePreview;
        private Transform draftRoot;
        private Material lineMaterial;
        private bool following;
        private string selectedId, sessionBoundary;
        public bool RouteEditing { get; set; }
        public event Action<string> EntitySelected;
        public event Action<long> RouteNodeSelected;

        public bool Ready { get; private set; }
        public string Diagnostic { get; private set; } = "Join a training session to see its world.";
        public event Action<bool, string> ReadinessChanged;

        public void Attach(VisualElement view, SceneLoader sceneLoader, MapTiles basemap = null, TransitClient busStops = null)
        {
            surface = view;
            loader = sceneLoader;
            tiles = basemap;
            transit = busStops;
            map = new FleetMap { name = "planar-map" };
            map.style.position = UnityEngine.UIElements.Position.Absolute;
            // Opaque, so a 3D session's frame does not show through its 2D map.
            map.style.backgroundColor = new Color(0.10f, 0.10f, 0.12f);
            map.style.left = map.style.top = map.style.right = map.style.bottom = 0;
            surface.Insert(0, map);
            map.UseRealMap(tiles, transit, islandWhenEmpty: true);
            map.VehicleSelected += SelectEntity;
            var actions = new VisualElement(); actions.AddToClassList("world-actions");
            actions.style.position = UnityEngine.UIElements.Position.Absolute; actions.style.left = 8; actions.style.bottom = 8;
            drafting = new Toggle("Draft in 3D"); actions.Add(drafting);
            surfaces = new DropdownField("Road surface / lane / deck"); actions.Add(surfaces);
            surfaces.RegisterValueChangedCallback(_ => DrawDraft());
            drafting.RegisterValueChangedCallback(_ => DrawDraft());
            actions.Add(new Button(Retry) { text = "Retry 3D" });
            surface.Add(actions);
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
            Clear();
            tiles?.Dispose();
            if (lineMaterial != null)
            {
                if (Application.isPlaying) Destroy(lineMaterial);
                else DestroyImmediate(lineMaterial);
            }
            if (texture != null) texture.Release();
        }

        // Called for every accepted snapshot of the selected session.
        public void Show(FullSnapshot snapshot)
        {
            var boundary = snapshot == null ? null : $"{snapshot.SessionId}/{snapshot.SessionEpoch}";
            if (boundary != sessionBoundary) { sessionBoundary = boundary; preferMap = false; following = false; selectedId = null; routeContext = null; DrawDraft(); }
            latest = snapshot;
            if (snapshot?.Spatial == null)
            {
                loading?.Cancel();
                Clear();
                // With a real map there is something to see before joining: the whole island
                map.style.display = snapshot == null && !map.ShowsIslandWhenEmpty ? DisplayStyle.None : DisplayStyle.Flex;
                map.Show(snapshot);
                surfaces.parent.style.display = DisplayStyle.None;
                SetReady(snapshot != null, snapshot == null ? "Join a training session to see its world." : NoWorld);
                return;
            }
            map.style.display = preferMap ? DisplayStyle.Flex : DisplayStyle.None;
            if (preferMap) map.Show(snapshot);
            surfaces.parent.style.display = preferMap ? DisplayStyle.None : DisplayStyle.Flex;
            if (!SamePins(shownPins, snapshot.Spatial))
            {
                _ = LoadAsync(snapshot.Spatial);
                return;
            }
            if (world == null) return; // still loading; the latest snapshot is applied when it finishes
            var mismatch = world.ApplyPoses(snapshot);
            if (mismatch != null) SetReady(false, $"3D unavailable · scene/vehicle binding mismatch: {mismatch}");
            if (following) FocusSelected(false);
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
                var mismatch = world.ApplyPoses(latest);
                if (mismatch != null) throw new SceneException("SCENE_VEHICLE_BINDING", mismatch);
                Frame(package.Manifest);
                viewCamera.enabled = true;
                Resize();
                // Render once before reporting the actual renderer/asset readiness to the backend.
                viewCamera.Render();
                await loader.ReportReadyAsync(package, cancellation.Token);
                if (cancellation.IsCancellationRequested || !SamePins(shownPins, pins)) return;
                surfaces.choices = package.Manifest.RoadSurfaces.Select(s => $"{s.SurfaceId} | {s.LaneId} | {s.DeckId}").ToList();
                surfaces.SetValueWithoutNotify(surfaces.choices.FirstOrDefault() ?? "");
                SetReady(true, $"3D ready · {world.Entities.Count} objects · {package.Manifest.SceneRevision}");
                DrawDraft(); Show(latest);
            }
            catch (OperationCanceledException) { }
            catch (SceneException error)
            {
                if (cancellation.IsCancellationRequested) return;
                SetReady(false, $"3D unavailable · {error.Message}");
            }
            catch (Exception error)
            {
                if (!cancellation.IsCancellationRequested) SetReady(false, $"3D unavailable · {error.Message}");
            }
        }

        private void Clear()
        {
            world?.Destroy();
            world = null;
            shownPins = null;
            if (draftRoot != null) WorldBuilder.Remove(draftRoot.gameObject);
            draftRoot = null;
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
            if (e.target != surface) return;
            if (e.button == 0) { Pick(e.localPosition); e.StopPropagation(); return; }
            if (e.button != 1 && e.button != 2) return;
            following = false;
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
            if (e.target != surface) return;
            distance = Mathf.Clamp(distance * Mathf.Exp(e.delta.y * 0.03f), MinDistance, MaxDistance);
            PlaceCamera();
            e.StopPropagation();
        }

        // The workspace's own planar maps sit on the same real Singapore as this one.
        public void UseRealMap(FleetMap other) => other.UseRealMap(tiles, transit);

        public string SelectedId => selectedId;
        // The 2D Map / 3D View switch. A session without a 3D world always shows its map.
        public bool ShowingMap => latest?.Spatial == null || preferMap;
        public void ShowMap(bool value) { preferMap = value; if (latest != null) Show(latest); }
        public bool Offline { get; private set; }
        public void SetOffline(bool offline) { Offline = offline; map?.SetOffline(offline); }

        public void SelectEntity(string id)
        {
            selectedId = id; map.SelectedId = id;
            EntitySelected?.Invoke(id);
        }

        public void FollowSelected() { following = true; FocusSelected(); }
        public void FocusSelected() => FocusSelected(true);
        private void FocusSelected(bool zoom)
        {
            if (world == null || !world.Entities.TryGetValue(selectedId ?? "", out var entity)) return;
            var renderers = entity.GetComponentsInChildren<Renderer>();
            target = renderers.Length > 0 ? renderers[0].bounds.center : entity.position;
            if (zoom) distance = 24;
            PlaceCamera();
        }
        public void Overview() { following = false; if (world != null) Frame(world.Package.Manifest); }

        public void SetRouteDraft(RouteEditorContext context, List<long> nodes, RouteEditPreview preview)
        {
            routeContext = context; draftNodes = nodes ?? new List<long>(); routePreview = preview; DrawDraft();
            map.SetRoadNetwork(context);
        }

        private string SurfaceId => (surfaces?.value ?? "").Split('|')[0].Trim();
        public bool CanDraftNode(long id) => world != null && routeContext != null && routeContext.Nodes.Any(n => n.NodeId == id &&
            WorldInteraction.NodeOnSurface(world.Package.Manifest, routeContext, n, SurfaceId));

        // A draft can cross several surfaces. The surface selector only disambiguates a click;
        // changing it must not invalidate nodes already chosen on another lane or deck.
        public bool HasDraftSupport(long id) => Ready && world != null && routeContext != null &&
            routeContext.Nodes.Any(n => n.NodeId == id && world.Package.Manifest.RoadSurfaces.Any(s =>
                WorldInteraction.NodeOnSurface(world.Package.Manifest, routeContext, n, s.SurfaceId)));

        private void DrawDraft()
        {
            if (draftRoot != null) WorldBuilder.Remove(draftRoot.gameObject);
            draftRoot = null;
            if (world == null || routeContext == null || !drafting.value) return;
            draftRoot = new GameObject("Route draft overlays").transform; draftRoot.SetParent(transform, false);
            if (lineMaterial == null) lineMaterial = new Material(Shader.Find("Sprites/Default"));
            foreach (var node in routeContext.Nodes.Where(n => CanDraftNode(n.NodeId)))
            {
                var center = WorldMath.ToUnity(new List<double> { node.X, node.Y, node.Z }) + Vector3.up * 0.08f;
                DrawLine(new[] { center - Vector3.right * 0.4f, center + Vector3.right * 0.4f }, draftNodes.Contains(node.NodeId) ? Color.yellow : Color.cyan);
                DrawLine(new[] { center - Vector3.forward * 0.4f, center + Vector3.forward * 0.4f }, Color.cyan);
            }
            if (routePreview != null) DrawLine(routePreview.Polyline.Select(p => WorldMath.ToUnity(new List<double> { p.X, p.Y, p.Z }) + Vector3.up * 0.1f).ToArray(), Color.green);
        }

        private void DrawLine(Vector3[] points, Color color)
        {
            if (points.Length < 2) return;
            var line = new GameObject("Draft line").AddComponent<LineRenderer>(); line.transform.SetParent(draftRoot, false);
            line.sharedMaterial = lineMaterial; line.positionCount = points.Length; line.SetPositions(points);
            line.startWidth = line.endWidth = 0.15f; line.startColor = line.endColor = color;
        }

        private void Pick(Vector2 pixel)
        {
            if (world == null || !Ready) return;
            var rect = surface.contentRect;
            var ray = viewCamera.ViewportPointToRay(new Vector3(pixel.x / rect.width, 1 - pixel.y / rect.height, 0));
            if (!WorldInteraction.TryPick(world, ray, out var id, out var canonical)) return;
            SelectEntity(id);
            if (!RouteEditing || !drafting.value || routeContext == null) return;
            var support = world.Package.Manifest.RoadSurfaces.FirstOrDefault(s => s.SurfaceId == SurfaceId);
            if (support == null || world.Definitions[id].Kind != "ROAD" || !WorldInteraction.OnSurface(support, canonical)) return;
            var node = routeContext.Nodes.Where(n => CanDraftNode(n.NodeId))
                .OrderBy(n => Vector3.Distance(canonical, new Vector3((float)n.X, (float)n.Y, (float)n.Z))).FirstOrDefault();
            if (node != null && Vector3.Distance(canonical, new Vector3((float)node.X, (float)node.Y, (float)node.Z)) < 1)
                RouteNodeSelected?.Invoke(node.NodeId);
        }
    }
}
