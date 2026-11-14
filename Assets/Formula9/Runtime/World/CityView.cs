using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;
using UnityEngine;
using UnityEngine.Rendering;
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
        // The bus model's body, for the traffic: it reaches 6.6 m ahead of the shuttle's position (6.0 m
        // behind) and is 4.6 m wide. The plain box drawn when the model is missing is ShuttleSize.
        private const float BusReachM = 6.6f, BusHalfWidthM = 2.3f;
        private float BodyHalfLength => shuttlePrefab != null ? BusReachM : ShuttleSize.z / 2;
        private float BodyHalfWidth => shuttlePrefab != null ? BusHalfWidthM : ShuttleSize.x / 2;
        // A route is a thin line lying on the road. It is cut through buildings wider than that, so a shuttle fits.
        private const float RouteWidthM = 1.35f, RouteHeightM = 0.3f, RouteCutHalfWidthM = 4;
        private static readonly Color KerbColor = new Color(0.66f, 0.66f, 0.64f), AsphaltColor = new Color(0.17f, 0.17f, 0.19f), MarkingColor = new Color(0.95f, 0.95f, 0.92f);
        private const int GrainSize = 128, GrainSeed = 9;
        private const float ProposedWidthM = 6, ProposedHeightM = 2, ClosureWidthM = 9, ClosureHeightM = 1.6f;
        // The layer ("Planning Overlay") for planning aids drawn over the city, such as closed
        // roads and proposed routes, so the shuttle cameras can leave them out.
        public const int PlanningLayer = 8;
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
        private string routesKey;
        private Material buildingMaterial, lineMaterial;
        private readonly CityIncidents incidents;
        private readonly TransitClient transit;
        private readonly BusStopShelterFactory busStopShelters;
        private CancellationTokenSource busStopCancellation;
        private readonly List<(Vector2 A, Vector2 B, float HalfWidth, bool Service)> roadSegments =
            new List<(Vector2, Vector2, float, bool)>();
        private readonly Dictionary<string, (Vector2 Move, float Yaw)> shelterOverrides =
            new Dictionary<string, (Vector2, float)>(StringComparer.Ordinal);
        private bool disposed;
        // Divide the city's bounding box into smaller LTA queries to avoid huge
        // responses and to keep requests limited to the displayed 3D district.
        private const float LtaQueryCellMetres = 1250f;
        private const float LtaQueryMarginMetres = 75f;
        private const int MaxLtaQueries = 64;
        // Bus-stop pins are not guaranteed to be at the physical shelter centre.
        // Adjust ONLY shelters that extend into the nearest road. Do not shift all pins.
        private const float OrientationRoadMatchMetres = 35f;
        private const float KerbGapM = 0.45f;
        private const float MaxAutomaticMoveM = 8f;
        private const float ShelterHalfDepthM = 1.3f * BusStopShelterFactory.ShelterScale;
        private List<CityBuilding> buildings;
        private Mesh buildingMesh;
        private CityTrafficRenderer traffic;
        // A queue grows slowly, so the traffic need not look at the incidents every frame
        private const float HazardRefreshS = 0.5f;
        private float hazardsDueS;
        private readonly List<TrafficObstacle> trafficObstacles = new List<TrafficObstacle>();

        public int BuildingCount { get; private set; }
        public int RoadCount { get; private set; }
        public int LtaBusStopCount { get; private set; }
        public bool LtaBusStopsLoaded { get; private set; }
        public string BusStopSource => transit != null &&
            !string.IsNullOrEmpty(transit.Attribution) && transit.Attribution.Contains("LTA")
            ? "LTA" : "mapped";
        public Bounds Bounds { get; private set; }
        public string IncidentStatus => incidents.Status;

        // The city can be opened with the legacy two-argument constructor, the
        // LTA transit client, the traffic-light prefab, or both dependencies.
        // Use the four-argument overload when supplying both optional features.
        private readonly List<GameObject> trafficLightObjects = new List<GameObject>();
        public int TrafficLightCount { get; private set; }
        private readonly GameObject trafficLightPrefab;
        private IReadOnlyList<CityRoad> loadedRoads = Array.Empty<CityRoad>();
        private readonly GameObject shuttlePrefab;

        public CityView(Transform parent, MapTiles tiles)
            : this(parent, tiles, (TransitClient)null, (GameObject)null) { }

        public CityView(Transform parent, MapTiles tiles, TransitClient transit)
            : this(parent, tiles, transit, (GameObject)null) { }

        public CityView(Transform parent, MapTiles tiles, GameObject trafficLightPrefab)
            : this(parent, tiles, (TransitClient)null, trafficLightPrefab) { }

        public CityView(Transform parent, MapTiles tiles, TransitClient transit, GameObject trafficLightPrefab)
        {
            root = new GameObject("City").transform;
            root.SetParent(parent, false);
            this.tiles = tiles;
            this.transit = transit;
            this.trafficLightPrefab = trafficLightPrefab;

            // load the shuttle prefab once.
            shuttlePrefab = Resources.Load<GameObject>("Formula9/ShuttleBus");
            if (shuttlePrefab == null)
                Debug.LogWarning("ShuttleBus prefab missing; using cubes.");

            buildingMaterial = Own(new Material(Shader.Find("Formula9/CityBuilding")));
            ApplyBuildingTextures(buildingMaterial);
            lineMaterial = Own(new Material(Shader.Find("Sprites/Default")));
            incidents = new CityIncidents(root, buildingMaterial, lineMaterial);
            busStopShelters = new BusStopShelterFactory();
            LoadShelterOverrides();
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
            // Buildings and roads load first. LTA shelters are fetched independently.
            if (transit != null)
            {
                busStopCancellation?.Cancel();
                busStopCancellation?.Dispose();
                busStopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                _ = LoadLtaSheltersAsync(busStopCancellation.Token);
            }
            else Debug.LogWarning("[CityView] No TransitClient connected: LTA bus-stop shelters cannot be loaded.");

            // Keep main's traffic light rendering as a separate optional visual.
            try { await LoadTrafficLightsAsync(client, area, cancellation); }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                Debug.LogWarning("[CityView] Traffic-light visuals unavailable: " + error.Message);
            }
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
            loadedRoads = roads;
            roadSegments.Clear();
            foreach (var cityRoad in roads)
            {
                Vector2? previous = null;
                foreach (var (x, y) in cityRoad.Points)
                {
                    var current = new Vector2((float)x, (float)y);
                    if (previous.HasValue && (current - previous.Value).sqrMagnitude > 0.01f)
                        roadSegments.Add((previous.Value, current,
                            Mathf.Clamp(RoadMesh.HalfWidth(cityRoad), 2.5f, 18f),
                            string.Equals(cityRoad.Class, "service", StringComparison.OrdinalIgnoreCase)));
                    previous = current;
                }
            }
            try
            {
                var surface = Shader.Find("Formula9/CityRoadAsh");
                if (surface == null || !surface.isSupported)
                    throw new InvalidOperationException("Formula9/CityRoadAsh shader is unavailable.");

                var grain = Own(AsphaltGrain());
                var roadTexture = Resources.Load<Texture2D>("Formula9/Textures/road_lane_texture");
                var textureShader = Shader.Find("Formula9/CityRoadTexture");
                bool hasTextureShader = textureShader != null && textureShader.isSupported;
                bool useAtlas = hasTextureShader && roadTexture != null;
                if (!hasTextureShader)
                    Debug.LogWarning("[CityView] CityRoadTexture shader unavailable. " +
                        "Using fallback ash-grey asphalt.");
                if (roadTexture != null)
                {
                    roadTexture.wrapMode = TextureWrapMode.Repeat;
                    roadTexture.filterMode = FilterMode.Bilinear;
                    roadTexture.anisoLevel = 8;
                }
                var materials = new Material[RoadMesh.LayerCount];
                materials[RoadMesh.KerbLayer] = Own(new Material(surface) { name = "City kerbs", color = KerbColor, mainTexture = grain });
                materials[RoadMesh.AsphaltLayer] = Own(new Material(hasTextureShader ? textureShader : surface)
                {
                    name = "Ash-grey asphalt",
                    color = useAtlas ? new Color(1.34f, 1.36f, 1.39f, 1f) : AsphaltColor,
                    mainTexture = useAtlas ? roadTexture : grain
                });
                if (hasTextureShader)
                    materials[RoadMesh.AsphaltLayer].SetFloat("_UseRoadAtlas", useAtlas ? 1f : 0f);

                materials[RoadMesh.MarkingLayer] = Own(new Material(surface)
                    { name = "Unused legacy paint submesh", color = Color.clear });
                materials[RoadMesh.KerbLayer].renderQueue = (int)RenderQueue.Geometry + 1;
                materials[RoadMesh.AsphaltLayer].renderQueue = (int)RenderQueue.Geometry + 2;

                var road = new GameObject("Roads", typeof(MeshFilter), typeof(MeshRenderer));
                road.transform.SetParent(root, false);
                LtaMappedRoadPaint.Result junctionPaint = null;
                if (string.Equals(area, "toa-payoh", StringComparison.OrdinalIgnoreCase))
                {
                    try { junctionPaint = LtaMappedRoadPaint.LoadJunctionData(roads); }
                    catch (Exception error)
                    {
                        Debug.LogWarning("[CityView] Could not load junction paint: " + error.Message);
                    }
                }

                var geometry = Own(RoadMesh.Build(roads, out int clippedTriangles, junctionPaint));
                // Hide kerbs in this view to avoid grey bars at intersections.
                geometry.SetTriangles(Array.Empty<int>(), RoadMesh.KerbLayer);
                road.GetComponent<MeshFilter>().sharedMesh = geometry;
                var renderer = road.GetComponent<MeshRenderer>();
                renderer.sharedMaterials = materials;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                if (roadTexture == null)
                    Debug.LogWarning("[CityView] Missing Resources/Formula9/Textures/road_lane_texture. Using generated asphalt grain.");
                Debug.Log($"[CityView] RoadMesh built {RoadCount} backend roads; " +
                    $"asphalt triangles clipped={clippedTriangles}; originalTexture={useAtlas}");

            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                Debug.LogError("[CityView] Road visuals failed, but the city can continue loading: " + error);
            }

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

        // The same LTA client supplies FleetMap's 2D bus-stop dots. Here its
        // coordinates are used only in the real Singapore 3D CityView.
        private async Task LoadLtaSheltersAsync(CancellationToken cancellation)
        {
            try
            {
                // CityMesh bounds are Unity metres (X east, Z north); MapGeo
                // converts them to LTA latitude/longitude for bounded queries.
                var minX = Bounds.min.x - LtaQueryMarginMetres;
                var maxX = Bounds.max.x + LtaQueryMarginMetres;
                var minZ = Bounds.min.z - LtaQueryMarginMetres;
                var maxZ = Bounds.max.z + LtaQueryMarginMetres;
                var width = Mathf.Max(1f, maxX - minX);
                var height = Mathf.Max(1f, maxZ - minZ);
                var cellSize = LtaQueryCellMetres;
                var cols = Mathf.Max(1, Mathf.CeilToInt(width / cellSize));
                var rows = Mathf.Max(1, Mathf.CeilToInt(height / cellSize));
                while ((long)cols * rows > MaxLtaQueries)
                {
                    cellSize *= 1.5f;
                    cols = Mathf.Max(1, Mathf.CeilToInt(width / cellSize));
                    rows = Mathf.Max(1, Mathf.CeilToInt(height / cellSize));
                }

                var unique = new Dictionary<string, BusStop>(StringComparer.Ordinal);
                var failedCells = 0;
                for (var row = 0; row < rows; row++)
                {
                    for (var col = 0; col < cols; col++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var westM = minX + col * cellSize;
                        var eastM = Mathf.Min(maxX, westM + cellSize);
                        var southM = minZ + row * cellSize;
                        var northM = Mathf.Min(maxZ, southM + cellSize);
                        var southWest = MapGeo.ToLatLon(westM, southM);
                        var northEast = MapGeo.ToLatLon(eastM, northM);
                        try
                        {
                            // Matches the exact signature already used by FleetMap:
                            // (south latitude, west longitude, north latitude, east longitude, token).
                            var stops = await transit.GetBusStopsAsync(
                                southWest.Latitude, southWest.Longitude,
                                northEast.Latitude, northEast.Longitude, cancellation);
                            cancellation.ThrowIfCancellationRequested();
                            foreach (var stop in stops)
                            {
                                if (string.IsNullOrWhiteSpace(stop.Code)) continue;
                                var local = MapGeo.ToLocal(new LatLon(stop.Latitude, stop.Longitude));
                                if (local.X < minX || local.X > maxX || local.Y < minZ || local.Y > maxZ)
                                    continue;
                                unique[stop.Code] = stop;
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error)
                        {
                            failedCells++;
                            Debug.LogWarning($"[CityView] LTA bus-stop area {col},{row} unavailable: {error.Message}");
                        }
                    }
                }

                cancellation.ThrowIfCancellationRequested();
                if (disposed) return;
                var placements = new List<BusStopShelterFactory.Placement>(unique.Count);
                var autoMoved = 0;
                var cappedMoves = 0;
                var noRoadMatch = 0;
                var manual = 0;
                var cappedStopCodes = new List<string>();
                var unmatchedStopCodes = new List<string>();
                foreach (var stop in unique.Values)
                {
                    var local = MapGeo.ToLocal(new LatLon(stop.Latitude, stop.Longitude));
                    var pin = new Vector2((float)local.X, (float)local.Y);
                    bool moved, capped, matched;
                    var placed = PositionShelter(pin, out moved, out capped, out matched);
                    if (moved) autoMoved++;
                    if (capped) { cappedMoves++; cappedStopCodes.Add(stop.Code); }
                    if (!matched) { noRoadMatch++; unmatchedStopCodes.Add(stop.Code); }
                    if (shelterOverrides.TryGetValue(stop.Code, out var correction))
                    {
                        var corrected = placed.Position + new Vector3(correction.Move.x, 0f, correction.Move.y);
                        placed = new BusStopShelterFactory.Placement(stop.Code, corrected,
                            Quaternion.Euler(0f, correction.Yaw, 0f) * placed.Rotation);
                        manual++;
                    }
                    else placed.StopId = stop.Code;
                    placements.Add(placed);
                }
                busStopShelters.Build(root, placements);
                LtaBusStopCount = placements.Count;
                LtaBusStopsLoaded = true;
                Debug.Log($"[CityView] Created {LtaBusStopCount} procedural {BusStopSource} bus-stop shelters " +
                    $"within the displayed 3D city. Area queries: {cols * rows}; failed: {failedCells}. " +
                    $"Roadside adjusted: {autoMoved}; capped: {cappedMoves}; no nearby road: {noRoadMatch}; " +
                    $"manual overrides: {manual}.");
                if (cappedMoves > 0)
                    Debug.LogWarning($"[CityView] {cappedMoves} LTA shelters needed >{MaxAutomaticMoveM}m " +
                        "adjustment to fully clear the road. Example stop codes: " +
                        string.Join(", ", cappedStopCodes.Take(20)) + ". Use placement overrides for these.");
                if (noRoadMatch > 0)
                    Debug.LogWarning("[CityView] No nearby mapped road for example stop codes: " +
                        string.Join(", ", unmatchedStopCodes.Take(20)) +
                        ". Their original LTA coordinates were preserved.");
                if (BusStopSource != "LTA")
                    Debug.LogWarning("[CityView] TransitClient is not attributed to LTA; verify the provider if LTA-only coverage is required.");
                if (failedCells > 0)
                    Debug.LogWarning("[CityView] LTA shelter coverage is partial because one or more area requests failed.");
            }
            catch (OperationCanceledException) { /* city closed or changed */ }
            catch (Exception error)
            {
                if (!disposed)
                    Debug.LogWarning("[CityView] Could not build the LTA 3D shelters: " + error.Message);
            }
        }

        // Find the road edge, not simply the nearest road CENTRE. Some stops lie
        // near a parallel service road or at a junction; prefer non-service roads
        // when equally near. Retain a genuine LTA sidewalk pin wherever possible.
        private BusStopShelterFactory.Placement PositionShelter(Vector2 pin,
            out bool moved, out bool capped, out bool matched)
        {
            moved = capped = matched = false;
            var bestScore = float.PositiveInfinity;
            var nearest = Vector2.zero;
            var tangent = Vector2.right;
            var halfWidth = 0f;
            foreach (var road in roadSegments)
            {
                var line = road.B - road.A;
                // Very short OSM fragments around an intersection often produce
                // unstable orientations. A shelter should follow a real segment.
                if (line.sqrMagnitude < 4f) continue;
                var t = Mathf.Clamp01(Vector2.Dot(pin - road.A, line) / line.sqrMagnitude);
                var onRoad = road.A + t * line;
                var dist = Vector2.Distance(pin, onRoad);
                if (dist > OrientationRoadMatchMetres + road.HalfWidth) continue;
                // Prefer a road with an interior closest point instead of an
                // endpoint of an unrelated slip road or junction fragment.
                var endpointPenalty = (t < .025f || t > .975f) ? 3f : 0f;
                var score = Mathf.Abs(dist - road.HalfWidth) + dist * .08f +
                    endpointPenalty + (road.Service ? 6f : 0f);
                if (score >= bestScore) continue;
                bestScore = score;
                nearest = onRoad;
                tangent = line.normalized;
                halfWidth = road.HalfWidth;
                matched = true;
            }
            if (!matched)
                return new BusStopShelterFactory.Placement(null,
                    new Vector3(pin.x, 0, pin.y), Quaternion.identity);

            // The previous version faced the raw pin-to-road vector. Near
            // segment endpoints this can be diagonal, rotating the shelter
            // into the road. Use the chosen ROAD TANGENT for orientation.
            var roadNormal = new Vector2(-tangent.y, tangent.x);
            float side = Vector2.Dot(pin - nearest, roadNormal) < 0f ? -1f : 1f;
            var outward = roadNormal * side;
            // The whole paved base should remain beyond the edge of the carriageway.
            var desiredDistance = halfWidth + ShelterHalfDepthM + KerbGapM;
            var currentDistance = Vector2.Dot(pin - nearest, outward);
            var missingDistance = Mathf.Max(0f, desiredDistance - currentDistance);
            capped = missingDistance > MaxAutomaticMoveM;
            var adjustment = Mathf.Min(missingDistance, MaxAutomaticMoveM);
            moved = adjustment > 0.02f;
            var finalPosition = pin + outward * adjustment;
            // In the generated shelter, local -Z is its road-facing front.
            var facing = Quaternion.LookRotation(new Vector3(outward.x, 0f, outward.y), Vector3.up);
            return new BusStopShelterFactory.Placement(null,
                new Vector3(finalPosition.x, 0f, finalPosition.y), facing);
        }

        // Optional per-stop fine tuning WITHOUT recompiling C#.
        // File: Assets/Formula9/Resources/Formula9/placement_overrides.csv
        // stop_code,east_m,north_m,yaw_delta_degrees
        // All values are relative to the automatically corrected placement.
        private void LoadShelterOverrides()
        {
            var asset = Resources.Load<TextAsset>("Formula9/placement_overrides");
            if (asset == null) return;
            var lines = asset.text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var row = line.Trim();
                if (row.Length == 0 || row.StartsWith("#") || row.StartsWith("stop_code")) continue;
                var columns = row.Split(',');
                if (columns.Length != 4 || string.IsNullOrWhiteSpace(columns[0]) ||
                    !float.TryParse(columns[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var east) ||
                    !float.TryParse(columns[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var north) ||
                    !float.TryParse(columns[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var yaw))
                {
                    Debug.LogWarning("[CityView] Ignoring invalid shelter placement row: " + row);
                    continue;
                }
                shelterOverrides[columns[0].Trim()] = (new Vector2(east, north), yaw);
            }
        }

        private async Task LoadTrafficLightsAsync(CityClient client, string area, CancellationToken cancellation)
        {
            var prefab = trafficLightPrefab != null ? trafficLightPrefab :
                Resources.Load<GameObject>("Formula9/TrafficLights");
            if (prefab == null)
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

                var lightObj = UnityEngine.Object.Instantiate(prefab, pose.Position, pose.Rotation, trafficLightsContainer.transform);
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
            // While the backend's traffic frames come, they draw the traffic and the shuttles' bodies and
            // keep the lights' clock; otherwise the traffic here drives the shuttles along their routes
            var backendDrives = traffic != null && traffic.FollowsBackend;
            // The lights change on the session's clock, as the backend's shuttles stop by them
            if (!backendDrives) traffic?.FollowSignalClock(snapshot.SimulationTimeS);
            var routes = ShuttleMotion.Routes(snapshot);
            foreach (var gone in shuttles.Keys.Where(id => ordered.All(v => v.VehicleId != id)).ToList())
            {
                WorldBuilder.Remove(shuttles[gone].gameObject);
                shuttles.Remove(gone);
                motion.Forget(gone);
                traffic?.ForgetShuttle(gone);
            }
            for (var i = 0; i < ordered.Count; i++)
            {
                var vehicle = ordered[i];
                var at = new Vector3((float)vehicle.Position.XM, ShuttleSize.y / 2, (float)vehicle.Position.YM);
                // The backend's heading is degrees anticlockwise from east, in the plane Unity calls XZ
                var heading = (float)vehicle.HeadingDeg * Mathf.Deg2Rad;
                var facing = Quaternion.LookRotation(new Vector3(Mathf.Cos(heading), 0, Mathf.Sin(heading)));
                if (!shuttles.ContainsKey(vehicle.VehicleId))
                    {
                        shuttles[vehicle.VehicleId] = Shuttle(
                            FleetMap.ShuttleColor(i),
                            "Shuttle " + vehicle.VehicleId);
                    }

                routes.TryGetValue(vehicle.RouteId ?? "", out var route);
                motion.SetTarget(vehicle.VehicleId, at, facing, now, route);
                // On its route the traffic drives it, keeping to its lane, the lights and the cars around
                // it; anywhere else, as in a depot yard, it glides to the backend's pose as before
                if (!backendDrives)
                    traffic?.DriveShuttle(vehicle.VehicleId, vehicle.RouteId, route, at, facing, (float)vehicle.SpeedMps, BodyHalfLength, BodyHalfWidth);
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
            line.gameObject.layer = PlanningLayer;
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

        private Transform Shuttle(Color color, string name)
        {
            if (shuttlePrefab == null)
                return Marker(PrimitiveType.Cube, ShuttleSize, color, name);

            var bus = UnityEngine.Object.Instantiate(shuttlePrefab, root, false);
            bus.name = name;

            var appearance = bus.GetComponent<ShuttleAppearance>();

            if (appearance != null)
                appearance.ApplyFleetColor(color);
            else
                Debug.LogWarning(
                    "Add ShuttleAppearance to the ShuttleBus prefab.",
                    bus);

            return bus.transform;
        }

        private void BuildRoutes(FullSnapshot snapshot, List<FleetVehicle> ordered)
        {
            foreach (var item in routeObjects) WorldBuilder.Remove(item);
            routeObjects.Clear();
            stopMarkers.Clear();
            CutRoutesThroughBuildings(snapshot);
            // Physical LTA shelters are populated separately from route markers.
            // Preserve the depot/charger objects provided by main.
            BuildDepots(snapshot);
            foreach (var route in snapshot.Routes)
            {
                var serving = ordered.Select((v, index) => (v, index)).Where(p => p.v.RouteId == route.RouteId).Select(p => p.index).ToList();
                var color = serving.Count > 0 ? FleetMap.ShuttleColor(serving[0]) : Color.gray;
                var points = route.EdgePathsM.SelectMany(p => p).Select(p => new Vector3((float)p.XM, RouteHeightM, (float)p.YM)).ToArray();
                if (points.Length > 1)
                {
                    var line = new GameObject("Route " + route.RouteId).AddComponent<LineRenderer>();
                    line.transform.SetParent(root, false);
                    // Flat on the road: by default a line turns to face the camera, like a wall in the shuttle's cameras
                    line.transform.localRotation = Quaternion.Euler(90, 0, 0);
                    line.alignment = LineAlignment.TransformZ;
                    line.numCornerVertices = 4;
                    line.useWorldSpace = true; line.sharedMaterial = lineMaterial; line.positionCount = points.Length; line.SetPositions(points);
                    line.startWidth = line.endWidth = RouteWidthM; line.startColor = line.endColor = RouteTint(color);
                    routeObjects.Add(line.gameObject);
                }
                foreach (var stop in route.StopsM)
                {
                    // Preserve the existing stop-status marker and dwelling/served colour behaviour.
                    // Small elevated status badge: don't cover the physical shelter
                    // with the older 16-metre-wide solid cylinder.
                    var marker = Marker(PrimitiveType.Cylinder, new Vector3(2f, 0.2f, 2f), color, "Stop " + stop.StopId);
                    marker.position = new Vector3((float)stop.Position.XM, 4f, (float)stop.Position.YM);
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
            CityMesh.Fill(buildingMesh, buildings, CityMesh.Corridors(paths, RouteCutHalfWidthM));
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

        // Called once a frame, before Tick places the shuttles, so the shuttles' bodies the backend's
        // traffic frames carry are drawn at the same moment as the vehicles around them. While those
        // frames come they move the traffic on their own clock, which runs down to a stop by itself when
        // they stop (a paused session), so running does not matter; otherwise the traffic here drives,
        // only while running says the session runs.
        public void AdvanceTraffic(float deltaTime, bool running)
        {
            if (traffic == null) return;
            if (traffic.FollowsBackend)
            {
                // The backend keeps the traffic off its hazards and away from the shuttles
                traffic.Advance(deltaTime);
                return;
            }
            deltaTime = running ? deltaTime : 0;
            hazardsDueS -= deltaTime;
            if (hazardsDueS <= 0)
            {
                hazardsDueS = HazardRefreshS;
                traffic.SetHazards(incidents.TrafficHazards());
            }
            // The shuttles the traffic does not drive, where they are drawn
            trafficObstacles.Clear();
            foreach (var pair in shuttles)
                if (!traffic.DrivesShuttle(pair.Key))
                    trafficObstacles.Add(new TrafficObstacle { Id = pair.Key, Position = pair.Value.position, Forward = pair.Value.forward, HalfLength = BodyHalfLength, HalfWidth = BodyHalfWidth });
            traffic.SetShuttles(trafficObstacles);
            traffic.Advance(deltaTime);
        }

        // Called once a frame, after the cameras are placed: draws the traffic where AdvanceTraffic left
        // it, for the cameras whose turn it is.
        public void DrawTraffic(IReadOnlyList<Camera> cameras) => traffic?.Draw(cameras);

        // Called each frame: a shuttle is where the backend's traffic frames put its body; without them,
        // one on its route is where the traffic here drove it, and any other glides between the
        // backend's poses.
        public void Tick(ShuttleMotion motion, float now)
        {
            foreach (var pair in shuttles)
            {
                if (traffic != null && traffic.TryGetBackendShuttlePose(pair.Key, ShuttleSize.y / 2, out var body, out var heading))
                    pair.Value.SetPositionAndRotation(body, heading);
                else if (traffic != null && traffic.TryGetShuttlePose(pair.Key, out var driven, out var facing))
                    pair.Value.SetPositionAndRotation(driven, facing);
                else if (motion.Sample(pair.Key, now, out var position, out var rotation))
                    pair.Value.SetPositionAndRotation(position, rotation);
            }
            incidents.Tick(now, id => shuttles.TryGetValue(id, out var shuttle) ? shuttle : null);
        }

        // The shuttles are put straight at their next poses, for a new session or a view that was not
        // being drawn, as ShuttleMotion.Clear is for the glide.
        public void ResetShuttles() => traffic?.ResetShuttles();

        // The backend's latest traffic frames, which then draw the traffic and the shuttles' bodies.
        public void ShowTraffic(IReadOnlyList<TrafficFrame> frames) => traffic?.FollowBackend(frames);

        // Forgets the backend's traffic frames, as for another session: until more come, the traffic
        // here drives itself.
        public void ForgetTraffic() => traffic?.ForgetBackend();

        // True while the backend's traffic frames draw the traffic.
        public bool FollowsBackendTraffic => traffic != null && traffic.FollowsBackend;

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

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            busStopCancellation?.Cancel();
            busStopCancellation?.Dispose();
            busStopCancellation = null;
            if (tiles != null) tiles.TileLoaded -= RefreshTiles;
            incidents.Dispose();
            busStopShelters.Dispose();
            traffic?.Dispose();
            traffic = null;
            if (root != null) WorldBuilder.Remove(root.gameObject);
            trafficLightObjects.Clear();
            foreach (var item in owned)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(item);
                else UnityEngine.Object.DestroyImmediate(item);
            }
            owned.Clear();
        }
    }
}
