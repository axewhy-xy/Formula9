/*
 * Reports page controls, filtering and result presentation.
 *
 * Author: Nur Syazliyarni Binte Zazali
 * Date Created: 04/11/2026
 * Last Modified: 04/11/2026
 *
 * Description:
 *     Binds the event and command search fields, filter dropdowns, matching counts,
 *     clear buttons and empty states. Keeps each tab's filters during live updates
 *     and resets them when the selected training session changes.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.Session;
using UnityEngine.UIElements;
using TrainingEvent = Formula9.Contracts.Event;

namespace Formula9.UI
{
    // Controls stay in place during polling so typing, focus and the chosen filters survive
    // incoming records. Each tab filters only the bounded records observed by this client.
    public sealed class ReportsPage
    {
        private const string AllTypes = "All event types", AllStatuses = "All statuses";
        private readonly TextField eventSearch, commandSearch;
        private readonly DropdownField eventType, commandStatus;
        private readonly Label eventCount, commandCount, eventEmpty, commandEmpty;
        private readonly Button clearEvents, clearCommands;
        private readonly VisualElement eventHost, commandHost, eventRows, commandRows;
        private IReadOnlyList<TrainingEvent> events = Array.Empty<TrainingEvent>();
        private IReadOnlyList<CommandOutcome> commands = Array.Empty<CommandOutcome>();
        private readonly List<(string Id, long Sequence, long Tick, string Type, string Entity)> eventState =
            new List<(string, long, long, string, string)>();
        private readonly List<(string Id, string TargetType, string TargetId, string Status, string Failure, long? Tick)> commandState =
            new List<(string, string, string, string, string, long?)>();
        private string sessionId;

        public ReportsPage(VisualElement root)
        {
            eventSearch = root.Q<TextField>("reports-event-search");
            commandSearch = root.Q<TextField>("reports-command-search");
            eventType = root.Q<DropdownField>("reports-event-type");
            commandStatus = root.Q<DropdownField>("reports-command-status");
            eventCount = root.Q<Label>("reports-event-count");
            commandCount = root.Q<Label>("reports-command-count");
            eventEmpty = root.Q<Label>("reports-event-empty");
            commandEmpty = root.Q<Label>("reports-command-empty");
            clearEvents = root.Q<Button>("reports-events-clear");
            clearCommands = root.Q<Button>("reports-commands-clear");
            eventHost = root.Q("events-table-host");
            commandHost = root.Q("ledger-table-host");
            eventRows = TrainingWorkspace.Table(eventHost, "", "Event ID", "Sequence", "Tick", "Type", "Entity ID");
            commandRows = TrainingWorkspace.Table(commandHost, "", "Command ID", "Target", "Status", "Failure code", "Effective tick");

            eventSearch.RegisterValueChangedCallback(_ => RenderEvents());
            eventType.RegisterValueChangedCallback(_ => RenderEvents());
            commandSearch.RegisterValueChangedCallback(_ => RenderCommands());
            commandStatus.RegisterValueChangedCallback(_ => RenderCommands());
            clearEvents.clicked += () => { Reset(eventSearch, eventType, AllTypes); RenderEvents(); };
            clearCommands.clicked += () => { Reset(commandSearch, commandStatus, AllStatuses); RenderCommands(); };
            Reset(eventSearch, eventType, AllTypes);
            Reset(commandSearch, commandStatus, AllStatuses);
            RenderEvents(); RenderCommands();
        }

        public void Render(string selectedSessionId, IReadOnlyList<TrainingEvent> retainedEvents, IReadOnlyList<CommandOutcome> retainedCommands)
        {
            var sessionChanged = sessionId != selectedSessionId;
            if (sessionChanged)
            {
                sessionId = selectedSessionId;
                Reset(eventSearch, eventType, AllTypes);
                Reset(commandSearch, commandStatus, AllStatuses);
            }
            events = retainedEvents;
            commands = retainedCommands;
            // Snapshot polling can call this repeatedly without new report data. Compare the
            // actual displayed/searchable values: command objects can change status in place.
            var eventsChanged = CaptureChanges(eventState, events, EventKey);
            var commandsChanged = CaptureChanges(commandState, commands, CommandKey);
            if (sessionChanged || eventsChanged) RenderEvents();
            if (sessionChanged || commandsChanged) RenderCommands();
        }

        private static (string, long, long, string, string) EventKey(TrainingEvent record) =>
            (record.EventId, record.EventSequence, record.Tick, record.EventType, record.EntityId);

        private static (string, string, string, string, string, long?) CommandKey(CommandOutcome record) =>
            (record.CommandId, record.TargetType, record.TargetId, record.Status, record.FailureCode, record.AppliedTick);

        private static bool CaptureChanges<TRecord, TKey>(List<TKey> previous, IReadOnlyList<TRecord> records, Func<TRecord, TKey> key)
        {
            var changed = previous.Count != records.Count;
            for (var i = 0; !changed && i < records.Count; i++)
                changed = !EqualityComparer<TKey>.Default.Equals(previous[i], key(records[i]));
            if (!changed) return false;
            previous.Clear();
            for (var i = 0; i < records.Count; i++) previous.Add(key(records[i]));
            return true;
        }

        private void RenderEvents()
        {
            UpdateChoices(eventType, AllTypes, events.Select(record => record.EventType));
            var selected = eventType.value == AllTypes ? null : eventType.value;
            var filtered = ReportFilter.Events(events, eventSearch.value, selected);
            TrainingWorkspace.Rows(eventRows, filtered.Select(record => new[]
                { record.EventId, record.EventSequence.ToString(), record.Tick.ToString(), record.EventType, record.EntityId }));
            eventCount.text = $"Showing {filtered.Count} of {events.Count} retained events";
            eventEmpty.text = events.Count == 0 ? "No events observed in this session yet." : "No events match these filters. Try another search or clear the filters.";
            Show(eventEmpty, filtered.Count == 0); Show(eventHost, filtered.Count > 0);
            clearEvents.SetEnabled(eventSearch.value.Length > 0 || selected != null);
        }

        private void RenderCommands()
        {
            UpdateChoices(commandStatus, AllStatuses, commands.Select(record => record.Status));
            var selected = commandStatus.value == AllStatuses ? null : commandStatus.value;
            var filtered = ReportFilter.Commands(commands, commandSearch.value, selected);
            TrainingWorkspace.Rows(commandRows, filtered.Select(record => new[]
                { record.CommandId, $"{record.TargetType} {record.TargetId}", record.Status,
                    string.IsNullOrEmpty(record.FailureCode) ? "—" : record.FailureCode, record.AppliedTick?.ToString() ?? "—" }));
            commandCount.text = $"Showing {filtered.Count} of {commands.Count} retained commands";
            commandEmpty.text = commands.Count == 0 ? "No commands observed in this session yet." : "No commands match these filters. Try another search or clear the filters.";
            Show(commandEmpty, filtered.Count == 0); Show(commandHost, filtered.Count > 0);
            clearCommands.SetEnabled(commandSearch.value.Length > 0 || selected != null);
        }

        private static void Reset(TextField search, DropdownField filter, string all)
        {
            search.SetValueWithoutNotify("");
            filter.SetValueWithoutNotify(all);
        }

        private static void UpdateChoices(DropdownField field, string all, IEnumerable<string> values)
        {
            var selected = string.IsNullOrEmpty(field.value) ? all : field.value;
            // Keep an active filter even when its last record leaves the retained window or a
            // pending command changes status. A live update must not silently broaden the view.
            var choices = new[] { all }.Concat(values.Append(selected).Where(value => !string.IsNullOrEmpty(value) && value != all)
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)).ToList();
            if (!field.choices.SequenceEqual(choices)) field.choices = choices;
            field.SetValueWithoutNotify(selected);
        }

        private static void Show(VisualElement element, bool visible) =>
            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
    }
}
