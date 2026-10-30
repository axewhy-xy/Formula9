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
        private readonly DropdownField routes, chargeOptions, incidents, candidates;
        private readonly TextField via, routeReason, chargeReason, incidentReason;
        private readonly Label routeStatus, routeDetails, chargingData, incidentData, incidentReport, reports, selection;
        private readonly Button validate, approve, charge, previewIncident, approveIncident, history, debrief;
        private readonly FleetMap routeMap, evidenceMap;
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

        public TrainingWorkspace(VisualElement root, TrainingApi api, SessionController session, WorldView world,
            Action<Task> run, Action<string> notify)
        {
            this.api = api; this.session = session; this.world = world; this.run = run; this.notify = notify;
            var host = root.Q("fleet-panel");
            var fleetChildren = host.Children().ToList();
            host.Clear();
            var nav = new VisualElement(); nav.AddToClassList("row"); nav.AddToClassList("workspace-nav"); host.Add(nav);
            var fleet = Page(host, nav, "Fleet");
            foreach (var child in fleetChildren) fleet.Add(child);
            selection = Text(fleet, "Select a shuttle in the fleet or map.");
            var cameraActions = Row(fleet);
            ActionButton(cameraActions, "Follow selected", () => world.FollowSelected());
            ActionButton(cameraActions, "Overview", () => world.Overview());

            var route = Page(host, nav, "Route planning");
            routes = new DropdownField("Route"); route.Add(routes);
            routes.RegisterValueChangedCallback(_ => { ResetDraft(); dependencies = null; RequestContext(); });
            ActionButton(route, "Reload route graph", RequestContext);
            routeStatus = Text(route, "Choose a route to load its graph.");
            routeMap = new FleetMap { name = "route-map" }; routeMap.AddToClassList("route-map"); route.Add(routeMap);
            routeMap.NodeSelected += AppendNode;
            via = new TextField("Via node IDs (ordered)") { maxLength = 512 }; route.Add(via);
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
            var draftActions = Row(route);
            ActionButton(draftActions, "Undo", () => RestoreDraft(-1));
            ActionButton(draftActions, "Redo", () => RestoreDraft(1));
            ActionButton(draftActions, "Cancel draft", () => { if (!session.HasPendingCommand) { via.value = ""; routeReason.value = ""; InvalidateRoute(); } });
            routeReason = new TextField("Approval reason") { maxLength = 2048 }; route.Add(routeReason);
            routeReason.RegisterValueChangedCallback(_ => UpdateActions());
            var routeActions = Row(route);
            validate = ActionButton(routeActions, "Validate / preview", () => run(PreviewRoute()));
            approve = ActionButton(routeActions, "Approve for assigned shuttles", () => run(ApproveRoute()));
            routeDetails = Text(route, "");

            var charging = Page(host, nav, "Charging");
            chargingData = Text(charging, "Battery and charger state are authoritative. Forecasts and decision consequences are backend estimates.");
            batteryTable = Table(charging, "Battery state", "Shuttle", "Charge / capacity (kWh)", "Service state", "Reserve margin (kWh, est.)", "Loops (est.)", "Next depot / ETA (est.)", "Above reserve (est.)");
            chargerTable = Table(charging, "Charger occupancy", "Charger", "Free / total ports", "Occupied shuttles", "Power (kW)");
            offerTable = Table(charging, "Decision consequences (estimates)", "Option", "Shuttle", "Action", "Charger", "Out of service (s)", "Battery after (kWh)", "Loops after", "Min. in service", "Validation", "Assumptions");
            chargeOptions = new DropdownField("Decision option"); charging.Add(chargeOptions);
            chargeOptions.RegisterValueChangedCallback(_ => UpdateActions());
            chargeReason = new TextField("Decision reason") { maxLength = 2048 }; charging.Add(chargeReason);
            chargeReason.RegisterValueChangedCallback(_ => UpdateActions());
            charge = ActionButton(charging, "Reserve / Continue service", () => run(SubmitCharge()));

            var incident = Page(host, nav, "Incidents");
            incidents = new DropdownField("Incident"); incident.Add(incidents);
            incidents.RegisterValueChangedCallback(_ => { ClearIncidentPreview(); Render(); });
            incidentData = Text(incident, "");
            incidentTable = Table(incident, "Incident list", "Incident", "Shuttle", "State", "Detected tick");
            candidateTable = Table(incident, "Offered options", "Candidate", "Action", "Validation", "Expires tick", "Assumptions");
            ActionButton(incident, "Focus incident shuttle", FocusIncident);
            candidates = new DropdownField("Option"); incident.Add(candidates);
            candidates.RegisterValueChangedCallback(_ => { ClearIncidentPreview(); UpdateActions(); });
            incidentReason = new TextField("Decision reason") { maxLength = 2048 }; incident.Add(incidentReason);
            incidentReason.RegisterValueChangedCallback(_ => { ClearIncidentPreview(); UpdateActions(); });
            var incidentActions = Row(incident);
            previewIncident = ActionButton(incidentActions, "Preview option", () => run(PreviewIncident()));
            approveIncident = ActionButton(incidentActions, "Approve previewed option", () => run(ApproveIncident()));
            history = ActionButton(incidentActions, "History", () => run(IncidentReport(false)));
            debrief = ActionButton(incidentActions, "Debrief", () => run(IncidentReport(true)));
            incidentReport = Text(incident, "Preview, historical evidence and debrief appear here. Live state stays in the fleet view.");
            evidenceStatus = Text(incident, "");
            evidenceMap = new FleetMap { name = "incident-evidence-map" }; evidenceMap.AddToClassList("route-map"); incident.Add(evidenceMap);
            historySample = new SliderInt("History sample", 0, 0); incident.Add(historySample);
            historySample.RegisterValueChangedCallback(_ => ShowHistorySample());
            evidenceMap.style.display = historySample.style.display = DisplayStyle.None;

            var report = Page(host, nav, "Reports"); reports = Text(report, "");
            ledgerTable = Table(report, "Command ledger", "Command", "Target", "Status", "Failure code", "Effective tick");
            eventsTable = Table(report, "Event log", "Event", "Sequence", "Tick", "Type", "Entity", "Data");
            session.SnapshotAccepted += (_, __) => { acceptedAt = Time.realtimeSinceStartup; Render(); };
            session.EventsAccepted += _ => RenderReports();
            session.AvailabilityChanged += _ => { if (!session.Available) { InvalidateRoute(); ClearIncidentPreview(); } Render(); };
            session.CommandLedgerChanged += outcome =>
            {
                ledger.RemoveAll(o => o.CommandId == outcome.CommandId); ledger.Add(outcome);
                if (ledger.Count > 1000) ledger.RemoveAt(0);
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
                boundary = currentBoundary; dependencies = null; context = null; requestGeneration++;
                if (ledgerSession != s?.SessionId) { ledger.Clear(); ledgerSession = s?.SessionId; }
                routeReason.value = chargeReason.value = incidentReason.value = "";
                via.value = ""; draftHistory.Clear(); draftHistory.Add(""); historyIndex = 0;
                ClearIncidentPreview(); ClearEvidence(); incidentReport.text = "";
                routeMap.Show(null); world.SetRouteDraft(null, null, null);
            }
            if (s == null) { UpdateActions(); return; }
            Choices(routes, s.Routes.Select(r => r.RouteId));
            var changed = DecisionRules.RouteDependencies(s, routes.value);
            if (changed != dependencies)
            {
                dependencies = changed; context = null; InvalidateRoute();
                RequestContext();
            }
            routeMap.Show(s, context, preview);
            Rows(batteryTable, s.Vehicles.Select(v =>
            {
                var f = s.EnergyForecasts.FirstOrDefault(e => e.VehicleId == v.VehicleId);
                return new[] { v.VehicleId, $"{v.BatteryKwh:0.00} / {v.BatteryCapacityKwh:0.00}", v.ServiceState,
                    f?.ReserveMarginKwh.ToString("0.00") ?? "unknown", f?.LoopsRemaining?.ToString() ?? "unknown",
                    $"{f?.NextDepotId ?? "unknown"} / {f?.NextDepotEtaS?.ToString("0.0") ?? "unknown"} s", f?.ReachesDepotAboveReserve?.ToString() ?? "unknown" };
            }));
            Rows(chargerTable, s.Chargers.Select(c => new[] { c.ChargerId, $"{c.PortCount - c.OccupiedVehicleIds.Count} / {c.PortCount}", string.Join(", ", c.OccupiedVehicleIds), c.PowerKw.ToString("0.0") }));
            Rows(offerTable, s.ChargeOptions.Select(o => new[] { o.OptionId, o.VehicleId, o.Action, o.ChargerId, o.OutOfServiceS.ToString("0.0"), o.BatteryAfterKwh.ToString("0.00"), o.LoopsAfter?.ToString() ?? "unknown", o.MinInServiceVehicles?.ToString() ?? "unknown", o.Validation, string.Join("; ", o.Assumptions) }));
            Choices(chargeOptions, s.ChargeOptions.Select(o => o.OptionId));
            Choices(incidents, s.Incidents.Select(i => i.IncidentId));
            var selectedIncident = s.Incidents.FirstOrDefault(i => i.IncidentId == incidents.value);
            var newIncidentBoundary = selectedIncident == null ? null : $"{boundary}/{selectedIncident.IncidentId}/{selectedIncident.AggregateRevision}/" +
                string.Join(";", s.Candidates.Where(c => c.IncidentId == selectedIncident.IncidentId).Select(c => $"{c.CandidateId}:{c.Revision}:{c.Validation}"));
            if (incidentBoundary != newIncidentBoundary) { incidentBoundary = newIncidentBoundary; ClearIncidentPreview(); ClearEvidence(); incidentReport.text = ""; }
            Choices(candidates, selectedIncident?.CandidateIds ?? Enumerable.Empty<string>());
            incidentData.text = s.Incidents.Count == 0 ? "No incidents detected." : "Select an incident and option, enter a reason, then preview the consequences before approval.";
            Rows(incidentTable, s.Incidents.Select(i => new[] { i.IncidentId, i.VehicleId, i.State, i.DetectedTick.ToString() }));
            Rows(candidateTable, s.Candidates.Where(c => c.IncidentId == incidents.value).Select(c => new[] { c.CandidateId, c.Action, c.Validation, c.ExpiresAtTick.ToString(), string.Join("; ", c.Assumptions) }));
            RenderReports(); UpdateActions();
        }

        private bool Current => session.Available && !session.HasPendingCommand && Time.realtimeSinceStartup - acceptedAt <= 5;

        private void UpdateActions()
        {
            if (validate == null) return;
            var s = session.Store.Snapshot;
            var edit = Current && !routeBusy && context != null && s?.Session.Status == "READY";
            validate.SetEnabled(edit);
            approve.SetEnabled(edit && preview != null && preview.Approvable && DecisionRules.RoutePreviewMatches(s, preview) && !string.IsNullOrWhiteSpace(routeReason.value));
            via.SetEnabled(!session.HasPendingCommand && !routeBusy); routes.SetEnabled(!session.HasPendingCommand && !routeBusy);
            charge.SetEnabled(Current && s?.Session.Status == "RUNNING" && !string.IsNullOrWhiteSpace(chargeReason.value) && s.ChargeOptions.Any(o => o.OptionId == chargeOptions.value && o.Validation == "PASS" && o.ExpiresAtTick >= s.Tick));
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
            if (routeDetails != null) routeDetails.text = "";
            routeMap?.Show(session.Store.Snapshot, context, null);
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
                routeStatus.text = $"{route} / revision {result.RouteRevision} · start {result.StartNodeId} → destination {result.DestinationNodeId}\n" +
                    "Click graph nodes to add ordered via points. Red edges are closed.\n" + string.Join("\n", result.Assumptions);
                routeMap.Show(session.Store.Snapshot, context, null);
                world.SetRouteDraft(context, ParseDraftOrEmpty(), null);
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
                routeDetails.text = string.Join("\n", result.Checks.Select(c => $"{c.Check}: {c.Status} ({(c.Mandatory ? "mandatory" : "advisory")}) · {c.Message}")) +
                    "\n\nOriginal (estimate): " + Metrics(result.Original) + "\nEdited (estimate): " + Metrics(result.Edited) +
                    "\n\nApproval targets: " + string.Join(", ", session.Store.Snapshot.Vehicles.Where(v => v.RouteId == result.RouteId).Select(v => v.VehicleId));
                routeMap.Show(session.Store.Snapshot, context, preview); world.SetRouteDraft(context, nodes, preview);
            }
            finally { routeBusy = false; UpdateActions(); if (reloadContext) run(LoadContext()); }
        }

        private async Task ApproveRoute()
        {
            if (!Current || preview == null) return;
            var routeId = preview.RouteId;
            var basedOn = preview.RouteRevision;
            if (!await session.ApproveRouteAsync(preview, routeReason.value)) return;
            InvalidateRoute(); await session.RefreshAsync();
            // Once applied, the route has a new revision and this draft is finished: start clean on
            // the new graph. A queued or rejected approval keeps the draft for another try.
            var route = session.Store.Snapshot?.Routes.FirstOrDefault(r => r.RouteId == routeId);
            if (route != null && route.Revision > basedOn)
            {
                ResetDraft();
                RequestContext();
            }
        }

        private async Task SubmitCharge()
        {
            if (!Current) return;
            var command = DecisionRules.ChargeCommand(session.Store.Snapshot, chargeOptions.value, chargeReason.value);
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
            reports.text = "Latest 1,000 events and 1,000 commands observed by this client. Scroll horizontally to inspect every column.";
            Rows(ledgerTable, ledger.AsEnumerable().Reverse().Select(o => new[] { o.CommandId, $"{o.TargetType}/{o.TargetId}", o.Status, o.FailureCode, o.AppliedTick?.ToString() ?? "unknown" }));
            Rows(eventsTable, session.Store.Events.Reverse().Select(e => new[] { e.EventId, e.EventSequence.ToString(), e.Tick.ToString(), e.EventType, e.EntityId, e.Data?.ToString(Formatting.None) }));
        }

        private static string Metrics(RouteMetrics m) => m == null ? "UNKNOWN" :
            $"{m.DistanceM:0.0} m · {m.DurationS:0.0} s · {m.EnergyKwh:0.0000} kWh · stops: " + string.Join(", ", m.StopCoverage.Select(s => s.Key + (s.Value ? " served" : " MISSED")));

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

        private VisualElement Page(VisualElement host, VisualElement nav, string name)
        {
            var index = pages.Count; var page = new ScrollView(); page.AddToClassList("workspace-page"); host.Add(page); pages.Add(page);
            ActionButton(nav, name, () => { ShowPage(index); Render(); }); return page;
        }
        private void ShowPage(int index) { for (var i = 0; i < pages.Count; i++) pages[i].style.display = i == index ? DisplayStyle.Flex : DisplayStyle.None; }
        private static VisualElement Row(VisualElement parent) { var row = new VisualElement(); row.AddToClassList("row"); parent.Add(row); return row; }
        private static Label Text(VisualElement parent, string text) { var label = new Label(text); label.AddToClassList("workspace-text"); parent.Add(label); return label; }
        private static VisualElement Table(VisualElement parent, string title, params string[] columns)
        {
            Text(parent, title);
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
        private static Button ActionButton(VisualElement parent, string text, Action action) { var button = new Button(action) { text = text }; parent.Add(button); return button; }
        private static void Choices(DropdownField field, IEnumerable<string> values)
        {
            var choices = values.OrderBy(v => v, StringComparer.Ordinal).ToList();
            if (field.choices.SequenceEqual(choices)) return;
            var selected = field.value; field.choices = choices;
            field.SetValueWithoutNotify(choices.Contains(selected) ? selected : choices.FirstOrDefault() ?? "");
        }
    }
}
