using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.Contracts;
using UnityEngine;

namespace Formula9.World
{
    // The live incidents as scenes in the 3D city: a closed road behind its barriers or a wreck and its
    // response van, a beacon to find it from afar, the traffic backing up the roads leading to it, and
    // the roads where the backend slows the shuttle. IncidentSimulation decides what goes where.
    public sealed class CityIncidents : IDisposable
    {
        private const float CarWidthM = 3, CarHeightM = 2.2f, CarLengthM = 6.5f, CabinHeightM = 1, RibbonWidthM = 8, RibbonHeightM = 1, SlowedWidthM = 9, SlowedHeightM = 1.4f;
        private const float BlockedWidthM = 10, BlockedHeightM = 1.6f, BarrierWidthM = 12, BarrierHeightM = 2.4f, ConeHeightM = 2.4f, ConeSpacingM = 14;
        private const float BeaconHeightM = 34, BeaconRadiusM = 12, RoofBeaconAboveM = 8, MaxGlideS = 0.5f, RibbonStepM = 0.5f, FlashRate = 5;
        private static readonly Color BlockColor = new Color(0.839f, 0.188f, 0.192f), AccidentColor = new Color(0.96f, 0.55f, 0.1f);
        private static readonly Color QueueNear = new Color(0.85f, 0.2f, 0.15f, 0.7f), QueueFar = new Color(1, 0.75f, 0.1f, 0.55f), SlowedColor = new Color(1, 0.6f, 0.05f, 0.85f);
        private static readonly Color WreckColor = new Color(0.25f, 0.25f, 0.28f), VanColor = new Color(0.95f, 0.95f, 0.95f), ConeColor = new Color(1, 0.45f, 0.05f), Dark = new Color(0.1f, 0.1f, 0.12f);
        private static readonly Color[] Paintwork =
        {
            new Color(0.75f, 0.76f, 0.78f), new Color(0.2f, 0.22f, 0.26f), new Color(0.62f, 0.14f, 0.14f), new Color(0.17f, 0.3f, 0.5f), new Color(0.9f, 0.9f, 0.88f),
        };

        private sealed class CarView { public QueueCar Car; public Transform Body; }
        private sealed class LaneRibbon { public QueueLane Lane; public LineRenderer Line; public float ShownM = -1; }

        private sealed class Scene
        {
            public IncidentPlan Plan;
            public GameObject Root;
            public Material Beacon;
            public Color BeaconColor;
            public int RoadCount;
            public readonly List<CarView> Cars = new List<CarView>();
            public readonly List<LaneRibbon> Ribbons = new List<LaneRibbon>();
        }

        private readonly Transform root;
        private readonly Material basis, lineMaterial;
        private readonly Material[] paints;
        private readonly Material cabin, wreck, van, cone, flash;
        private readonly List<Material> owned = new List<Material>();
        private readonly Dictionary<string, Scene> scenes = new Dictionary<string, Scene>();
        private readonly Dictionary<string, Transform> roofBeacons = new Dictionary<string, Transform>();
        private FullSnapshot snapshot;
        private float shownAt;

        public string Status { get; private set; }
        public int SceneCount => scenes.Count;

        public CityIncidents(Transform parent, Material basis, Material lineMaterial)
        {
            root = new GameObject("Incidents").transform;
            root.SetParent(parent, false);
            this.basis = basis;
            this.lineMaterial = lineMaterial;
            paints = Paintwork.Select(Paint).ToArray();
            cabin = Paint(Dark); wreck = Paint(WreckColor); van = Paint(VanColor); cone = Paint(ConeColor); flash = Paint(BlockColor);
        }

        private Material Paint(Color color)
        {
            var material = new Material(basis) { color = color };
            owned.Add(material);
            return material;
        }

        public void Show(IEnumerable<IncidentRecord> records, FullSnapshot latest, IReadOnlyList<DraftRoad> network, float now)
        {
            snapshot = latest;
            shownAt = now;
            var live = records.Where(r => IncidentSimulation.IsLive(r, latest)).ToDictionary(r => r.IncidentId);
            foreach (var gone in scenes.Keys.Where(id => !live.ContainsKey(id)).ToList()) Remove(gone);
            var roads = network?.Count ?? 0;
            foreach (var record in live.Values)
            {
                // The road network can arrive after the incident, and then the scene can be drawn properly
                if (scenes.TryGetValue(record.IncidentId, out var shown) && shown.RoadCount == roads) continue;
                if (shown != null) Remove(record.IncidentId);
                scenes[record.IncidentId] = Build(IncidentSimulation.Plan(record, network, latest), roads);
            }
            Status = scenes.Count == 0 ? null : string.Join("\n", scenes.Values.OrderBy(s => s.Plan.Record.IncidentId, StringComparer.Ordinal).Select(s => IncidentSimulation.Line(s.Plan, latest)));
        }

        private void Remove(string id)
        {
            var scene = scenes[id];
            WorldBuilder.Remove(scene.Root);
            Destroy(scene.Beacon);
            owned.Remove(scene.Beacon);
            scenes.Remove(id);
        }

        private Scene Build(IncidentPlan plan, int roads)
        {
            var color = plan.Record.Kind == IncidentSimulation.Block ? BlockColor : AccidentColor;
            var scene = new Scene { Plan = plan, RoadCount = roads, BeaconColor = color, Root = new GameObject("Incident " + plan.Record.IncidentId), Beacon = Paint(color) };
            scene.Root.transform.SetParent(root, false);
            foreach (var path in plan.Slowed) Ribbon(scene, "Slowed road", path, SlowedColor, SlowedColor, SlowedWidthM, SlowedHeightM);
            foreach (var lane in plan.Lanes)
                scene.Ribbons.Add(new LaneRibbon { Lane = lane, Line = Ribbon(scene, "Backed up " + lane.EdgeId, new Vector2[0], QueueNear, QueueFar, RibbonWidthM, RibbonHeightM) });
            if (plan.Record.Kind == IncidentSimulation.Block) BuildClosure(scene); else BuildAccident(scene);
            var post = Part(scene, PrimitiveType.Cylinder, new Vector3(1.6f, BeaconHeightM / 2, 1.6f), scene.Beacon, "Incident beacon post");
            post.position = Ground(plan.Site, BeaconHeightM / 2);
            var head = Part(scene, PrimitiveType.Sphere, Vector3.one * BeaconRadiusM, scene.Beacon, "Incident beacon");
            head.position = Ground(plan.Site, BeaconHeightM);
            foreach (var car in plan.Cars)
            {
                var body = Part(scene, PrimitiveType.Cube, new Vector3(CarWidthM, CarHeightM, CarLengthM), paints[car.Paint], "Queued car");
                var top = Part(scene, PrimitiveType.Cube, Vector3.one, cabin, "Queued car cabin");
                top.SetParent(body, false);
                top.localScale = new Vector3(0.82f, CabinHeightM / CarHeightM, 0.5f);
                top.localPosition = new Vector3(0, 0.5f + CabinHeightM / CarHeightM / 2, -0.05f);
                body.gameObject.SetActive(false);
                scene.Cars.Add(new CarView { Car = car, Body = body });
            }
            Animate(scene, Clock(scene));
            return scene;
        }

        private void BuildClosure(Scene scene)
        {
            var road = scene.Plan.Road;
            Ribbon(scene, "Closed road", road, BlockColor, BlockColor, BlockedWidthM, BlockedHeightM).SetPositions(road.Select(p => Ground(p, BlockedHeightM)).ToArray());
            Barrier(scene, road[0], road[1]);
            Barrier(scene, road[road.Length - 1], road[road.Length - 2]);
            var length = IncidentSimulation.Length(road);
            for (var along = ConeSpacingM; along < length; along += ConeSpacingM) Cone(scene, IncidentSimulation.At(road, along, out _));
        }

        private void BuildAccident(Scene scene)
        {
            var plan = scene.Plan;
            var side = new Vector2(-plan.Along.y, plan.Along.x);
            Ribbon(scene, "Accident road", plan.Road, AccidentColor, AccidentColor, BlockedWidthM, BlockedHeightM).SetPositions(plan.Road.Select(p => Ground(p, BlockedHeightM)).ToArray());
            // Two cars that met at an angle, and the van attending them
            Vehicle(scene, wreck, plan.Site - plan.Along * 2 + side * 1.5f, plan.Along, 32, 1);
            Vehicle(scene, paints[2], plan.Site + plan.Along * 3 - side * 0.5f, plan.Along, -48, 1);
            var bar = Vehicle(scene, van, plan.Site - plan.Along * 11 - side * 2, plan.Along, 0, 1.3f);
            var light = Part(scene, PrimitiveType.Cube, Vector3.one, flash, "Response van light");
            light.SetParent(bar, false);
            light.localScale = new Vector3(0.7f, 0.35f, 0.15f);
            light.localPosition = new Vector3(0, 0.68f, 0);
            for (var i = -2; i <= 2; i++) Cone(scene, plan.Site - plan.Along * 8 + side * i * 2);
        }

        private Transform Vehicle(Scene scene, Material paint, Vector2 at, Vector2 along, float turnDeg, float scale)
        {
            var body = Part(scene, PrimitiveType.Cube, new Vector3(CarWidthM, CarHeightM, CarLengthM) * scale, paint, "Incident vehicle");
            body.position = Ground(at, CarHeightM * scale / 2);
            body.rotation = Quaternion.LookRotation(new Vector3(along.x, 0, along.y)) * Quaternion.Euler(0, turnDeg, 0);
            return body;
        }

        // Across the road, at one end of the closure.
        private void Barrier(Scene scene, Vector2 at, Vector2 toward)
        {
            var along = toward - at;
            if (along.sqrMagnitude <= 0) return;
            var bar = Part(scene, PrimitiveType.Cube, new Vector3(BarrierWidthM, BarrierHeightM, 1.2f), flash, "Road block barrier");
            bar.position = Ground(at, BarrierHeightM / 2);
            bar.rotation = Quaternion.LookRotation(new Vector3(along.x, 0, along.y));
        }

        private void Cone(Scene scene, Vector2 at)
        {
            var item = Part(scene, PrimitiveType.Cylinder, new Vector3(1.2f, ConeHeightM / 2, 1.2f), cone, "Traffic cone");
            item.position = Ground(at, ConeHeightM / 2);
        }

        private static Vector3 Ground(Vector2 at, float height) => new Vector3(at.x, height, at.y);

        private Transform Part(Scene scene, PrimitiveType shape, Vector3 size, Material material, string name)
        {
            var item = GameObject.CreatePrimitive(shape);
            item.name = name;
            UnityEngine.Object.DestroyImmediate(item.GetComponent<Collider>());
            item.transform.SetParent(scene.Root.transform, false);
            item.transform.localScale = size;
            var renderer = item.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return item.transform;
        }

        private LineRenderer Ribbon(Scene scene, string name, Vector2[] points, Color from, Color to, float width, float height)
        {
            var line = new GameObject(name).AddComponent<LineRenderer>();
            line.transform.SetParent(scene.Root.transform, false);
            line.useWorldSpace = true; line.sharedMaterial = lineMaterial; line.numCornerVertices = 4;
            line.startWidth = line.endWidth = width; line.startColor = from; line.endColor = to;
            line.positionCount = points.Length;
            line.SetPositions(points.Select(p => Ground(p, height)).ToArray());
            return line;
        }

        private float Clock(Scene scene) => IncidentSimulation.ElapsedS(scene.Plan.Record, snapshot);

        // Every scene and queue the background traffic must keep off, as they stand now.
        public List<TrafficHazard> TrafficHazards()
        {
            var hazards = new List<TrafficHazard>();
            foreach (var scene in scenes.Values) hazards.AddRange(IncidentSimulation.Hazards(scene.Plan, Clock(scene)));
            return hazards;
        }

        // Called each frame: the queue grows along its roads and the lights flash. The queue runs on the
        // incident's own clock, so it stands still when the session or the backend does: a gap in the
        // snapshots cannot move it on by more than a moment.
        public void Tick(float now, Func<string, Transform> shuttle)
        {
            if (snapshot == null) return;
            var running = snapshot.Session?.Status == "RUNNING";
            var glide = running ? Mathf.Clamp(now - shownAt, 0, MaxGlideS) : 0;
            foreach (var scene in scenes.Values) Animate(scene, Clock(scene) + glide);
            var pulse = 0.5f + 0.5f * Mathf.Sin(now * FlashRate);
            foreach (var scene in scenes.Values)
                scene.Beacon.color = Color.Lerp(scene.BeaconColor * 0.55f, Color.Lerp(scene.BeaconColor, Color.white, 0.35f), pulse);
            flash.color = Color.Lerp(BlockColor * 0.5f, BlockColor, pulse);
            UpdateRoofBeacons(shuttle);
        }

        private void Animate(Scene scene, float elapsed)
        {
            var queue = IncidentSimulation.QueueM(elapsed);
            foreach (var view in scene.Cars)
            {
                var lane = scene.Plan.Lanes[view.Car.Lane];
                var seen = IncidentSimulation.CarPose(view.Car, lane, elapsed, out var at, out var travel);
                if (view.Body.gameObject.activeSelf != seen) view.Body.gameObject.SetActive(seen);
                if (seen) view.Body.SetPositionAndRotation(Ground(at, CarHeightM / 2), Quaternion.LookRotation(new Vector3(travel.x, 0, travel.y)));
            }
            foreach (var ribbon in scene.Ribbons)
            {
                var length = Mathf.Clamp(queue - ribbon.Lane.StartM, 0, ribbon.Lane.LengthM);
                if (Mathf.Abs(length - ribbon.ShownM) < RibbonStepM) continue;
                ribbon.ShownM = length;
                var cut = length > 0 ? IncidentSimulation.Between(ribbon.Lane.Points, 0, length) : new Vector2[0];
                ribbon.Line.positionCount = cut.Length;
                ribbon.Line.SetPositions(cut.Select(p => Ground(p, RibbonHeightM)).ToArray());
                ribbon.Line.startColor = Color.Lerp(QueueNear, QueueFar, ribbon.Lane.StartM / IncidentSimulation.ReachM);
                ribbon.Line.endColor = Color.Lerp(QueueNear, QueueFar, (ribbon.Lane.StartM + length) / IncidentSimulation.ReachM);
            }
        }

        // A light over a shuttle held at an incident (red) or crawling through its traffic (amber).
        private void UpdateRoofBeacons(Func<string, Transform> shuttle)
        {
            foreach (var shown in roofBeacons.Values) shown.gameObject.SetActive(false);
            foreach (var vehicle in snapshot.Vehicles)
            {
                var held = vehicle.ServiceState == "AWAITING_GUIDANCE";
                var slowed = !held && scenes.Values.Any(s => IncidentSimulation.IsSlowing(s.Plan, vehicle, snapshot));
                var carrier = held || slowed ? shuttle(vehicle.VehicleId) : null;
                if (carrier == null) continue;
                roofBeacons.TryGetValue(vehicle.VehicleId, out var light);
                if (light == null)
                {
                    var item = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    item.name = "Shuttle hazard light " + vehicle.VehicleId;
                    UnityEngine.Object.DestroyImmediate(item.GetComponent<Collider>());
                    item.transform.SetParent(root, false);
                    item.transform.localScale = Vector3.one * 3;
                    item.GetComponent<MeshRenderer>().sharedMaterial = Paint(AccidentColor);
                    roofBeacons[vehicle.VehicleId] = light = item.transform;
                }
                light.gameObject.SetActive(true);
                light.position = carrier.position + Vector3.up * RoofBeaconAboveM;
                var color = held ? BlockColor : AccidentColor;
                light.GetComponent<MeshRenderer>().sharedMaterial.color = Color.Lerp(color * 0.5f, color, flash.color.r / BlockColor.r);
            }
        }

        private static void Destroy(UnityEngine.Object item)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(item);
            else UnityEngine.Object.DestroyImmediate(item);
        }

        public bool TryGetSite(string incidentId, out Vector2 site)
        {
            site = default;
            if (!scenes.TryGetValue(incidentId ?? "", out var scene)) return false;
            site = scene.Plan.Site;
            return true;
        }

        public void Dispose()
        {
            if (root != null) WorldBuilder.Remove(root.gameObject);
            foreach (var material in owned) Destroy(material);
            owned.Clear();
            scenes.Clear();
        }
    }
}
