using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.Session;
using Formula9.World;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Formula9.UI
{
    // Persistent controls survive the one-second poll, including the reason being typed and
    // the button under the pointer. Read models always come from the accepted live store.
    public sealed class TrainingWorkspace
    {
        private readonly TrainingApi api;
        private readonly SessionController session;
        private readonly WorldView world;
        private readonly Action<Task> run;
        private readonly List<VisualElement> pages = new List<VisualElement>();
        private readonly Dictionary<string, int> pageIndex = new Dictionary<string, int>();
        private readonly List<CommandOutcome> ledger = new List<CommandOutcome>();
        private readonly DropdownField shuttles, incidents, candidates;
        private readonly Dictionary<string, string> shuttleByChoice = new Dictionary<string, string>();
        private readonly VisualElement routePending, routePendingItems;
        private readonly Label routePendingTitle;
        private readonly TextField routeReason, incidentReason;
        private readonly Label routeProblem, routeVerdict, routeAssumptions, routeApplyNote, routeOutcomeHeadline, routeOutcomeNotes, incidentData, incidentReport, selection;
        private readonly Button routeUndo, routeRedo, routeClear, routeSuggest, routeShowClosure, routeCameras, fleetCameras, approve, routeStart, routeWatch, previewIncident, approveIncident, rerouteIncident;
        private readonly FleetMap evidenceMap;
        private readonly VisualElement routeStages, routeWaypoints, routeIssues, routeMetrics, routeDecide, routeFeedback, routeApply, routeOutcomePanel, routeOutcomeRows, fleetTable;
        private readonly List<VisualElement> operationalViews;
        private readonly Label evidenceStatus;
        private readonly ChargingPage charging;
        private readonly ReportsPage reports;
        private readonly IncidentReportsTab incidentReports;
        private readonly Button sessionToggle;
        private readonly VisualElement sessionCard;
        private bool? sessionToolsOpen;
        private string sessionToolsState;
        private readonly VisualElement incidentTable, candidateTable;
        private readonly List<List<RouteWaypoint>> draftHistory = new List<List<RouteWaypoint>> { new List<RouteWaypoint>() };
        private List<RouteWaypoint> draft = new List<RouteWaypoint>();
        private int historyIndex;
        private long draftRevision, checkedRevision = -1, requestGeneration, incidentGeneration;
        private string dependencies, boundary, incidentBoundary, ledgerSession;
        // The shuttle Route Planning is for, the route it drives now, the incident its detour would
        // answer, and the shuttles that were waiting for a decision at the last snapshot.
        private string selectedVehicle, route, selectedDetour, pendingSeen, pendingStrip;
        private bool routeBusy, incidentBusy, reloadContext, previewQueued, suggesting, outcomeBusy;
        private float acceptedAt, outcomeAt;
        private long outcomeSequence = -1;
        private RouteEditorContext context;
        private RouteEditPreview preview;
        private RouteDraftView draftView;
        private RouteOutcome outcome;
        private readonly IVisualElementScheduledItem previewTimer;
        private const long PreviewSettleMs = 120;
        private const float OutcomeRefreshSeconds = 2;
        private const int MaxWaypoints = 16;
        private DispatcherCommand incidentPreviewCommand;
        private (string Route, long BasedOn)? approvedDraft;  // route and revision of a submitted approval

        private readonly List<Button> navButtons = new List<Button>();
        public string SelectedIncident => incidents.value;
        public void SelectIncident(string incidentId)
        {
            if (string.IsNullOrEmpty(incidentId)) return;
            incidents.value = incidentId;
            ShowSection("operations");
        }
        public event Action IncidentSelected;
        private const int MaxLedgerEntries = 200;
        public event Action <string> SectionChanged;
        // A page's index by its UXML name, or -1 when the layout leaves it out
        private int Page(string name) =>pageIndex.TryGetValue(name, out var index) ? index : -1;
        private int FleetPage => Page("page-fleet");
        private int RoutePage => Page("page-route");
        private int ChargingPageIndex => Page("page-charging");
        private int ReportsPageIndex => Page("page-reports");
        private int ScenariosPageIndex => Page("page-scenarios");
        private int shownPage;
        private readonly VisualElement incidentDecisions;

        public TrainingWorkspace(VisualElement root, TrainingApi api, SessionController session, WorldView world,
            Action<Task> run, Action<string> notify)
        {
            this.api = api; this.session = session; this.world = world; this.run = run;
            // The pages are laid out in UI/Pages/*.uxml; this binds their controls by name.
            foreach (var (pageName, navName) in new[] { ("page-fleet", "nav-fleet"), ("page-route", "nav-route"), ("page-charging", "nav-charging"), ("page-incident", "nav-incident"), ("page-reports", "nav-reports"), ("page-scenarios", null) })
            {
                // A page the layout leaves out gets no index: its lookups give -1 and it is never shown
                var page = root.Q(pageName);
                if (page == null) continue;
                var index = pages.Count;
                pages.Add(page);
                pageIndex[pageName] = index;
                var button = navName == null? null : root.Q<Button>(navName);
                navButtons.Add(button);
                if (button != null) button.clicked+= () => {ShowPage(index); Render();};
            }
            selection = root.Q<Label>("fleet-selection");
            Bind(root, "follow-selected", () => world.FollowSelected());
            fleetCameras = Bind(root, "fleet-cameras", ToggleSelectedCameras);
            Bind(root, "camera-overview", () => world.Overview());

            shuttles = root.Q<DropdownField>("shuttles");
            shuttles.RegisterValueChangedCallback(change => { if (shuttleByChoice.TryGetValue(change.newValue ?? "", out var id)) ChooseShuttle(id); });
            routePending = root.Q("route-pending");
            routePendingTitle = root.Q<Label>("route-pending-title");
            routePendingItems = root.Q("route-pending-items");
            Bind(root, "refresh", RequestContext);
            routeCameras = Bind(root, "route-cameras", ToggleCameras);
            routeStages = root.Q("route-stages");
            foreach (var stage in RoutePresenter.Stages) { var chip = new Label(stage); chip.AddToClassList("route-stage"); routeStages.Add(chip); }
            routeProblem = root.Q<Label>("route-problem");
            routeVerdict = root.Q<Label>("route-verdict");
            routeAssumptions = root.Q<Label>("route-assumptions");
            routeApplyNote = root.Q<Label>("route-apply-note");
            routeOutcomeHeadline = root.Q<Label>("route-outcome-headline");
            routeOutcomeNotes = root.Q<Label>("route-outcome-notes");
            routeWaypoints = root.Q("route-waypoints");
            routeIssues = root.Q("route-issues");
            routeMetrics = root.Q("route-metrics");
            routeDecide = root.Q("route-decide");
            routeFeedback = root.Q("route-feedback");
            routeApply = root.Q("route-apply");
            routeOutcomePanel = root.Q("route-outcome");
            routeOutcomeRows = root.Q("route-outcome-rows");
            fleetTable = Table(root.Q("route-fleet-host"), "", "Shuttle ID", "State", "Route / revision", "Battery", "Provenance");
            Tabs(root, ("route-tab-editor", "route-editor-panel"), ("route-tab-fleet", "route-fleet-panel"));
            Tabs(root, ("reports-tab-log", "reports-log-panel"), ("reports-tab-commands", "reports-commands-panel"), ("reports-tab-incidents", "reports-incidents-panel"));
            operationalViews = new List<VisualElement> { root.Q("view-bar"), root.Q("world") };
            routeUndo = Bind(root, "route-undo", () => RestoreDraft(-1));
            routeRedo = Bind(root, "route-redo", () => RestoreDraft(1));
            routeClear = Bind(root, "route-cancel", () => { if (!session.HasPendingCommand) { routeReason.value = ""; SetDraft(new List<RouteWaypoint>()); } });
            routeSuggest = Bind(root, "route-suggest", SuggestDetour);
            routeShowClosure = Bind(root, "route-show-closure", ShowClosure);
            routeReason = root.Q<TextField>("route-reason");
            routeReason.RegisterValueChangedCallback(_ => UpdateActions());
            approve = Bind(root, "route-approve", () => run(ApproveRoute()));
            routeStart = Bind(root, "route-start", () => run(StartAndWatch()));
            routeWatch = Bind(root, "route-watch", WatchRoute);
            previewTimer = root.schedule.Execute(() => run(PreviewRoute()));
            previewTimer.Pause();

            charging = new ChargingPage(root, api, session, run, notify, () => Current);
            sessionCard = root.Q("content-card");
            sessionToggle = root.Q<Button>("session-toggle");
            sessionToggle.clicked += ToggleSessionTools;

            incidentDecisions = root.Q("incident-decisions");
            incidents = root.Q<DropdownField>("incidents");
            incidents.RegisterValueChangedCallback(_ => { ClearIncidentPreview(); Render(); IncidentSelected?.Invoke(); });
            incidentData = root.Q<Label>("incident-data");
            incidentTable = Table(root.Q("incident-table-host"), "", "Incident ID", "Shuttle ID", "Reaches it", "State", "Decided", "Detected tick", "Options");
            candidateTable = Table(root.Q("candidate-table-host"), "", "Candidate", "Shuttle", "Action", "Validation", "Expires tick", "Assumptions");
            Bind(root, "incident-focus", FocusIncident);
            rerouteIncident = Bind(root, "incident-reroute", () =>
            {
                var vehicle = RerouteVehicle(session.Store.Snapshot, incidents.value);
                if (vehicle != null) DecideFor(vehicle, incidents.value);
            });
            candidates = root.Q<DropdownField>("candidates");
            candidates.RegisterValueChangedCallback(_ => { ClearIncidentPreview(); UpdateActions(); });
            incidentReason = root.Q<TextField>("incident-reason");
            incidentReason.RegisterValueChangedCallback(_ => { ClearIncidentPreview(); UpdateActions(); });
            previewIncident = Bind(root, "incident-preview", () => run(PreviewIncident()));
            approveIncident = Bind(root, "incident-approve", () => run(ApproveIncident()));
            incidentReport = root.Q<Label>("incident-report");
            evidenceStatus = root.Q<Label>("evidence-status");
            evidenceMap = root.Q<FleetMap>("incident-evidence-map");
            world.UseRealMap(evidenceMap);

            reports = new ReportsPage(root);
            incidentReports = new IncidentReportsTab(root, api, session, world, run);
            session.SnapshotAccepted += (_, __) => { acceptedAt = Time.realtimeSinceStartup; Render(); };
            session.EventsAccepted += _ => RenderReports();
            session.AvailabilityChanged += _ => { if (!session.Available) { InvalidateRoute(); ClearIncidentPreview(); } Render(); };
            session.CommandLedgerChanged += outcome =>
            {
                ledger.RemoveAll(o => o.CommandId == outcome.CommandId); ledger.Add(outcome);
                if (ledger.Count > MaxLedgerEntries) ledger.RemoveAt(0);
                if (outcome.Status != "QUEUED" && outcome.Status != "PENDING_CONFIRMATION") { InvalidateRoute(); ClearIncidentPreview(); }
                RenderReports(); UpdateActions();
            };
            // The Cameras buttons depend on which shuttle is selected
            world.EntitySelected += id => { selection.text = "Selected: " + id; UpdateActions(); };
            world.RouteNodeSelected += AppendNode;
            world.RoadClicked += AddRoad;
            world.WaypointClicked += RemoveWaypoint;
            root.schedule.Execute(UpdateActions).Every(250);
            ShowPage(FleetPage);
        }

        public void SelectVehicle(string id) { world.SelectEntity(id); selection.text = "Selected: " + id; }

        // The operator picking a shuttle in the fleet table or the Fleet List. Open cameras move to it,
        // except on Route Planning to a shuttle other than the one it is for: they stay on that one.
        public void PickVehicle(string id)
        {
            if (shownPage == RoutePage && id != selectedVehicle) world.SelectEntity(id); else world.PickShuttle(id);
            selection.text = "Selected: " + id;
        }

        // Opens Route Planning on a shuttle's decision and frames the incident: from the Needs a
        // decision strip, and the Incident Monitor's re-route. Without an incident, the shuttle's
        // soonest one. This is the operator's own choice, so it replaces any draft for another shuttle.
        public void DecideFor(string vehicleId, string incidentId = null)
        {
            var s = session.Store.Snapshot;
            if (s == null || s.Vehicles.All(v => v.VehicleId != vehicleId)) return;
            if (vehicleId != selectedVehicle) SetShuttle(s, vehicleId);
            SelectVehicle(vehicleId);
            ShowPage(RoutePage);
            Render();
            var incident = s.Incidents.FirstOrDefault(i => i.IncidentId == (incidentId ?? DecisionRules.OptionsFor(s, vehicleId).FirstOrDefault()?.IncidentId));
            if (incident == null) return;
            // The 3D city frames the incident itself; the map frames its road
            if (!world.FocusIncident(incident.IncidentId) && incident.Location?.EdgeId != null) world.FocusRoads(new[] { incident.Location.EdgeId });
        }

        public void Render()
        {
            var s = session.Store.Snapshot;
            var currentBoundary = s == null ? null : $"{s.SessionId}/{s.SessionEpoch}";
            if (boundary != currentBoundary)
            {
                sessionToolsOpen = null;
                boundary = currentBoundary; dependencies = null; context = null; requestGeneration++; approvedDraft = null;
                selectedVehicle = route = selectedDetour = pendingSeen = pendingStrip = null;
                if (ledgerSession != s?.SessionId) { ledger.Clear(); ledgerSession = s?.SessionId; }
                charging.Reset();
                routeReason.value = incidentReason.value = "";
                draft = new List<RouteWaypoint>(); draftHistory.Clear(); draftHistory.Add(new List<RouteWaypoint>()); historyIndex = 0;
                preview = null; draftView = null; outcome = null; outcomeSequence = -1; suggesting = false;
                ClearIncidentPreview(); ClearEvidence(); incidentReport.text = "";
                world.SetRouteDraft(null, null, null);
            }
            var setupState = s?.Session?.Status;
            if (sessionToolsState != setupState)
            {
                if (TrainingPresenter.IsTerminalSession(setupState)) sessionToolsOpen = null;
                sessionToolsState = setupState;
            }
            FitSessionControls();
            incidentReports.Render(s);
            if (s == null) { RenderReports(); UpdateActions(); return; }
            // An applied approval moves its route to a new revision, which finishes the draft. A queued
            // or rejected one never does, so its draft stays for another try.
            if (approvedDraft is (string approvedRoute, long basedOn) && s.Routes.Any(r => r.RouteId == approvedRoute && r.Revision > basedOn))
            {
                approvedDraft = null;
                routeReason.value = "";
                draft = new List<RouteWaypoint>(); draftHistory.Clear(); draftHistory.Add(new List<RouteWaypoint>()); historyIndex = 0;
                outcomeSequence = -1;
            }
            var pending = DecisionRules.PendingDecisions(s);
            FollowPending(s, pending);
            RenderShuttles(s, pending);
            var changed = DecisionRules.RouteDependencies(s, route);
            if (changed != dependencies)
            {
                dependencies = changed; context = null; InvalidateRoute();
                RequestContext();
            }
            RenderExercise(s);
            RefreshOutcome(s);
            Rows(fleetTable, TrainingPresenter.Fleet(s, SelectedIncident).Select(r => new[] { r.ShuttleId, r.State, r.Route, r.Battery, r.Provenance }));
            foreach (var row in fleetTable.Children()) { var id = ((Label)row[0]).text; row.RegisterCallback<ClickEvent>(_ => PickVehicle(id)); }
            charging.Render(s);
            Choices(incidents, s.Incidents.Select(i => i.IncidentId));
            var selectedIncident = s.Incidents.FirstOrDefault(i => i.IncidentId == incidents.value);
            var newIncidentBoundary = selectedIncident == null ? null : $"{boundary}/{selectedIncident.IncidentId}/{selectedIncident.AggregateRevision}/" +
                string.Join(";", s.Candidates.Where(c => c.IncidentId == selectedIncident.IncidentId).Select(c => $"{c.CandidateId}:{c.Revision}:{c.Validation}"));
            if (incidentBoundary != newIncidentBoundary) { incidentBoundary = newIncidentBoundary; ClearIncidentPreview(); ClearEvidence(); incidentReport.text = ""; }
            Choices(candidates, selectedIncident?.CandidateIds ?? Enumerable.Empty<string>());
            var hasOptions = selectedIncident != null && selectedIncident.CandidateIds.Count > 0;
            incidentData.text = selectedIncident == null ? "No incident selected." : TrainingPresenter.IncidentSummary(selectedIncident, hasOptions);
            incidentDecisions.style.display = s.Incidents.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            rerouteIncident.style.display = RerouteVehicle(s, selectedIncident?.IncidentId) != null ? DisplayStyle.Flex : DisplayStyle.None;
            candidates.style.display = hasOptions ? DisplayStyle.Flex : DisplayStyle.None;
            incidentReason.style.display = hasOptions ? DisplayStyle.Flex : DisplayStyle.None;
            previewIncident.style.display = hasOptions ? DisplayStyle.Flex : DisplayStyle.None;
            approveIncident.style.display = hasOptions ? DisplayStyle.Flex : DisplayStyle.None;
            Rows(incidentTable, s.Incidents.OrderBy(i => i.IncidentId, StringComparer.Ordinal).Select(i => new[] { i.IncidentId, i.VehicleId ?? "—", IncidentRecord.Reach(i.Exposures, 2) ?? "—", i.State, TrainingPresenter.Responses(i) ?? "—", i.DetectedTick.ToString(), string.Join(", ", i.CandidateIds) }));
            foreach (var row in incidentTable.Children()) { var id = ((Label)row[0]).text; row.RegisterCallback<ClickEvent>(_ => incidents.value = id); }
            Rows(candidateTable, s.Candidates.Where(c => c.IncidentId == incidents.value).Select(c => new[] { c.CandidateId, c.VehicleId ?? selectedIncident?.VehicleId ?? "—", TrainingPresenter.ActionLabel(c.Action), c.Validation, c.ExpiresAtTick.ToString(), string.Join("; ", c.Assumptions) }));
            RenderReports(); UpdateActions();
        }

        // A row of tab buttons, each showing its own panel; the first starts selected.
        private static void Tabs(VisualElement root, params (string Button, string Panel)[] tabs)
        {
            void Select(string chosen)
            {
                foreach (var (button, panel) in tabs)
                {
                    root.Q<Button>(button).EnableInClassList("tab-item-selected", button == chosen);
                    root.Q(panel).style.display = button == chosen ? DisplayStyle.Flex : DisplayStyle.None;
                }
            }
            foreach (var (button, _) in tabs) root.Q<Button>(button).clicked += () => Select(button);
            Select(tabs[0].Button);
        }

        private bool Current => session.Available && !session.HasPendingCommand && Time.realtimeSinceStartup - acceptedAt <= 5;

        private void UpdateActions()
        {
            if (approve == null) return;
            var s = session.Store.Snapshot;
            var edit = Editable(s);
            routeSuggest.SetEnabled(edit && !routeBusy);
            routeUndo.SetEnabled(edit && historyIndex > 0);
            routeRedo.SetEnabled(edit && historyIndex + 1 < draftHistory.Count);
            routeClear.SetEnabled(edit && (draft.Count > 0 || preview != null));
            approve.SetEnabled(edit && !routeBusy && preview != null && preview.Approvable && DecisionRules.RoutePreviewMatches(s, selectedVehicle, preview) && !string.IsNullOrWhiteSpace(routeReason.value));
            routeApplyNote.text = RoutePresenter.ApprovalNote(s, preview, routeReason.value);
            // Any shuttle can be picked, even with others waiting: the operator decides in which order
            shuttles.SetEnabled(!session.HasPendingCommand && !routeBusy);
            var routeClosed = s != null && TrainingPresenter.AssignedRouteIsClosed(s);
            routeStart.style.display = s?.Session.Status == "READY" && !routeClosed ? DisplayStyle.Flex : DisplayStyle.None;
            routeStart.SetEnabled(Current && s?.Session.Status == "READY" && !routeClosed);
            routeWatch.style.display = s != null && s.Session.Status != "READY" && outcome?.VehicleId != null ? DisplayStyle.Flex : DisplayStyle.None;
            routeCameras.SetEnabled(world.CamerasShown || (world.HasCity && RouteVehicle() != null));
            routeCameras.text = world.CamerasShown ? "Hide cameras" : "Cameras";
            routeCameras.EnableInClassList("segment-selected", world.CamerasShown);
            fleetCameras.SetEnabled(world.CamerasShown || (world.HasCity && s?.Vehicles.Any(v => v.VehicleId == world.SelectedId) == true));
            fleetCameras.text = world.CamerasShown ? "Hide cameras" : "Cameras";
            fleetCameras.EnableInClassList("segment-selected", world.CamerasShown);
            charging.UpdateActions();
            var candidate = s?.Candidates.FirstOrDefault(c => c.CandidateId == candidates.value && c.IncidentId == incidents.value);
            var decision = Current && !incidentBusy && s?.Session.Status == "RUNNING" && candidate?.Validation == "PASS" && candidate.ExpiresAtTick >= s.Tick &&
                s.Incidents.Any(i => i.IncidentId == incidents.value && i.State == "OPTIONS_READY") && !string.IsNullOrWhiteSpace(incidentReason.value);
            previewIncident.SetEnabled(decision);
            approveIncident.SetEnabled(decision && incidentPreviewCommand != null);
            world.RouteEditing = edit;
            // A decision or a lost connection withdraws the verdict but leaves the draft: ask once more.
            if (edit && draft.Count > 0 && preview == null && !routeBusy && approvedDraft == null && checkedRevision != draftRevision && !previewTimer.isActive) SchedulePreview();
            if (draftView != null && draftView.Editable != (edit && shownPage == RoutePage)) ShowDraftInViews();
        }

        private bool Editable(FullSnapshot s) => Current && context != null && (s?.Session.Status == "READY" || DecisionRules.ReroutableRoute(s, selectedVehicle) == context.RouteId);

        // Before the start every route can be drafted; running, the route of a shuttle with a detour on offer.
        private bool Drafting(FullSnapshot s) => s?.Session.Status == "READY" || DecisionRules.DetourCandidate(s, selectedVehicle) != null;

        // Nothing the operator would lose: no draft, no reason typed, nothing on its way to the backend.
        private bool Idle => draft.Count == 0 && !suggesting && preview == null && approvedDraft == null &&
                             string.IsNullOrWhiteSpace(routeReason.value) && !session.HasPendingCommand;

        // Route Planning is for one shuttle at a time. It keeps to the one the operator picked, and only
        // moves by itself, to the shuttle that reaches its incident soonest, when the shuttles waiting for
        // a decision change while the panel is idle and its own shuttle needs none. It never changes the
        // page, the 3D view or the cameras, and says nothing: the incident alert does.
        private void FollowPending(FullSnapshot s, List<PendingDecision> decisions)
        {
            var pending = decisions.Select(d => d.VehicleId).Distinct().ToList();
            var seen = string.Join(";", pending.OrderBy(v => v, StringComparer.Ordinal));
            var changed = seen != pendingSeen;
            pendingSeen = seen;
            if (s.Vehicles.All(v => v.VehicleId != selectedVehicle))
            {
                var fallback = DefaultShuttle(s, pending);
                if (fallback != selectedVehicle) SetShuttle(s, fallback);
                return;
            }

            // The shuttle drives another route now: the detour it was given
            var current = s.Vehicles.First(v => v.VehicleId == selectedVehicle).RouteId;
            if (current != route) { route = current; ClearDraft(); }
            // Its detour was decided or withdrawn, or a newer incident's replaced it: the draft answers nothing now
            var detour = DecisionRules.DetourCandidate(s, selectedVehicle)?.IncidentId;
            var ended = false;
            if (detour != selectedDetour) { ended = detour == null && selectedDetour != null; selectedDetour = detour; ClearDraft(); }

            if (changed && pending.Count > 0 && !pending.Contains(selectedVehicle) && Idle) { SetShuttle(s, pending[0]); return; }
            // Decided: a route is edited on the map; running, it is watched in 3D, or through its cameras
            if (ended && shownPage == RoutePage && !world.CamerasShown) world.WatchShuttle(selectedVehicle);
        }

        // With nothing picked yet: the shuttle that reaches its incident soonest, else one whose route
        // is closed (the exercise before the start), else the first.
        private static string DefaultShuttle(FullSnapshot s, List<string> pending) =>
            pending.FirstOrDefault() ?? TrainingPresenter.ClosedRouteVehicles(s).OrderBy(v => v, StringComparer.Ordinal).FirstOrDefault() ??
            s.Vehicles.Select(v => v.VehicleId).OrderBy(v => v, StringComparer.Ordinal).FirstOrDefault();

        // Points the panel at a shuttle and the route it drives now, with a fresh draft.
        private void SetShuttle(FullSnapshot s, string vehicleId)
        {
            selectedVehicle = vehicleId;
            route = s?.Vehicles.FirstOrDefault(v => v.VehicleId == vehicleId)?.RouteId;
            selectedDetour = DecisionRules.DetourCandidate(s, vehicleId)?.IncidentId;
            ClearDraft();
        }

        private void ClearDraft()
        {
            ResetDraft();
            RenderWaypoints();
            dependencies = null;
        }

        // The operator choosing a shuttle in the picker. Open cameras move to it; Render loads its roads.
        private void ChooseShuttle(string vehicleId)
        {
            var s = session.Store.Snapshot;
            if (s == null || vehicleId == selectedVehicle) return;
            SetShuttle(s, vehicleId);
            if (world.CamerasShown) ShowRouteCameras();
            Render();
        }

        // The picker, one entry per shuttle, and the strip of those waiting for a decision. Both keep
        // their elements while nothing changes, so a click is never split across two of them.
        private void RenderShuttles(FullSnapshot s, List<PendingDecision> decisions)
        {
            var pending = new HashSet<string>(decisions.Select(d => d.VehicleId));
            var vehicles = s.Vehicles.OrderBy(v => v.VehicleId, StringComparer.Ordinal).ToList();
            var labels = vehicles.Select(v => RoutePresenter.ShuttleChoice(v, pending.Contains(v.VehicleId))).ToList();
            if (!shuttles.choices.SequenceEqual(labels))
            {
                shuttleByChoice.Clear();
                for (var i = 0; i < labels.Count; i++) shuttleByChoice[labels[i]] = vehicles[i].VehicleId;
                shuttles.choices = labels;
            }
            var selected = vehicles.FindIndex(v => v.VehicleId == selectedVehicle);
            var label = selected < 0 ? "" : labels[selected];
            if (shuttles.value != label) shuttles.SetValueWithoutNotify(label);

            // The soonest decision of each shuttle, soonest first
            var first = decisions.GroupBy(d => d.VehicleId).Select(g => g.First()).ToList();
            routePending.style.display = first.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            routePendingTitle.text = RoutePresenter.PendingTitle(first.Count);
            var strip = string.Join(";", first.Select(d => d.VehicleId));
            if (strip != pendingStrip)
            {
                pendingStrip = strip;
                routePendingItems.Clear();
                foreach (var decision in first)
                {
                    var vehicle = decision.VehicleId;
                    var item = new Button(() => DecideFor(vehicle)) { userData = vehicle };
                    item.AddToClassList("route-pending-item");
                    routePendingItems.Add(item);
                }
            }
            for (var i = 0; i < first.Count; i++)
            {
                var item = (Button)routePendingItems[i];
                item.text = RoutePresenter.PendingChip(first[i]);
                item.tooltip = first[i].Incident.Location?.RoadName;
                item.EnableInClassList("route-pending-item-selected", first[i].VehicleId == selectedVehicle);
            }
        }

        // The shuttle the Incident Monitor's re-route opens: the soonest whose detour answers this incident.
        private static string RerouteVehicle(FullSnapshot s, string incidentId) => incidentId == null ? null :
            DecisionRules.PendingVehicles(s).FirstOrDefault(v => DecisionRules.DetourCandidate(s, v)?.IncidentId == incidentId);

        private void InvalidateRoute()
        {
            draftRevision++; preview = null; suggesting = false;
            RenderFeedback();
            ShowDraftInViews();
            UpdateActions();
        }

        // The proposal, as it stands, in the map and the 3D city.
        private void ShowDraftInViews()
        {
            if (context == null) { draftView = null; world.SetRouteDraft(null, null, null); return; }
            if (draftView == null || draftView.Context != context) draftView = new RouteDraftView(context);
            // While a change is being checked the last proposal stays drawn; only its verdict is withdrawn.
            if (preview != null || draft.Count == 0) draftView.Preview = preview;
            draftView.Waypoints = draft;
            draftView.Highlighted = RoutePresenter.Highlighted(preview);
            var s = session.Store.Snapshot;
            draftView.Editable = Editable(s) && shownPage == RoutePage;
            world.SetRouteDraft(context, draft.Where(w => w.EdgeId == null).Select(w => w.NodeId).ToList(), preview, draftView, shownPage == RoutePage && Drafting(s));
        }

        private void ResetDraft()
        {
            context = null; routeReason.value = ""; draft = new List<RouteWaypoint>();
            draftHistory.Clear(); draftHistory.Add(new List<RouteWaypoint>()); historyIndex = 0;
            outcome = null; outcomeSequence = -1;
            InvalidateRoute();
        }

        private void RequestContext()
        {
            requestGeneration++; reloadContext = true;
            context = null; InvalidateRoute();
            if (!routeBusy) run(LoadContext());
        }

        private async Task LoadContext()
        {
            var s = session.Store.Snapshot;
            if (s == null || string.IsNullOrEmpty(route) || routeBusy) return;
            reloadContext = false;
            var generation = ++requestGeneration;
            var asked = route;
            var stamp = DecisionRules.RouteDependencies(s, asked);
            context = null; InvalidateRoute(); routeBusy = true; UpdateActions();
            try
            {
                var result = await api.GetRouteEditorAsync(s.SessionId, asked);
                if (generation != requestGeneration || stamp != DecisionRules.RouteDependencies(session.Store.Snapshot, route)) return;
                if (result.SessionEpoch != s.SessionEpoch || result.SceneRevision != s.SceneRevision || result.RouteRevision != s.Routes.Single(r => r.RouteId == asked).Revision)
                    throw new ContractException("Route graph is stale; refresh it.");
                context = result; dependencies = stamp;
                routeAssumptions.text = string.Join("\n", result.Assumptions);
                ShowDraftInViews();
                RenderExercise(session.Store.Snapshot);
                // A closure or revision moved on while a draft was open: check it again.
                if (draft.Count > 0) SchedulePreview();
            }
            // Only some routes have roads to edit. Choosing another is not a failure, so say so here,
            // as the Qt editor does, rather than raising it.
            catch (BackendErrorException error) when (error.Error.Code == "INVALID_REFERENCE")
            {
                if (generation == requestGeneration) routeProblem.text = $"{asked} can't be edited in this scenario: {error.Error.Message}.";
            }
            finally { routeBusy = false; UpdateActions(); if (reloadContext) run(LoadContext()); }
        }

        private void SchedulePreview()
        {
            previewTimer.ExecuteLater(PreviewSettleMs);
        }

        // Each change to the draft asks the backend again, so the proposal appears as it is made.
        private async Task PreviewRoute()
        {
            previewTimer.Pause();
            if (routeBusy) { previewQueued = true; return; }
            if (!Current || context == null || (draft.Count == 0 && !suggesting)) return;
            var picksRoads = draftView?.PicksRoads == true;
            var stamp = dependencies; var revision = draftRevision; var generation = requestGeneration;
            checkedRevision = revision;
            routeBusy = true; RenderFeedback(); UpdateActions();
            try
            {
                var request = new RouteEditRequest { RouteId = context.RouteId, ExpectedRouteRevision = context.RouteRevision, DraftRevision = revision };
                if (picksRoads) request.WaypointEdgeIds = draft.Select(w => w.EdgeId).ToList();
                else request.ViaNodeIds = draft.Select(w => w.NodeId).ToList();
                var result = await api.PreviewRouteAsync(context.SessionId, request);
                if (generation != requestGeneration || revision != draftRevision || stamp != DecisionRules.RouteDependencies(session.Store.Snapshot, route)) return;
                preview = result;
                ShowDraftInViews();
                // The verdict is what the operator just asked for: bring it into view, once it is laid out
                routeFeedback.schedule.Execute(() => routeFeedback.GetFirstAncestorOfType<ScrollView>()?.ScrollTo(routeFeedback)).ExecuteLater(50);
            }
            finally
            {
                routeBusy = false; RenderFeedback(); UpdateActions();
                if (reloadContext) run(LoadContext());
                else if (previewQueued) { previewQueued = false; SchedulePreview(); }
            }
        }

        private async Task ApproveRoute()
        {
            if (!Current || preview == null) return;
            var s = session.Store.Snapshot;
            if (s.Session.Status != "READY")
            {
                // FollowPending clears the draft once a snapshot shows the shuttle decided for; a refusal keeps it.
                if (await session.SubmitAsync(DecisionRules.RerouteCommand(s, selectedVehicle, preview, routeReason.value))) await session.RefreshAsync();
                InvalidateRoute(); Render();
                return;
            }
            var approved = (preview.RouteId, preview.RouteRevision);
            if (!await session.ApproveRouteAsync(preview, routeReason.value)) return;
            // Render clears the draft once a snapshot shows the route's new revision; a refresh may
            // return before that snapshot arrives, so it cannot be checked here.
            approvedDraft = approved;
            InvalidateRoute(); await session.RefreshAsync();
            Render();
        }

        private async Task StartAndWatch()
        {
            var s = session.Store.Snapshot;
            if (!Current || s?.Session.Status != "READY") return;
            if (await session.SendControlAsync("START")) { if (!world.CamerasShown) WatchRoute(); await session.RefreshAsync(); }
        }

        // The shuttle Route Planning shows the cameras of: the one it is for.
        private string RouteVehicle() =>
            session.Store.Snapshot?.Vehicles.Any(v => v.VehicleId == selectedVehicle) == true ? selectedVehicle : null;

        // Shows the cameras of the panel's shuttle in place of the 3D view.
        private void ShowRouteCameras()
        {
            var vehicle = RouteVehicle();
            if (vehicle == null) return;
            SelectVehicle(vehicle);
            world.OpenCameras();
        }

        // The Cameras button on Route Planning: the panel's shuttle's cameras, or back to 3D.
        private void ToggleCameras()
        {
            if (world.CamerasShown) CloseCameras();
            else ShowRouteCameras();
            UpdateActions();
        }

        // The Cameras button on Fleet Overview: the selected shuttle's cameras, or back to 3D.
        private void ToggleSelectedCameras()
        {
            if (world.CamerasShown) CloseCameras();
            else if (session.Store.Snapshot?.Vehicles.Any(v => v.VehicleId == world.SelectedId) == true)
                world.OpenCameras();
            UpdateActions();
        }

        private void CloseCameras() => world.CloseCameras();

        private void WatchRoute()
        {
            var vehicle = RouteVehicle() ?? outcome?.VehicleId;
            if (vehicle == null) return;
            world.WatchShuttle(vehicle);
            selection.text = "Selected: " + vehicle;
        }

        private void AddRoad(DraftRoad road, Vector2 at)
        {
            if (!Editable(session.Store.Snapshot) || draft.Count >= MaxWaypoints) return;
            var node = context.Edges.First(e => e.EdgeId == road.EdgeId);
            // Clicking a road already in the draft does nothing: a pin on it removes it
            if (draft.Any(w => w.EdgeId == road.EdgeId)) return;
            var next = new List<RouteWaypoint>(draft) { new RouteWaypoint { EdgeId = draftView.PicksRoads ? road.EdgeId : null, NodeId = node.Target, X = at.x, Y = at.y, Road = road.Name ?? "An unnamed road" } };
            SetDraft(next);
        }

        // A point chosen in a 3D scene: a node of the curated graph.
        private void AppendNode(long id)
        {
            if (!Editable(session.Store.Snapshot) || id == context.StartNodeId || id == context.DestinationNodeId || draft.Count >= MaxWaypoints) return;
            var node = context.Nodes.FirstOrDefault(n => n.NodeId == id);
            if (node == null) return;
            var edge = draftView?.PicksRoads == true ? context.Edges.FirstOrDefault(e => e.Source == id) : null;
            SetDraft(new List<RouteWaypoint>(draft) { new RouteWaypoint { EdgeId = edge?.EdgeId, NodeId = id, X = node.X, Y = node.Y, Road = edge?.RoadName ?? "A point on the road" } });
        }

        private void RemoveWaypoint(int index)
        {
            if (!Editable(session.Store.Snapshot) || index < 0 || index >= draft.Count) return;
            var next = new List<RouteWaypoint>(draft);
            next.RemoveAt(index);
            SetDraft(next);
        }

        private void SetDraft(List<RouteWaypoint> next)
        {
            draftHistory.RemoveRange(historyIndex + 1, draftHistory.Count - historyIndex - 1);
            draftHistory.Add(next);
            if (draftHistory.Count > 100) draftHistory.RemoveAt(0);
            historyIndex = draftHistory.Count - 1;
            ApplyDraft(next);
        }

        private void ApplyDraft(List<RouteWaypoint> next)
        {
            draft = next;
            InvalidateRoute();
            RenderWaypoints();
            if (draft.Count > 0) SchedulePreview();
        }

        private void RestoreDraft(int delta)
        {
            if (session.HasPendingCommand || historyIndex + delta < 0 || historyIndex + delta >= draftHistory.Count) return;
            historyIndex += delta;
            ApplyDraft(draftHistory[historyIndex]);
        }

        private void SuggestDetour()
        {
            if (!Editable(session.Store.Snapshot) || routeBusy) return;
            SetDraft(new List<RouteWaypoint>());
            suggesting = true;
            run(PreviewRoute());
        }

        private void ShowClosure()
        {
            if (context == null) return;
            var committed = new HashSet<string>(context.OriginalDirectedEdgeIds);
            var closed = context.Edges.Where(e => e.Closed).OrderByDescending(e => committed.Contains(e.EdgeId)).Select(e => e.EdgeId).ToList();
            if (closed.Count > 0) world.FocusRoads(closed);
        }

        private void RenderExercise(FullSnapshot s)
        {
            if (routeProblem == null) return;
            routeProblem.text = RoutePresenter.Problem(context, s);
            routeShowClosure.style.display = context != null && context.Edges.Any(e => e.Closed) ? DisplayStyle.Flex : DisplayStyle.None;
            var routeClosed = s != null && TrainingPresenter.AssignedRouteIsClosed(s);
            var deciding = Drafting(s) && routeClosed;
            routeDecide.style.display = deciding ? DisplayStyle.Flex : DisplayStyle.None;
            routeFeedback.style.display = deciding ? DisplayStyle.Flex : DisplayStyle.None;
            routeApply.style.display = deciding ? DisplayStyle.Flex : DisplayStyle.None;
            routeOutcomePanel.style.display = deciding ? DisplayStyle.None : DisplayStyle.Flex;
            RenderStages(s);
            RenderOutcome(s);
        }

        private void RenderStages(FullSnapshot s)
        {
            var stage = RoutePresenter.Stage(s, selectedVehicle, s != null && TrainingPresenter.AssignedRouteIsClosed(s), draft.Count > 0 || suggesting, preview);
            for (var i = 0; i < routeStages.childCount; i++)
            {
                routeStages[i].EnableInClassList("route-stage-current", i == stage);
                routeStages[i].EnableInClassList("route-stage-done", i < stage);
            }
        }

        private void RenderWaypoints()
        {
            routeWaypoints.Clear();
            for (var i = 0; i < draft.Count; i++)
            {
                var index = i;
                var row = new VisualElement(); row.AddToClassList("route-waypoint");
                var number = new Label((i + 1).ToString()); number.AddToClassList("route-waypoint-number"); row.Add(number);
                var name = new Label(draft[i].Road); name.AddToClassList("route-waypoint-name"); row.Add(name);
                var remove = new Button(() => RemoveWaypoint(index)) { text = "×", tooltip = "Remove this road from the route" };
                remove.AddToClassList("route-waypoint-remove");
                row.Add(remove);
                routeWaypoints.Add(row);
            }
        }

        private void RenderFeedback()
        {
            if (routeVerdict == null) return;
            RenderStages(session.Store.Snapshot);
            routeVerdict.text =RoutePresenter.Verdict(preview, routeBusy && (draft.Count > 0 || suggesting), draft.Count > 0 || suggesting);
            routeVerdict.EnableInClassList("route-verdict-good", preview?.Validation == "PASS");
            routeVerdict.EnableInClassList("route-verdict-bad", preview != null && preview.Validation != "PASS");
            routeIssues.Clear();
            foreach (var issue in RoutePresenter.Issues(preview))
            {
                var row = new VisualElement(); row.AddToClassList("route-issue");
                var text = new Label(issue.Text); text.AddToClassList("route-issue-text"); row.Add(text);
                if (issue.EdgeIds.Count > 0)
                {
                    var show = new Button(() => world.FocusRoads(issue.EdgeIds)) { text = "Show" };
                    show.AddToClassList("route-link");
                    row.Add(show);
                }
                routeIssues.Add(row);
            }
            FillMetrics(routeMetrics, RoutePresenter.Metrics(preview));
        }

        private static void FillMetrics(VisualElement host, IEnumerable<RouteMetricRow> rows)
        {
            host.Clear();
            foreach (var metric in rows)
            {
                var row = new VisualElement(); row.AddToClassList("route-metric");
                var label = new Label(metric.Label); label.AddToClassList("route-metric-label"); row.Add(label);
                var value = new Label(metric.Value); value.AddToClassList("route-metric-value"); row.Add(value);
                if (metric.Change != null)
                {
                    var change = new Label(metric.Change); change.AddToClassList("route-metric-change");
                    change.EnableInClassList("route-metric-worse", metric.Worse);
                    row.Add(change);
                }
                host.Add(row);
            }
        }

        // The report follows the session's events, at the pace a person can read it.
        private void RefreshOutcome(FullSnapshot s)
        {
            if (outcomeBusy || string.IsNullOrEmpty(route) || !session.Available) return;
            if (s.EventSequence == outcomeSequence || Time.realtimeSinceStartup - outcomeAt < OutcomeRefreshSeconds) return;
            if (TrainingPresenter.AssignedRouteIsClosed(s) && s.Session.Status == "READY") return;
            outcomeSequence = s.EventSequence; outcomeAt = Time.realtimeSinceStartup; outcomeBusy = true;
            run(LoadOutcome(s.SessionId, route));
        }

        private async Task LoadOutcome(string sessionId, string route)
        {
            try
            {
                var result = await api.GetRouteOutcomeAsync(sessionId, route);
                if (session.Store.Snapshot?.SessionId != sessionId || this.route != route) return;
                outcome = result;
                RenderOutcome(session.Store.Snapshot);
                UpdateActions();
            }
            catch (BackendErrorException) { outcomeSequence = -1; }
            catch (NoReplyException) { outcomeSequence = -1; }
            finally { outcomeBusy = false; }
        }

        private void RenderOutcome(FullSnapshot s)
        {
            routeOutcomeHeadline.text = RoutePresenter.OutcomeHeadline(outcome);
            routeOutcomeHeadline.EnableInClassList("route-verdict-good", outcome?.ServicePreserved == true);
            routeOutcomeHeadline.EnableInClassList("route-verdict-bad", outcome?.ServicePreserved == false);
            FillMetrics(routeOutcomeRows, RoutePresenter.OutcomeRows(outcome));
            routeOutcomeNotes.text = outcome == null ? "" : string.Join("\n", outcome.Summary);
            world.SetServedStops(outcome == null ? (IReadOnlyCollection<string>)Array.Empty<string>() : outcome.Stops.Where(stop => stop.ServedThisLoop).Select(stop => stop.StopId).ToList());
            world.SetExecutionStatus(RoutePresenter.ExecutionLine(s, outcome));
        }

        private void ClearIncidentPreview() { incidentPreviewCommand = null; incidentGeneration++; }

        private async Task PreviewIncident()
        {
            if (!Current || incidentBusy) return;
            var command = DecisionRules.IncidentCommand(session.Store.Snapshot, incidents.value, candidates.value, incidentReason.value);
            var stamp = incidentBoundary;
            incidentBusy = true; ClearIncidentPreview(); UpdateActions();
            var generation = incidentGeneration;
            try
            {
                var result = await api.PreviewIncidentAsync(command);
                if (generation != incidentGeneration || stamp != incidentBoundary || candidates.value != (string)command.Payload["candidate_id"] || incidentReason.value.Trim() != command.Reason) return;
                incidentPreviewCommand = command;
                incidentReport.text = "HYPOTHETICAL SIMULATION · backend estimate\n" + result["camera_unavailable_reason"] + "\n" + FormatDocument(result["decision"]);
                ClearEvidence();
                evidenceMap.Show(ContractParser.Parse<FullSnapshot>(result["snapshot"].ToString()));
                evidenceMap.style.display = DisplayStyle.Flex;
                evidenceStatus.text = "Hypothetical branch · base tick " + result["base_tick"] + " · live fleet continues separately.";
            }
            finally { incidentBusy = false; UpdateActions(); }
        }

        private async Task ApproveIncident()
        {
            if (!Current || incidentPreviewCommand == null || incidentBusy) return;
            // Recheck the live candidate before submitting the exact previewed command identity.
            DecisionRules.IncidentCommand(session.Store.Snapshot, incidents.value, candidates.value, incidentReason.value);
            var command = incidentPreviewCommand; ClearIncidentPreview();
            if (await session.SubmitAsync(command)) await session.RefreshAsync();
            UpdateActions();
        }

        private void FocusIncident()
        {
            var incident = session.Store.Snapshot?.Incidents.FirstOrDefault(i => i.IncidentId == incidents.value);
            if (incident == null) return;
            // A world event may belong to no shuttle: show the first to reach it
            if (incident.FocusVehicleId != null) SelectVehicle(incident.FocusVehicleId);
            if (!world.FocusIncident(incident.IncidentId)) world.FocusSelected();
        }

        private void ClearEvidence()
        {
            evidenceMap.Show(null);
            evidenceMap.style.display = DisplayStyle.None;
            evidenceStatus.text = "";
        }

        private void RenderReports() => reports.Render(session.Store.SelectedSessionId, session.Store.Events, ledger);

        // Human-readable nested fields preserve the backend's assumptions, evidence and rubric.
        internal static string FormatDocument(JToken token, int depth = 0)
        {
            if (token == null) return "Unavailable";
            if (token is JObject obj) return string.Join("\n", obj.Properties().Where(p => p.Name != "snapshots").Select(p =>
                new string(' ', depth * 2) + p.Name.Replace('_', ' ') + ": " + (p.Value is JContainer ? "\n" : "") + FormatDocument(p.Value, depth + 1))) +
                (obj["snapshots"] is JArray snapshots ? "\nHistory samples:\n" + string.Join("\n", snapshots.Select(s => $"tick {s["tick"]} · simulation time {s["simulation_time_s"]} s\n" +
                    string.Join("\n", (s["vehicles"] as JArray ?? new JArray()).Select(v => $"  {v["vehicle_id"]}: {v["service_state"]} · {v["battery_kwh"]} kWh · position {v["position"]?.ToString(Formatting.None)}")))) : "");
            if (token is JArray array) return string.Join("\n", array.Select(t => FormatDocument(t, depth)));
            return token.Type == JTokenType.Null ? "Unavailable" : token.ToString();
        }

        private void ToggleSessionTools()
        {
            sessionToolsOpen = !TrainingPresenter.SessionSetup(session.Store.Snapshot, shownPage == ChargingPageIndex, sessionToolsOpen).ToolsVisible;
            FitSessionControls();
        }

        // Finished attempts reopen setup; Charging keeps its one-screen layout while tools are closed.
        private void FitSessionControls()
        {
            var setup = TrainingPresenter.SessionSetup(session.Store.Snapshot, shownPage == ChargingPageIndex, sessionToolsOpen);
            sessionToggle.style.display = setup.ToggleVisible ? DisplayStyle.Flex : DisplayStyle.None;
            sessionCard.EnableInClassList("session-tools-hidden", !setup.ToolsVisible);
            sessionCard.EnableInClassList("session-compact", setup.Compact);
            sessionToggle.text = setup.ToolsVisible ? "Hide session options" : "Session options";
        }

        private void ShowPage(int index)
        {
            if (index < 0) return;
            // Fleet Overview and Route Planning have the map beside them; the others take the full width, as in Qt.
            foreach (var view in operationalViews) view.style.display = index == FleetPage || index == RoutePage ? DisplayStyle.Flex : DisplayStyle.None;
            var entering = index == RoutePage && shownPage != RoutePage;
            shownPage = index;
            FitSessionControls();
            // A route is edited on the map; running, it is watched in 3D. Closing the cameras is the operator's call.
            if (entering && Drafting(session.Store.Snapshot) && !world.CamerasShown) world.ShowMap(true);
            ShowDraftInViews();
            RenderWaypoints();
            RenderFeedback();
            // The Fleet List button may have hidden the panel these pages live in.
            pages[index].parent.style.display = DisplayStyle.Flex;
            for (var i = 0; i < pages.Count; i++)
            {
                pages[i].style.display = i == index ? DisplayStyle.Flex : DisplayStyle.None;
                navButtons[i]?.EnableInClassList("nav-item-selected", i == index);
            }
            // The top menu shows the page's section: Reports and Scenarios, or everything else
            SectionChanged?.Invoke(index == ReportsPageIndex ? "reports" : index == ScenariosPageIndex ? "scenarios" : "operations");
        }

        // A refused start sends the trainee here to approve a detour first.
        public void ShowRoutePlanning() { ShowPage(RoutePage); Render(); }
        public void ShowSection(string section) { ShowPage(section == "reports" ? ReportsPageIndex : section == "scenarios" ? ScenariosPageIndex : FleetPage); Render(); }
        private static VisualElement Row(VisualElement parent) { var row = new VisualElement(); row.AddToClassList("row"); parent.Add(row); return row; }
        private static Label Text(VisualElement parent, string text) { var label = new Label(text); label.AddToClassList("workspace-text"); parent.Add(label); return label; }
        internal static VisualElement Table(VisualElement parent, string title, params string[] columns)
        {
            if (title.Length > 0) Text(parent, title);
            var scroll = new ScrollView(ScrollViewMode.Horizontal); scroll.AddToClassList("data-table"); parent.Add(scroll);
            // A horizontal scroll view lays its children out side by side, so the heading and the
            // rows share one column inside it; otherwise the rows land to the right of the heading.
            var body = new VisualElement(); body.AddToClassList("data-table-body"); scroll.Add(body);
            var head = Row(body);
            foreach (var column in columns) { var cell = new Label(column); cell.AddToClassList("data-cell"); cell.AddToClassList("data-heading"); head.Add(cell); }
            var rows = new VisualElement(); rows.AddToClassList("data-table-body"); body.Add(rows); return rows;
        }
        internal static void Rows(VisualElement table, IEnumerable<string[]> values)
        {
            table.Clear();
            foreach (var row in values)
            {
                var line = Row(table);
                foreach (var value in row) { var cell = new Label(value ?? "—") { tooltip = value }; cell.AddToClassList("data-cell"); line.Add(cell); }
            }
        }
        private static Button Bind(VisualElement root, string name, Action action) { var button = root.Q<Button>(name); button.clicked += action; return button; }
        internal static void Choices(DropdownField field, IEnumerable<string> values)
        {
            var choices = values.OrderBy(v => v, StringComparer.Ordinal).ToList();
            if (field.choices.SequenceEqual(choices)) return;
            var selected = field.value; field.choices = choices;
            field.SetValueWithoutNotify(choices.Contains(selected) ? selected : choices.FirstOrDefault() ?? "");
        }
    }
}
