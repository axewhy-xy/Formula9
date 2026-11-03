/*
 * UI tests for the Reports page's filtering controls.
 *
 * Author: Nur Syazliyarni Binte Zazali
 * Date Created: 04/11/2026
 * Last Modified: 04/11/2026
 *
 * Description:
 *     Exercises the Reports layout on an attached Unity UI Toolkit panel. Verifies
 *     filtered rows, matching counts, empty states, independent clear buttons,
 *     filter retention during live updates and resets when switching sessions.
 */

using System.Linq;
using Formula9.Session;
using Formula9.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using TrainingEvent = Formula9.Contracts.Event;

namespace Formula9.Tests
{
    public class ReportsPageTests
    {
        private VisualElement root;
        private ReportsPage page;
        private ReportsTestWindow window;

        private sealed class ReportsTestWindow : EditorWindow { }

        [SetUp]
        public void OpenReports()
        {
            root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Formula9/UI/Pages/Reports.uxml").CloneTree();
            // Value-change events need a real panel, just as they do in the running app.
            window = ScriptableObject.CreateInstance<ReportsTestWindow>();
            window.ShowUtility();
            window.rootVisualElement.Add(root);
            page = new ReportsPage(root);
        }

        [TearDown]
        public void CloseReports()
        {
            if (window != null) window.Close();
        }

        private static TrainingEvent[] Events() => new[]
        {
            new TrainingEvent { EventId = "event-01", EventSequence = 1, Tick = 10, EntityId = "shuttle-01", EventType = "BATTERY_LOW" },
            new TrainingEvent { EventId = "event-02", EventSequence = 2, Tick = 11, EntityId = "shuttle-02", EventType = "BATTERY_LOW" },
            new TrainingEvent { EventId = "event-03", EventSequence = 3, Tick = 12, EntityId = "shuttle-01", EventType = "INCIDENT_RESOLVED" },
        };

        private static CommandOutcome[] Commands() => new[]
        {
            new CommandOutcome { CommandId = "command-01", TargetType = "VEHICLE", TargetId = "shuttle-01", Status = "APPLIED", AppliedTick = 10 },
            new CommandOutcome { CommandId = "command-02", TargetType = "ROUTE", TargetId = "route-01", Status = "REJECTED", FailureCode = "STALE_REVISION" },
        };

        private string[] RowIds(string host) => root.Q(host).Query<Label>(className: "data-cell").ToList()
            .Where(cell => !cell.ClassListContains("data-heading") && cell.parent.IndexOf(cell) == 0).Select(cell => cell.text).ToArray();

        [Test]
        public void EventControlsCombineFiltersAndUpdateRowsAndCountsImmediately()
        {
            page.Render("session-a", Events(), Commands());
            root.Q<TextField>("reports-event-search").value = "shuttle-01";
            root.Q<DropdownField>("reports-event-type").value = "INCIDENT_RESOLVED";

            CollectionAssert.AreEqual(new[] { "event-03" }, RowIds("events-table-host"));
            Assert.AreEqual("Showing 1 of 3 retained events", root.Q<Label>("reports-event-count").text);
            Assert.AreEqual(DisplayStyle.None, root.Q("reports-event-empty").style.display.value);
            Assert.IsTrue(root.Q<Button>("reports-events-clear").enabledSelf);
            CollectionAssert.AreEqual(new[] { "command-01", "command-02" }, RowIds("ledger-table-host"));
        }

        [Test]
        public void CommandControlsFilterByStatusAndFailureCodeIndependentlyOfEvents()
        {
            page.Render("session-a", Events(), Commands());
            root.Q<TextField>("reports-command-search").value = "stale";
            root.Q<DropdownField>("reports-command-status").value = "REJECTED";

            CollectionAssert.AreEqual(new[] { "command-02" }, RowIds("ledger-table-host"));
            Assert.AreEqual("Showing 1 of 2 retained commands", root.Q<Label>("reports-command-count").text);
            Assert.IsEmpty(root.Q<TextField>("reports-event-search").value);
            Assert.AreEqual(3, RowIds("events-table-host").Length);
        }

        [Test]
        public void IncomingEventsKeepFiltersAndIncludeNewMatchingRecords()
        {
            page.Render("session-a", Events(), Commands());
            var search = root.Q<TextField>("reports-event-search");
            var type = root.Q<DropdownField>("reports-event-type");
            search.value = "shuttle-01";
            type.value = "BATTERY_LOW";
            search.Focus();
            var focused = root.panel.focusController.focusedElement;
            Assert.IsNotNull(focused, "the search field must have keyboard focus before the update");
            var more = Events().Append(new TrainingEvent { EventId = "event-04", EntityId = "shuttle-01", EventType = "BATTERY_LOW" }).ToArray();

            page.Render("session-a", more, Commands());

            Assert.AreSame(search, root.Q<TextField>("reports-event-search"), "polling must retain the text field and its focus");
            Assert.AreEqual("shuttle-01", search.value);
            Assert.AreEqual("BATTERY_LOW", type.value);
            Assert.AreSame(focused, root.panel.focusController.focusedElement, "new records must not interrupt typing");
            CollectionAssert.AreEqual(new[] { "event-01", "event-04" }, RowIds("events-table-host"));
            Assert.AreEqual("Showing 2 of 4 retained events", root.Q<Label>("reports-event-count").text);
        }

        [Test]
        public void UnchangedPollKeepsRowsEvenWhenTheBackendReturnsNewObjects()
        {
            page.Render("session-a", Events(), Commands());
            var eventCell = root.Q("events-table-host").Query<Label>().ToList().Single(cell => cell.text == "event-01");
            var commandCell = root.Q("ledger-table-host").Query<Label>().ToList().Single(cell => cell.text == "command-01");
            var search = root.Q<TextField>("reports-event-search");
            search.Focus();
            var focused = root.panel.focusController.focusedElement;
            Assert.IsNotNull(focused, "the search field must have keyboard focus before polling");

            page.Render("session-a", Events(), Commands());

            Assert.AreSame(eventCell, root.Q("events-table-host").Query<Label>().ToList().Single(cell => cell.text == "event-01"),
                "unchanged polls must not rebuild the event table");
            Assert.AreSame(commandCell, root.Q("ledger-table-host").Query<Label>().ToList().Single(cell => cell.text == "command-01"),
                "unchanged polls must not rebuild the command table");
            Assert.AreSame(focused, root.panel.focusController.focusedElement);
        }

        [Test]
        public void UpdatingAnEventInPlaceRechecksSearchWithoutRebuildingCommands()
        {
            var events = Events();
            page.Render("session-a", events, Commands());
            root.Q<TextField>("reports-event-search").value = "shuttle-01";
            var commandCell = root.Q("ledger-table-host").Query<Label>().ToList().Single(cell => cell.text == "command-01");
            events[0].EntityId = "shuttle-02";

            page.Render("session-a", events, Commands());

            CollectionAssert.AreEqual(new[] { "event-03" }, RowIds("events-table-host"));
            Assert.AreEqual("Showing 1 of 3 retained events", root.Q<Label>("reports-event-count").text);
            Assert.AreSame(commandCell, root.Q("ledger-table-host").Query<Label>().ToList().Single(cell => cell.text == "command-01"));
        }

        [Test]
        public void SameSizeRetentionWindowRefreshesRowsAndDropdownChoices()
        {
            page.Render("session-a", Events(), Commands());
            var replacement = Events();
            replacement[0] = new TrainingEvent { EventId = "event-04", EventType = "ROUTE_APPROVED", EntityId = "route-01" };

            page.Render("session-a", replacement, Commands());

            CollectionAssert.AreEqual(new[] { "event-04", "event-02", "event-03" }, RowIds("events-table-host"));
            CollectionAssert.Contains(root.Q<DropdownField>("reports-event-type").choices, "ROUTE_APPROVED");
            Assert.AreEqual("Showing 3 of 3 retained events", root.Q<Label>("reports-event-count").text);
        }

        [Test]
        public void ACommandChangingStatusDoesNotSilentlyClearTheSelectedFilter()
        {
            var commands = Commands();
            page.Render("session-a", Events(), commands);
            var status = root.Q<DropdownField>("reports-command-status");
            status.value = "REJECTED";
            commands[1].Status = "APPLIED";

            page.Render("session-a", Events(), commands);

            Assert.AreEqual("REJECTED", status.value);
            CollectionAssert.Contains(status.choices, "REJECTED");
            Assert.IsEmpty(RowIds("ledger-table-host"));
            Assert.AreEqual("Showing 0 of 2 retained commands", root.Q<Label>("reports-command-count").text);
            StringAssert.Contains("No commands match", root.Q<Label>("reports-command-empty").text);
        }

        [Test]
        public void RetentionRemovingAnEventTypeKeepsTheActiveFilter()
        {
            page.Render("session-a", Events(), Commands());
            var type = root.Q<DropdownField>("reports-event-type");
            type.value = "INCIDENT_RESOLVED";

            page.Render("session-a", Events().Take(2).ToArray(), Commands());

            Assert.AreEqual("INCIDENT_RESOLVED", type.value);
            Assert.IsEmpty(RowIds("events-table-host"));
            StringAssert.Contains("No events match", root.Q<Label>("reports-event-empty").text);
        }

        [Test]
        public void SwitchingSessionsResetsBothTabsAndTheirDropdownOptions()
        {
            page.Render("session-a", Events(), Commands());
            root.Q<TextField>("reports-event-search").value = "shuttle-01";
            root.Q<DropdownField>("reports-event-type").value = "BATTERY_LOW";
            root.Q<TextField>("reports-command-search").value = "stale";
            root.Q<DropdownField>("reports-command-status").value = "REJECTED";

            page.Render("session-b", new TrainingEvent[0], new CommandOutcome[0]);

            Assert.IsEmpty(root.Q<TextField>("reports-event-search").value);
            Assert.IsEmpty(root.Q<TextField>("reports-command-search").value);
            CollectionAssert.AreEqual(new[] { "All event types" }, root.Q<DropdownField>("reports-event-type").choices);
            CollectionAssert.AreEqual(new[] { "All statuses" }, root.Q<DropdownField>("reports-command-status").choices);
            Assert.IsFalse(root.Q<Button>("reports-events-clear").enabledSelf);
            Assert.IsFalse(root.Q<Button>("reports-commands-clear").enabledSelf);
            Assert.IsEmpty(RowIds("events-table-host"));
            Assert.IsEmpty(RowIds("ledger-table-host"));
        }

        [Test]
        public void SwitchingEmptySessionsStillResetsTheirSearchFields()
        {
            page.Render("session-a", new TrainingEvent[0], new CommandOutcome[0]);
            root.Q<TextField>("reports-event-search").value = "shuttle-01";
            root.Q<TextField>("reports-command-search").value = "rejected";

            page.Render("session-b", new TrainingEvent[0], new CommandOutcome[0]);

            Assert.IsEmpty(root.Q<TextField>("reports-event-search").value);
            Assert.IsEmpty(root.Q<TextField>("reports-command-search").value);
            Assert.IsFalse(root.Q<Button>("reports-events-clear").enabledSelf);
            Assert.IsFalse(root.Q<Button>("reports-commands-clear").enabledSelf);
        }

        [Test]
        public void UnknownAndMissingCommandStatusesRemainReadableAndFilterable()
        {
            var commands = new[]
            {
                new CommandOutcome { CommandId = "command-01", Status = "FUTURE_STATUS" },
                new CommandOutcome { CommandId = "command-02" },
            };
            page.Render("session-a", new TrainingEvent[0], commands);

            Assert.AreEqual("Showing 2 of 2 retained commands", root.Q<Label>("reports-command-count").text);
            CollectionAssert.AreEqual(new[] { "All statuses", "FUTURE_STATUS" }, root.Q<DropdownField>("reports-command-status").choices);
            var missingStatusRow = root.Q("ledger-table-host").Query<Label>().ToList().Single(cell => cell.text == "command-02").parent;
            Assert.AreEqual("—", ((Label)missingStatusRow[2]).text, "a missing status must have a readable placeholder");

            root.Q<DropdownField>("reports-command-status").value = "FUTURE_STATUS";

            CollectionAssert.AreEqual(new[] { "command-01" }, RowIds("ledger-table-host"));
        }

        [Test]
        public void EmptySessionAndNoMatchesHaveDifferentExplanations()
        {
            StringAssert.Contains("No events observed", root.Q<Label>("reports-event-empty").text);
            StringAssert.Contains("No commands observed", root.Q<Label>("reports-command-empty").text);
            page.Render("session-a", Events(), Commands());
            root.Q<TextField>("reports-event-search").value = "unknown-shuttle";
            root.Q<TextField>("reports-command-search").value = "unknown-command";

            StringAssert.Contains("No events match", root.Q<Label>("reports-event-empty").text);
            StringAssert.Contains("No commands match", root.Q<Label>("reports-command-empty").text);
            Assert.AreEqual(DisplayStyle.None, root.Q("events-table-host").style.display.value);
            Assert.AreEqual(DisplayStyle.None, root.Q("ledger-table-host").style.display.value);
        }

        [Test]
        public void ClearButtonsResetOnlyTheirOwnTab()
        {
            page.Render("session-a", Events(), Commands());
            root.Q<TextField>("reports-event-search").value = "shuttle-01";
            root.Q<DropdownField>("reports-event-type").value = "BATTERY_LOW";
            root.Q<TextField>("reports-command-search").value = "stale";
            root.Q<DropdownField>("reports-command-status").value = "REJECTED";

            Submit("reports-events-clear");

            Assert.IsEmpty(root.Q<TextField>("reports-event-search").value);
            Assert.AreEqual("All event types", root.Q<DropdownField>("reports-event-type").value);
            Assert.AreEqual(3, RowIds("events-table-host").Length);
            Assert.IsFalse(root.Q<Button>("reports-events-clear").enabledSelf);
            Assert.AreEqual("stale", root.Q<TextField>("reports-command-search").value);
            Assert.AreEqual("REJECTED", root.Q<DropdownField>("reports-command-status").value);
            Assert.AreEqual(1, RowIds("ledger-table-host").Length);

            Submit("reports-commands-clear");

            Assert.IsEmpty(root.Q<TextField>("reports-command-search").value);
            Assert.AreEqual("All statuses", root.Q<DropdownField>("reports-command-status").value);
            Assert.AreEqual(2, RowIds("ledger-table-host").Length);
            Assert.IsFalse(root.Q<Button>("reports-commands-clear").enabledSelf);
        }

        private void Submit(string name)
        {
            using (var submit = NavigationSubmitEvent.GetPooled())
            {
                var button = root.Q<Button>(name);
                submit.target = button;
                button.SendEvent(submit);
            }
        }
    }
}
