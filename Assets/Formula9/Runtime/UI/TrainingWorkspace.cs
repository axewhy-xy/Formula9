using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.Session;
using Formula9.World;
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
        private readonly DropdownField shuttles;
        private readonly Dictionary<string, string> shuttleByChoice = new Dictionary<string, string>();
        private readonly VisualElement routePending, routePendingItems;
        private readonly Label routePendingTitle, routeBadge;
        private readonly TextField routeReason;
        private readonly Label routeProblem, routeOptionsExpiry, routeVerdict, routeAssumptions, routeApplyNote, routeOutcomeHeadline, routeOutcomeNotes;
        private readonly Button routeUndo, routeRedo, routeClear, routeSuggest, routeShowProblem, routeCameras, fleetCameras, approve, routeStart, routeWatch;
        private readonly VisualElement routeStages, routeWaypoints, routeIssues, routeMetrics, routeDecide, routeFeedback, routeApply, routeOutcomePanel, routeOutcomeRows, fleetTable;
        private readonly List<VisualElement> operationalViews;
        private readonly ChargingPage charging;
        private readonly ReportsPage reports;
        private readonly IncidentReportsTab incidentReports;
        private readonly EmergencyDispatchPanel emergency; //added panel for emergency dispatch
        private readonly AssessmentReportsTab assessmentReports;
        private readonly Button sessionToggle;
        private readonly VisualElement sessionCard;
        private bool? sessionToolsOpen;
        private string sessionToolsState;
        private readonly List<List<RouteWaypoint>> draftHistory = new List<List<RouteWaypoint>> { new List<RouteWaypoint>() };
        private List<RouteWaypoint> draft = new List<RouteWaypoint>();
        private int historyIndex;
        private long draftRevision, checkedRevision = -1, requestGeneration;
        private string dependencies, boundary, ledgerSession;
        // The shuttle Route Planning is for, the route it drives now, the incident its detour would
        // answer, and the shuttles that were waiting for a decision at the last snapshot.
        private string selectedVehicle, route, selectedDetour, pendingSeen, pendingStrip;
        private bool routeBusy, reloadContext, previewQueued, suggesting, outcomeBusy;
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
        private (string Route, long BasedOn)? approvedDraft;  // route and revision of a submitted approval

        // The decision card: a button per option, and the confirm row of the one chosen. A choice is
        // bound to (incident, shuttle, action); choiceCandidate is the option's ID when it was chosen, to
        // tell a reissue apart, and the ID sent is looked up again when Confirm is pressed.
        private readonly VisualElement routeOptions, routeConfirm;
        private readonly Label routeOptionsTitle, routeOptionsHint, routeConfirmSummary, routeConfirmNote;
        private readonly TextField confirmReason;
        private readonly Button routeConfirmSend, routeConfirmBack, routeRerouteBack;
        private readonly Dictionary<string, Button> optionButtons = new Dictionary<string, Button>();
        private List<DecisionOption> options = new List<DecisionOption>();
        private DecisionOption choice;
        private string choiceCandidate, confirmCommand, optionsNote;
        // rerouting: the operator chose Re-route on the map, so the route editor is open for the shuttle.
        // awaitingError: a refusal whose backend message comes next, through ErrorOccurred.
        private bool rerouting, awaitingError;

        private readonly List<Button> navButtons = new List<Button>();
        // The Fleet List: its page (which remembers being collapsed), the panel it shares with the
        // other pages, its header bar, and the map it floats over
        private readonly VisualElement fleetPage, fleetPanel, fleetHeaderBar, mapArea;
        private const float MapGapPoints = 8;
        // The incident last picked on the map; the Fleet List marks the shuttles it concerns
        public string SelectedIncident { get; private set; }
        public event Action IncidentSelected;
        // Selecting, or clearing, the incident tells the Fleet List to mark its shuttles again
        private void SelectIncident(string incidentId)
        {
            if (SelectedIncident == incidentId) return;
            SelectedIncident = incidentId;
            IncidentSelected?.Invoke();
        }
        private const int MaxLedgerEntries = 200;
        // A page's index by its UXML name, or -1 when the layout leaves it out
        private int Page(string name) =>pageIndex.TryGetValue(name, out var index) ? index : -1;
        private int FleetPage => Page("page-fleet");
        private int RoutePage => Page("page-route");
        private int ChargingPageIndex => Page("page-charging");
        private int ReportsPageIndex => Page("page-reports");
        private int ScenariosPageIndex => Page("page-scenarios");
        private int shownPage;

        public TrainingWorkspace(VisualElement root, TrainingApi api, SessionController session, WorldView world,
            Action<Task> run, Action<string> notify)
        {
            this.api = api; this.session = session; this.world = world; this.run = run;
            // The pages are laid out in UI/Pages/*.uxml; this binds their controls by name.
            foreach (var (pageName, navName) in new[] { ("page-fleet", "nav-fleet"), ("page-route", "nav-route"), ("page-charging", "nav-charging"), ("page-reports", "nav-reports"), ("page-scenarios", "nav-scenarios") })
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
            Bind(root, "follow-selected", () => world.FollowSelected());
            fleetCameras = Bind(root, "fleet-cameras", ToggleSelectedCameras);
            Bind(root, "camera-overview", () => world.Overview());
            fleetPage = root.Q("fleet-page");
            fleetPanel = root.Q("fleet-panel");
            fleetHeaderBar = fleetPage?.Q(className: "fleet-panel-header");
            mapArea = root.Q("world");
            var fleetToggle = root.Q<Button>("fleet-toggle-btn");
            if (fleetToggle != null) fleetToggle.clicked += ToggleFleetList;
            // Whatever resizes the sheet or the map (the window, a header that wraps) fits them again
            fleetPanel?.RegisterCallback<GeometryChangedEvent>(_ => FitMapToFleetList());
            mapArea?.RegisterCallback<GeometryChangedEvent>(_ => FitMapToFleetList());

            shuttles = root.Q<DropdownField>("shuttles");
            shuttles.RegisterValueChangedCallback(change => { if (shuttleByChoice.TryGetValue(change.newValue ?? "", out var id)) ChooseShuttle(id); });
            routePending = root.Q("route-pending");
            routePendingTitle = root.Q<Label>("route-pending-title");
            routePendingItems = root.Q("route-pending-items");
            routeBadge = root.Q<Label>("nav-route-badge");
            Bind(root, "refresh", RequestContext);
            routeCameras = Bind(root, "route-cameras", ToggleCameras);
            routeStages = root.Q("route-stages");
            foreach (var stage in RoutePresenter.Stages) { var chip = new Label(stage); chip.AddToClassList("route-stage"); routeStages.Add(chip); }
            routeProblem = root.Q<Label>("route-problem");
            routeOptionsExpiry = root.Q<Label>("route-options-expiry");
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
            Tabs(root, ("reports-tab-log", "reports-log-panel"), ("reports-tab-commands", "reports-commands-panel"),
                ("reports-tab-incidents", "reports-incidents-panel"), ("reports-tab-assessment", "reports-assessment-panel"));
            operationalViews = new List<VisualElement> { root.Q("view-bar"), root.Q("world") };
            routeUndo = Bind(root, "route-undo", () => RestoreDraft(-1));
            routeRedo = Bind(root, "route-redo", () => RestoreDraft(1));
            routeClear = Bind(root, "route-cancel", () => { if (!session.HasPendingCommand) { routeReason.value = ""; SetDraft(new List<RouteWaypoint>()); } });
            routeSuggest = Bind(root, "route-suggest", SuggestDetour);
            routeShowProblem = Bind(root, "route-show-problem", ShowProblem);
            routeOptions = root.Q("route-options");
            routeOptionsTitle = root.Q<Label>("route-options-title");
            routeOptionsHint = root.Q<Label>("route-options-hint");
            foreach (var (action, name) in new[] { ("KEEP_ROUTE", "option-keep"), ("HOLD_AT_STOP", "option-hold"), ("ESCALATE", "option-assist"), ("ROAD_DETOUR", "option-reroute") })
                optionButtons[action] = Bind(root, name, () => ChooseOption(action));
            routeConfirm = root.Q("route-confirm");
            routeConfirmSummary = root.Q<Label>("route-confirm-summary");
            routeConfirmNote = root.Q<Label>("route-confirm-note");
            confirmReason = root.Q<TextField>("route-confirm-reason");
            confirmReason.RegisterValueChangedCallback(_ => UpdateActions());
            routeConfirmSend = Bind(root, "route-confirm-send", () => run(ConfirmChoice()));
            routeConfirmBack = Bind(root, "route-confirm-back", () => { if (confirmCommand == null) CloseChoice(null); });
            routeRerouteBack = Bind(root, "route-reroute-back", () => { if (!session.HasPendingCommand) { LeaveReroute(); Render(); } });
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

            reports = new ReportsPage(root);
            incidentReports = new IncidentReportsTab(root, api, session, world, run);
            emergency = new EmergencyDispatchPanel(root, session, run, notify, () => Current);
            assessmentReports = new AssessmentReportsTab(root, api, run);
            root.Q<Button>("reports-tab-assessment").clicked += Render;
            session.SnapshotAccepted += (_, __) => { acceptedAt = Time.realtimeSinceStartup; Render(); };
            session.EventsAccepted += _ => RenderReports();
            session.AvailabilityChanged += _ => { if (!session.Available) InvalidateRoute(); Render(); };
            session.CommandLedgerChanged += outcome =>
            {
                ledger.RemoveAll(o => o.CommandId == outcome.CommandId); ledger.Add(outcome);
                if (ledger.Count > MaxLedgerEntries) ledger.RemoveAt(0);
                if (outcome.Status != "QUEUED" && outcome.Status != "PENDING_CONFIRMATION") InvalidateRoute();
                if (outcome.CommandId != null && outcome.CommandId == confirmCommand) ConfirmOutcome(outcome);
                RenderReports(); UpdateActions();
            };
            // A submission the backend refused outright reports its message right after its outcome
            session.ErrorOccurred += message =>
            {
                if (!awaitingError) return;
                awaitingError = false;
                const string prefix = "Training command failed: ";
                SetConfirmNote("The backend refused it: " + (message.StartsWith(prefix) ? message.Substring(prefix.Length) : message), true);
            };
            // The Cameras buttons depend on which shuttle is selected
            world.EntitySelected += _ => UpdateActions();
            world.RouteNodeSelected += AppendNode;
            world.RoadClicked += AddRoad;
            world.WaypointClicked += RemoveWaypoint;
            root.schedule.Execute(UpdateActions).Every(250);
            ShowPage(FleetPage);
        }

        public void SelectVehicle(string id) => world.SelectEntity(id);

        // True while a route is being loaded or previewed, or a charging or incident request is on
        // its way: buttons greyed out then are waiting, not refusing
        public bool Busy => routeBusy || charging.Busy || incidentReports.Busy;

        // The operator picking a shuttle in the fleet table or the Fleet List. Open cameras move to it,
        // except on Route Planning to a shuttle other than the one it is for: they stay on that one.
        public void PickVehicle(string id)
        {
            if (shownPage == RoutePage && id != selectedVehicle) world.SelectEntity(id); else world.PickShuttle(id);
        }

        // Opens Route Planning on a shuttle's decision and frames the incident: from a notification's
        // Decide, the Needs a decision strip and an incident's map marker. Without an incident, the
        // shuttle's soonest one. This is the operator's own choice, so it replaces any draft for another
        // shuttle.
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

        // An incident's map marker was clicked: decide for the first shuttle still waiting for a
        // decision about it, else the first that reaches it and was not decided for, else the shuttle
        // it concerns, whose Problem card describes it. An incident that concerns no shuttle is only
        // framed. The incident is also selected, so the Fleet List follows.
        public void DecideForIncident(string incidentId)
        {
            var s = session.Store.Snapshot;
            var incident = s?.Incidents.FirstOrDefault(i => i.IncidentId == incidentId);
            if (incident == null) return;
            SelectIncident(incidentId);
            var vehicle = DecisionRules.PendingDecisions(s).FirstOrDefault(d => d.IncidentId == incidentId)?.VehicleId ??
                          (incident.Exposures ?? new List<IncidentExposure>()).OrderBy(e => e.EtaS).FirstOrDefault(e => incident.ResponseOf(e.VehicleId) == null)?.VehicleId ??
                          incident.FocusVehicleId;
            if (vehicle != null && s.Vehicles.Any(v => v.VehicleId == vehicle)) { DecideFor(vehicle, incidentId); return; }
            if (!world.FocusIncident(incidentId) && incident.Location?.EdgeId != null) world.FocusRoads(new[] { incident.Location.EdgeId });
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
                choice = null; choiceCandidate = confirmCommand = optionsNote = null; rerouting = awaitingError = false;
                // Incident IDs repeat across sessions, so another session's selection would mark the wrong one
                SelectIncident(null);
                if (ledgerSession != s?.SessionId) { ledger.Clear(); ledgerSession = s?.SessionId; }
                charging.Reset();
                emergency?.Reset();
                routeReason.value = confirmReason.value = "";
                draft = new List<RouteWaypoint>(); draftHistory.Clear(); draftHistory.Add(new List<RouteWaypoint>()); historyIndex = 0;
                preview = null; draftView = null; outcome = null; outcomeSequence = -1; suggesting = false;
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
            emergency?.Render(s);
            assessmentReports.Render(s, session.SignedInUsername, shownPage == ReportsPageIndex);
            if (s == null) { RenderBadge(0); RenderReports(); UpdateActions(); return; }
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
            RenderBadge(pending.Select(d => d.VehicleId).Distinct().Count());
            var changed = DecisionRules.RouteDependencies(s, route);
            if (changed != dependencies)
            {
                dependencies = changed; context = null; InvalidateRoute();
                RequestContext();
            }
            RenderExercise(s);
            RefreshOutcome(s);
            // An incident that has left the session is no longer selected
            if (SelectedIncident != null && s.Incidents.All(i => i.IncidentId != SelectedIncident)) SelectIncident(null);
            Rows(fleetTable, TrainingPresenter.Fleet(s, SelectedIncident).Select(r => new[] { r.ShuttleId, r.State, r.Route, r.Battery, r.Provenance }));
            foreach (var row in fleetTable.Children()) { var id = ((Label)row[0]).text; row.RegisterCallback<ClickEvent>(_ => PickVehicle(id)); }
            charging.Render(s);
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
            // UpdateActions can run before the constructor has created the panel, so emergency may still be null
            emergency?.UpdateActions();
            // The decision card: usable while the session runs, is connected and is up to date
            var free = Current && confirmCommand == null;
            foreach (var option in options)
                if (optionButtons.TryGetValue(option.Action, out var button)) button.SetEnabled(free && option.Enabled);
            routeConfirmSend.SetEnabled(free && choice != null && s?.Session.Status == "RUNNING" && !string.IsNullOrWhiteSpace(confirmReason.value));
            routeConfirmBack.SetEnabled(confirmCommand == null);
            confirmReason.SetEnabled(confirmCommand == null);
            routeRerouteBack.SetEnabled(!session.HasPendingCommand);
            var hint = optionsNote ?? RoutePresenter.OptionsHint(s, session.Available, session.HasPendingCommand, Time.realtimeSinceStartup - acceptedAt <= 5);
            routeOptionsHint.text = hint ?? "";
            routeOptionsHint.style.display = string.IsNullOrEmpty(hint) ? DisplayStyle.None : DisplayStyle.Flex;
            world.RouteEditing = edit;
            // A decision or a lost connection withdraws the verdict but leaves the draft: ask once more.
            if (edit && draft.Count > 0 && preview == null && !routeBusy && approvedDraft == null && checkedRevision != draftRevision && !previewTimer.isActive) SchedulePreview();
            if (draftView != null && draftView.Editable != (edit && shownPage == RoutePage)) ShowDraftInViews();
        }

        private bool Editable(FullSnapshot s) => Current && context != null &&
            (s?.Session.Status == "READY" || (rerouting && DecisionRules.ReroutableRoute(s, selectedVehicle) == context.RouteId));

        // Before the start every route can be drafted; running, the route of a shuttle with a detour on
        // offer, once the operator chose to re-route it.
        private bool Drafting(FullSnapshot s) => s?.Session.Status == "READY" || (rerouting && DecisionRules.DetourCandidate(s, selectedVehicle) != null);

        // Nothing the operator would lose: no draft, no reason typed, no decision being confirmed or
        // re-routed, nothing on its way to the backend.
        private bool Idle => draft.Count == 0 && !suggesting && preview == null && approvedDraft == null && choice == null && !rerouting &&
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
            if (detour == null) rerouting = false;
            FollowChoice(s);

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
            choice = null; choiceCandidate = optionsNote = null; rerouting = false;
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

        // How many shuttles need a decision, on the Route Planning nav item; nothing when none do.
        private void RenderBadge(int shuttles)
        {
            if (routeBadge == null) return;
            var text = shuttles.ToString();
            if (routeBadge.text != text) routeBadge.text = text;
            // Shown, the stylesheet decides: it keeps the badge when the nav panel collapses
            routeBadge.style.display = shuttles > 0 ? new StyleEnum<DisplayStyle>(StyleKeyword.Null) : new StyleEnum<DisplayStyle>(DisplayStyle.None);
            var hint = shuttles == 0 ? "Route Planning" : shuttles == 1 ? "Route Planning: 1 shuttle needs a decision" : $"Route Planning: {shuttles} shuttles need a decision";
            var button = routeBadge.parent;
            if (button != null && button.tooltip != hint) button.tooltip = hint;
        }

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

        // Frames what the Problem card is about: the shuttle's first incident, on the map by its road and
        // in the 3D city by its scene; without one, the closure on its route.
        private void ShowProblem()
        {
            var incident = RoutePresenter.ProblemIncidents(session.Store.Snapshot, selectedVehicle).FirstOrDefault();
            if (incident != null)
            {
                if (incident.Location?.EdgeId != null) world.FocusRoads(new[] { incident.Location.EdgeId });
                world.FocusIncident(incident.IncidentId);
                return;
            }
            if (context == null) return;
            var committed = new HashSet<string>(context.OriginalDirectedEdgeIds);
            var closed = context.Edges.Where(e => e.Closed).OrderByDescending(e => committed.Contains(e.EdgeId)).Select(e => e.EdgeId).ToList();
            if (closed.Count > 0) world.FocusRoads(closed);
        }

        private void RenderExercise(FullSnapshot s)
        {
            if (routeProblem == null) return;
            routeProblem.text = RoutePresenter.Problem(context, s, selectedVehicle);
            var incident = RoutePresenter.ProblemIncidents(s, selectedVehicle).Count > 0;
            routeShowProblem.style.display = incident || (context != null && context.Edges.Any(e => e.Closed)) ? DisplayStyle.Flex : DisplayStyle.None;
            var expiry = RoutePresenter.OptionsExpiry(s, selectedVehicle);
            routeOptionsExpiry.text = expiry ?? "";
            routeOptionsExpiry.style.display = expiry == null ? DisplayStyle.None : DisplayStyle.Flex;
            // Before the start: the problem, the editor while a route is closed, else the outcome. Running
            // with a decision to make: the problem and the options, and the editor while re-routing.
            // Otherwise: the problem and the outcome.
            var ready = s?.Session.Status == "READY";
            var pending = s != null && DecisionRules.OptionsFor(s, selectedVehicle).Count > 0;
            var editing = ready ? s != null && TrainingPresenter.AssignedRouteIsClosed(s) : Drafting(s);
            routeOptions.style.display = !ready && pending ? DisplayStyle.Flex : DisplayStyle.None;
            routeDecide.style.display = editing ? DisplayStyle.Flex : DisplayStyle.None;
            routeFeedback.style.display = editing ? DisplayStyle.Flex : DisplayStyle.None;
            routeApply.style.display = editing ? DisplayStyle.Flex : DisplayStyle.None;
            routeRerouteBack.style.display = !ready && editing ? DisplayStyle.Flex : DisplayStyle.None;
            routeOutcomePanel.style.display = editing || (!ready && pending) ? DisplayStyle.None : DisplayStyle.Flex;
            // The stages are the re-route's (and the exercise's before the start); the other options have none
            routeStages.style.display = ready || editing ? DisplayStyle.Flex : DisplayStyle.None;
            RenderOptions(s);
            RenderStages(s);
            RenderOutcome(s);
        }

        // The option buttons, from the snapshot and the latest options event of the shuttle's first
        // incident, and the confirm row of the option chosen.
        private void RenderOptions(FullSnapshot s)
        {
            var first = DecisionRules.OptionsFor(s, selectedVehicle).FirstOrDefault();
            options = s == null ? new List<DecisionOption>() : RoutePresenter.Options(s, selectedVehicle, RoutePresenter.OptionsEvent(session.Store.Events, first?.IncidentId));
            routeOptionsTitle.text = RoutePresenter.OptionsTitle(selectedVehicle);
            foreach (var option in options)
            {
                if (!optionButtons.TryGetValue(option.Action, out var button)) continue;
                button.text = option.Label;
                button.tooltip = option.Tooltip;
                button.style.display = option.Shown ? DisplayStyle.Flex : DisplayStyle.None;
                button.EnableInClassList("segment-selected", option.Action == "ROAD_DETOUR" ? rerouting : option.Action == choice?.Action);
            }
            routeConfirm.style.display = choice != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (choice != null) routeConfirmSummary.text = choice.Confirm;
            UpdateActions();
        }

        // The operator pressing an option. Re-route opens the editor; any other opens its confirm row,
        // with a reason to start from, and leaves a re-route.
        private void ChooseOption(string action)
        {
            var s = session.Store.Snapshot;
            var option = options.FirstOrDefault(o => o.Action == action);
            if (s == null || option == null || !option.Enabled || !Current || confirmCommand != null) return;
            optionsNote = null;
            if (action == "ROAD_DETOUR")
            {
                if (rerouting) return;
                choice = null; choiceCandidate = null;
                rerouting = true;
                if (string.IsNullOrWhiteSpace(routeReason.value)) routeReason.value = option.Reason;
                // A route is drawn on the map; closing the cameras is the operator's call
                if (shownPage == RoutePage && !world.CamerasShown) world.ShowMap(true);
                ShowDraftInViews();
                Render();
                return;
            }
            if (choice?.Action == action) return;
            if (rerouting) LeaveReroute();
            choice = option;
            choiceCandidate = DecisionRules.CandidateFor(s, option.IncidentId, selectedVehicle, option.CandidateAction)?.CandidateId;
            confirmReason.value = option.Reason;
            SetConfirmNote("", false);
            Render();
        }

        // Leaves the re-route; the route drawn for it is dropped.
        private void LeaveReroute()
        {
            rerouting = false;
            routeReason.value = "";
            draft = new List<RouteWaypoint>(); draftHistory.Clear(); draftHistory.Add(new List<RouteWaypoint>()); historyIndex = 0;
            InvalidateRoute();
            RenderWaypoints();
        }

        // Closes the confirm row, saying why when it was not the operator's own Back.
        private void CloseChoice(string note)
        {
            choice = null; choiceCandidate = null;
            optionsNote = note;
            SetConfirmNote("", false);
            RenderOptions(session.Store.Snapshot);
        }

        private void SetConfirmNote(string text, bool refused)
        {
            routeConfirmNote.text = text;
            routeConfirmNote.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
            routeConfirmNote.EnableInClassList("route-confirm-refused", refused);
        }

        // An open confirm row whose option was reissued, or is gone, closes: the figures it showed may
        // no longer hold. Not while its decision is on its way, which itself moves the options on.
        private void FollowChoice(FullSnapshot s)
        {
            if (choice == null || confirmCommand != null) return;
            var current = DecisionRules.CandidateFor(s, choice.IncidentId, selectedVehicle, choice.CandidateAction);
            if (current == null) CloseChoice(DecisionRules.OptionsFor(s, selectedVehicle).Count > 0 ? RoutePresenter.OptionsWithdrawn : null);
            else if (current.CandidateId != choiceCandidate) CloseChoice(RoutePresenter.OptionsRefreshed);
        }

        // Sends the chosen option, by the ID it has now. A refusal keeps the row open with the reason.
        private async Task ConfirmChoice()
        {
            var s = session.Store.Snapshot;
            if (s == null || choice == null || !Current || confirmCommand != null) return;
            var candidate = DecisionRules.CandidateFor(s, choice.IncidentId, selectedVehicle, choice.CandidateAction);
            if (candidate == null) { CloseChoice(RoutePresenter.OptionsWithdrawn); return; }
            if (candidate.CandidateId != choiceCandidate) { CloseChoice(RoutePresenter.OptionsRefreshed); return; }
            DispatcherCommand command;
            try { command = DecisionRules.IncidentCommand(s, choice.IncidentId, candidate.CandidateId, confirmReason.value); }
            catch (ArgumentException error) { SetConfirmNote(error.Message, true); return; }
            confirmCommand = command.CommandId; awaitingError = false;
            SetConfirmNote("Sending…", false);
            UpdateActions();
            var sent = await session.SubmitAsync(command);
            if (!sent && confirmCommand == command.CommandId)
            {
                confirmCommand = null;
                SetConfirmNote("It couldn't be sent: the session is busy or not connected. Try again in a moment.", true);
            }
            UpdateActions();
            if (sent && choice == null) await session.RefreshAsync();
        }

        // The outcome of the decision sent from the confirm row.
        private void ConfirmOutcome(CommandOutcome outcome)
        {
            if (outcome.Status == "PENDING_CONFIRMATION" || outcome.Status == "QUEUED")
            {
                SetConfirmNote(outcome.Status == "QUEUED" ? "Accepted; waiting for it to be applied…" : "Sending…", false);
                return;
            }
            confirmCommand = null;
            if (outcome.Status == "APPLIED") { CloseChoice(null); return; }
            awaitingError = outcome.Status == "NOT_ACCEPTED" || outcome.Status == "UNCONFIRMED";
            SetConfirmNote(RoutePresenter.Refusal(outcome), true);
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

        private void RenderReports() => reports.Render(session.Store.SelectedSessionId, session.Store.Events, ledger);

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
            // Fleet Overview (list over the map) and Route Planning (beside it) show the map; the others take the full width, as in Qt.
            foreach (var view in operationalViews) view.style.display = index == FleetPage || index == RoutePage ? DisplayStyle.Flex : DisplayStyle.None;
            var entering = index == RoutePage && shownPage != RoutePage;
            shownPage = index;
            FitSessionControls();
            // A route is edited on the map; running, it is watched in 3D. Closing the cameras is the operator's call.
            if (entering && Drafting(session.Store.Snapshot) && !world.CamerasShown) world.ShowMap(true);
            ShowDraftInViews();
            RenderWaypoints();
            RenderFeedback();
            for (var i = 0; i < pages.Count; i++)
            {
                pages[i].style.display = i == index ? DisplayStyle.Flex : DisplayStyle.None;
                navButtons[i]?.EnableInClassList("nav-item-selected", i == index);
            }
            LayOutFleetList();
        }

        // Collapses or expands the Fleet List. The Fleet page keeps the state, so it survives
        // switching pages.
        private void ToggleFleetList()
        {
            fleetPage?.ToggleInClassList("collapsed");
            LayOutFleetList();
        }

        // The one place that decides where the Fleet List sits. On the Fleet page it floats over
        // the bottom of the map and can be collapsed to its header bar; every other page gets the
        // whole panel beside the map.
        private void LayOutFleetList()
        {
            if (fleetPanel == null) return;
            var onFleetPage = shownPage == FleetPage;
            fleetPanel.EnableInClassList("fleet-panel-collapsed", onFleetPage && fleetPage != null && fleetPage.ClassListContains("collapsed"));
            fleetPanel.parent?.EnableInClassList("body-stacked", onFleetPage);
            FitMapToFleetList();
        }

        // Keeps the map usable under the Fleet List. The map ends just above the collapsed bar,
        // measured rather than assumed, so a header that wraps on a narrow window still fits.
        // While the list is expanded over the map, the map's bottom controls move up above it.
        private void FitMapToFleetList()
        {
            if (fleetPanel == null || mapArea == null) return;
            var stacked = fleetPanel.parent != null && fleetPanel.parent.ClassListContains("body-stacked");
            if (!stacked)
            {
                mapArea.style.marginBottom = StyleKeyword.Null;
                if (world != null) world.SetBottomInset(0);
                return;
            }
            var panel = fleetPanel.resolvedStyle;
            var bar = panel.paddingTop + panel.paddingBottom + panel.borderTopWidth + panel.borderBottomWidth + (fleetHeaderBar?.layout.height ?? 0);
            if (!float.IsNaN(bar) && bar > 0) mapArea.style.marginBottom = bar + MapGapPoints;
            // How far the sheet reaches up into the map; 0 or less while it is collapsed below it
            var covered = mapArea.worldBound.yMax - fleetPanel.worldBound.yMin;
            if (world != null) world.SetBottomInset(float.IsNaN(covered) ? 0 : covered);
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
