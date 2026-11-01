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
        private static readonly Color KerbColor = new Color(0.66f, 0.66f, 0.64f), AsphaltColor = new Color(0.53f, 0.54f, 0.56f), MarkingColor = new Color(0.95f, 0.95f, 0.92f);
        private const int GrainSize = 128, GrainSeed = 9;
        private const float ProposedWidthM = 6, ProposedHeightM = 2, ClosureWidthM = 9, ClosureHeightM = 1.6f;
        private const float BarrierWidthM = 12, BarrierHeightM = 2.4f, PinHeightM = 26, StopReachM = 14;
        private static readonly Color ProposedColor = new Color(0.086f, 0.396f, 0.753f, 0.95f);
        private static readonly Color ClosureColor = new Color(0.839f, 0.188f, 0.192f, 0.85f);
        private static readonly Color ClosureSolid = new Color(0.839f, 0.188f, 0.192f);
        private static readonly Color StopServed = new Color(0.153f, 0.682f, 0.376f);
        private static readonly Color StopDwelling = new Color(1, 0.84f, 0.2f);
        private static readonly Color WindscreenColor = new Color(0.08f, 0.12f, 0.16f);
        private HashSet<string> served = new HashSet<string>();

        private readonly Transform root;
        private readonly MapTiles tiles;
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private readonly Dictionary<string, Transform> shuttles = new Dictionary<string, Transform>();
        private readonly List<(int Zoom, int X, int Y, Material Material)> groundTiles = new List<(int, int, int, Material)>();
        private readonly List<GameObject> routeObjects = new List<GameObject>();
        private readonly List<GameObject> draftObjects = new List<GameObject>();
        private readonly Dictionary<string, (Material Material, Color Color)> stopMarkers = new Dictionary<string, (Material, Color)>();
        private readonly HashSet<string> dwelling = new HashSet<string>();
        private RouteDraftView shownDraft;
        private RouteEditPreview shownPreview;
        private string shownHighlights, shownWaypoints, shownClosures;
        private string routesKey, hiddenShuttle;
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

        public int BuildingCount { get; private set; }
        public int RoadCount { get; private set; }
        public int LtaBusStopCount { get; private set; }
        public bool LtaBusStopsLoaded { get; private set; }
        public string BusStopSource => transit != null &&
            !string.IsNullOrEmpty(transit.Attribution) && transit.Attribution.Contains("LTA")
            ? "LTA" : "mapped";
        public Bounds Bounds { get; private set; }
        public string IncidentStatus => incidents.Status;

        public CityView(Transform parent, MapTiles tiles, TransitClient transit = null)
        {
            root = new GameObject("City").transform;
            root.SetParent(parent, false);
            this.tiles = tiles;
            this.transit = transit;
            buildingMaterial = Own(new Material(Shader.Find("Formula9/CityBuilding")));
            ApplyBuildingTextures(buildingMaterial);
            lineMaterial = Own(new Material(Shader.Find("Sprites/Default")));
            incidents = new CityIncidents(root, buildingMaterial, lineMaterial);
            busStopShelters = new BusStopShelterFactory();
            LoadShelterOverrides();
            if (tiles != null) tiles.TileLoaded += RefreshTiles;
        }

        // Wall and window textures in Resources/Formula9/Textures, by shader property. Without all four
        // the buildings keep their plain colours rather than showing white where a texture is missing.
        private static readonly (string Property, string File)[] BuildingTextures =
        {
            ("_WallHome", "building_wall_home"), ("_WallOther", "building_wall_other"),
            ("_WindowHome", "building_window_home"), ("_WindowOther", "building_window_other"),
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
            // Buildings become visible immediately; LTA shelters arrive as soon
            // as the independent bounded bus-stop requests finish.
            if (transit != null)
            {
                busStopCancellation?.Cancel();
                busStopCancellation?.Dispose();
                busStopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                _ = LoadLtaSheltersAsync(busStopCancellation.Token);
            }
            else Debug.LogWarning("[CityView] No TransitClient connected: LTA bus-stop shelters cannot be loaded.");
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
                return;
            }
            cancellation.ThrowIfCancellationRequested();
            RoadCount = roads.Count;
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
                // Original road geometry and kerbs are generated by RoadMesh; no replacement renderer.
                var surface = Shader.Find("Formula9/CityRoadAsh");
                if (surface == null || !surface.isSupported)
                {
                    Debug.LogWarning("[CityView] CityRoadAsh unavailable; using fallback road shader.");
                    surface = Shader.Find("Formula9/CityRoad");
                    if (surface == null || !surface.isSupported) surface = Shader.Find("Unlit/Texture");
                    if (surface == null || !surface.isSupported) surface = Shader.Find("Sprites/Default");
                    if (surface == null) throw new InvalidOperationException("No usable road shader found");
                }
                var grain = Own(AsphaltGrain());
                var roadTexture = Resources.Load<Texture2D>("Formula9/Textures/road_lane_texture");
                var textureShader = Shader.Find("Formula9/CityRoadTexture");
                bool texturedRoads = roadTexture != null && textureShader != null && textureShader.isSupported;
                if (!texturedRoads)
                    Debug.LogWarning("[CityView] Road texture or CityRoadTexture shader unavailable; " +
                        "falling back to plain ash-grey roads so the 3D map can still load. " +
                        "Check Resources/Formula9/Textures/road_lane_texture and shader import errors.");
                if (roadTexture != null)
                {
                    roadTexture.wrapMode = TextureWrapMode.Repeat;
                    roadTexture.filterMode = FilterMode.Bilinear;
                    roadTexture.anisoLevel = 8;
                }
                var materials = new Material[RoadMesh.LayerCount];
                materials[RoadMesh.KerbLayer] = Own(new Material(surface) { name = "City kerbs", color = KerbColor, mainTexture = grain });
                materials[RoadMesh.AsphaltLayer] = Own(new Material(texturedRoads ? textureShader : surface)
                {
                    name = texturedRoads ? "Textured ash-grey lanes (user image)" : "Ash-grey fallback asphalt",
                    color = texturedRoads ? new Color(1.34f, 1.36f, 1.39f, 1f) : AsphaltColor,
                    mainTexture = texturedRoads ? roadTexture : grain
                });
                // These materials share one shader but DIFFERENT stencil behaviour:
                // kerbs never affect the mask; asphalt writes 1 only where the road is.
                // LTA paint will require stencil 1, clipping mismatched GIS lines
                // at the visible asphalt boundary.
                // Mask kerbs against ALL road asphalt, not only their own road.
                // We draw asphalt first, writing stencil 1. Kerbs are drawn later
                // only in pixels with no asphalt, eliminating grey junction bars.
                materials[RoadMesh.KerbLayer].SetInt("_StencilRef", 1);
                materials[RoadMesh.KerbLayer].SetInt("_StencilComp", (int)CompareFunction.NotEqual);
                materials[RoadMesh.KerbLayer].SetInt("_StencilPass", (int)StencilOp.Keep);
                materials[RoadMesh.KerbLayer].SetInt("_StencilWriteMask", 0);
                materials[RoadMesh.AsphaltLayer].SetInt("_StencilRef", 1);
                materials[RoadMesh.AsphaltLayer].SetInt("_StencilComp", (int)CompareFunction.Always);
                materials[RoadMesh.AsphaltLayer].SetInt("_StencilPass", (int)StencilOp.Replace);
                materials[RoadMesh.AsphaltLayer].SetInt("_StencilWriteMask", 255);
                // RoadMesh.Build retains its historical third submesh for EditMode
                // compatibility, but that encoded-UV shader layer is never rendered
                // by this view. Standard lines are now sampled from the user-provided texture
                // by CityRoadTexture, not drawn as additional meshes.
                var oldLineShader = Shader.Find("Formula9/CityRoadMarkings");
                bool useLegacyPaint = !texturedRoads && oldLineShader != null && oldLineShader.isSupported;
                materials[RoadMesh.MarkingLayer] = Own(new Material(useLegacyPaint ? oldLineShader : surface)
                    { name = useLegacyPaint ? "Legacy fallback lane markings" : "Unused legacy paint submesh",
                      color = useLegacyPaint ? MarkingColor : Color.clear });
                materials[RoadMesh.AsphaltLayer].renderQueue = (int)RenderQueue.Geometry + 1;
                materials[RoadMesh.KerbLayer].renderQueue = (int)RenderQueue.Geometry + 2;
                materials[RoadMesh.MarkingLayer].renderQueue = (int)RenderQueue.Geometry + 3;

                var road = new GameObject("OSM roads (RoadMesh)", typeof(MeshFilter), typeof(MeshRenderer));
                road.transform.SetParent(root, false);
                var geometry = Own(RoadMesh.Build(roads));
                // Disable only the legacy shader-encoded lane layer. Keep the
                // asphalt and kerb submeshes and all existing RoadMesh tests.
                if (!useLegacyPaint)
                    geometry.SetTriangles(Array.Empty<int>(), RoadMesh.MarkingLayer);
                // The old kerb ribbon still crosses neighbouring roads at complex
                // junctions. Keep RoadMesh's public API for EditMode tests, but
                // hide only the kerb submesh in THIS 3D view. This removes the
                // thick grey overlaid bars without changing road geometry or JSON.
                geometry.SetTriangles(Array.Empty<int>(), RoadMesh.KerbLayer);
                road.GetComponent<MeshFilter>().sharedMesh = geometry;
                var renderer = road.GetComponent<MeshRenderer>();
                renderer.sharedMaterials = materials;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                Debug.Log($"[CityView] RoadMesh built {RoadCount} backend roads; " +
                    $"textured={texturedRoads}; kerbsHidden=true; fallbackLaneShader={useLegacyPaint}; tint={AsphaltColor}");

                // The LTA dataset contributes special paint ONLY. Standard lane
                // separators and edges are sampled from the user-supplied road texture.
                LtaMappedRoadPaint.Result mappedPaint = null;
                if (string.Equals(area, "toa-payoh", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        mappedPaint = LtaMappedRoadPaint.Build(roads);
                        var paintRoot = new GameObject("LTA special crossings, arrows, yellow boxes (3D)").transform;
                        paintRoot.SetParent(root, false);
                        var paintMaterials = new Dictionary<LtaMappedRoadPaint.PaintColour, Material>();
                        foreach (var (kind, color) in new[]
                        {
                            (LtaMappedRoadPaint.PaintColour.White, new Color(1f, 1f, .97f)),
                            (LtaMappedRoadPaint.PaintColour.Yellow, new Color(1f, .88f, .03f)),
                            (LtaMappedRoadPaint.PaintColour.Red, new Color(.80f, .16f, .18f)),
                        })
                        {
                            var mat = Own(new Material(surface)
                            {
                                name = "LTA special paint " + kind,
                                color = color,
                                mainTexture = Texture2D.whiteTexture,
                                renderQueue = (int)RenderQueue.Geometry + 10
                            });
                            mat.SetInt("_StencilRef", 1);
                            mat.SetInt("_StencilComp", (int)CompareFunction.Equal);
                            mat.SetInt("_StencilPass", (int)StencilOp.Keep);
                            mat.SetInt("_StencilWriteMask", 0);
                            mat.SetInt("_ZWrite", 0);
                            paintMaterials.Add(kind, mat);
                        }
                        foreach (var piece in mappedPaint.Meshes)
                        {
                            Own(piece.Mesh);
                            var obj = new GameObject(piece.Mesh.name, typeof(MeshFilter), typeof(MeshRenderer));
                            obj.transform.SetParent(paintRoot, false);
                            obj.GetComponent<MeshFilter>().sharedMesh = piece.Mesh;
                            var visual = obj.GetComponent<MeshRenderer>();
                            visual.sharedMaterial = paintMaterials[piece.Colour];
                            visual.shadowCastingMode = ShadowCastingMode.Off;
                            visual.receiveShadows = false;
                        }
                        Debug.Log($"[CityView] LTA special paint: " +
                            $"{mappedPaint.YellowBoxLineFeatures} yellow box paths, " +
                            $"{mappedPaint.GroundStopLineFeatures} transverse stop/edge features, " +
                            $"{mappedPaint.GroundArrowFeatures} mapped arrows, " +
                            $"{mappedPaint.ZebraCrossings} zebra crossings and " +
                            $"{mappedPaint.SignalCrossings} signal crossings.");
                    }
                    catch (Exception error)
                    {
                        Debug.LogWarning("[CityView] LTA special paint unavailable: " + error.Message +
                            ". Textured standard lane markings will still render.");
                    }
                }

                // No secondary generated lane-line mesh. Ordinary solid edges and
                // dashed boundaries are now sampled directly from road_lane_texture.
                // LTA paint above remains separate for crossings, arrows and boxes.
                Debug.Log("[CityView] Textured lane markings enabled using Resources/Formula9/Textures/road_lane_texture.");

            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                Debug.LogError("[CityView] Road visuals failed, but the city can continue loading: " + error);
            }

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
            // Physical shelters are populated separately from ALL LTA stops.
            // Route cylinders remain for served/dwelling operational status.
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
            if (disposed) return;
            disposed = true;
            busStopCancellation?.Cancel();
            busStopCancellation?.Dispose();
            busStopCancellation = null;
            if (tiles != null) tiles.TileLoaded -= RefreshTiles;
            incidents.Dispose();
            busStopShelters.Dispose();
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
