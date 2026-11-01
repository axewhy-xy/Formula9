using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;
using UnityEngine;
using HealthbarGames;

namespace Formula9.World
{
    // The real streets in 3D: extruded OSM buildings and asphalt roads on a ground of map tiles, with
    // the session's routes, stops and shuttles from the backend's snapshots.
    public sealed class CityView : IDisposable
    {
        // Sharp tiles around the buildings, over coarser ones out to the horizon
        private const int TileZoom = 17, FarTileZoom = 14;
        private const float GroundMarginM = 250, FarGroundMarginM = 6000, FarGroundDrop = 2;
        private static readonly Vector3 ShuttleSize = new Vector3(5, 5, 12);
        // Routes are cut through buildings a little wider than they are drawn, so a shuttle fits
        private const float RouteWidthM = 5, RouteClearanceM = 1.5f;
        private static readonly Color KerbColor = new Color(0.66f, 0.66f, 0.64f), AsphaltColor = new Color(0.17f, 0.17f, 0.19f), MarkingColor = new Color(0.95f, 0.95f, 0.92f);
        private const int GrainSize = 128, GrainSeed = 9;
        private const float ProposedWidthM = 6, ProposedHeightM = 2, ClosureWidthM = 9, ClosureHeightM = 1.6f;
        private const float BarrierWidthM = 12, BarrierHeightM = 2.4f, PinHeightM = 26, StopReachM = 14;
        private static readonly Color ProposedColor = new Color(0.086f, 0.396f, 0.753f, 0.95f);
        private static readonly Color ClosureColor = new Color(0.839f, 0.188f, 0.192f, 0.85f);
        private static readonly Color ClosureSolid = new Color(0.839f, 0.188f, 0.192f);
        private static readonly Color StopServed = new Color(0.153f, 0.682f, 0.376f);
        private static readonly Color StopDwelling = new Color(1, 0.84f, 0.2f);
        private static readonly Color WindscreenColor = new Color(0.08f, 0.12f, 0.16f);
        // A depot is a pad with a charger post for each bay; the backend lays its bays out 30 m apart
        private static readonly Vector3 DepotPadSize = new Vector3(60, 0.4f, 34), ChargerPostSize = new Vector3(2, 6, 2);
        // Shuttles drive into a bay from in front and leave it behind, so a post stands beside its bay
        private const float ChargerSpacingM = 30, ChargerGapM = 1;
        private static readonly Color DepotPadColor = new Color(0.122f, 0.306f, 0.349f);
        private static readonly Color ChargerFree = new Color(0.153f, 0.682f, 0.376f), ChargerTaken = new Color(1, 0.6f, 0.1f);
        private HashSet<string> served = new HashSet<string>();

        private readonly Transform root;
        private readonly MapTiles tiles;
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private readonly Dictionary<string, Transform> shuttles = new Dictionary<string, Transform>();
        private readonly List<(int Zoom, int X, int Y, Material Material)> groundTiles = new List<(int, int, int, Material)>();
        private readonly List<GameObject> routeObjects = new List<GameObject>();
        private readonly List<GameObject> draftObjects = new List<GameObject>();
        private readonly Dictionary<string, (Material Material, Color Color)> stopMarkers = new Dictionary<string, (Material, Color)>();
        private readonly Dictionary<string, Material> chargerPosts = new Dictionary<string, Material>();
        private readonly HashSet<string> dwelling = new HashSet<string>();
        private RouteDraftView shownDraft;
        private RouteEditPreview shownPreview;
        private string shownHighlights, shownWaypoints, shownClosures;
        private string routesKey, hiddenShuttle;
        private Material buildingMaterial, lineMaterial;
        private readonly CityIncidents incidents;
        private List<CityBuilding> buildings;
        private Mesh buildingMesh;
        private CityTrafficRenderer traffic;
        // A queue grows slowly, so the traffic need not look at the incidents every frame
        private const float HazardRefreshS = 0.5f;
        private float hazardsDueS;
        private readonly List<TrafficObstacle> trafficObstacles = new List<TrafficObstacle>();

        public int BuildingCount { get; private set; }
        public int RoadCount { get; private set; }
        public Bounds Bounds { get; private set; }
        public string IncidentStatus => incidents.Status;

        private readonly List<GameObject> trafficLightObjects = new List<GameObject>();
        public int TrafficLightCount { get; private set; }
        private readonly GameObject trafficLightPrefab;
        private IReadOnlyList<CityRoad> loadedRoads = Array.Empty<CityRoad>();

        public CityView(Transform parent, MapTiles tiles, GameObject trafficLightPrefab = null)
        {
            root = new GameObject("City").transform;
            root.SetParent(parent, false);
            this.tiles = tiles;

            this.trafficLightPrefab = trafficLightPrefab;

            buildingMaterial = Own(new Material(Shader.Find("Formula9/CityBuilding")));
            ApplyBuildingTextures(buildingMaterial);
            lineMaterial = Own(new Material(Shader.Find("Sprites/Default")));
            incidents = new CityIncidents(root, buildingMaterial, lineMaterial);
            traffic = new CityTrafficRenderer();
            if (tiles != null) tiles.TileLoaded += RefreshTiles;
        }

        // Wall and window textures in Resources/Formula9/Textures, by shader property, one pair for each of
        // CityMesh's texture sets. Without all of them the buildings keep their plain colours rather than
        // showing white where a texture is missing.
        public static readonly (string Property, string File)[] BuildingTextures =
        {
            ("_WallHome", "building_wall_home"), ("_WallOther", "building_wall_other"),
            ("_WallHdb", "building_wall_hdb"), ("_WallLowRise", "building_wall_lowrise"), ("_WallOffice", "building_wall_office"),
            ("_WindowHome", "building_window_home"), ("_WindowOther", "building_window_other"),
            ("_WindowHdb", "building_window_hdb"), ("_WindowLowRise", "building_window_lowrise"), ("_WindowOffice", "building_window_office"),
        };

        private static void ApplyBuildingTextures(Material material)
        {
            var textures = BuildingTextures.Select(t => (t.Property, Texture: Resources.Load<Texture2D>("Formula9/Textures/" + t.File))).ToList();
            if (textures.Any(t => t.Texture == null))
            {
                Debug.LogWarning("Building textures are missing from Resources/Formula9/Textures, so buildings are plain colour");
                material.SetFloat("_Plain", 1);
                return;
            }
            foreach (var (property, texture) in textures) material.SetTexture(property, texture);
        }

        private T Own<T>(T item) where T : UnityEngine.Object { owned.Add(item); return item; }

        public async Task LoadAsync(CityClient client, string area, CancellationToken cancellation)
        {
            buildings = await client.GetBuildingsAsync(area, cancellation);
            cancellation.ThrowIfCancellationRequested();
            buildingMesh = Own(CityMesh.Build(buildings));
            BuildingCount = buildings.Count;
            Bounds = buildingMesh.bounds;
            var city = new GameObject("Buildings", typeof(MeshFilter), typeof(MeshRenderer));
            city.transform.SetParent(root, false);
            city.GetComponent<MeshFilter>().sharedMesh = buildingMesh;
            city.GetComponent<MeshRenderer>().sharedMaterial = buildingMaterial;
            BuildGround();
            await LoadRoadsAsync(client, area, cancellation);

            await LoadTrafficLightsAsync(client, area, cancellation);
        }

        public void SetVisible(bool visible) { if (root != null) root.gameObject.SetActive(visible); }

        // Roads only dress the ground, which already shows them: without their data the city still stands
        private async Task LoadRoadsAsync(CityClient client, string area, CancellationToken cancellation)
        {
            List<CityRoad> roads;
            try { roads = await client.GetRoadsAsync(area, cancellation); }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                Debug.LogWarning("No road surfaces, so the map's roads show instead: " + error.Message);
                traffic?.Rebuild(Array.Empty<CityRoad>(), null);
                return;
            }
            cancellation.ThrowIfCancellationRequested();
            RoadCount = roads.Count;
            var surface = Shader.Find("Formula9/CityRoad");
            var grain = Own(AsphaltGrain());
            var materials = new Material[RoadMesh.LayerCount];
            materials[RoadMesh.KerbLayer] = Own(new Material(surface) { color = KerbColor, mainTexture = grain });
            materials[RoadMesh.AsphaltLayer] = Own(new Material(surface) { color = AsphaltColor, mainTexture = grain });
            materials[RoadMesh.MarkingLayer] = Own(new Material(Shader.Find("Formula9/CityRoadMarkings")) { color = MarkingColor });
            // In this order, after the ground and buildings have written their depth
            for (var i = 0; i < materials.Length; i++) materials[i].renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry + 1 + i;
            var road = new GameObject("Roads", typeof(MeshFilter), typeof(MeshRenderer));
            road.transform.SetParent(root, false);
            road.GetComponent<MeshFilter>().sharedMesh = Own(RoadMesh.Build(roads));
            var renderer = road.GetComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            CityTrafficData trafficData = null;
            try { trafficData = await client.GetTrafficAsync(area, cancellation); }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                Debug.LogWarning("No traffic sample; roads still render: " + error.Message);
            }
            cancellation.ThrowIfCancellationRequested();
            CitySignalData signals = null;
            if (trafficData != null)
            {
                // Without the lights, traffic still gives way at junctions
                try { signals = await client.GetSignalsAsync(area, cancellation); }
                catch (Exception error) when (!(error is OperationCanceledException))
                {
                    Debug.LogWarning("No traffic lights; traffic only gives way at junctions: " + error.Message);
                }
                cancellation.ThrowIfCancellationRequested();
            }
            traffic?.Rebuild(roads, trafficData, signals);
        }

        private async Task LoadTrafficLightsAsync(CityClient client, string area, CancellationToken cancellation)
        {
            var trafficLightPrefab = Resources.Load<GameObject>("Formula9/TrafficLights");
            if (trafficLightPrefab == null)
            {
                Debug.LogWarning("Traffic light prefab is missing. Skipping traffic light rendering.");
                return;
            }

            List<CitySignalTrafficLight> signals;
            try
            {
                signals = await client.GetSignalTrafficLightAsync(area, cancellation);
            }

            catch (Exception error) when (!(error is OperationCanceledException))
            {
                Debug.LogWarning("Failed to load traffic signals from backend: " + error.Message);
                return;
            }

            cancellation.ThrowIfCancellationRequested();

            var filteredSignals = TrafficLightPlacement.OnePerPole(signals);

            TrafficLightCount = filteredSignals.Count;
            if (filteredSignals.Count > 0 && TrafficLightManager.Instance != null)
            {
                var first = filteredSignals[0];
                TrafficLightManager.Instance.InitializeTiming(new CitySignalTiming
                {
                    GreenS = (int)first.GL_duration,
                    YellowS = (int)first.YL_duration,
                    AllRedS = (int)first.RL_duration,
                });
                if (traffic != null) TrafficLightManager.Instance.SetClock(() => traffic.SignalClockS);
            }

            var trafficLightsContainer = new GameObject("TrafficLights");
            trafficLightsContainer.transform.SetParent(root, false);

            foreach (var signal in filteredSignals)
            {
                var pose = TrafficLightPlacement.Place(signal, loadedRoads);

                var lightObj = UnityEngine.Object.Instantiate(trafficLightPrefab, pose.Position, pose.Rotation, trafficLightsContainer.transform);
                lightObj.name = $"TrafficLight_{signal.Id ?? signal.Junction}";
                if (pose.Mirrored) lightObj.transform.localScale = new Vector3(-1, 1, 1);

                trafficLightObjects.Add(lightObj);
                var lightScript = lightObj.GetComponentInChildren<HaloTrafficLight>();
                if (lightScript == null)
                {
                    Debug.LogWarning($"[CityView] FAILED: Instantiated prefab for '{lightObj.name}' is missing the HaloTrafficLight component!");
                }

                if (TrafficLightManager.Instance == null)
                {
                    Debug.LogWarning("[CityView] FAILED: TrafficLightManager.Instance returned null!");
                }

                if (lightScript != null && TrafficLightManager.Instance != null)
                {
                    string junctionId = signal.Junction ?? signal.Id ?? "default_junction";
                    int offsetS = (int)signal.OffsetS;
                    int phase = signal.Phase;

                    TrafficLightManager.Instance.RegisterTrafficLight(lightScript, junctionId, offsetS, phase);
                    //Debug.LogWarning($"[CityView] Spawned & Registered light for junction: {junctionId}, Phase: {signal.Phase}, Offset: {signal.OffsetS}");
                }
            }

            Debug.LogWarning(filteredSignals.Count < signals.Count
            ? $"Successfully rendered {TrafficLightCount} unique traffic lights (filtered out {signals.Count - TrafficLightCount} duplicates)."
            : $"Successfully rendered {TrafficLightCount} traffic lights into the 3D map.");
        }

        // Pale speckle that the road colours tint. Its mipmaps average out to flat colour far away.
        private static Texture2D AsphaltGrain()
        {
            var texture = new Texture2D(GrainSize, GrainSize, TextureFormat.RGB24, true) { name = "Asphalt grain", wrapMode = TextureWrapMode.Repeat, anisoLevel = 4 };
            var random = new System.Random(GrainSeed);
            var pixels = new Color32[GrainSize * GrainSize];
            for (var i = 0; i < pixels.Length; i++)
            {
                var shade = (byte)random.Next(170, 256);
                pixels[i] = new Color32(shade, shade, shade, 255);
            }
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }

        private void BuildGround()
        {
            AddGroundLayer(FarTileZoom, FarGroundMarginM, -FarGroundDrop);
            AddGroundLayer(TileZoom, GroundMarginM, 0);
            RefreshTiles();
        }

        private void AddGroundLayer(int zoom, float margin, float height)
        {
            var min = new Vector2(Bounds.min.x - margin, Bounds.min.z - margin);
            var max = new Vector2(Bounds.max.x + margin, Bounds.max.z + margin);
            var northWest = MapGeo.TileAt(MapGeo.ToLatLon(min.x, max.y), zoom);
            var southEast = MapGeo.TileAt(MapGeo.ToLatLon(max.x, min.y), zoom);
            var shader = Shader.Find("Formula9/CityGround");
            for (var y = (int)Math.Floor(northWest.Y); y <= (int)Math.Floor(southEast.Y); y++)
                for (var x = (int)Math.Floor(northWest.X); x <= (int)Math.Floor(southEast.X); x++)
                {
                    var nw = MapGeo.ToLocal(MapGeo.TileCorner(x, y, zoom));
                    var se = MapGeo.ToLocal(MapGeo.TileCorner(x + 1, y + 1, zoom));
                    var mesh = Own(new Mesh { name = $"Tile {zoom}/{x}/{y}" });
                    mesh.vertices = new[] { new Vector3((float)nw.X, height, (float)se.Y), new Vector3((float)nw.X, height, (float)nw.Y), new Vector3((float)se.X, height, (float)nw.Y), new Vector3((float)se.X, height, (float)se.Y) };
                    mesh.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
                    mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                    mesh.RecalculateNormals(); mesh.RecalculateBounds();
                    var tile = new GameObject(mesh.name, typeof(MeshFilter), typeof(MeshRenderer));
                    tile.transform.SetParent(root, false);
                    tile.GetComponent<MeshFilter>().sharedMesh = mesh;
                    var material = Own(new Material(shader));
                    tile.GetComponent<MeshRenderer>().sharedMaterial = material;
                    groundTiles.Add((zoom, x, y, material));
                }
        }

        private void RefreshTiles()
        {
            if (tiles == null) return;
            foreach (var (zoom, x, y, material) in groundTiles)
            {
                var texture = tiles.Tile(zoom, x, y);
                if (texture != null) material.mainTexture = texture;
            }
        }

        public void Show(FullSnapshot snapshot, ShuttleMotion motion, float now)
        {
            var ordered = snapshot.Vehicles.OrderBy(v => v.VehicleId, StringComparer.Ordinal).ToList();
            var routes = ShuttleMotion.Routes(snapshot);
            foreach (var gone in shuttles.Keys.Where(id => ordered.All(v => v.VehicleId != id)).ToList())
            {
                WorldBuilder.Remove(shuttles[gone].gameObject);
                shuttles.Remove(gone);
                motion.Forget(gone);
            }
            for (var i = 0; i < ordered.Count; i++)
            {
                var vehicle = ordered[i];
                var at = new Vector3((float)vehicle.Position.XM, ShuttleSize.y / 2, (float)vehicle.Position.YM);
                // The backend's heading is degrees anticlockwise from east, in the plane Unity calls XZ
                var heading = (float)vehicle.HeadingDeg * Mathf.Deg2Rad;
                var facing = Quaternion.LookRotation(new Vector3(Mathf.Cos(heading), 0, Mathf.Sin(heading)));
                if (!shuttles.ContainsKey(vehicle.VehicleId))
                    shuttles[vehicle.VehicleId] = Shuttle(FleetMap.ShuttleColor(i), "Shuttle " + vehicle.VehicleId);
                routes.TryGetValue(vehicle.RouteId ?? "", out var route);
                motion.SetTarget(vehicle.VehicleId, at, facing, now, route);
            }
            Tick(motion, now);
            var key = string.Join(";", snapshot.Routes.Select(r => $"{r.RouteId}:{r.Revision}")) + snapshot.Disruptions.Count;
            if (key != routesKey) { routesKey = key; BuildRoutes(snapshot, ordered); }
            NoteDwelling(snapshot, ordered);
            RefreshChargers(snapshot);
        }

        public int DepotCount { get; private set; }

        // A charger post shows green while it has a free port and amber once every port is taken.
        private void RefreshChargers(FullSnapshot snapshot)
        {
            foreach (var charger in snapshot.Chargers ?? new List<Charger>())
                if (chargerPosts.TryGetValue(charger.ChargerId, out var material))
                    material.color = charger.OccupiedVehicleIds.Count < charger.PortCount ? ChargerFree : ChargerTaken;
        }

        private void BuildDepots(FullSnapshot snapshot)
        {
            chargerPosts.Clear();
            DepotCount = 0;
            foreach (var depot in snapshot.Depots)
            {
                var at = new Vector3((float)depot.Position.XM, 0, (float)depot.Position.YM);
                var pad = Marker(PrimitiveType.Cube, DepotPadSize, DepotPadColor, "Depot " + depot.DepotId);
                pad.position = at + Vector3.up * DepotPadSize.y / 2;
                routeObjects.Add(pad.gameObject);
                for (var i = 0; i < depot.ChargerIds.Count; i++)
                {
                    var offset = (i - (depot.ChargerIds.Count - 1) / 2f) * ChargerSpacingM;
                    var post = Marker(PrimitiveType.Cube, ChargerPostSize, ChargerFree, "Charger " + depot.ChargerIds[i]);
                    // On the bay's outer side (the middle bay's east side), clear of the lanes in and out
                    var side = (offset < 0 ? -1 : 1) * (ShuttleSize.x / 2 + ChargerGapM + ChargerPostSize.x / 2);
                    post.position = at + new Vector3(offset + side, ChargerPostSize.y / 2, 0);
                    chargerPosts[depot.ChargerIds[i]] = post.GetComponent<MeshRenderer>().sharedMaterial;
                    routeObjects.Add(post.gameObject);
                }
                DepotCount++;
            }
        }

        // The live incidents and the traffic they back up, over the roads they happened on.
        public void ShowIncidents(IEnumerable<IncidentRecord> records, FullSnapshot snapshot, IReadOnlyList<DraftRoad> network, float now) =>
            incidents.Show(records, snapshot, network, now);

        public bool TryGetIncidentSite(string incidentId, out Vector2 site) => incidents.TryGetSite(incidentId, out site);

        // A route is a paler shade of its shuttle, so the shuttle stands out on its own road.
        private static Color RouteTint(Color shuttle)
        {
            var pale = Color.Lerp(shuttle, Color.white, 0.55f);
            return new Color(pale.r, pale.g, pale.b, 0.9f);
        }

        // A stop lights up while a shuttle stands at it.
        private void NoteDwelling(FullSnapshot snapshot, List<FleetVehicle> vehicles)
        {
            dwelling.Clear();
            foreach (var route in snapshot.Routes)
                foreach (var stop in route.StopsM)
                    if (vehicles.Any(v => v.ServiceState == "DWELLING" && Mathf.Abs((float)(v.Position.XM - stop.Position.XM)) < StopReachM && Mathf.Abs((float)(v.Position.YM - stop.Position.YM)) < StopReachM))
                        dwelling.Add(stop.StopId);
            RefreshStops();
        }

        private void RefreshStops()
        {
            foreach (var pair in stopMarkers)
                pair.Value.Material.color = dwelling.Contains(pair.Key) ? StopDwelling : served.Contains(pair.Key) ? StopServed : pair.Value.Color;
        }

        // What the operator proposes, in the city: the closure stays up whatever happens to the draft,
        // the proposal lies on the road, and the road a check is about shows red.
        public void ShowDraft(RouteDraftView draft, IReadOnlyCollection<string> servedStops)
        {
            served = new HashSet<string>(servedStops ?? Array.Empty<string>());
            RefreshStops();
            var highlights = draft == null ? "" : string.Join(";", draft.Highlighted);
            var waypoints = draft == null ? "" : string.Join(";", draft.Waypoints.Select(w => w.EdgeId + w.NodeId));
            var closures = draft == null ? "" : string.Join(";", draft.Roads.Where(r => r.Closed).Select(r => r.EdgeId));
            if (draft == shownDraft && draft?.Preview == shownPreview && highlights == shownHighlights && waypoints == shownWaypoints && closures == shownClosures) return;
            shownDraft = draft; shownPreview = draft?.Preview; shownHighlights = highlights; shownWaypoints = waypoints; shownClosures = closures;
            foreach (var item in draftObjects) WorldBuilder.Remove(item);
            draftObjects.Clear();
            if (draft == null) return;
            foreach (var road in draft.Roads.Where(r => r.Closed && !r.OnTwinSide))
            {
                AddRibbon("Closed road", road.Points, ClosureColor, ClosureWidthM, ClosureHeightM);
                Barrier(road.Points[0], road.Points[1]);
                Barrier(road.Points[road.Points.Length - 1], road.Points[road.Points.Length - 2]);
            }
            foreach (var path in draft.ProposalPaths()) AddRibbon("Proposed route", path.Points, ProposedColor, ProposedWidthM, ProposedHeightM);
            foreach (var id in draft.Highlighted)
                if (draft.TryRoad(id, out var road)) AddRibbon("Road at fault", road.Points, ClosureColor, ProposedWidthM + 2, ProposedHeightM + 0.3f);
            foreach (var waypoint in draft.Waypoints)
            {
                var pin = Marker(PrimitiveType.Cylinder, new Vector3(2, PinHeightM / 2, 2), ProposedColor, "Waypoint");
                pin.position = new Vector3((float)waypoint.X, PinHeightM / 2, (float)waypoint.Y);
                draftObjects.Add(pin.gameObject);
                var head = Marker(PrimitiveType.Sphere, new Vector3(7, 7, 7), ProposedColor, "Waypoint head");
                head.position = new Vector3((float)waypoint.X, PinHeightM, (float)waypoint.Y);
                draftObjects.Add(head.gameObject);
            }
        }

        private void AddRibbon(string name, Vector2[] points, Color color, float width, float height)
        {
            if (points.Length < 2) return;
            var line = new GameObject(name).AddComponent<LineRenderer>();
            line.transform.SetParent(root, false);
            line.useWorldSpace = true; line.sharedMaterial = lineMaterial; line.positionCount = points.Length;
            line.SetPositions(points.Select(p => new Vector3(p.x, height, p.y)).ToArray());
            line.startWidth = line.endWidth = width; line.startColor = line.endColor = color;
            line.numCornerVertices = 4;
            draftObjects.Add(line.gameObject);
        }

        // A red barrier across the road at one end of the closure.
        private void Barrier(Vector2 at, Vector2 toward)
        {
            var along = toward - at;
            if (along.sqrMagnitude <= 0) return;
            var bar = Marker(PrimitiveType.Cube, new Vector3(BarrierWidthM, BarrierHeightM, 1.2f), ClosureSolid, "Closure barrier");
            bar.position = new Vector3(at.x, BarrierHeightM / 2, at.y);
            bar.rotation = Quaternion.LookRotation(new Vector3(along.x, 0, along.y));
            draftObjects.Add(bar.gameObject);
        }

        // A box with a dark windscreen at its front, so which way it is going reads from any angle.
        private Transform Shuttle(Color color, string name)
        {
            var body = Marker(PrimitiveType.Cube, ShuttleSize, color, name);
            var screen = Marker(PrimitiveType.Cube, Vector3.one, WindscreenColor, name + " windscreen");
            screen.SetParent(body, false);
            screen.localPosition = new Vector3(0, 0.2f, 0.5f);
            screen.localScale = new Vector3(0.86f, 0.4f, 0.04f);
            return body;
        }

        private void BuildRoutes(FullSnapshot snapshot, List<FleetVehicle> ordered)
        {
            foreach (var item in routeObjects) WorldBuilder.Remove(item);
            routeObjects.Clear();
            stopMarkers.Clear();
            CutRoutesThroughBuildings(snapshot);
            BuildDepots(snapshot);
            foreach (var route in snapshot.Routes)
            {
                var serving = ordered.Select((v, index) => (v, index)).Where(p => p.v.RouteId == route.RouteId).Select(p => p.index).ToList();
                var color = serving.Count > 0 ? FleetMap.ShuttleColor(serving[0]) : Color.gray;
                var points = route.EdgePathsM.SelectMany(p => p).Select(p => new Vector3((float)p.XM, 1.2f, (float)p.YM)).ToArray();
                if (points.Length > 1)
                {
                    var line = new GameObject("Route " + route.RouteId).AddComponent<LineRenderer>();
                    line.transform.SetParent(root, false);
                    line.useWorldSpace = true; line.sharedMaterial = lineMaterial; line.positionCount = points.Length; line.SetPositions(points);
                    line.startWidth = line.endWidth = RouteWidthM; line.startColor = line.endColor = RouteTint(color);
                    routeObjects.Add(line.gameObject);
                }
                foreach (var stop in route.StopsM)
                {
                    var marker = Marker(PrimitiveType.Cylinder, new Vector3(16, 3, 16), color, "Stop " + stop.StopId);
                    marker.position = new Vector3((float)stop.Position.XM, 3, (float)stop.Position.YM);
                    stopMarkers[stop.StopId] = (marker.GetComponent<MeshRenderer>().sharedMaterial, color);
                    routeObjects.Add(marker.gameObject);
                }
            }
        }

        // A building's outline takes no account of a road under it, which would hide the route inside
        private void CutRoutesThroughBuildings(FullSnapshot snapshot)
        {
            if (buildingMesh == null) return;
            var paths = snapshot.Routes.Select(r => (IReadOnlyList<Vector2>)r.EdgePathsM.SelectMany(p => p).Select(p => new Vector2((float)p.XM, (float)p.YM)).ToList());
            CityMesh.Fill(buildingMesh, buildings, CityMesh.Corridors(paths, RouteWidthM / 2 + RouteClearanceM));
        }

        private Transform Marker(PrimitiveType shape, Vector3 size, Color color, string name)
        {
            var item = GameObject.CreatePrimitive(shape);
            item.name = name;
            UnityEngine.Object.DestroyImmediate(item.GetComponent<Collider>());
            item.transform.SetParent(root, false);
            item.transform.localScale = size;
            var material = Own(new Material(buildingMaterial) { color = color });
            // A shuttle or stop is not a building: no walls or windows on it
            material.SetFloat("_Plain", 1);
            item.GetComponent<MeshRenderer>().sharedMaterial = material;
            return item.transform;
        }

        // Called once a frame, unlike Tick, which a new snapshot also calls. A delta of zero holds traffic still.
        public void TickTraffic(float deltaTime, Camera camera)
        {
            if (traffic == null) return;
            hazardsDueS -= deltaTime;
            if (hazardsDueS <= 0)
            {
                hazardsDueS = HazardRefreshS;
                traffic.SetHazards(incidents.TrafficHazards());
            }
            trafficObstacles.Clear();
            foreach (var pair in shuttles)
                trafficObstacles.Add(new TrafficObstacle { Id = pair.Key, Position = pair.Value.position, Forward = pair.Value.forward, HalfLength = ShuttleSize.z / 2, HalfWidth = ShuttleSize.x / 2 });
            traffic.SetShuttles(trafficObstacles);
            traffic.Tick(deltaTime, camera);
        }

        // Called each frame: shuttles glide between the backend's poses.
        public void Tick(ShuttleMotion motion, float now)
        {
            foreach (var pair in shuttles)
                if (motion.Sample(pair.Key, now, out var position, out var rotation))
                    pair.Value.SetPositionAndRotation(position, rotation);
            incidents.Tick(now, id => shuttles.TryGetValue(id, out var shuttle) ? shuttle : null);
        }

        public bool TryGetShuttle(string id, out Vector3 position)
        {
            position = default;
            if (id == null || !shuttles.TryGetValue(id, out var shuttle)) return false;
            position = shuttle.position;
            return true;
        }

        // The shuttle's smoothed position and facing, for the driver camera.
        public bool TryGetShuttlePose(string id, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = default;
            if (id == null || !shuttles.TryGetValue(id, out var shuttle)) return false;
            position = shuttle.position;
            rotation = shuttle.rotation;
            return true;
        }

        // Hides the shuttle the driver camera sits in, or none: from inside, the back of its own
        // windscreen would fill the view. A hidden shuttle still moves, so the camera can follow it.
        public void HideShuttle(string id)
        {
            if (id == hiddenShuttle) return;
            if (hiddenShuttle != null && shuttles.TryGetValue(hiddenShuttle, out var shown)) shown.gameObject.SetActive(true);
            hiddenShuttle = id;
            if (id != null && shuttles.TryGetValue(id, out var hidden)) hidden.gameObject.SetActive(false);
        }

        public void Dispose()
        {
            if (tiles != null) tiles.TileLoaded -= RefreshTiles;
            incidents.Dispose();
            traffic?.Dispose();
            traffic = null;
            if (root != null) WorldBuilder.Remove(root.gameObject);
            foreach (var item in owned)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(item);
                else UnityEngine.Object.DestroyImmediate(item);
            }
            owned.Clear();
        }
    }
}
