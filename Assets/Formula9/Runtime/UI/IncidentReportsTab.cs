/*
 * The Reports page's Incidents tab: an incident's history and debrief.
 *
 * Author: Jian Wei
 * Date Created: 14/11/2026
 * Last Modified: 14/11/2026
 *
 * Description:
 *     Part of the Unity client's Reports page. Replays the historical simulation around an
 *     incident on a map, one sample at a time, and shows its debrief. Moved here from the
 *     Incident Monitor, which is being removed (issue #86).
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.Session;
using Formula9.World;
using Newtonsoft.Json.Linq;
using UnityEngine.UIElements;

namespace Formula9.UI
{
    public sealed class IncidentReportsTab
    {
        private readonly TrainingApi api;
        private readonly SessionController session;
        private readonly DropdownField incidents;
        private readonly Button history, debrief;
        private readonly Label report, evidenceStatus, summary;
        private readonly VisualElement incidentTable;
        private readonly FleetMap evidenceMap;
        private readonly SliderInt historySample;
        private readonly List<FullSnapshot> historySnapshots = new List<FullSnapshot>();
        private string boundary;
        private bool busy;

        public IncidentReportsTab(VisualElement root, TrainingApi api, SessionController session, WorldView world, Action<Task> run)
        {
            this.api = api; this.session = session;
            incidents = root.Q<DropdownField>("reports-incidents");
            incidents.RegisterValueChangedCallback(_ => { Clear(); UpdateActions(); });
            incidentTable = TrainingWorkspace.Table(root.Q("reports-incident-table-host"), "", "Incident ID", "Shuttle ID", "Reaches it", "State", "Decided", "Detected tick");
            summary = root.Q<Label>("reports-incident-summary");
            history = root.Q<Button>("reports-incident-history");
            history.clicked += () => run(Load(false));
            debrief = root.Q<Button>("reports-incident-debrief");
            debrief.clicked += () => run(Load(true));
            report = root.Q<Label>("reports-incident-report");
            evidenceStatus = root.Q<Label>("reports-evidence-status");
            evidenceMap = root.Q<FleetMap>("reports-evidence-map");
            world.UseRealMap(evidenceMap);
            historySample = root.Q<SliderInt>("reports-history-sample");
            historySample.RegisterValueChangedCallback(_ => ShowSample());
        }

        // Called on every snapshot. A new session or epoch clears what was shown.
        public void Render(FullSnapshot s)
        {
            var current = s == null ? null : $"{s.SessionId}/{s.SessionEpoch}";
            if (boundary != current) { boundary = current; Clear(); }
            TrainingWorkspace.Choices(incidents, s?.Incidents.Select(i => i.IncidentId) ?? Enumerable.Empty<string>());
            var list = s?.Incidents.OrderBy(i => i.IncidentId, StringComparer.Ordinal).ToList() ?? new List<Incident>();
            TrainingWorkspace.Rows(incidentTable, list.Select(i => new[] { i.IncidentId, i.VehicleId ?? "—", IncidentRecord.Reach(i.Exposures, 2) ?? "—", i.State, TrainingPresenter.Responses(i) ?? "—", i.DetectedTick.ToString() }));
            // Clicking a row picks that incident, as the menu does
            foreach (var row in incidentTable.Children()) { var id = ((Label)row[0]).text; row.RegisterCallback<ClickEvent>(_ => incidents.value = id); }
            var chosen = list.FirstOrDefault(i => i.IncidentId == incidents.value);
            summary.text = chosen == null ? "" : TrainingPresenter.IncidentDescription(chosen);
            UpdateActions();
        }

        private void UpdateActions()
        {
            history.SetEnabled(!busy && session.Store.Snapshot != null && !string.IsNullOrEmpty(incidents.value));
            debrief.SetEnabled(history.enabledSelf);
        }

        private async Task Load(bool showDebrief)
        {
            var s = session.Store.Snapshot;
            if (s == null || busy || string.IsNullOrEmpty(incidents.value)) return;
            var id = incidents.value; var stamp = boundary;
            busy = true; UpdateActions();
            try
            {
                var result = await api.GetIncidentReportAsync(s.SessionId, id, showDebrief);
                // The session or the chosen incident changed while waiting: this answer is stale
                if (stamp != boundary || incidents.value != id) return;
                Clear();
                if (!showDebrief)
                {
                    historySnapshots.AddRange(((JArray)result["snapshots"]).Select(raw => ContractParser.Parse<FullSnapshot>(raw.ToString())));
                    historySample.highValue = Math.Max(0, historySnapshots.Count - 1);
                    historySample.SetValueWithoutNotify(0);
                    historySample.style.display = historySnapshots.Count > 1 ? DisplayStyle.Flex : DisplayStyle.None;
                    ShowSample();
                }
                report.text = (showDebrief ? "DEBRIEF · provisional / uncalibrated\n" : "HISTORICAL SIMULATION · complete: " + result["complete"] + "\n" + result["unavailable_reason"] + "\n") + TrainingWorkspace.FormatDocument(result);
            }
            finally { busy = false; UpdateActions(); }
        }

        private void Clear()
        {
            historySnapshots.Clear(); evidenceMap.Show(null);
            evidenceMap.style.display = historySample.style.display = DisplayStyle.None;
            evidenceStatus.text = report.text = "";
        }

        private void ShowSample()
        {
            if (historySnapshots.Count == 0) return;
            var sample = historySnapshots[Math.Min(historySample.value, historySnapshots.Count - 1)];
            evidenceMap.Show(sample); evidenceMap.style.display = DisplayStyle.Flex;
            evidenceStatus.text = $"Historical simulation · sample {historySample.value + 1}/{historySnapshots.Count} · tick {sample.Tick}.";
        }
    }
}