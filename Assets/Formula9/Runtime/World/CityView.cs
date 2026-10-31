using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;
using UnityEngine;

namespace Formula9.World
{
    // The real streets in 3D: extruded OSM buildings on a ground of map tiles, with the session's
    // routes, stops and shuttles from the backend's snapshots. It adds no road geometry of its own.
    public sealed class CityView : IDisposable
    {
        // Sharp tiles around the buildings, over coarser ones out to the horizon
        private const int TileZoom = 17, FarTileZoom = 14;
        private const float GroundMarginM = 250, FarGroundMarginM = 6000, FarGroundDrop = 2;
        private static readonly Vector3 ShuttleSize = new Vector3(5, 5, 12);
        // Routes are cut through buildings a little wider than they are drawn, so a shuttle fits
        private const float RouteWidthM = 5, RouteClearanceM = 1.5f;

        private readonly Transform root;
        private readonly MapTiles tiles;
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private readonly Dictionary<string, Transform> shuttles = new Dictionary<string, Transform>();
        private readonly List<(int Zoom, int X, int Y, Material Material)> groundTiles = new List<(int, int, int, Material)>();
        private readonly List<GameObject> routeObjects = new List<GameObject>();
        private string routesKey;
        private Material buildingMaterial, lineMaterial;
        private List<CityBuilding> buildings;
        private Mesh buildingMesh;

        public int BuildingCount { get; private set; }
        public Bounds Bounds { get; private set; }

        public CityView(Transform parent, MapTiles tiles)
        {
            root = new GameObject("City").transform;
            root.SetParent(parent, false);
            this.tiles = tiles;
            buildingMaterial = Own(new Material(Shader.Find("Formula9/CityBuilding")));
            lineMaterial = Own(new Material(Shader.Find("Sprites/Default")));
            if (tiles != null) tiles.TileLoaded += RefreshTiles;
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
        }

        public void SetVisible(bool visible) { if (root != null) root.gameObject.SetActive(visible); }

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
            for (var i = 0; i < ordered.Count; i++)
            {
                var vehicle = ordered[i];
                var at = new Vector3((float)vehicle.Position.XM, ShuttleSize.y / 2, (float)vehicle.Position.YM);
                // The backend's heading is degrees anticlockwise from east, in the plane Unity calls XZ
                var heading = (float)vehicle.HeadingDeg * Mathf.Deg2Rad;
                var facing = Quaternion.LookRotation(new Vector3(Mathf.Cos(heading), 0, Mathf.Sin(heading)));
                if (!shuttles.ContainsKey(vehicle.VehicleId))
                    shuttles[vehicle.VehicleId] = Marker(PrimitiveType.Cube, ShuttleSize, FleetMap.ShuttleColor(i), "Shuttle " + vehicle.VehicleId);
                routes.TryGetValue(vehicle.RouteId ?? "", out var route);
                motion.SetTarget(vehicle.VehicleId, at, facing, now, route);
            }
            Tick(motion, now);
            var key = string.Join(";", snapshot.Routes.Select(r => $"{r.RouteId}:{r.Revision}")) + snapshot.Disruptions.Count;
            if (key != routesKey) { routesKey = key; BuildRoutes(snapshot, ordered); }
        }

        private void BuildRoutes(FullSnapshot snapshot, List<FleetVehicle> ordered)
        {
            foreach (var item in routeObjects) WorldBuilder.Remove(item);
            routeObjects.Clear();
            CutRoutesThroughBuildings(snapshot);
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
                    line.startWidth = line.endWidth = RouteWidthM; line.startColor = line.endColor = new Color(color.r, color.g, color.b, 0.9f);
                    routeObjects.Add(line.gameObject);
                }
                foreach (var stop in route.StopsM)
                {
                    var marker = Marker(PrimitiveType.Cylinder, new Vector3(16, 3, 16), color, "Stop " + stop.StopId);
                    marker.position = new Vector3((float)stop.Position.XM, 3, (float)stop.Position.YM);
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
            item.GetComponent<MeshRenderer>().sharedMaterial = material;
            return item.transform;
        }

        // Called each frame: shuttles glide between the backend's poses.
        public void Tick(ShuttleMotion motion, float now)
        {
            foreach (var pair in shuttles)
                if (motion.Sample(pair.Key, now, out var position, out var rotation))
                    pair.Value.SetPositionAndRotation(position, rotation);
        }

        public bool TryGetShuttle(string id, out Vector3 position)
        {
            position = default;
            if (id == null || !shuttles.TryGetValue(id, out var shuttle)) return false;
            position = shuttle.position;
            return true;
        }

        public void Dispose()
        {
            if (tiles != null) tiles.TileLoaded -= RefreshTiles;
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
