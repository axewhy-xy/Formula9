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
    // An operational planar view, also the route editor (FleetMap.RouteEditing.cs). Coordinates and
    // routes come from the backend; the client never creates substitute road geometry. A 2D session sits
    // on real Singapore: OpenStreetMap tiles underneath and LTA bus stops when zoomed in. A 3D
    // session's local frame is not a real place, so it gets neither.
    [UxmlElement]
    public sealed partial class FleetMap : VisualElement
    {
        // Far enough out to see the whole island (about 50 km across) in a small window
        private const float MinMetresPerPoint = 0.1f, MaxMetresPerPoint = 120;
        // Bus stops only this close in, and their names closer still, where they have room
        private const float BusStopMaxMetresPerPoint = 4, BusStopLabelMetresPerPoint = 1.2f;
        // Bus stops are asked for over the view plus this much on each side, so short pans need no request
        private const float BusStopAreaMargin = 0.5f;
        private const long BusStopSettleMs = 350;
        // Most tiles one layout may place; only reached on a huge window
        private const int MaxTiles = 400;
        // How many zoom levels up to look for an already loaded tile to stretch over a missing one
        private const int ParentTileLevels = 4;

        private static readonly Color BasemapBackground = new Color(0.925f, 0.945f, 0.941f);
        private static readonly Color PlannedRoute = new Color(0.153f, 0.682f, 0.376f);
        private static readonly Color RoadClosed = new Color(0.839f, 0.188f, 0.192f);
        private static readonly Color StopColor = new Color(0.09f, 0.412f, 0.667f);
        // Gold: no shuttle in the palette uses it. Its names need a darker shade to read on white.
        private static readonly Color BusStopColor = new Color(0.96f, 0.76f, 0.05f);
        private static readonly Color BusStopText = new Color(0.45f, 0.33f, 0);
        private static readonly Color DotOutline = new Color(0.118f, 0.157f, 0.176f);
        private static readonly Color Frozen = new Color(0.627f, 0.627f, 0.627f);
        public static Color FrozenColor => Frozen;
        private static readonly Color ApprovedDetour = new Color(0.949f, 0.6f, 0.29f);
        private static readonly Color OtherRoads = new Color(0.588f, 0.745f, 0.902f);
        private static readonly Color DepotColor = new Color(0.122f, 0.306f, 0.349f);
        private static readonly Color ChargingTrip = new Color(0.925f, 0.102f, 0.6f);
        private static readonly Color[] ShuttlePalette =
        {
            // No green: planned routes are green, so a green shuttle would vanish on one.
            new Color(0.184f, 0.502f, 0.929f), new Color(0.91f, 0.365f, 0.627f), new Color(0.949f, 0.6f, 0.29f), new Color(0.608f, 0.318f, 0.878f),
            new Color(0, 0.639f, 0.639f), new Color(0.51f, 0.392f, 0.235f), new Color(0.353f, 0.431f, 0.549f),
        };
        // The same colour for a shuttle on the map and on its fleet card.
        public static Color ShuttleColor(int index) => ShuttlePalette[index % ShuttlePalette.Length];

        private FullSnapshot snapshot;
        private readonly VisualElement tileLayer = new VisualElement { pickingMode = PickingMode.Ignore };
        private readonly VisualElement drawing = new VisualElement { pickingMode = PickingMode.Ignore };
        // The shuttles have a layer of their own: moving them must not redraw every road and stop
        private readonly VisualElement vehicleLayer = new VisualElement { pickingMode = PickingMode.Ignore };
        private readonly Dictionary<string, (Label Label, Vector2 PlacedAt)> vehicleLabels = new Dictionary<string, (Label, Vector2)>();
        private readonly VisualElement labels = new VisualElement { pickingMode = PickingMode.Ignore };
        private readonly Label credit = new Label { pickingMode = PickingMode.Ignore };
        private readonly List<Image> tileViews = new List<Image>();
        private readonly List<Rect> placedLabels = new List<Rect>();
        private readonly List<Rect> busBoxes = new List<Rect>();
        private const float BusBoxSize = 38;
        private Vector2 min, max, pan, lastPointer;
        private float zoom = 1;
        private bool pressed, dragging;
        private Vector2 pressStart;
        private const float DragStartDistance = 4;
        private string selectedId;
        private bool offline;
        private readonly VisualElement legend = new VisualElement { pickingMode = PickingMode.Ignore };
        private readonly VisualElement busStopLegend, proposedLegend, notClearedLegend;
        private readonly Action<bool> showLegend;
        private readonly Label scale = new Label { pickingMode = PickingMode.Ignore };
        private readonly Label banner = new Label { pickingMode = PickingMode.Ignore };
        // Shuttles drive between snapshots along the road, as in the 3D view, instead of jumping to each one.
        private readonly ShuttleMotion motion = new ShuttleMotion();

        private MapTiles tiles;
        private TransitClient transit;
        private bool islandWhenEmpty;
        private readonly IVisualElementScheduledItem busStopTimer;
        private CancellationTokenSource busStopRequest;
        private List<BusStop> busStops = new List<BusStop>();
        private List<(double X, double Y)> busStopPositions = new List<(double, double)>();
        private string busStopNotice;
        private Vector2 requestedCenter = new Vector2(float.NaN, float.NaN), requestedSize;
        private float requestedScale;

        public string SelectedId { get => selectedId; set { selectedId = value; vehicleLayer.MarkDirtyRepaint(); } }
        public bool ShowsIslandWhenEmpty => islandWhenEmpty && (tiles != null || transit != null);
        private readonly Dictionary<string, IncidentRecord> incidentMarkers = new Dictionary<string, IncidentRecord>();
        public event Action<string> IncidentSelected;
        public void SetIncidentEvents(IEnumerable<Formula9.Contracts.Event> events)
        {
            // Events are delivered incrementally; clearing here loses markers from
            // older batches. Keep them until the session changes. The original detection
            // position outlasts resolution: the snapshot supplies the current state, so a
            // historical marker turns grey.
            IncidentRecord.Merge(events, incidentMarkers);
            drawing.MarkDirtyRepaint();
        }
        public event Action<string> VehicleSelected;

        public FleetMap()
        {
            pickingMode = PickingMode.Position;
            foreach (var layer in new[] { tileLayer, drawing, vehicleLayer, labels })
            {
                layer.style.position = UnityEngine.UIElements.Position.Absolute;
                layer.style.left = layer.style.top = layer.style.right = layer.style.bottom = 0;
                Add(layer);
            }
            credit.style.position = UnityEngine.UIElements.Position.Absolute;
            credit.style.right = 52; credit.style.bottom = 8; credit.style.fontSize = 10;
            credit.style.color = DotOutline; credit.style.backgroundColor = new Color(1, 1, 1, 0.8f);
            credit.style.paddingLeft = credit.style.paddingRight = 4;
            credit.style.display = DisplayStyle.None;
            Add(credit);
            scale.AddToClassList("map-scale"); Add(scale);
            banner.AddToClassList("map-banner"); banner.style.display = DisplayStyle.None; Add(banner);
            legend.AddToClassList("map-legend"); legend.style.display = DisplayStyle.None; Add(legend);
            // Click the title to fold the list away and see the map underneath.
            var legendRows = new VisualElement { pickingMode = PickingMode.Ignore };
            var legendToggle = new Button { text = "Legend  -", tooltip = "Show or hide the legend" };
            showLegend = show =>
            {
                legendRows.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
                legendToggle.text = show ? "Legend  -" : "Legend  +";
            };
            legendToggle.clicked += () => showLegend(legendRows.resolvedStyle.display == DisplayStyle.None);
            legendToggle.AddToClassList("map-legend-title");
            legend.Add(legendToggle);
            legend.Add(legendRows);
            LegendRow(legendRows, "Planned route", PlannedRoute, "swatch-line");
            LegendRow(legendRows, "Approved detour", ApprovedDetour, "swatch-line");
            LegendRow(legendRows, "Trip to or from a charger", ChargingTrip, "swatch-line");
            LegendRow(legendRows, "Charger", Glyph.ChargerColor, "swatch-square");
            LegendRow(legendRows, "Road closed", RoadClosed, "swatch-band");
            proposedLegend = LegendRow(legendRows, "Proposed route", Proposed, "swatch-line");
            notClearedLegend = LegendRow(legendRows, "Not cleared for the shuttle", RoadNotCleared, "swatch-thin");
            LegendRow(legendRows, "Other roads", OtherRoads, "swatch-thin");
            LegendRow(legendRows, "Shuttle", ShuttlePalette[0], "swatch-square");
            LegendRow(legendRows, "Incident", RoadClosed, "swatch-dot");
            LegendRow(legendRows, "No recent update", Frozen, "swatch-square");
            LegendRow(legendRows, "Shuttle's stop (its colour)", StopColor, "swatch-ring");
            LegendRow(legendRows, "Depot", DepotColor, "swatch-dot");
            busStopLegend = LegendRow(legendRows, "Other bus stop", BusStopColor, "swatch-dot");
            var controls = new VisualElement { pickingMode = PickingMode.Ignore }; controls.AddToClassList("map-controls");
            var compass = new VisualElement { pickingMode = PickingMode.Ignore, tooltip = "The map is always north-up" }; compass.AddToClassList("map-compass");
            compass.Add(new Glyph(GlyphKind.Compass, RoadClosed, 14)); compass.Add(new Label("N") { pickingMode = PickingMode.Ignore }); controls.Add(compass);
            var fit = new Button(() => { pan = Vector2.zero; zoom = 1; Refresh(); }) { tooltip = "Fit the whole fleet" }; fit.AddToClassList("map-button");
            fit.Add(new Glyph(GlyphKind.Fit, DotOutline, 18)); controls.Add(fit);
            foreach (var (text, factor, tip) in new[] { ("+", 1.5f, "Zoom in"), ("-", 1 / 1.5f, "Zoom out") })
            {
                var button = new Button(() => ZoomAt(contentRect.size / 2, factor)) { text = text, tooltip = tip };
                button.AddToClassList("map-button"); button.AddToClassList("map-text-button"); controls.Add(button);
            }
            Add(controls);
            schedule.Execute(AnimationFrame).Every(16);
            drawing.generateVisualContent += Draw;
            vehicleLayer.generateVisualContent += DrawVehicles;
            busStopTimer = schedule.Execute(RequestBusStops);
            busStopTimer.Pause();
            RegisterCallback<GeometryChangedEvent>(_ => Refresh());
            RegisterCallback<AttachToPanelEvent>(_ => { if (tiles != null) tiles.TileLoaded += RefreshTiles; });
            RegisterCallback<DetachFromPanelEvent>(_ => { if (tiles != null) tiles.TileLoaded -= RefreshTiles; busStopRequest?.Cancel(); });
            RegisterCallback<WheelEvent>(e => { ZoomAt(e.localMousePosition, Mathf.Exp(-e.delta.y * 0.06f)); e.StopPropagation(); });
            // Left or middle drag pans. A left press that does not travel is a click, so it selects.
            RegisterCallback<PointerDownEvent>(e =>
            {
                if ((e.button != 0 && e.button != 2) || e.target != this) return;
                pressed = true; dragging = e.button == 2; pressStart = lastPointer = e.localPosition;
                this.CapturePointer(e.pointerId);
                e.StopPropagation();
            });
            RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!pressed) return;
                Vector2 next = e.localPosition;
                if (!dragging && Vector2.Distance(next, pressStart) < DragStartDistance) return;
                dragging = true; pan += next - lastPointer; lastPointer = next; Refresh();
            });
            RegisterCallback<PointerUpEvent>(e =>
            {
                if (!pressed || (e.button != 0 && e.button != 2)) return;
                var clicked = !dragging;
                pressed = dragging = false;
                this.ReleasePointer(e.pointerId);
                if (clicked) Click(pressStart);
            });
            RegisterCallback<PointerCaptureOutEvent>(_ => pressed = dragging = false);
            style.overflow = Overflow.Hidden;
            BuildEditing();
        }

        private void Click(Vector2 point)
        {
            if (ClickEditor(point)) return;
            if (snapshot != null)
            {
                var marker = incidentMarkers.Where(kv => snapshot.Incidents.Any(i => i.IncidentId == kv.Key && !IncidentEnded(i))).OrderBy(kv => Vector2.Distance(Project(kv.Value.Position.x, kv.Value.Position.y), point)).FirstOrDefault();
                if (marker.Key != null && Vector2.Distance(Project(marker.Value.Position.x, marker.Value.Position.y), point) <= 16)
                { IncidentSelected?.Invoke(marker.Key); return; }
                var nearest = snapshot.Vehicles.OrderBy(v => Vector2.Distance(ShownAt(v), point)).FirstOrDefault();
                if (nearest != null && Vector2.Distance(ShownAt(nearest), point) <= 18)
                { SelectedId = nearest.VehicleId; VehicleSelected?.Invoke(SelectedId); Refresh(); }
            }
        }

        // Puts the map on real Singapore. Either source may be null; with both null it stays a plain
        // diagram. With islandWhenEmpty it shows the whole island until a session is joined.
        public void UseRealMap(MapTiles basemap, TransitClient busStopSource, bool islandWhenEmpty = false)
        {
            if (panel != null && tiles != null) tiles.TileLoaded -= RefreshTiles;
            tiles = basemap; transit = busStopSource; this.islandWhenEmpty = islandWhenEmpty;
            if (panel != null && tiles != null) tiles.TileLoaded += RefreshTiles;
            Show(snapshot);
        }

        public void Show(FullSnapshot value)
        {
            if (snapshot?.SessionId != value?.SessionId)
                incidentMarkers.Clear();
            if (snapshot?.SessionId != value?.SessionId) { pan = Vector2.zero; zoom = 1; }
            UpdateTracks(value);
            snapshot = value;
            var points = new List<Vector2>();
            if (draft != null) points.AddRange(draft.FocusPoints());
            else if (snapshot != null)
            {
                points.AddRange(snapshot.Routes.SelectMany(r => r.EdgePathsM).SelectMany(p => p).Select(p => new Vector2((float)p.XM, (float)p.YM)));
                points.AddRange(snapshot.Vehicles.Select(v => new Vector2((float)v.Position.XM, (float)v.Position.YM)));
                points.AddRange(snapshot.Depots.Select(d => new Vector2((float)d.Position.XM, (float)d.Position.YM)));
            }
            else if (ShowsIslandWhenEmpty) points.AddRange(new[] { new Vector2(-25000, -15000), new Vector2(25000, 15000) });
            min = points.Count == 0 ? Vector2.zero : new Vector2(points.Min(p => p.x), points.Min(p => p.y));
            max = points.Count == 0 ? Vector2.one : new Vector2(points.Max(p => p.x), points.Max(p => p.y));
            Refresh();
        }

        // A dead backend is not a stopped fleet: say so, and show the positions as old.
        public void SetOffline(bool value)
        {
            if (offline == value) return;
            offline = value;
            banner.text = "Backend offline · positions frozen at the last report";
            banner.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
            if (value) motion.Clear();
            Refresh();
        }

        private void UpdateTracks(FullSnapshot next)
        {
            var now = Time.realtimeSinceStartup;
            if (next == null || next.SessionId != snapshot?.SessionId) motion.Clear();
            if (next == null) return;
            motion.BeginSnapshot(next.Tick, now);
            var routes = ShuttleMotion.Routes(next);
            foreach (var vehicle in next.Vehicles)
            {
                // A shuttle on a charging trip drives that trip's road, not its route
                var trip = next.ChargeBookings.FirstOrDefault(b => b.VehicleId == vehicle.VehicleId && b.Status == "ACTIVE" && b.TripPathM.Count > 1);
                routes.TryGetValue(vehicle.RouteId, out var road);
                if (trip != null) road = trip.TripPathM.Select(p => new Vector2((float)p[0], (float)p[1])).ToList();
                motion.SetTarget(vehicle.VehicleId, new Vector3((float)vehicle.Position.XM, 0, (float)vehicle.Position.YM), Quaternion.identity, now, road);
            }
        }

        private Vector2 ShownMetres(string id, Vector2 fallback)
        {
            return motion.Sample(id, Time.realtimeSinceStartup, out var position, out _) ? new Vector2(position.x, position.z) : fallback;
        }

        private Vector2 ShownAt(FleetVehicle vehicle)
        {
            var at = ShownMetres(vehicle.VehicleId, new Vector2((float)vehicle.Position.XM, (float)vehicle.Position.YM));
            return Project(at.x, at.y);
        }

        private void AnimationFrame()
        {
            if (snapshot == null || contentRect.width < 1 || contentRect.height < 1) return;
            var gliding = !offline && motion.IsGliding(Time.realtimeSinceStartup);
            if (gliding) MoveVehicleLabels();
            // An incident's ring pulses around its shuttle
            if (gliding || (!offline && snapshot.Incidents.Any(i => i.State != "RESOLVED"))) vehicleLayer.MarkDirtyRepaint();
            if (draft?.Highlighted.Count > 0) drawing.MarkDirtyRepaint();
        }

        // Labels are placed once per snapshot, as that is costly; between them a shuttle's label just follows it.
        private void MoveVehicleLabels()
        {
            foreach (var vehicle in snapshot.Vehicles)
                if (vehicleLabels.TryGetValue(vehicle.VehicleId, out var placed))
                {
                    var moved = ShownAt(vehicle) - placed.PlacedAt;
                    placed.Label.style.translate = new Translate(moved.x, moved.y);
                }
        }

        private static VisualElement LegendRow(VisualElement legend, string text, Color color, string swatchClass)
        {
            var row = new VisualElement { pickingMode = PickingMode.Ignore }; row.AddToClassList("map-legend-row");
            var key = new VisualElement { pickingMode = PickingMode.Ignore }; key.AddToClassList("map-legend-key");
            var swatch = new VisualElement { pickingMode = PickingMode.Ignore }; swatch.AddToClassList(swatchClass);
            if (swatchClass == "swatch-ring") swatch.style.borderTopColor = swatch.style.borderBottomColor = swatch.style.borderLeftColor = swatch.style.borderRightColor = color;
            else swatch.style.backgroundColor = color;
            key.Add(swatch); row.Add(key);
            row.Add(new Label(text) { pickingMode = PickingMode.Ignore });
            legend.Add(row);
            return row;
        }

        private static double NiceLength(double value)
        {
            var power = Math.Pow(10, Math.Floor(Math.Log10(value)));
            foreach (var step in new[] { 5.0, 2.0, 1.0 })
                if (step * power <= value) return step * power;
            return power;
        }

        // Each stop once, with the colours of every shuttle whose route calls there.
        private List<(RouteStop Stop, List<Color> Colors)> ServedStops()
        {
            var vehicles = snapshot.Vehicles.OrderBy(v => v.VehicleId, StringComparer.Ordinal)
                .Select((v, index) => (v.RouteId, Color: offline ? Frozen : ShuttleColor(index))).ToList();
            return snapshot.Routes.SelectMany(r => r.StopsM.Select(s => (Stop: s, r.RouteId))).GroupBy(s => s.Stop.StopId)
                .Select(g => (g.First().Stop, g.SelectMany(s => vehicles.Where(v => v.RouteId == s.RouteId).Select(v => v.Color)).ToList())).ToList();
        }

        // An LTA stop one of our routes serves is drawn in that route's colours instead of gold.
        // Route stop IDs start with the LTA stop code, such as 52449-toa-payoh-police-centre.
        private bool ServedByARoute(string code) => snapshot != null && snapshot.Routes.SelectMany(r => r.StopsM)
            .Any(s => s.StopId == code || s.StopId.StartsWith(code + "-", StringComparison.Ordinal));

        private void RefreshOverlays()
        {
            legend.style.display = islandWhenEmpty ? DisplayStyle.Flex : DisplayStyle.None;
            busStopLegend.style.display = BusStopsShown && busStops.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            proposedLegend.style.display = notClearedLegend.style.display = draft != null ? DisplayStyle.Flex : DisplayStyle.None;
            var metres = NiceLength(110 * MetresPerPoint);
            scale.text = metres >= 1000 ? $"{metres / 1000:0.#} km" : $"{metres:0.#} m";
            scale.style.width = (float)(metres / MetresPerPoint);
        }

        private bool OnRealMap => (tiles != null || transit != null) && (snapshot != null ? snapshot.Spatial == null : islandWhenEmpty);
        private bool BusStopsShown => transit != null && OnRealMap && MetresPerPoint <= BusStopMaxMetresPerPoint;

        private float FitScale => Mathf.Min(Mathf.Max(1, contentRect.width - 60) / Mathf.Max(1, max.x - min.x),
            Mathf.Max(1, contentRect.height - 60) / Mathf.Max(1, max.y - min.y));
        private float Scale => FitScale * zoom;
        private float MetresPerPoint => 1 / Scale;

        private Vector2 Project(double x, double y)
        {
            var center = (min + max) / 2;
            return new Vector2(contentRect.width / 2 + (float)((x - center.x) * Scale), contentRect.height / 2 - (float)((y - center.y) * Scale)) + pan;
        }

        private (double X, double Y) Unproject(Vector2 point)
        {
            var center = (min + max) / 2;
            return (center.x + (double)(point.x - pan.x - contentRect.width / 2) / Scale, center.y - (double)(point.y - pan.y - contentRect.height / 2) / Scale);
        }

        // Keeps the point under the pointer still.
        private void ZoomAt(Vector2 pointer, float factor)
        {
            var low = OnRealMap ? 1 / (FitScale * MaxMetresPerPoint) : 0.5f;
            var high = OnRealMap ? 1 / (FitScale * MinMetresPerPoint) : 12;
            var next = Mathf.Clamp(zoom * factor, Mathf.Min(low, 1), Mathf.Max(high, 1));
            var fromCenter = pointer - contentRect.size / 2;
            pan = fromCenter - (fromCenter - pan) * (next / zoom);
            zoom = next;
            Refresh();
        }

        private void Refresh()
        {
            RefreshTiles();
            RefreshLabels();
            NoteViewChanged();
            RefreshCredit();
            RefreshOverlays();
            drawing.MarkDirtyRepaint();
            vehicleLayer.MarkDirtyRepaint();
        }

        private void RefreshLabels()
        {
            labels.Clear();
            vehicleLabels.Clear();
            placedLabels.Clear();
            busBoxes.Clear();
            // Shuttles matter most: a stop or depot name that would cover one waits until it has passed
            if (snapshot != null)
                foreach (var vehicle in snapshot.Vehicles) { var at = ShownAt(vehicle); busBoxes.Add(new Rect(at.x - BusBoxSize / 2, at.y - BusBoxSize / 2, BusBoxSize, BusBoxSize)); }
            if (draft != null) PlaceDraftLabels();
            if (snapshot != null)
            {
                // The selected shuttle first; a label that would cover another is left out, as in the Qt map
                foreach (var vehicle in snapshot.Vehicles.OrderBy(v => v.VehicleId == SelectedId ? 0 : 1).ThenBy(v => v.VehicleId, StringComparer.Ordinal))
                {
                    var at = ShownAt(vehicle);
                    var label = PlaceLabel(vehicle.VehicleId + (snapshot.Incidents.Any(i => i.VehicleId == vehicle.VehicleId && i.State != "RESOLVED") ? " !" : ""), at, LabelStyle.Below, DotOutline);
                    if (label != null) vehicleLabels[vehicle.VehicleId] = (label, at);
                }
                foreach (var depot in snapshot.Depots) PlaceLabel(DepotName(depot.DepotId), Project(depot.Position.XM, depot.Position.YM), LabelStyle.Below, DepotColor);
                foreach (var charger in snapshot.Chargers.Where(c => c.Position != null)) PlaceLabel(charger.ChargerId, Project(charger.Position.XM, charger.Position.YM), LabelStyle.Below, DepotColor);
                if (draft == null) foreach (var (stop, colors) in ServedStops()) PlaceLabel(RouteDraftView.StopName(stop.StopId), Project(stop.Position.XM, stop.Position.YM), LabelStyle.Beside, colors.Count > 0 ? colors[0] : StopColor);
            }
            // Bus stop names last, so they never push a shuttle or session stop label off the map
            if (BusStopsShown && MetresPerPoint <= BusStopLabelMetresPerPoint)
                for (var i = 0; i < busStops.Count; i++)
                {
                    var at = Project(busStopPositions[i].X, busStopPositions[i].Y);
                    if (contentRect.Contains(at) && busStops[i].Name.Length > 0 && !ServedByARoute(busStops[i].Code)) PlaceLabel(busStops[i].Name, at, LabelStyle.Beside, BusStopText);
                }
        }

        private enum LabelStyle { Beside, Below }

        // Label sizes are not known until layout, so the box is an estimate from the font size.
        private static bool IncidentEnded(Incident incident) => incident.State == "RESOLVED" || incident.State == "UNRESOLVED";

        // "depot-toa-payoh-interchange" reads as "Depot: Toa Payoh Interchange"
        private static string DepotName(string depotId) => "Depot: " + string.Join(" ", depotId.Replace("depot-", "").Split('-')
            .Select(word => word.Length <= 1 ? word : char.ToUpperInvariant(word[0]) + word.Substring(1)));

        private Label PlaceLabel(string text, Vector2 point, LabelStyle placement, Color color)
        {
            var width = text.Length * 6.5f + 12;
            var box = placement == LabelStyle.Below ? new Rect(point.x - width / 2, point.y + 18, width, 20) : new Rect(point.x + 10, point.y - 10, width, 20);
            if (placedLabels.Any(other => other.Overlaps(box))) return null;
            if (placement == LabelStyle.Beside && busBoxes.Any(bus => bus.Overlaps(box))) return null;
            placedLabels.Add(box);
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.style.position = UnityEngine.UIElements.Position.Absolute;
            label.style.left = box.x; label.style.top = box.y;
            label.style.fontSize = 12; label.style.color = color;
            if (placement == LabelStyle.Below) label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.backgroundColor = new Color(1, 1, 1, 0.92f);
            label.style.paddingLeft = label.style.paddingRight = 6;
            label.style.borderTopLeftRadius = label.style.borderTopRightRadius = label.style.borderBottomLeftRadius = label.style.borderBottomRightRadius = 6;
            labels.Add(label);
            return label;
        }

        // Credits, as the OSM and LTA licences require, and why bus stops are missing if they are.
        private void RefreshCredit()
        {
            var credits = new List<string>();
            if (tiles != null && OnRealMap) credits.Add(tiles.Attribution);
            if (BusStopsShown)
            {
                if (busStops.Count > 0) credits.Add(transit.Attribution != null && transit.Attribution.Contains("LTA") ? "Bus stops: LTA DataMall" : "Bus stops: © OpenStreetMap contributors");
                else if (!string.IsNullOrEmpty(busStopNotice)) credits.Add(busStopNotice);
            }
            credit.text = string.Join(" · ", credits);
            credit.style.display = credits.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void RefreshTiles()
        {
            var used = 0;
            var shown = tiles != null && OnRealMap && contentRect.width >= 1 && contentRect.height >= 1;
            tileLayer.style.backgroundColor = shown ? new StyleColor(BasemapBackground) : new StyleColor(StyleKeyword.Null);
            if (shown)
            {
                tiles.BeginFrame();
                var ratio = panel?.scaledPixelsPerPoint ?? 1;
                var centerM = Unproject(contentRect.size / 2);
                var centre = MapGeo.ToLatLon(centerM.X, centerM.Y);
                var level = MapGeo.TileZoomFor(MetresPerPoint, centre.Latitude, ratio);
                var northWest = Unproject(Vector2.zero); var southEast = Unproject(contentRect.size);
                var first = MapGeo.TileAt(MapGeo.ToLatLon(northWest.X, northWest.Y), level);
                var last = MapGeo.TileAt(MapGeo.ToLatLon(southEast.X, southEast.Y), level);
                var count = 1 << level;
                int left = Math.Max(0, (int)Math.Floor(first.X)), right = Math.Min(count - 1, (int)Math.Floor(last.X));
                int top = Math.Max(0, (int)Math.Floor(first.Y)), bottom = Math.Min(count - 1, (int)Math.Floor(last.Y));
                if (right >= left && bottom >= top && (right - left + 1) * (bottom - top + 1) <= MaxTiles)
                {
                    // Centre tiles first, so they are the first to be downloaded
                    var middle = MapGeo.TileAt(centre, level);
                    var order = Enumerable.Range(left, right - left + 1)
                        .SelectMany(x => Enumerable.Range(top, bottom - top + 1).Select(y => (X: x, Y: y)))
                        .OrderBy(t => Math.Pow(t.X + 0.5 - middle.X, 2) + Math.Pow(t.Y + 0.5 - middle.Y, 2));
                    foreach (var tile in order)
                    {
                        var (texture, uv) = TileOrStandIn(level, tile.X, tile.Y);
                        if (texture == null) continue;
                        if (used == tileViews.Count)
                        {
                            var created = new Image { pickingMode = PickingMode.Ignore, scaleMode = ScaleMode.StretchToFill };
                            created.style.position = UnityEngine.UIElements.Position.Absolute;
                            tileViews.Add(created); tileLayer.Add(created);
                        }
                        Place(tileViews[used++], texture, uv, tile.X, tile.Y, level, ratio);
                    }
                }
            }
            for (var i = used; i < tileViews.Count; i++) tileViews[i].style.display = DisplayStyle.None;
        }

        // Until a tile arrives, the matching part of a coarser tile that is already loaded.
        private (Texture2D, Rect) TileOrStandIn(int level, int x, int y)
        {
            var texture = tiles.Tile(level, x, y);
            if (texture != null) return (texture, new Rect(0, 0, 1, 1));
            for (var up = 1; up <= ParentTileLevels && up <= level; up++)
            {
                var parent = tiles.Cached(level - up, x >> up, y >> up);
                if (parent == null) continue;
                var part = 1f / (1 << up);
                // Texture coordinates start at the bottom, tile rows at the top.
                return (parent, new Rect((x - ((x >> up) << up)) * part, 1 - (y - ((y >> up) << up) + 1) * part, part, part));
            }
            return (null, default);
        }

        private void Place(Image view, Texture2D texture, Rect uv, int x, int y, int level, float ratio)
        {
            // Corners snapped to physical pixels, so neighbouring tiles share an edge with no hairline gap
            Vector2 Corner(int cornerX, int cornerY)
            {
                var local = MapGeo.ToLocal(MapGeo.TileCorner(cornerX, cornerY, level));
                var at = Project(local.X, local.Y);
                return new Vector2(Mathf.Round(at.x * ratio) / ratio, Mathf.Round(at.y * ratio) / ratio);
            }
            var northWest = Corner(x, y); var southEast = Corner(x + 1, y + 1);
            view.image = texture; view.uv = uv;
            view.style.left = northWest.x; view.style.top = northWest.y;
            view.style.width = southEast.x - northWest.x; view.style.height = southEast.y - northWest.y;
            view.style.display = DisplayStyle.Flex;
        }

        // Bus stops are requested for the visible area once panning and zooming settle.
        private void NoteViewChanged()
        {
            if (!BusStopsShown || contentRect.width < 1 || contentRect.height < 1) { busStopTimer.Pause(); requestedScale = 0; return; }
            var centerM = Unproject(contentRect.size / 2);
            var center = new Vector2((float)centerM.X, (float)centerM.Y);
            if (center == requestedCenter && Scale == requestedScale && contentRect.size == requestedSize) return;
            requestedCenter = center; requestedScale = Scale; requestedSize = contentRect.size;
            busStopTimer.ExecuteLater(BusStopSettleMs);
        }

        private void RequestBusStops()
        {
            busStopTimer.Pause();
            if (BusStopsShown) _ = RequestBusStopsAsync();
        }

        private async Task RequestBusStopsAsync()
        {
            busStopRequest?.Cancel();
            var request = busStopRequest = new CancellationTokenSource();
            var margin = contentRect.size * BusStopAreaMargin;
            var northWestM = Unproject(-margin); var southEastM = Unproject(contentRect.size + margin);
            var northWest = MapGeo.ToLatLon(northWestM.X, northWestM.Y); var southEast = MapGeo.ToLatLon(southEastM.X, southEastM.Y);
            try
            {
                var stops = await transit.GetBusStopsAsync(southEast.Latitude, northWest.Longitude, northWest.Latitude, southEast.Longitude, request.Token);
                if (request != busStopRequest) return;
                busStops = stops;
                busStopPositions = stops.Select(s => MapGeo.ToLocal(new LatLon(s.Latitude, s.Longitude))).ToList();
                busStopNotice = null;
            }
            catch (OperationCanceledException) { return; }
            catch (TransitUnavailableException error) { if (request != busStopRequest) return; busStopNotice = error.Message; }
            catch (Exception) { if (request != busStopRequest) return; busStopNotice = "bus stops unavailable"; }
            Refresh();
        }

        private void Draw(MeshGenerationContext mesh)
        {
            var painter = mesh.painter2D;
            var realMap = OnRealMap;
            // While editing they stay under the road layer; otherwise over the lines, drawn below
            if (draft != null || snapshot == null) DrawOtherBusStops(painter);
            if (draft != null) DrawRoadLayer(painter);
            else DrawClosedRoads(painter);
            if (snapshot != null)
            {
                var closures = new HashSet<string>(snapshot.Disruptions.Where(d => d.ActiveFromTick <= snapshot.Tick && (d.ActiveUntilTick == null || snapshot.Tick < d.ActiveUntilTick)).SelectMany(d => d.AffectedEdgeIds));
                // A route is an approved detour when it was revised, or a shuttle on it is detouring
                var detouring = new HashSet<string>(snapshot.Vehicles.Where(v => v.ServiceState == "DETOURING").Select(v => v.RouteId));
                // While editing, only the route being edited: the rest of the fleet would hide the roads
                foreach (var route in snapshot.Routes.Where(r => draft == null || r.RouteId == draft.Context.RouteId))
                {
                    var routeColor = draft != null ? CommittedInEdit : !realMap ? new Color(0.4f, 0.6f, 0.7f) : route.Revision > 1 || detouring.Contains(route.RouteId) ? ApprovedDetour : PlannedRoute;
                    for (var i = 0; i < route.EdgePathsM.Count; i++)
                    {
                        var path = route.EdgePathsM[i].Select(p => Project(p.XM, p.YM)).ToArray();
                        if (i < route.DirectedEdgeIds.Count && closures.Contains(route.DirectedEdgeIds[i]))
                        {
                            Line(painter, path, new Color(RoadClosed.r, RoadClosed.g, RoadClosed.b, 0.40f), 28);
                            Line(painter, path, RoadClosed, 10);
                        }
                        else Line(painter, path, routeColor, 6);
                    }
                }
                // Where a shuttle is driving for a charge: its path is not a route, so it would otherwise be invisible
                foreach (var booking in snapshot.ChargeBookings.Where(b => b.Status == "ACTIVE" && b.TripPathM.Count > 1))
                {
                    var trip = booking.TripPathM.Select(p => Project(p[0], p[1])).ToArray();
                    Line(painter, trip, Color.white, 10);
                    Line(painter, trip, ChargingTrip, 6);
                }
                if (draft == null) DrawOtherBusStops(painter);
                // A stop wears the colours of the shuttles that serve it: an outer ring for the first,
                // an inner one for a second where two share it
                if (draft != null) DrawProposal(painter);
                foreach (var (stop, colors) in ServedStops().Where(s => draft == null))
                {
                    var at = Project(stop.Position.XM, stop.Position.YM);
                    Dot(painter, at, colors.Count > 0 ? colors[0] : StopColor, 9, false);
                    if (colors.Count > 1) Dot(painter, at, colors[1], 6, false);
                    Dot(painter, at, Color.white, 3, false);
                }
                if (draft != null) DrawPins(painter);
                foreach (var depot in snapshot.Depots)
                {
                    var at = Project(depot.Position.XM, depot.Position.YM);
                    Dot(painter, at, DepotColor, 9, false);
                }
                foreach (var charger in snapshot.Chargers.Where(c => c.Position != null))
                    Glyph.DrawCharger(painter, Project(charger.Position.XM, charger.Position.YM), 26);
                foreach (var item in incidentMarkers)
                {
                    var active = snapshot.Incidents.FirstOrDefault(i => i.IncidentId == item.Key);
                    // A finished incident leaves the map; its record stays in the Incident Monitor
                    if (active == null || IncidentEnded(active)) continue;
                    var at = Project(item.Value.Position.x, item.Value.Position.y);
                    var color = item.Value.Kind == "ROAD_ACCIDENT" ? ApprovedDetour : RoadClosed;
                    // Network incidents can occur off the shuttle's route: render
                    // the incident edge itself, not only route closure overlays.
                    if (item.Value.Kind == "ROAD_BLOCK" &&
                        closures.Contains(item.Value.EdgeId) &&
                        Vector2.Distance(item.Value.Start, item.Value.End) > 0.01f)
                    {
                        var segment = new[] { Project(item.Value.Start.x, item.Value.Start.y),
                                              Project(item.Value.End.x, item.Value.End.y) };
                        Line(painter, segment, new Color(RoadClosed.r, RoadClosed.g, RoadClosed.b, 0.35f), 28);
                        Line(painter, segment, RoadClosed, 10);
                    }
                    Dot(painter, at, color, 12);
                    Dot(painter, at, Color.white, 3, false);
                }
            }
        }

        private void DrawVehicles(MeshGenerationContext mesh)
        {
            if (snapshot == null) return;
            var painter = mesh.painter2D;
            var ordered = snapshot.Vehicles.OrderBy(v => v.VehicleId, StringComparer.Ordinal).ToList();
            // An incident pulses, unless the position is too old to mean anything
            var pulse = 0.5f + 0.5f * Mathf.Sin(Time.realtimeSinceStartup * 4);
            for (var i = 0; i < ordered.Count; i++)
            {
                var vehicle = ordered[i]; var at = ShownAt(vehicle);
                if (!offline && snapshot.Incidents.Any(n => n.VehicleId == vehicle.VehicleId && n.State != "RESOLVED"))
                {
                    painter.strokeColor = new Color(RoadClosed.r, RoadClosed.g, RoadClosed.b, 1 - 0.6f * pulse); painter.lineWidth = 3;
                    painter.BeginPath(); painter.Arc(at, 10 + 8 * pulse, 0, 360); painter.Stroke();
                }
                if (vehicle.VehicleId == SelectedId) Dot(painter, at, new Color(0.47f, 0.8f, 0.82f, 0.7f), 22, false);
                Glyph.DrawBus(painter, at, 30, offline ? Frozen : ShuttleColor(i));
            }
        }

        private void DrawOtherBusStops(Painter2D painter)
        {
            if (!BusStopsShown) return;
            for (var i = 0; i < busStopPositions.Count; i++)
            {
                var at = Project(busStopPositions[i].X, busStopPositions[i].Y);
                if (contentRect.Contains(at) && !ServedByARoute(busStops[i].Code)) Dot(painter, at, BusStopColor, 5);
            }
        }

        private static void Line(Painter2D painter, Vector2[] points, Color color, float width)
        {
            if (points.Length < 2) return;
            painter.strokeColor = color; painter.lineWidth = width; painter.BeginPath(); painter.MoveTo(points[0]);
            foreach (var point in points.Skip(1)) painter.LineTo(point);
            painter.Stroke();
        }
        // The outline keeps pale dots visible on the light basemap as well as the dark diagram.
        private static void Dot(Painter2D painter, Vector2 point, Color color, float radius, bool outlined = true)
        {
            painter.fillColor = color; painter.BeginPath(); painter.Arc(point, radius, 0, 360); painter.Fill();
            if (!outlined) return;
            painter.strokeColor = DotOutline; painter.lineWidth = 1; painter.Stroke();
        }
    }
}
