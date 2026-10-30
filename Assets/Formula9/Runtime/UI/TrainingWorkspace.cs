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
        private readonly Action<string> notify;
        private readonly List<VisualElement> pages = new List<VisualElement>();
        private readonly List<CommandOutcome> ledger = new List<CommandOutcome>();
        private readonly DropdownField routes, incidents, candidates;
        private readonly TextField via, routeReason, chargeReason, incidentReason;
        private readonly Label routeStatus, routeCommitted, routeAssumptions, incidentData, incidentReport, selection;
        private readonly Button validate, approve, previewIncident, approveIncident, history, debrief;
        private readonly FleetMap evidenceMap;
        private readonly RouteGraph routeGraph;
        private readonly VisualElement routeShuttles, checksTable, comparisonTable, fleetTable;
        private readonly List<VisualElement> operationalViews;
        private readonly SliderInt historySample;
        private readonly Label evidenceStatus;
        private readonly List<FullSnapshot> historySnapshots = new List<FullSnapshot>();
        private readonly VisualElement batteryTable, chargerTable, offerTable, incidentTable, candidateTable, eventsTable, ledgerTable;
        private readonly List<string> draftHistory = new List<string> { "" };
        private int historyIndex;
        private long draftRevision, requestGeneration, incidentGeneration;
        private string dependencies, boundary, incidentBoundary, ledgerSession;
        private bool restoring, routeBusy, incidentBusy, reloadContext;
        private float acceptedAt;
        private RouteEditorContext context;
        private RouteEditPreview preview;
        private DispatcherCommand incidentPreviewCommand;
        private (string Route, long BasedOn)? approvedDraft;  // route and revision of a submitted approval

        private readonly List<Button> navButtons = new List<Button>();
        public string SelectedIncident => incidents.value;
        public event Action IncidentSelected;
        public event Action<string> SectionChanged;
        private const int ReportsPage = 4, ScenariosPage = 5, MaxLedgerEntries = 200;
        private readonly VisualElement incidentDecisions;
        private string offersKey;

        public TrainingWorkspace(VisualElement root, TrainingApi api, SessionController session, WorldView world,
            Action<Task> run, Action<string> notify)
        {
            this.api = api; this.session = session; this.world = world; this.run = run; this.notify = notify;
            // The pages are laid out in UI/Pages/*.uxml; this binds their controls by name.
            foreach (var (pageName, navName) in new[] { ("page-fleet", "nav-fleet"), ("page-route", "nav-route"), ("page-charging", "nav-charging"), ("page-incident", "nav-incident"), ("page-reports", "nav-reports"), ("page-scenarios", null) })
            {
                var index = pages.Count;
                pages.Add(root.Q(pageName));
                if (navName == null) continue;
                var button = root.Q<Button>(navName);
                button.clicked += () => { ShowPage(index); Render(); };
                navButtons.Add(button);
            }
            selection = root.Q<Label>("fleet-selection");
            Bind(root, "follow-selected", () => world.FollowSelected());
            Bind(root, "camera-overview", () => world.Overview());

            routes = root.Q<DropdownField>("routes");
            routes.RegisterValueChangedCallback(_ => { ResetDraft(); dependencies = null; RenderRouteShuttles(); RequestContext(); });
            Bind(root, "refresh", RequestContext);
            routeStatus = root.Q<Label>("route-status");
            routeCommitted = root.Q<Label>("route-committed");
            routeAssumptions = root.Q<Label>("route-assumptions");
            routeShuttles = root.Q("route-shuttles");
            routeGraph = root.Q<RouteGraph>("route-graph");
            routeGraph.NodeClicked += AppendNode;
            routeGraph.Edited += nodes => { if (Current && !routeBusy && context != null && session.Store.Snapshot.Session.Status == "READY") via.value = string.Join(", ", nodes); };
            checksTable = Table(root.Q("route-checks-host"), "", "Backend check", "Result", "Requirement", "Explanation / feature");
            comparisonTable = Table(root.Q("route-comparison-host"), "", "Simulation estimate", "Original", "Proposed");
            fleetTable = Table(root.Q("route-fleet-host"), "", "Shuttle ID", "State", "Route / revision", "Battery", "Provenance");
            Tabs(root, ("route-tab-editor", "route-editor-panel"), ("route-tab-fleet", "route-fleet-panel"));
            Tabs(root, ("reports-tab-log", "reports-log-panel"), ("reports-tab-commands", "reports-commands-panel"));
            operationalViews = new List<VisualElement> { root.Q("view-bar"), root.Q("world") };
            via = root.Q<TextField>("route-via");
            via.RegisterValueChangedCallback(change =>
            {
                if (!restoring)
                {
                    draftHistory.RemoveRange(historyIndex + 1, draftHistory.Count - historyIndex - 1);
                    draftHistory.Add(change.newValue);
                    if (draftHistory.Count > 100) draftHistory.RemoveAt(0);
                    historyIndex = draftHistory.Count - 1;
                }
                InvalidateRoute();
            });
            Bind(root, "route-undo", () => RestoreDraft(-1));
            Bind(root, "route-redo", () => RestoreDraft(1));
            Bind(root, "route-cancel", () => { if (!session.HasPendingCommand) { via.value = ""; routeReason.value = ""; InvalidateRoute(); } });
            routeReason = root.Q<TextField>("route-reason");
            routeReason.RegisterValueChangedCallback(_ => UpdateActions());
            validate = Bind(root, "route-validate", () => run(PreviewRoute()));
            approve = Bind(root, "route-approve", () => run(ApproveRoute()));

            batteryTable = Table(root.Q("battery-table-host"), "", "Shuttle ID", "Charge", "Capacity", "Service state", "Reserve margin", "Loops left", "Next depot (ETA)", "Reaches depot above reserve");
            chargerTable = Table(root.Q("charger-table-host"), "", "Charger ID", "Ports", "Free ports", "Occupied shuttles", "Power");
            offerTable = Table(root.Q("offer-table-host"), "", "Decision", "Shuttle ID", "Action", "Charger", "Out of service", "Battery after", "Loops after", "Min. in service", "Validation", "Assumptions", "Option ID");
            chargeReason = root.Q<TextField>("charge-reason");
            chargeReason.RegisterValueChangedCallback(_ => UpdateActions());

            incidentDecisions = root.Q("incident-decisions");
            incidents = root.Q<DropdownField>("incidents");
            incidents.RegisterValueChangedCallback(_ => { ClearIncidentPreview(); Render(); IncidentSelected?.Invoke(); });
            incidentData = root.Q<Label>("incident-data");
            incidentTable = Table(root.Q("incident-table-host"), "", "Incident ID", "Shuttle ID", "State", "Detected tick", "Options");
            candidateTable = Table(root.Q("candidate-table-host"), "", "Candidate", "Action", "Validation", "Expires tick", "Assumptions");
            Bind(root, "incident-focus", FocusIncident);
            candidates = root.Q<DropdownField>("candidates");
            candidates.RegisterValueChangedCallback(_ => { ClearIncidentPreview(); UpdateActions(); });
            incidentReason = root.Q<TextField>("incident-reason");
            incidentReason.RegisterValueChangedCallback(_ => { ClearIncidentPreview(); UpdateActions(); });
            previewIncident = Bind(root, "incident-preview", () => run(PreviewIncident()));
            approveIncident = Bind(root, "incident-approve", () => run(ApproveIncident()));
            history = Bind(root, "incident-history", () => run(IncidentReport(false)));
            debrief = Bind(root, "incident-debrief", () => run(IncidentReport(true)));
            incidentReport = root.Q<Label>("incident-report");
            evidenceStatus = root.Q<Label>("evidence-status");
            evidenceMap = root.Q<FleetMap>("incident-evidence-map");
            world.UseRealMap(evidenceMap);
            historySample = root.Q<SliderInt>("history-sample");
            historySample.RegisterValueChangedCallback(_ => ShowHistorySample());

            ledgerTable = Table(root.Q("ledger-table-host"), "", "Command ID", "Target", "Status", "Failure code", "Effective tick");
            eventsTable = Table(root.Q("events-table-host"), "", "Event ID", "Sequence", "Tick", "Type", "Entity ID");
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
            world.EntitySelected += id => { selection.text = "Selected: " + id; };
            world.RouteNodeSelected += AppendNode;
            root.schedule.Execute(UpdateActions).Every(250);
            ShowPage(0);
        }

        public void SelectVehicle(string id) { world.SelectEntity(id); selection.text = "Selected: " + id; }

        public void Render()
        {
            var s = session.Store.Snapshot;
            var currentBoundary = s == null ? null : $"{s.SessionId}/{s.SessionEpoch}";
            if (boundary != currentBoundary)
            {
                boundary = currentBoundary; dependencies = null; context = null; requestGeneration++; approvedDraft = null;
                if (ledgerSession != s?.SessionId) { ledger.Clear(); ledgerSession = s?.SessionId; }
                routeReason.value = chargeReason.value = incidentReason.value = "";
                via.value = ""; draftHistory.Clear(); draftHistory.Add(""); historyIndex = 0;
                ClearIncidentPreview(); ClearEvidence(); incidentReport.text = "";
                routeGraph.Show(null, null, null); world.SetRouteDraft(null, null, null);
            }
            if (s == null) { UpdateActions(); return; }
            // An applied approval moves its route to a new revision, which finishes the draft. A queued
            // or rejected one never does, so its draft stays for another try.
            if (approvedDraft is (string approvedRoute, long basedOn) && s.Routes.Any(r => r.RouteId == approvedRoute && r.Revision > basedOn))
            {
                approvedDraft = null;
                via.value = ""; routeReason.value = "";
                draftHistory.Clear(); draftHistory.Add(""); historyIndex = 0;
            }
            Choices(routes, s.Routes.Select(r => r.RouteId));
            var changed = DecisionRules.RouteDependencies(s, routes.value);
            if (changed != dependencies)
            {
                dependencies = changed; context = null; InvalidateRoute();
                RenderRouteShuttles();
                RequestContext();
            }
            var committedRoute = s.Routes.FirstOrDefault(r => r.RouteId == routes.value);
            routeCommitted.text = committedRoute == null ? "No route" : $"{committedRoute.RouteId} / revision {committedRoute.Revision} (backend snapshot {s.SnapshotId})";
            routeGraph.Show(context, preview, ParseDraftOrEmpty());
            Rows(fleetTable, TrainingPresenter.Fleet(s, SelectedIncident).Select(r => new[] { r.ShuttleId, r.State, r.Route, r.Battery, r.Provenance }));
            foreach (var row in fleetTable.Children()) { var id = ((Label)row[0]).text; row.RegisterCallback<ClickEvent>(_ => SelectVehicle(id)); }
            Rows(batteryTable, s.Vehicles.OrderBy(v => v.VehicleId, StringComparer.Ordinal).Select(v =>
            {
                var f = s.EnergyForecasts.FirstOrDefault(e => e.VehicleId == v.VehicleId);
                return new[] { v.VehicleId, $"{v.BatteryKwh:0.##} kWh", $"{v.BatteryCapacityKwh:0.##} kWh", v.ServiceState,
                    f == null ? "—" : $"{f.ReserveMarginKwh:0.##} kWh", f?.LoopsRemaining?.ToString() ?? "—",
                    string.IsNullOrEmpty(f?.NextDepotId) ? "—" : $"{f.NextDepotId} ({f.NextDepotEtaS ?? 0:0.#} s)",
                    f?.ReachesDepotAboveReserve == null ? "—" : f.ReachesDepotAboveReserve.Value ? "Yes" : "No" };
            }));
            Rows(chargerTable, s.Chargers.Select(c => new[] { c.ChargerId, c.PortCount.ToString(), (c.PortCount - c.OccupiedVehicleIds.Count).ToString(), string.Join(", ", c.OccupiedVehicleIds), $"{c.PowerKw:0.0} kW" }));
            RenderOffers(s);
            Choices(incidents, s.Incidents.Select(i => i.IncidentId));
            var selectedIncident = s.Incidents.FirstOrDefault(i => i.IncidentId == incidents.value);
            var newIncidentBoundary = selectedIncident == null ? null : $"{boundary}/{selectedIncident.IncidentId}/{selectedIncident.AggregateRevision}/" +
                string.Join(";", s.Candidates.Where(c => c.IncidentId == selectedIncident.IncidentId).Select(c => $"{c.CandidateId}:{c.Revision}:{c.Validation}"));
            if (incidentBoundary != newIncidentBoundary) { incidentBoundary = newIncidentBoundary; ClearIncidentPreview(); ClearEvidence(); incidentReport.text = ""; }
            Choices(candidates, selectedIncident?.CandidateIds ?? Enumerable.Empty<string>());
            incidentData.text = s.Incidents.Count == 0 ? "No incidents detected." : "Select an incident and option, enter a reason, then preview the consequences before approval.";
            incidentDecisions.style.display = s.Incidents.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            Rows(incidentTable, s.Incidents.OrderBy(i => i.IncidentId, StringComparer.Ordinal).Select(i => new[] { i.IncidentId, i.VehicleId, i.State, i.DetectedTick.ToString(), string.Join(", ", i.CandidateIds) }));
            foreach (var row in incidentTable.Children()) { var id = ((Label)row[0]).text; row.RegisterCallback<ClickEvent>(_ => incidents.value = id); }
            Rows(candidateTable, s.Candidates.Where(c => c.IncidentId == incidents.value).Select(c => new[] { c.CandidateId, c.Action, c.Validation, c.ExpiresAtTick.ToString(), string.Join("; ", c.Assumptions) }));
            RenderReports(); UpdateActions();
        }

        private bool AllShuttlesChecked => routeShuttles.Query<Toggle>().ToList().All(t => t.value);

        // Every shuttle on the route, checked: M2 approves a route for all of them or none.
        private void RenderRouteShuttles()
        {
            routeShuttles.Clear();
            var s = session.Store.Snapshot;
            if (s == null) return;
            foreach (var vehicle in s.Vehicles.Where(v => v.RouteId == routes.value).OrderBy(v => v.VehicleId, StringComparer.Ordinal))
            {
                var toggle = new Toggle(vehicle.VehicleId) { value = true };
                toggle.RegisterValueChangedCallback(_ => InvalidateRoute());
                routeShuttles.Add(toggle);
            }
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
            if (validate == null) return;
            var s = session.Store.Snapshot;
            var edit = Current && !routeBusy && context != null && s?.Session.Status == "READY";
            validate.SetEnabled(edit);
            approve.SetEnabled(edit && AllShuttlesChecked && preview != null && preview.Approvable && DecisionRules.RoutePreviewMatches(s, preview) && !string.IsNullOrWhiteSpace(routeReason.value));
            via.SetEnabled(!session.HasPendingCommand && !routeBusy); routes.SetEnabled(!session.HasPendingCommand && !routeBusy);
            foreach (var button in offerTable.Query<Button>().ToList()) { var option = s?.ChargeOptions.FirstOrDefault(o => o.OptionId == (string)button.userData); button.SetEnabled(option != null && OfferOpen(s, option)); }
            var candidate = s?.Candidates.FirstOrDefault(c => c.CandidateId == candidates.value && c.IncidentId == incidents.value);
            var decision = Current && !incidentBusy && s?.Session.Status == "RUNNING" && candidate?.Validation == "PASS" && candidate.ExpiresAtTick >= s.Tick &&
                s.Incidents.Any(i => i.IncidentId == incidents.value && i.State == "OPTIONS_READY") && !string.IsNullOrWhiteSpace(incidentReason.value);
            previewIncident.SetEnabled(decision);
            approveIncident.SetEnabled(decision && incidentPreviewCommand != null);
            history.SetEnabled(!incidentBusy && s != null && !string.IsNullOrEmpty(incidents.value));
            debrief.SetEnabled(history.enabledSelf);
            world.RouteEditing = edit;
        }

        private void InvalidateRoute()
        {
            draftRevision++; preview = null;
            if (routeStatus != null) routeStatus.text = "Draft needs a fresh backend preview before approval.";
            if (checksTable != null) { Rows(checksTable, Enumerable.Empty<string[]>()); Rows(comparisonTable, Enumerable.Empty<string[]>()); }
            routeGraph?.Show(context, null, ParseDraftOrEmpty());
            world.SetRouteDraft(context, ParseDraftOrEmpty(), null);
            UpdateActions();
        }

        private List<long> ParseDraftOrEmpty() { try { return DecisionRules.ParseViaNodes(via?.value); } catch (ArgumentException) { return new List<long>(); } }

        private void ResetDraft()
        {
            context = null; via.value = ""; routeReason.value = "";
            draftHistory.Clear(); draftHistory.Add(""); historyIndex = 0;
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
            if (s == null || string.IsNullOrEmpty(routes.value) || routeBusy) return;
            reloadContext = false;
            var generation = ++requestGeneration;
            var route = routes.value;
            var stamp = DecisionRules.RouteDependencies(s, route);
            context = null; InvalidateRoute(); routeBusy = true; UpdateActions();
            try
            {
                var result = await api.GetRouteEditorAsync(s.SessionId, route);
                if (generation != requestGeneration || stamp != DecisionRules.RouteDependencies(session.Store.Snapshot, routes.value)) return;
                if (result.SessionEpoch != s.SessionEpoch || result.SceneRevision != s.SceneRevision || result.RouteRevision != s.Routes.Single(r => r.RouteId == route).Revision)
                    throw new ContractException("Route graph is stale; refresh it.");
                context = result; dependencies = stamp;
                routeStatus.text = "Route graph loaded; validate a proposal before approval.";
                routeAssumptions.text = string.Join("\n", result.Assumptions);
                routeGraph.Show(context, null, ParseDraftOrEmpty());
                world.SetRouteDraft(context, ParseDraftOrEmpty(), null);
            }
            // Only some routes have a graph to edit. Choosing another is not a failure, so say so here,
            // as the Qt editor does, rather than raising it.
            catch (BackendErrorException error) when (error.Error.Code == "INVALID_REFERENCE")
            {
                if (generation == requestGeneration) routeStatus.text = $"{route} can't be edited in this scenario: {error.Error.Message}.";
            }
            finally { routeBusy = false; UpdateActions(); if (reloadContext) run(LoadContext()); }
        }

        private async Task PreviewRoute()
        {
            if (!Current || context == null || routeBusy) return;
            var nodes = DecisionRules.ParseViaNodes(via.value);
            if (nodes.Any(n => !context.Nodes.Any(x => x.NodeId == n))) throw new ArgumentException("Choose via nodes from this route graph.");
            if (session.Store.Snapshot.Spatial != null && nodes.Any(n => !world.HasDraftSupport(n))) throw new ArgumentException("Choose nodes bound to a visible road surface.");
            var stamp = dependencies; var revision = draftRevision; var generation = requestGeneration;
            routeBusy = true; UpdateActions();
            try
            {
                var result = await api.PreviewRouteAsync(context.SessionId, new RouteEditRequest
                {
                    RouteId = context.RouteId, ExpectedRouteRevision = context.RouteRevision, DraftRevision = revision, ViaNodeIds = nodes,
                });
                if (generation != requestGeneration || revision != draftRevision || stamp != DecisionRules.RouteDependencies(session.Store.Snapshot, routes.value)) return;
                preview = result;
                routeStatus.text = $"Backend {result.Validation} · preview {result.PreviewId} / draft {result.PreviewRevision}";
                Rows(checksTable, result.Checks.Select(c => new[] { c.Check, c.Status, c.Mandatory ? "Mandatory" : "Advisory", c.Message + (string.IsNullOrEmpty(c.FeatureId) ? "" : " · " + c.FeatureId) }));
                Rows(comparisonTable, new[]
                {
                    new[] { "Distance (m)", Metric(result.Original, m => m.DistanceM.ToString("0.0")), Metric(result.Edited, m => m.DistanceM.ToString("0.0")) },
                    new[] { "Duration (s)", Metric(result.Original, m => m.DurationS.ToString("0.0")), Metric(result.Edited, m => m.DurationS.ToString("0.0")) },
                    new[] { "Estimated energy (kWh)", Metric(result.Original, m => m.EnergyKwh.ToString("0.0000")), Metric(result.Edited, m => m.EnergyKwh.ToString("0.0000")) },
                    new[] { "Required stops served", Metric(result.Original, Coverage), Metric(result.Edited, Coverage) },
                });
                routeGraph.Show(context, preview, nodes); world.SetRouteDraft(context, nodes, preview);
            }
            finally { routeBusy = false; UpdateActions(); if (reloadContext) run(LoadContext()); }
        }

        private async Task ApproveRoute()
        {
            if (!Current || preview == null) return;
            var approved = (preview.RouteId, preview.RouteRevision);
            if (!await session.ApproveRouteAsync(preview, routeReason.value)) return;
            // Render clears the draft once a snapshot shows the route's new revision; a refresh may
            // return before that snapshot arrives, so it cannot be checked here.
            approvedDraft = approved;
            InvalidateRoute(); await session.RefreshAsync();
            Render();
        }

        private bool OfferOpen(FullSnapshot s, ChargeOption o) => Current && s.Session.Status == "RUNNING" && !string.IsNullOrWhiteSpace(chargeReason.value) && o.Validation == "PASS" && o.ExpiresAtTick >= s.Tick;

        // A Decision button per offer. Polling refreshes every second, so the rows stay while the
        // offers are unchanged: a refresh between press and release must not delete the button.
        private void RenderOffers(FullSnapshot s)
        {
            var rows = s.ChargeOptions.OrderBy(o => o.OptionId, StringComparer.Ordinal).Select(o => (Option: o, Cells: new[]
            {
                o.VehicleId, o.Action, string.IsNullOrEmpty(o.ChargerId) ? "—" : o.ChargerId, $"{o.OutOfServiceS:0.#} s", $"{o.BatteryAfterKwh:0.##} kWh",
                o.LoopsAfter?.ToString() ?? "—", o.MinInServiceVehicles?.ToString() ?? "—", o.Validation, string.Join("; ", o.Assumptions), o.OptionId,
            })).ToList();
            var key = boundary + string.Concat(rows.Select(r => r.Option.Revision + "|" + string.Join("|", r.Cells) + "\n"));
            if (key == offersKey) return;
            offersKey = key;
            offerTable.Clear();
            foreach (var (option, cells) in rows)
            {
                var line = Row(offerTable);
                var decision = new VisualElement(); decision.AddToClassList("data-cell"); line.Add(decision);
                var label = option.Action == "CHARGE_NOW" ? "Reserve" : option.Action == "CONTINUE" ? "Keep in service" : null;
                if (label == null) decision.Add(new Label("—"));
                else
                {
                    var id = option.OptionId;
                    decision.Add(new Button(() => run(SubmitCharge(id))) { text = label, userData = id, tooltip = "Submit this charging decision with revision checks." });
                }
                foreach (var value in cells) { var cell = new Label(value) { tooltip = value }; cell.AddToClassList("data-cell"); line.Add(cell); }
            }
        }

        private async Task SubmitCharge(string optionId)
        {
            if (!Current) return;
            var command = DecisionRules.ChargeCommand(session.Store.Snapshot, optionId, chargeReason.value);
            if (await session.SubmitAsync(command)) await session.RefreshAsync();
            UpdateActions();
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

        private async Task IncidentReport(bool showDebrief)
        {
            var s = session.Store.Snapshot; if (s == null || incidentBusy || string.IsNullOrEmpty(incidents.value)) return;
            var id = incidents.value; var stamp = boundary; incidentBusy = true; UpdateActions();
            try
            {
                var result = await api.GetIncidentReportAsync(s.SessionId, id, showDebrief);
                if (stamp != boundary || incidents.value != id) return;
                ClearIncidentPreview();
                ClearEvidence();
                if (!showDebrief)
                {
                    historySnapshots.AddRange(((JArray)result["snapshots"]).Select(raw => ContractParser.Parse<FullSnapshot>(raw.ToString())));
                    historySample.highValue = Math.Max(0, historySnapshots.Count - 1);
                    historySample.SetValueWithoutNotify(0);
                    historySample.style.display = historySnapshots.Count > 1 ? DisplayStyle.Flex : DisplayStyle.None;
                    ShowHistorySample();
                }
                incidentReport.text = (showDebrief ? "DEBRIEF · provisional / uncalibrated\n" : "HISTORICAL SIMULATION · complete: " + result["complete"] + "\n" + result["unavailable_reason"] + "\n") + FormatDocument(result);
            }
            finally { incidentBusy = false; UpdateActions(); }
        }

        private void FocusIncident()
        {
            var incident = session.Store.Snapshot?.Incidents.FirstOrDefault(i => i.IncidentId == incidents.value);
            if (incident != null) { SelectVehicle(incident.VehicleId); world.FocusSelected(); }
        }

        private void ClearEvidence()
        {
            historySnapshots.Clear(); evidenceMap.Show(null);
            evidenceMap.style.display = historySample.style.display = DisplayStyle.None;
            evidenceStatus.text = "";
        }

        private void ShowHistorySample()
        {
            if (historySnapshots.Count == 0) return;
            var sample = historySnapshots[Math.Min(historySample.value, historySnapshots.Count - 1)];
            evidenceMap.Show(sample); evidenceMap.style.display = DisplayStyle.Flex;
            evidenceStatus.text = $"Historical simulation · sample {historySample.value + 1}/{historySnapshots.Count} · tick {sample.Tick}.";
        }

        private void AppendNode(long id)
        {
            if (!Current || routeBusy || context == null || session.Store.Snapshot.Session.Status != "READY" || id == context.StartNodeId || id == context.DestinationNodeId) return;
            var nodes = DecisionRules.ParseViaNodes(via.value); if (nodes.Count >= 16) return;
            nodes.Add(id); via.value = string.Join(", ", nodes);
        }

        private void RestoreDraft(int delta)
        {
            if (routeBusy || session.HasPendingCommand || historyIndex + delta < 0 || historyIndex + delta >= draftHistory.Count) return;
            restoring = true; via.value = draftHistory[historyIndex += delta]; restoring = false;
        }

        private void RenderReports()
        {
            Rows(ledgerTable, ledger.Select(o => new[] { o.CommandId, $"{o.TargetType} {o.TargetId}", o.Status, string.IsNullOrEmpty(o.FailureCode) ? "—" : o.FailureCode, o.AppliedTick?.ToString() ?? "—" }));
            Rows(eventsTable, session.Store.Events.Select(e => new[] { e.EventId, e.EventSequence.ToString(), e.Tick.ToString(), e.EventType, e.EntityId }));
        }

        private static string Metric(RouteMetrics metrics, Func<RouteMetrics, string> read) => metrics == null ? "UNKNOWN" : read(metrics);
        private static string Coverage(RouteMetrics m) => m.StopCoverage.Count == 0 ? "None required" :
            string.Join(", ", m.StopCoverage.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => s.Key + (s.Value ? ": served" : ": MISSED")));

        // Human-readable nested fields preserve the backend's assumptions, evidence and rubric.
        private static string FormatDocument(JToken token, int depth = 0)
        {
            if (token == null) return "Unavailable";
            if (token is JObject obj) return string.Join("\n", obj.Properties().Where(p => p.Name != "snapshots").Select(p =>
                new string(' ', depth * 2) + p.Name.Replace('_', ' ') + ": " + (p.Value is JContainer ? "\n" : "") + FormatDocument(p.Value, depth + 1))) +
                (obj["snapshots"] is JArray snapshots ? "\nHistory samples:\n" + string.Join("\n", snapshots.Select(s => $"tick {s["tick"]} · simulation time {s["simulation_time_s"]} s\n" +
                    string.Join("\n", (s["vehicles"] as JArray ?? new JArray()).Select(v => $"  {v["vehicle_id"]}: {v["service_state"]} · {v["battery_kwh"]} kWh · position {v["position"]?.ToString(Formatting.None)}")))) : "");
            if (token is JArray array) return string.Join("\n", array.Select(t => FormatDocument(t, depth)));
            return token.Type == JTokenType.Null ? "Unavailable" : token.ToString();
        }

        private void ShowPage(int index)
        {
            // Only Fleet Overview has the map beside it; the other pages take the full width, as in Qt.
            foreach (var view in operationalViews) view.style.display = index == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            // The Fleet List button may have hidden the panel these pages live in.
            pages[index].parent.style.display = DisplayStyle.Flex;
            for (var i = 0; i < pages.Count; i++)
            {
                pages[i].style.display = i == index ? DisplayStyle.Flex : DisplayStyle.None;
                if (i < navButtons.Count) navButtons[i].EnableInClassList("nav-item-selected", i == index);
            }
            // The top menu shows the page's section: Reports and Scenarios, or everything else
            SectionChanged?.Invoke(index == ReportsPage ? "reports" : index == ScenariosPage ? "scenarios" : "operations");
        }

        // A refused start sends the trainee here to approve a detour first.
        public void ShowRoutePlanning() { ShowPage(1); Render(); }
        public void ShowSection(string section) { ShowPage(section == "reports" ? ReportsPage : section == "scenarios" ? ScenariosPage : 0); Render(); }
        private static VisualElement Row(VisualElement parent) { var row = new VisualElement(); row.AddToClassList("row"); parent.Add(row); return row; }
        private static Label Text(VisualElement parent, string text) { var label = new Label(text); label.AddToClassList("workspace-text"); parent.Add(label); return label; }
        private static VisualElement Table(VisualElement parent, string title, params string[] columns)
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
        private static void Rows(VisualElement table, IEnumerable<string[]> values)
        {
            table.Clear();
            foreach (var row in values)
            {
                var line = Row(table);
                foreach (var value in row) { var cell = new Label(value ?? "—") { tooltip = value }; cell.AddToClassList("data-cell"); line.Add(cell); }
            }
        }
        private static Button Bind(VisualElement root, string name, Action action) { var button = root.Q<Button>(name); button.clicked += action; return button; }
        private static void Choices(DropdownField field, IEnumerable<string> values)
        {
            var choices = values.OrderBy(v => v, StringComparer.Ordinal).ToList();
            if (field.choices.SequenceEqual(choices)) return;
            var selected = field.value; field.choices = choices;
            field.SetValueWithoutNotify(choices.Contains(selected) ? selected : choices.FirstOrDefault() ?? "");
        }
    }
}
