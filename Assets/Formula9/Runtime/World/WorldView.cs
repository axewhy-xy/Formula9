/*
 * The operational 3D view of a training session.
 *
 * Author: Choo Jian Wei
 * Contributors: Lau Xiang Yang (3D route drafting), Rishika (real Singapore map)
 * Date Created: 30/10/2026
 * Last Modified: 31/10/2026
 *
 * Description:
 *     Part of the Unity client's 3D world. It renders the selected session's verified scene into a
 *     UI Toolkit element and, each frame, glides shuttles towards the latest accepted snapshot. It
 *     owns no simulation clock or commands: like the Qt client's TrainingWorldViewport it only
 *     shows what the backend says, and the glide never predicts past the backend's last pose. A
 *     session without a 3D world shows its 2D map instead, and the trainee can switch between the
 *     two. Right-drag orbits, middle-drag pans, the wheel zooms and a left click selects.
 */

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
    // -----------------------------------------------------------------------------------
    // Class: WorldView
    // -----------------------------------------------------------------------------------
    // Description:
    //     This component shows the 3D world and the 2D map of the selected session.
    //
    //     Attributes: Ready and Diagnostic say whether the world is shown and why not, and
    //     ReadinessChanged reports changes; SelectedId and EntitySelected track the selected
    //     entity; RouteEditing enables picking route nodes, reported through RouteNodeSelected;
    //     ShowingMap and Offline describe the view.
    public sealed class WorldView : MonoBehaviour
    {
        public const string NoWorld = "3D unavailable · this session has no spatial capability.";
        public const string MapHint = "2D map · press 3D View for the buildings in 3D.";
        private const string CityArea = "toa-payoh";
        private const float CityMaxDistance = 3000, CityFarClip = 8000, CityPanMargin = 150;
        private static readonly Color CitySky = new Color(0.72f, 0.84f, 0.92f), WorldBackground = new Color(0.10f, 0.10f, 0.12f);
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
        private CityClient cityClient;
        private CityView city;
        private readonly ShuttleMotion motion = new ShuttleMotion();
        private Task cityLoad;
        private CancellationTokenSource cityCancellation;
        private bool CityShown => latest != null && latest.Spatial == null && cityClient != null && !preferMap;
        private float maxDistance = MaxDistance;
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
        private bool driving;
        private float lookYaw, lookPitch;
        private const float LookDegreesPerPixel = 0.2f, MaxLookPitchDeg = 30f;
        private const float EyeHeightM = 1.5f, EyeForwardM = 5.5f;
        private string selectedId, sessionBoundary;
        public bool RouteEditing { get; set; }
        public event Action<string> EntitySelected;
        public event Action<long> RouteNodeSelected;

        public bool Ready { get; private set; }
        public string Diagnostic { get; private set; } = "Join a training session to see its world.";
        public event Action<bool, string> ReadinessChanged;

        // -----------------------------------------------------------------------------------
        // Function: Attach
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function sets the view up inside a UI element: the opaque 2D map, the drafting
        //     controls, the off-screen camera, the lighting and the pointer handlers.
        //
        // Args:
        //     view (VisualElement): The element the world is drawn into.
        //     sceneLoader (SceneLoader): Downloads scene packages.
        //     basemap (MapTiles): The map tiles under the 2D map, or null.
        //     busStops (TransitClient): Bus stop data for the 2D map, or null.
        //     buildings (CityClient): Building data for the 3D city, or null.
        public void Attach(VisualElement view, SceneLoader sceneLoader, MapTiles basemap = null, TransitClient busStops = null, CityClient buildings = null)
        {
            cityClient = buildings;
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

        // -----------------------------------------------------------------------------------
        // Function: OnDestroy
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function cancels loading and releases the world, tiles, material and render
        //     texture.
        private void OnDestroy()
        {
            loading?.Cancel();
            Clear();
            cityCancellation?.Cancel();
            city?.Dispose();
            tiles?.Dispose();
            if (lineMaterial != null)
            {
                if (Application.isPlaying) Destroy(lineMaterial);
                else DestroyImmediate(lineMaterial);
            }
            if (texture != null) texture.Release();
        }

        // -----------------------------------------------------------------------------------
        // Function: Show
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function is called for every accepted snapshot of the selected session. A new
        //     session or epoch resets the view. A planar session shows its 2D map; a spatial one
        //     loads its scene when the pins change, otherwise moves the shuttles, reporting a
        //     snapshot that does not fit the scene.
        //
        // Args:
        //     snapshot (FullSnapshot): The snapshot, or null when no session is joined.
        public void Show(FullSnapshot snapshot)
        {
            var boundary = snapshot == null ? null : $"{snapshot.SessionId}/{snapshot.SessionEpoch}";
            if (boundary != sessionBoundary) { sessionBoundary = boundary; preferMap = snapshot?.Spatial == null; following = false; driving = false; selectedId = null; routeContext = null; motion.Clear(); DrawDraft(); }
            latest = snapshot;
            if (snapshot != null) motion.BeginSnapshot(snapshot.Tick, Time.unscaledTime);
            if (snapshot?.Spatial == null)
            {
                loading?.Cancel();
                Clear();
                surfaces.parent.style.display = DisplayStyle.None;
                if (CityShown) { ShowCity(snapshot); return; }
                city?.SetVisible(false);
                maxDistance = MaxDistance; viewCamera.farClipPlane = 1200;
                // With a real map there is something to see before joining: the whole island
                map.style.display = snapshot == null && !map.ShowsIslandWhenEmpty ? DisplayStyle.None : DisplayStyle.Flex;
                map.Show(snapshot);
                SetReady(snapshot != null, snapshot == null ? "Join a training session to see its world." : cityClient != null ? MapHint : NoWorld);
                return;
            }
            city?.SetVisible(false);
            maxDistance = MaxDistance; viewCamera.farClipPlane = 1200; viewCamera.backgroundColor = WorldBackground;
            map.style.display = preferMap ? DisplayStyle.Flex : DisplayStyle.None;
            if (preferMap) map.Show(snapshot);
            surfaces.parent.style.display = preferMap ? DisplayStyle.None : DisplayStyle.Flex;
            if (!SamePins(shownPins, snapshot.Spatial))
            {
                _ = LoadAsync(snapshot.Spatial);
                return;
            }
            if (world == null) return; // still loading; the latest snapshot is applied when it finishes
            var mismatch = world.ApplyPoses(snapshot, motion, Time.unscaledTime);
            if (mismatch != null) SetReady(false, $"3D unavailable · scene/vehicle binding mismatch: {mismatch}");
        }

        // -----------------------------------------------------------------------------------
        // Function: ShowCity
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function shows a 2D session's streets in 3D: its buildings are real, its routes
        //     and shuttles the backend's.
        //
        //     Args:
        //         snapshot (FullSnapshot): The latest snapshot.
        private void ShowCity(FullSnapshot snapshot)
        {
            map.style.display = DisplayStyle.None;
            viewCamera.farClipPlane = CityFarClip;
            viewCamera.backgroundColor = CitySky;
            maxDistance = CityMaxDistance;
            viewCamera.enabled = true;
            Resize();
            if (city == null)
            {
                city = new CityView(transform, tiles);
                cityCancellation = new CancellationTokenSource();
                SetReady(false, "Loading 3D buildings…");
                cityLoad = LoadCityAsync(city, cityCancellation.Token);
                return;
            }
            city.SetVisible(true);
            if (cityLoad != null && !cityLoad.IsCompleted) return;
            if (city.BuildingCount > 0)
            {
                city.Show(snapshot, motion, Time.unscaledTime);
                SetReady(true, $"3D city · {city.BuildingCount} buildings · right-drag orbit, left-drag pan, wheel zoom");
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: FrameCity
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function points the camera at the whole city.
        //
        //     Args:
        //         view (CityView): The loaded city.
        private void FrameCity(CityView view)
        {
            var center = view.Bounds.center;
            target = new Vector3(center.x, 0, center.z);
            // From the south, looking north, so the map's text reads the right way up
            distance = 1200; pitch = 0.75f; yaw = Mathf.PI - 0.35f;
            PlaceCamera();
        }

        // -----------------------------------------------------------------------------------
        // Function: LoadCityAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function loads the buildings, then draws the latest snapshot over them. A failure
        //     goes back to the 2D map.
        //
        //     Args:
        //         view (CityView): The city to load.
        //         cancellation (CancellationToken): Cancels the load.
        //
        //     Returns:
        //         Task: Finishes when the load does.
        private async Task LoadCityAsync(CityView view, CancellationToken cancellation)
        {
            try
            {
                await view.LoadAsync(cityClient, CityArea, cancellation);
                if (cancellation.IsCancellationRequested) return;
                FrameCity(view);
                // The load has finished by the time this runs, so the next Show draws it
                cityLoad = null;
                if (latest != null && CityShown) Show(latest);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                // Forget it, so going back to 3D View tries again
                city?.Dispose(); city = null; cityLoad = null;
                preferMap = true;
                viewCamera.enabled = false;
                if (latest != null) { map.style.display = DisplayStyle.Flex; map.Show(latest); }
                SetReady(latest != null, $"3D buildings unavailable · {error.Message}");
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: Update
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function is the 3D view's animation loop, independent of the snapshot rate: it
        //     moves the shuttles of the city or scene each frame and keeps a followed one in view.
        private void Update()
        {
            if (CityShown && city != null) city.Tick(motion, Time.unscaledTime);
            else if (world != null) world.Advance(motion, Time.unscaledTime);
            else return;
            if (driving) PlaceDriverCamera();
            else if (following) FocusSelected(false);
        }

        // -----------------------------------------------------------------------------------
        // Function: Retry
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function loads the current session's scene again, after a failure.
        public void Retry()
        {
            if (latest?.Spatial == null) return;
            shownPins = null;
            Show(latest);
        }

        // -----------------------------------------------------------------------------------
        // Function: LoadAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function downloads, builds and frames the scene, renders it once, and only then
        //     reports readiness to the backend. A load overtaken by newer pins is discarded.
        //
        // Args:
        //     pins (SpatialReference): The session's spatial pins.
        private async Task LoadAsync(SpatialReference pins)
        {
            loading?.Cancel();
            var cancellation = loading = new CancellationTokenSource();
            Clear();
            motion.Clear();
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

        // -----------------------------------------------------------------------------------
        // Function: Clear
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function removes the world and any route draft and stops rendering.
        private void Clear()
        {
            world?.Destroy();
            world = null;
            shownPins = null;
            if (draftRoot != null) WorldBuilder.Remove(draftRoot.gameObject);
            draftRoot = null;
            if (viewCamera != null) viewCamera.enabled = false;
        }

        // -----------------------------------------------------------------------------------
        // Function: SetReady
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function records whether the world is shown and reports a change.
        //
        // Args:
        //     ready (bool): True when the world is shown.
        //     diagnostic (string): What the trainee sees about it.
        private void SetReady(bool ready, string diagnostic)
        {
            if (Ready == ready && Diagnostic == diagnostic) return;
            Ready = ready;
            Diagnostic = diagnostic;
            ReadinessChanged?.Invoke(ready, diagnostic);
        }

        // -----------------------------------------------------------------------------------
        // Function: SamePins
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function tells whether two pins name the same scene revision and manifest.
        //
        // Args:
        //     a (SpatialReference): The first pins.
        //     b (SpatialReference): The second pins.
        //
        // Returns:
        //     bool: True when both are present and the same.
        private static bool SamePins(SpatialReference a, SpatialReference b) =>
            a != null && b != null && a.ManifestSha256 == b.ManifestSha256 && a.SceneRevision == b.SceneRevision;

        // -----------------------------------------------------------------------------------
        // Function: Frame
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function points the camera from the manifest's first camera bookmark, like the
        //     Qt overview camera.
        //
        // Args:
        //     manifest (TrainingSceneRevision): The scene manifest.
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

        // -----------------------------------------------------------------------------------
        // Function: PlaceCamera
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function places the orbit camera from its target, yaw, pitch and distance.
        private void PlaceCamera()
        {
            if (viewCamera == null) return;
            // Keep the view over the city; the map runs out beyond it
            if (CityShown && city != null && city.BuildingCount > 0)
            {
                var bounds = city.Bounds;
                target.x = Mathf.Clamp(target.x, bounds.min.x - CityPanMargin, bounds.max.x + CityPanMargin);
                target.z = Mathf.Clamp(target.z, bounds.min.z - CityPanMargin, bounds.max.z + CityPanMargin);
            }
            var offset = new Vector3(Mathf.Sin(yaw) * Mathf.Cos(pitch), Mathf.Sin(pitch), Mathf.Cos(yaw) * Mathf.Cos(pitch)) * distance;
            viewCamera.transform.position = target + offset;
            viewCamera.transform.LookAt(target, Vector3.up);
        }

        // -----------------------------------------------------------------------------------
        // Function: Resize
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function matches the render texture to the element's size in physical pixels, so
        //     the image stays sharp; UI Toolkit lays out in points.
        private void Resize()
        {
            var rect = surface.contentRect;
            // Not on screen yet (no panel), or too small to draw into
            if (surface.panel == null || rect.width < 1 || rect.height < 1) return;
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

        // -----------------------------------------------------------------------------------
        // Function: OnPointerDown
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function selects what was clicked, or starts an orbit (right button) or pan
        //     (middle button).
        //
        // Args:
        //     e (PointerDownEvent): The pointer event.
        private void OnPointerDown(PointerDownEvent e)
        {
            if (e.target != surface) return;
            if (e.button == 0 && !CityShown) { Pick(e.localPosition); e.StopPropagation(); return; }
            if (e.button != 0 && e.button != 1 && e.button != 2) return;
            following = false;
            dragButton = e.button;
            surface.CapturePointer(e.pointerId);
            e.StopPropagation();
        }

        // -----------------------------------------------------------------------------------
        // Function: OnPointerMove
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function orbits or pans while a drag is in progress.
        //
        // Args:
        //     e (PointerMoveEvent): The pointer event.
        private void OnPointerMove(PointerMoveEvent e)
        {
            if (dragButton < 0 || !surface.HasPointerCapture(e.pointerId)) return;
            var delta = e.deltaPosition;
            if (driving)
            {
                lookYaw += delta.x * LookDegreesPerPixel;
                lookPitch = Mathf.Clamp(lookPitch + delta.y * LookDegreesPerPixel, -MaxLookPitchDeg, MaxLookPitchDeg);
                return;
            }
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

        // -----------------------------------------------------------------------------------
        // Function: OnPointerUp
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function ends a drag.
        //
        // Args:
        //     e (PointerUpEvent): The pointer event.
        private void OnPointerUp(PointerUpEvent e)
        {
            if (e.button != dragButton) return;
            dragButton = -1;
            surface.ReleasePointer(e.pointerId);
        }

        // -----------------------------------------------------------------------------------
        // Function: OnWheel
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function zooms the camera.
        //
        // Args:
        //     e (WheelEvent): The wheel event.
        private void OnWheel(WheelEvent e)
        {
            if (e.target != surface) return;
            if (driving) { e.StopPropagation(); return; }
            distance = Mathf.Clamp(distance * Mathf.Exp(e.delta.y * 0.03f), MinDistance, maxDistance);
            PlaceCamera();
            e.StopPropagation();
        }

        // -----------------------------------------------------------------------------------
        // Function: UseRealMap
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function puts another planar map on the same real Singapore map as this one.
        //
        // Args:
        //     other (FleetMap): The map, for example the workspace's.
        public void UseRealMap(FleetMap other) => other.UseRealMap(tiles, transit);
        public event Action<string> IncidentSelected
        {
            add { if (map != null) map.IncidentSelected += value; }
            remove { if (map != null) map.IncidentSelected -= value; }
        }
        public void SetIncidentEvents(IEnumerable<Formula9.Contracts.Event> events) => map?.SetIncidentEvents(events);

        public string SelectedId => selectedId;
        // The 2D Map / 3D View switch. A session without a 3D world always shows its map.
        public bool ShowingMap => latest == null || preferMap || (latest.Spatial == null && cityClient == null);
        // -----------------------------------------------------------------------------------
        // Function: ShowMap
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function is the 2D Map / 3D View switch. A session without a 3D world always
        //     shows its map.
        //
        // Args:
        //     value (bool): True for the 2D map.
        public void ShowMap(bool value) { preferMap = value; motion.Clear(); if (latest != null) Show(latest); }
        public bool Offline { get; private set; }
        // -----------------------------------------------------------------------------------
        // Function: SetOffline
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function marks the view as showing positions frozen at the last report.
        //
        // Args:
        //     offline (bool): True while the backend cannot be reached.
        public void SetOffline(bool offline) { Offline = offline; map?.SetOffline(offline); }

        // -----------------------------------------------------------------------------------
        // Function: SelectEntity
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function selects an entity in both views and reports it.
        //
        // Args:
        //     id (string): The entity ID, or null.
        public void SelectEntity(string id)
        {
            selectedId = id; map.SelectedId = id;
            EntitySelected?.Invoke(id);
        }

        // -----------------------------------------------------------------------------------
        // Function: FollowSelected
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function focuses the camera on the selected entity and keeps it there as it
        //     moves.
        public void FollowSelected() { following = true; FocusSelected(); }

        // -----------------------------------------------------------------------------------
        // Function: DriveSelected
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function puts the camera in the selected shuttle's driver seat, looking where
        //     it is heading, until Overview is chosen. It does nothing without a selection.
        public void DriveSelected()
        {
            if (selectedId == null) return;
            driving = true;
            lookYaw = lookPitch = 0;
            following = false;
        }
        // -----------------------------------------------------------------------------------
        // Function: PlaceDriverCamera
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function puts the camera at the selected shuttle's eye point, facing the way
        //     the shuttle faces. If the shuttle is gone, it leaves driver view.
        private void PlaceDriverCamera()
        {
            if (city == null || city.TryGetShuttlePose(selectedId, out var position, out var rotation) == false)
            { 
              driving = false; 
              return; 
            }  
            var eye = position + rotation * new Vector3(0, EyeHeightM, EyeForwardM);
            viewCamera.transform.position = eye;
            viewCamera.transform.rotation = rotation * Quaternion.Euler(lookPitch, lookYaw, 0);
        }
        // -----------------------------------------------------------------------------------
        // Function: FocusSelected
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function focuses and zooms the camera on the selected entity once.
        public void FocusSelected() => FocusSelected(true);
        // -----------------------------------------------------------------------------------
        // Function: FocusSelected
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function points the camera at the selected entity.
        //
        // Args:
        //     zoom (bool): True to also move in close.
        private void FocusSelected(bool zoom)
        {
            if (CityShown && city != null && city.TryGetShuttle(selectedId, out var shuttle))
            {
                target = new Vector3(shuttle.x, 0, shuttle.z);
                if (zoom) distance = 120;
                PlaceCamera();
                return;
            }
            if (world == null || !world.Entities.TryGetValue(selectedId ?? "", out var entity)) return;
            var renderers = entity.GetComponentsInChildren<Renderer>();
            target = renderers.Length > 0 ? renderers[0].bounds.center : entity.position;
            if (zoom) distance = 24;
            PlaceCamera();
        }
        // -----------------------------------------------------------------------------------
        // Function: Overview
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function stops following and returns to the overview camera of the scene or city.
        public void Overview()
        {
            following = false;
            driving = false;
            if (world != null) Frame(world.Package.Manifest);
            else if (CityShown && city != null && city.BuildingCount > 0) FrameCity(city);
        }

        // -----------------------------------------------------------------------------------
        // Function: SetRouteDraft
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function shows a route proposal: the road network on the 2D map, and in 3D the
        //     nodes that can be picked, the chosen ones and the previewed path.
        //
        // Args:
        //     context (RouteEditorContext): The route editor graph, or null.
        //     nodes (List<long>): The chosen nodes.
        //     preview (RouteEditPreview): The validated preview, or null.
        public void SetRouteDraft(RouteEditorContext context, List<long> nodes, RouteEditPreview preview)
        {
            routeContext = context; draftNodes = nodes ?? new List<long>(); routePreview = preview; DrawDraft();
            map.SetRoadNetwork(context);
        }

        private string SurfaceId => (surfaces?.value ?? "").Split('|')[0].Trim();
        // -----------------------------------------------------------------------------------
        // Function: CanDraftNode
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function tells whether a node lies on the road surface currently selected for
        //     drafting.
        //
        // Args:
        //     id (long): The node ID.
        //
        // Returns:
        //     bool: True when it can be picked now.
        public bool CanDraftNode(long id) => world != null && routeContext != null && routeContext.Nodes.Any(n => n.NodeId == id &&
            WorldInteraction.NodeOnSurface(world.Package.Manifest, routeContext, n, SurfaceId));

        // -----------------------------------------------------------------------------------
        // Function: HasDraftSupport
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function tells whether a node lies on any road surface of the shown world. A
        //     draft can cross several surfaces: the surface selector only disambiguates a click,
        //     and changing it must not invalidate nodes already chosen on another lane or deck.
        //
        // Args:
        //     id (long): The node ID.
        //
        // Returns:
        //     bool: True when the node is supported.
        public bool HasDraftSupport(long id) => Ready && world != null && routeContext != null &&
            routeContext.Nodes.Any(n => n.NodeId == id && world.Package.Manifest.RoadSurfaces.Any(s =>
                WorldInteraction.NodeOnSurface(world.Package.Manifest, routeContext, n, s.SurfaceId)));

        // -----------------------------------------------------------------------------------
        // Function: DrawDraft
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function redraws the 3D route draft overlays: a cross on each pickable node
        //     (chosen ones in yellow) and the previewed path in green.
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

        // -----------------------------------------------------------------------------------
        // Function: DrawLine
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function draws one overlay line.
        //
        // Args:
        //     points (Vector3[]): The points in Unity space.
        //     color (Color): The colour.
        private void DrawLine(Vector3[] points, Color color)
        {
            if (points.Length < 2) return;
            var line = new GameObject("Draft line").AddComponent<LineRenderer>(); line.transform.SetParent(draftRoot, false);
            line.sharedMaterial = lineMaterial; line.positionCount = points.Length; line.SetPositions(points);
            line.startWidth = line.endWidth = 0.15f; line.startColor = line.endColor = color;
        }

        // -----------------------------------------------------------------------------------
        // Function: Pick
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function selects the entity under a click and, while drafting a route on a road,
        //     picks the nearest node within 1 m on the selected surface.
        //
        // Args:
        //     pixel (Vector2): The click position in the element.
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
