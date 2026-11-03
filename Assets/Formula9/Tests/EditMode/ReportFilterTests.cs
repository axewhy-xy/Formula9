/*
 * Unit tests for the Reports page's search and filtering rules.
 *
 * Author: Nur Syazliyarni Binte Zazali
 * Date Created: 04/11/2026
 * Last Modified: 04/11/2026
 *
 * Description:
 *     Verifies case-insensitive searches across columns, combined type and status
 *     filters, blank queries, missing optional fields and empty results. Checks that
 *     filtering preserves the order and contents of the source records.
 */

using System.Linq;
using Formula9.Contracts;
using Formula9.Session;
using Formula9.UI;
using NUnit.Framework;

namespace Formula9.Tests
{
    public class ReportFilterTests
    {
        private static Event[] Events() => new[]
        {
            new Event { EventId = "event-01", EntityId = "shuttle-01", EventType = "BATTERY_LOW" },
            new Event { EventId = "event-02", EntityId = "shuttle-02", EventType = "BATTERY_LOW" },
            new Event { EventId = "event-03", EntityId = "shuttle-01", EventType = "INCIDENT_RESOLVED" },
        };

        private static CommandOutcome[] Commands() => new[]
        {
            new CommandOutcome { CommandId = "command-01", TargetType = "VEHICLE", TargetId = "shuttle-01", Status = "APPLIED" },
            new CommandOutcome { CommandId = "command-02", TargetType = "ROUTE", TargetId = "route-02", Status = "REJECTED", FailureCode = "STALE_REVISION" },
            new CommandOutcome { CommandId = "command-03", TargetType = "ROUTE", TargetId = "route-01", Status = "PENDING_CONFIRMATION" },
        };

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" \t\r\n ")]
        public void BlankSearchPreservesEveryEventInJournalOrder(string search)
        {
            var records = Events();
            CollectionAssert.AreEqual(records, ReportFilter.Events(records, search));
        }

        [Test]
        public void EventWordsCanMatchDifferentColumnsAndIgnoreCaseAndWhitespace()
        {
            var result = ReportFilter.Events(Events(), "  SHUTTLE-01\t battery  ");
            CollectionAssert.AreEqual(new[] { "event-01" }, result.Select(record => record.EventId));
        }

        [Test]
        public void EventSearchAndExactTypeFilterApplyTogether()
        {
            var records = Events();
            CollectionAssert.AreEqual(new[] { records[2] }, ReportFilter.Events(records, "shuttle-01", "incident_resolved"));
            Assert.IsEmpty(ReportFilter.Events(records, "shuttle-01", "INCIDENT"), "the type dropdown selects a whole type");
            Assert.IsEmpty(ReportFilter.Events(records, "event-02", "INCIDENT_RESOLVED"));
        }

        [Test]
        public void FilteringDoesNotRemoveOrReorderSourceRecords()
        {
            var records = Events().ToList();
            var before = records.ToArray();
            CollectionAssert.AreEqual(new[] { before[0], before[2] }, ReportFilter.Events(records, "shuttle-01"));
            CollectionAssert.AreEqual(before, records);
        }

        [Test]
        public void CommandSearchCombinesTargetAndFailureCodeWithStatus()
        {
            var result = ReportFilter.Commands(Commands(), " ROUTE-02 stale ", "rejected");
            CollectionAssert.AreEqual(new[] { "command-02" }, result.Select(record => record.CommandId));
            Assert.IsEmpty(ReportFilter.Commands(Commands(), "route-02", "APPLIED"));
        }

        [TestCase("COMMAND-01", "command-01")]
        [TestCase("vehicle", "command-01")]
        [TestCase("pending_confirmation", "command-03")]
        public void CommandSearchFindsIdsTargetTypesAndStatuses(string search, string expected)
        {
            CollectionAssert.AreEqual(new[] { expected }, ReportFilter.Commands(Commands(), search).Select(record => record.CommandId));
        }

        [Test]
        public void OptionalMissingFieldsAndEmptySourcesAreSafe()
        {
            var incomplete = new CommandOutcome { CommandId = "command-04", Status = "QUEUED" };
            Assert.AreEqual(1, ReportFilter.Commands(new[] { incomplete }, "command-04").Count);
            Assert.IsEmpty(ReportFilter.Commands(new[] { incomplete }, "missing"));
            Assert.IsEmpty(ReportFilter.Commands(new CommandOutcome[0], "", "APPLIED"));
            Assert.IsEmpty(ReportFilter.Events(new Event[0], "", "BATTERY_LOW"));
        }

        [Test]
        public void BlankCommandSearchStillAppliesStatusAndPreservesOrder()
        {
            var records = Commands();
            CollectionAssert.AreEqual(records, ReportFilter.Commands(records, null));
            CollectionAssert.AreEqual(new[] { records[1] }, ReportFilter.Commands(records, " \t ", "REJECTED"));
        }
    }
}
