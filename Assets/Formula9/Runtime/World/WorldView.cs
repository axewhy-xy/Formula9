/*
 * The operational 3D view of a training session.
 *
 * Author: Choo Jian Wei
 * Contributors: Lau Xiang Yang (3D route drafting), Rishika (real Singapore map)
 * Date Created: 30/10/2026
 * Last Modified: 01/11/2026
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
using System.Runtime.CompilerServices;
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
    //     Attributes: Ready and Diagnostic say whether the world is shown and why not, Notice
    //     marks a Diagnostic worth surfacing (loading or a failure), and
    //     ReadinessChanged reports changes; SelectedId and EntitySelected track the selected
    //     entity; RouteEditing enables picking route nodes, reported through RouteNodeSelected;
    //     ShowingMap and Offline describe the view.
    public sealed class WorldView : MonoBehaviour
    {
        public const string NoWorld = "3D unavailable · this session has no spatial capability.";
        public const string MapHint = "2D map · press 3D View for the buildings in 3D.";
        internal const string CityArea = "toa-payoh";
        private const float CityMaxDistance = 3000, CityFarClip = 8000, CityPanMargin = 150;
        private static readonly Color CitySky = new Color(0.72f, 0.84f, 0.92f), WorldBackground = new Color(0.10f, 0.10f, 0.12f);
        private const float MinDistance = 5, MaxDistance = 300, MinPitch = 0.12f, MaxPitch = 1.45f;

        private VisualElement surface;
        private SceneLoader loader;
        private Camera viewCamera;
        private readonly List<Camera> trafficCameras = new List<Camera>();
        private ShuttleCameraRig shuttleCameras;
        private VisualElement cameraPanel, mainCameraView;
        private Label mainCameraLabel;
        private readonly List<VisualElement> cameraTiles = new List<VisualElement>();
        private int mainCamera;
        private RenderTexture texture;
        private WorldScene world;
        private SpatialReference shownPins;
        private FullSnapshot latest;
        // The sun and sky, run on the session's clock
        private DayCycle dayCycle;
        private Label clock;
        private CancellationTokenSource loading;
        private Vector3 target;
        private float yaw, pitch = 0.6f, distance = 90;
        private int dragButton = -1;
        private FleetMap map;
        // The 3D view's Draft in 3D / road surface / Retry 3D row, in its bottom-left corner
        private VisualElement worldActions;
        // The column of camera thumbnails over the camera view's left edge
        private VisualElement cameraRail;
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
        private RouteDraftView draft;
        private IReadOnlyCollection<string> servedStops = Array.Empty<string>();
        private Label hud;
        private string selectedId, sessionBoundary;
        private readonly Dictionary<string, IncidentRecord> incidentRecords = new Dictionary<string, IncidentRecord>();
        // The road network, kept while the route editor reloads its own copy: closures make it do so
        private IReadOnlyList<DraftRoad> roadNetwork;
        private string executionLine;
        private const float IncidentFocusDistance = 200, IncidentFocusPitch = 0.85f;
        public bool RouteEditing { get; set; }
        public event Action<string> EntitySelected;
        public event Action<long> RouteNodeSelected;
        public event Action<DraftRoad, Vector2> RoadClicked;
        public event Action<int> WaypointClicked;
        public event Action ViewChanged;
        // The shuttle cameras opened (true) or closed (false), whatever closed them
        public event Action<bool> CamerasToggled;

        public bool Ready { get; private set; }
        public string Diagnostic { get; private set; } = "Join a training session to see its world.";
        // True while Diagnostic reports loading or a failure, not just the routine state of the view
        public bool Notice { get; private set; }
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
            map.RoadClicked += (road, at) => RoadClicked?.Invoke(road, at);
            map.WaypointClicked += index => WaypointClicked?.Invoke(index);
            hud = new Label { name = "world-hud", pickingMode = PickingMode.Ignore };
            hud.AddToClassList("world-hud");
            hud.style.display = DisplayStyle.None;
            surface.Add(hud);
            worldActions = new VisualElement(); worldActions.AddToClassList("world-actions");
            worldActions.style.position = UnityEngine.UIElements.Position.Absolute; worldActions.style.left = 8; worldActions.style.bottom = 8;
            drafting = new Toggle("Draft in 3D"); worldActions.Add(drafting);
            surfaces = new DropdownField("Road surface / lane / deck"); worldActions.Add(surfaces);
            surfaces.RegisterValueChangedCallback(_ => DrawDraft());
            drafting.RegisterValueChangedCallback(_ => DrawDraft());
            worldActions.Add(new Button(Retry) { text = "Retry 3D" });
            surface.Add(worldActions);
            var cameraObject = new GameObject("World Camera");
            cameraObject.transform.SetParent(transform, false);
            viewCamera = cameraObject.AddComponent<Camera>();
            shuttleCameras = new ShuttleCameraRig(transform);
            trafficCameras.AddRange(shuttleCameras.Cameras);
            // The camera view covers the whole 3D view: one camera large, the rest as thumbnails
            cameraPanel = new VisualElement();
            cameraPanel.AddToClassList("camera-panel");
            mainCameraView = new VisualElement();
            mainCameraView.AddToClassList("camera-main");
            mainCameraLabel = new Label();
            mainCameraLabel.AddToClassList("camera-label");
            mainCameraView.Add(mainCameraLabel);
            cameraPanel.Add(mainCameraView);
            // The large camera image follows the view's size, so it stays sharp at any layout
            mainCameraView.RegisterCallback<GeometryChangedEvent>(_ => SizeCameraView());
            // The thumbnails sit in a rail over the large view's left edge, which the operator can fold
            // away. Left, because incident notifications stack in the top-right corner.
            var rail = new VisualElement { pickingMode = PickingMode.Ignore };
            rail.AddToClassList("camera-rail");
            cameraRail = rail;
            var railToggle = new Button { text = "‹ Cameras", tooltip = "Show or hide the camera thumbnails" };
            railToggle.AddToClassList("camera-rail-toggle");
            railToggle.clicked += () =>
            {
                rail.ToggleInClassList("camera-rail-collapsed");
                railToggle.text = rail.ClassListContains("camera-rail-collapsed") ? "Cameras ›" : "‹ Cameras";
            };
            rail.Add(railToggle);
            // It scrolls, so on a short view (or above the Fleet List) every thumbnail stays reachable
            var strip = new ScrollView(ScrollViewMode.Vertical);
            strip.AddToClassList("camera-strip");
            for (var i = 0; i < shuttleCameras.Cameras.Count; i++)
            {
                var tile = new VisualElement();
                tile.AddToClassList("camera-tile");
                var label = new Label {text = shuttleCameras.NameOf(i)};
                label.AddToClassList("camera-label");
                tile.Add(label);
                // A click shows this camera large
                var index = i;
                tile.RegisterCallback<ClickEvent>(_ => SelectCamera(index));
                strip.Add(tile);
                cameraTiles.Add(tile);
            }
            rail.Add(strip);
            cameraPanel.Add(rail);
            cameraPanel.style.display = DisplayStyle.None;    // hidden until the button is pressed
            surface.Add(cameraPanel);
            // The world's time of day, bottom left over the 3D view and the cameras; added last, so it shows over both
            clock = new Label { name = "world-clock", pickingMode = PickingMode.Ignore };
            clock.AddToClassList("world-clock");
            clock.style.display = DisplayStyle.None;
            surface.Add(clock);
            cameraRail.style.bottom = RailBottomGap + ClockRoom;
            SelectCamera(0);
            trafficCameras.Add(viewCamera); 
            viewCamera.clearFlags = CameraClearFlags.SolidColor;
            viewCamera.backgroundColor = new Color(0.10f, 0.10f, 0.12f);
            viewCamera.nearClipPlane = 0.1f;
            viewCamera.farClipPlane = 1200;
            viewCamera.fieldOfView = 50;
            viewCamera.enabled = false;
            dayCycle = WorldLighting.Apply(transform).GetComponent<DayCycle>();

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
            shuttleCameras?.Dispose();
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
            if (boundary != sessionBoundary) { sessionBoundary = boundary; preferMap = snapshot?.Spatial == null; following = false; LeaveCameras(); selectedId = null; routeContext = null; roadNetwork = null; incidentRecords.Clear(); motion.Clear(); city?.ResetShuttles(); city?.ForgetTraffic(); DrawDraft(); }
            latest = snapshot;
            if (dayCycle != null) { if (snapshot != null) dayCycle.SetSessionTick(snapshot.Tick); else dayCycle.ClearSession(); }
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
            viewCamera.clearFlags = CameraClearFlags.SolidColor;
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
            if (mismatch != null) SetReady(false, $"3D unavailable · scene/vehicle binding mismatch: {mismatch}", notice: true);
        }

        // -----------------------------------------------------------------------------------
        // Function: WantsTraffic
        // -----------------------------------------------------------------------------------
        // Description:
        //     True while the 3D city is drawn, so the backend's traffic frames are worth fetching:
        //     they come far more often than snapshots, and only the city draws them.
        public bool WantsTraffic => CityShown && city != null && city.BuildingCount > 0 && (cityLoad == null || cityLoad.IsCompleted);

        // -----------------------------------------------------------------------------------
        // Function: ShowTraffic
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function hands the backend's latest traffic frames to the 3D city, which then
        //     draws the traffic and the shuttles' bodies where the backend drives them.
        //
        // Args:
        //     frames (IReadOnlyList<TrafficFrame>): The frames, oldest first.
        public void ShowTraffic(IReadOnlyList<TrafficFrame> frames)
        {
            if (latest == null || frames == null || frames.Count == 0 || !CityShown || city == null) return;
            city.ShowTraffic(frames);
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
            // The sky from world_look.json, or the plain sky colour without it
            viewCamera.clearFlags = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
            // Cameras opened while the city was loading take its sky now
            if (camerasShown) shuttleCameras.Match(viewCamera);
            maxDistance = CityMaxDistance;
            // The shuttle's cameras take the place of the 3D view while they are shown
            viewCamera.enabled = !camerasShown;
            Resize();
            if (city == null)
            {
                city = new CityView(transform, tiles, transit);
                cityCancellation = new CancellationTokenSource();
                SetReady(false, "Loading 3D buildings…", notice: true);
                cityLoad = LoadCityAsync(city, cityCancellation.Token);
                return;
            }
            city.SetVisible(true);
            if (cityLoad != null && !cityLoad.IsCompleted) return;
            if (city.BuildingCount > 0)
            {
                city.Show(snapshot, motion, Time.unscaledTime);
                city.ShowDraft(draft, servedStops);
                city.ShowIncidents(incidentRecords.Values, snapshot, roadNetwork, Time.unscaledTime);
                RefreshHud();
                SetReady(true, CityHint());
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
                // Someone asked to watch a shuttle while this loaded: keep them on it
                if (!following) FrameCity(view);
                // The load has finished by the time this runs, so the next Show draws it
                cityLoad = null;
                if (latest != null && CityShown) Show(latest);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                // Forget it, so going back to 3D View tries again
                city?.Dispose(); city = null; cityLoad = null;
                LeaveCameras();
                preferMap = true;
                viewCamera.enabled = false;
                if (latest != null) { map.style.display = DisplayStyle.Flex; map.Show(latest); }
                SetReady(latest != null, $"3D buildings unavailable · {error.Message}", notice: true);
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
            ShowClock();
            if (CityShown && city != null)
            {
                // The traffic first: the backend's traffic frames carry the shuttles' bodies too, so the
                // shuttles, the cameras on them and the vehicles around them are all drawn at one moment
                city.AdvanceTraffic(Time.unscaledDeltaTime, latest?.Session?.Status == "RUNNING");
                city.Tick(motion, Time.unscaledTime);
                if (city.TryGetShuttlePose(watchedId, out var position, out var rotation))
                    shuttleCameras.Place(position, rotation);
                // Before the traffic is drawn, for the cameras whose turn it is
                shuttleCameras.Tick();
                city.DrawTraffic(trafficCameras);
            }
            else if (world != null) world.Advance(motion, Time.unscaledTime);
            else return;
            // Following waits behind the camera view and carries on when it closes
            if (following && !camerasShown) FocusSelected(false);
        }

        // The time of day, bottom left: under the camera thumbnails, or above the Draft in 3D row in the
        // 3D view; hidden with the 2D map
        private void ShowClock()
        {
            if (clock == null) return;
            var shown = dayCycle != null && (camerasShown || (viewCamera != null && viewCamera.enabled));
            clock.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
            if (!shown) return;
            clock.text = DayCycle.Clock(dayCycle.Hour);
            var actions = !camerasShown && worldActions != null && worldActions.resolvedStyle.display == DisplayStyle.Flex ? worldActions.layout.height + 6 : 0;
            clock.style.bottom = RailBottomGap + bottomInset + (float.IsNaN(actions) ? 0 : actions);
        }

        // -----------------------------------------------------------------------------------
        // Function: LateUpdate
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function closes the cameras once the frame's work is done, when there is no
        //     shuttle left to watch, and tells the workspace so its buttons can update. Unity
        //     calls it after every Update, so the workspace never redraws partway through one.
        private void LateUpdate()
        {
            // While the city is still loading its shuttles are not placed yet, so wait for them
            // before deciding there is nothing to watch
            if (camerasShown && !CanShowCameras && !(CityShown && cityLoad != null && !cityLoad.IsCompleted))
            {
                CloseCameras();
                // Nobody pressed a button, so tell the workspace to redraw its Cameras buttons
                ViewChanged?.Invoke();
            }
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
            SetReady(false, "Loading mandatory 3D geometry…", notice: true);
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
                SetReady(false, $"3D unavailable · {error.Message}", notice: true);
            }
            catch (Exception error)
            {
                if (!cancellation.IsCancellationRequested) SetReady(false, $"3D unavailable · {error.Message}", notice: true);
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
        //     notice (bool): True when the diagnostic is loading or a failure the trainee should see.
        private void SetReady(bool ready, string diagnostic, bool notice = false)
        {
            if (Ready == ready && Diagnostic == diagnostic && Notice == notice) return;
            Ready = ready;
            Diagnostic = diagnostic;
            Notice = notice;
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
            // The camera view covers the 3D view; zooming it would move a camera nobody sees
            if (camerasShown) { e.StopPropagation(); return; }
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
        public void SetIncidentEvents(IEnumerable<Formula9.Contracts.Event> events)
        {
            map?.SetIncidentEvents(events);
            IncidentRecord.Merge(events, incidentRecords);
        }

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
        public void ShowMap(bool value)
        {
            // The map has no shuttle cameras, so coming back to 3D starts over the city
            if (value) LeaveCameras();
            if (preferMap == value) { ViewChanged?.Invoke(); return; }
            preferMap = value;
            motion.Clear();
            city?.ResetShuttles();
            if (latest != null) Show(latest);
            if (!value) FrameDraft();
            RefreshHud();
            ViewChanged?.Invoke();
        }

        // -----------------------------------------------------------------------------------
        // Function: WatchShuttle
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function goes to the 3D view and follows a shuttle from above and behind, to watch
        //     it carry out what the operator approved.
        //
        // Args:
        //     id (string): The shuttle.
        public void WatchShuttle(string id)
        {
            ShowMap(false);
            SelectEntity(id);
            LeaveCameras();
            following = true;
            // Steep enough that the blocks along the road don't hide the shuttle
            distance = 170; pitch = 1.2f; yaw = Mathf.PI - 0.35f;
            FocusSelected(false);
        }

        // -----------------------------------------------------------------------------------
        // Function: SetExecutionStatus
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function shows a line over the 3D view saying what the watched shuttle is doing.
        //
        // Args:
        //     text (string): The line, or null to hide it.
        public void SetExecutionStatus(string text)
        {
            executionLine = text;
            RefreshHud();
        }

        // -----------------------------------------------------------------------------------
        // Function: RefreshHud
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function shows the watched shuttle's line and a line for each live incident
        //     over the 3D view, and hides them everywhere else.
        private void RefreshHud()
        {
            if (hud == null) return;
            var text = string.Join("\n", new[] { executionLine, CityShown ? city?.IncidentStatus : null }.Where(line => !string.IsNullOrEmpty(line)));
            hud.text = text;
            hud.style.display = text.Length > 0 && CityShown ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // -----------------------------------------------------------------------------------
        // Function: FocusIncident
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function points the 3D city's camera at a live incident and the roads backing
        //     up behind it.
        //
        // Args:
        //     id (string): The incident.
        //
        // Returns:
        //     bool: False when the 3D city is not showing that incident.
        public bool FocusIncident(string id)
        {
            if (!CityShown || city == null || !city.TryGetIncidentSite(id, out var site)) return false;
            following = false; LeaveCameras();
            target = new Vector3(site.x, 0, site.y);
            distance = IncidentFocusDistance; pitch = IncidentFocusPitch; yaw = Mathf.PI - 0.35f;
            PlaceCamera();
            return true;
        }

        // -----------------------------------------------------------------------------------
        // Function: FocusRoads
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function brings roads into view on whichever of the map and the 3D city is shown.
        //
        // Args:
        //     edgeIds (IEnumerable<string>): The roads.
        public void FocusRoads(IEnumerable<string> edgeIds)
        {
            var ids = edgeIds.ToList();
            map.Focus(ids);
            if (!CityShown || city == null || draft == null) return;
            var points = ids.SelectMany(id => draft.TryRoad(id, out var road) ? road.Points : Array.Empty<Vector2>()).ToList();
            if (points.Count == 0) return;
            following = false; LeaveCameras();
            target = new Vector3(points.Average(p => p.x), 0, points.Average(p => p.y));
            distance = Mathf.Clamp(Mathf.Max(points.Max(p => p.x) - points.Min(p => p.x), points.Max(p => p.y) - points.Min(p => p.y)) * 1.6f, 120, CityMaxDistance);
            PlaceCamera();
        }

        // -----------------------------------------------------------------------------------
        // Function: FrameDraft
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function points the 3D camera at the proposal and the closure it avoids.
        private void FrameDraft()
        {
            if (draft == null || !CityShown || following || camerasShown) return;
            var points = draft.FocusPoints();
            foreach (var path in draft.ProposalPaths()) points.AddRange(path.Points);
            if (points.Count == 0) return;
            target = new Vector3((points.Min(p => p.x) + points.Max(p => p.x)) / 2, 0, (points.Min(p => p.y) + points.Max(p => p.y)) / 2);
            distance = Mathf.Clamp(Mathf.Max(points.Max(p => p.x) - points.Min(p => p.x), points.Max(p => p.y) - points.Min(p => p.y)) * 1.15f, 200, CityMaxDistance);
            pitch = 0.95f; yaw = Mathf.PI - 0.35f;
            PlaceCamera();
        }
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
        //     This function selects an entity in both views and reports it. Open cameras stay on
        //     the shuttle they are watching; PickShuttle is for the operator choosing another.
        //
        // Args:
        //     id (string): The entity ID, or null.
        public void SelectEntity(string id)
        {
            selectedId = id; map.SelectedId = id;
            EntitySelected?.Invoke(id);
        }

        // -----------------------------------------------------------------------------------
        // Function: PickShuttle
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function selects the shuttle the operator picked. Open cameras move to it when
        //     it is another shuttle in the 3D city; otherwise they stay as they are.
        //
        // Args:
        //     id (string): The shuttle, or null.
        public void PickShuttle(string id)
        {
            SelectEntity(id);
            if (camerasShown && id != null && id != watchedId && city != null && city.TryGetShuttlePose(id, out _, out _))
                ShowCameras(true);
        }

        // -----------------------------------------------------------------------------------
        // Function: FollowSelected
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function focuses the camera on the selected entity and keeps it there as it
        //     moves.
        public void FollowSelected() { LeaveCameras(); following = true; FocusSelected(); }

        private bool camerasShown;
        private string watchedId;

        // -----------------------------------------------------------------------------------
        // Function: OpenCameras
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function shows the selected shuttle's cameras in the 3D city, the way both
        //     Cameras buttons open them, leaving the 2D map if it is showing.
        public void OpenCameras()
        {
            ShowMap(false);
            ShowCameras(true);
        }

        // -----------------------------------------------------------------------------------
        // Function: CloseCameras
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function closes the cameras and always goes back to the 3D view, wherever
        //     they were opened from. It is used by Hide cameras and when the watched
        //     shuttle leaves; Overview and the like use LeaveCameras, as they need the 3D view.
        public void CloseCameras()
        {
            ShowCameras(false);
        }

        // Whether the shuttle's cameras are shown in place of the 3D view, whether there is a
        // selected shuttle in the 3D city to watch, and whether this session has a 3D city at all.
        public bool CamerasShown => camerasShown;
        // The shuttle whose cameras are shown, or null. Selecting another shuttle in the 3D city
        // moves the cameras to it; clearing the selection keeps the shuttle being watched.
        public string WatchedId => camerasShown ? watchedId : null;
        public bool CanShowCameras => CityShown && city != null && city.TryGetShuttlePose(camerasShown ? watchedId : selectedId, out _, out _);
        public bool HasCity => latest != null && latest.Spatial == null && cityClient != null;
        // The camera shown large.
        public int MainCamera => mainCamera;

        // -----------------------------------------------------------------------------------
        // Function: ShowCameras
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function shows the selected shuttle's cameras in place of the 3D view, or puts
        //     the 3D view back. The 3D view's own camera stops drawing while it is covered. The
        //     shuttle's cameras are placed before they switch on, so their first frame is not
        //     drawn from where they were last.
        //
        // Args:
        //     show (bool): True to show the cameras, false to go back to the 3D view.
        public void ShowCameras(bool show)
        {
            camerasShown = show;
            if (show) watchedId = selectedId;
            cameraPanel.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (show) { shuttleCameras.Match(viewCamera); SizeCameraView(); }
            if (show && city != null && city.TryGetShuttlePose(watchedId, out var position, out var rotation))
                shuttleCameras.Place(position, rotation);
            shuttleCameras.SetActive(show);
            if (show)
            {
                // The pictures only exist once the cameras have been opened
                ShowTextures();
                viewCamera.enabled = false;
            }
            else if (CityShown) viewCamera.enabled = true;
            if (show) SelectCamera(mainCamera);
            RefreshHint();
            CamerasToggled?.Invoke(show);
        }

        // -----------------------------------------------------------------------------------
        // Function: SetBottomInset
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function lifts the controls along the view's bottom edge (the 2D map's buttons,
        //     scale bar and credits, and the 3D view's Draft in 3D row) by this many points, and
        //     ends the camera thumbnails' column above them, so a panel laid over the bottom of the
        //     view, such as the Fleet List, hides none of them. 0 puts them back.
        //
        // Args:
        //     points (float): How far the panel reaches up into the view.
        public void SetBottomInset(float points)
        {
            points = Mathf.Max(0, points);
            map?.SetBottomInset(points);
            if (worldActions != null) worldActions.style.translate = new StyleTranslate(new Translate(0, -points));
            if (cameraRail != null) cameraRail.style.bottom = RailBottomGap + ClockRoom + points;
            bottomInset = points;
        }

        // The gap below the camera thumbnails' column, as in .camera-rail
        private const float RailBottomGap = 10;
        // Room kept under the camera thumbnails for the clock
        private const float ClockRoom = 34;
        private float bottomInset;

        // -----------------------------------------------------------------------------------
        // Function: SelectCamera
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function shows one of the shuttle's cameras large and marks its thumbnail.
        //
        // Args:
        //     index (int): The camera, in the rig's order.
        public void SelectCamera(int index)
        {
            mainCamera = index;
            shuttleCameras.SetMain(index);
            // Names the watched shuttle too, as the selection elsewhere can be another one
            mainCameraLabel.text = watchedId == null
                ? shuttleCameras.NameOf(index)
                : $"{watchedId} · {shuttleCameras.NameOf(index)}";
            for (var i = 0; i < cameraTiles.Count; i++)
                cameraTiles[i].EnableInClassList("camera-tile-selected", i == index);
            ShowTextures();
            RefreshHint();
        }

        // -----------------------------------------------------------------------------------
        // Function: ShowTextures
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function puts the large image on the large view and each camera's thumbnail
        //     image on its tile. While the cameras are closed there are no images, so it does
        //     nothing.
        // Sizes the large camera image to the large view in physical pixels, as Resize does for the 3D view
        private void SizeCameraView()
        {
            var rect = mainCameraView.contentRect;
            if (mainCameraView.panel == null || rect.width < 1 || rect.height < 1) return;
            var root = mainCameraView.panel.visualTree.layout;
            var scale = root.width > 0 ? Screen.width / root.width : 1;
            if (shuttleCameras.SetLargeSize(Mathf.RoundToInt(rect.width * scale), Mathf.RoundToInt(rect.height * scale)))
                ShowTextures();
        }

        private void ShowTextures()
        {
            if (shuttleCameras.Large == null) return;
            mainCameraView.style.backgroundImage = new StyleBackground(Background.FromRenderTexture(shuttleCameras.Large));
            for (var i = 0; i < cameraTiles.Count; i++)
                cameraTiles[i].style.backgroundImage = new StyleBackground(Background.FromRenderTexture(shuttleCameras.Thumbnail(i)));
        }

        // -----------------------------------------------------------------------------------
        // Function: CityHint
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function returns the hint for the 3D view: the shuttle and camera being
        //     watched, or how to move around the city.
        private string CityHint() => camerasShown
            ? $"Shuttle cameras · {watchedId} · {shuttleCameras.NameOf(mainCamera)} · click a thumbnail to switch"
            : $"3D city · {city.BuildingCount} buildings · " +
              (city.LtaBusStopsLoaded ? $"{city.LtaBusStopCount} {city.BusStopSource} bus-stop shelters" : "loading bus-stop shelters") +
              " · right-drag orbit, left-drag pan, wheel zoom";

        // -----------------------------------------------------------------------------------
        // Function: RefreshHint
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function rewrites the hint as soon as the cameras open, close or switch,
        //     instead of at the next snapshot. It leaves the hint alone while the city is
        //     loading or not shown.
        private void RefreshHint()
        {
            if (!Ready || !CityShown || city == null || city.BuildingCount == 0 || (cityLoad != null && !cityLoad.IsCompleted)) return;
            SetReady(true, CityHint());
        }

        // -----------------------------------------------------------------------------------
        // Function: LeaveCameras
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function puts the 3D view back when something else needs it, such as Overview,
        //     Follow selected, the 2D map or a new session.
        private void LeaveCameras()
        {
            if (camerasShown) ShowCameras(false);
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
            LeaveCameras();
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
        //     view (RouteDraftView): The proposal as both views draw it, or null to edit nothing.
        //     editing (bool): True while the route page shows it on the map.
        public void SetRouteDraft(RouteEditorContext context, List<long> nodes, RouteEditPreview preview, RouteDraftView view = null, bool editing = false)
        {
            routeContext = context; draftNodes = nodes ?? new List<long>(); routePreview = preview; DrawDraft();
            draft = view;
            if (view != null) roadNetwork = view.Roads;
            map.ShowClosedRoads(view);
            map.ShowDraft(editing ? view : null);
            if (CityShown && city != null && city.BuildingCount > 0) city.ShowDraft(draft, servedStops);
        }

        // -----------------------------------------------------------------------------------
        // Function: SetServedStops
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function marks the stops served on the current loop, to show them in 3D.
        //
        // Args:
        //     stops (IReadOnlyCollection<string>): The stop IDs.
        public void SetServedStops(IReadOnlyCollection<string> stops)
        {
            servedStops = stops;
            if (CityShown && city != null && city.BuildingCount > 0) city.ShowDraft(draft, servedStops);
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
