/*
 * Search and filtering rules for the Reports page.
 *
 * Author: Nur Syazliyarni Binte Zazali
 * Date Created: 04/11/2026
 * Last Modified: 04/11/2026
 *
 * Description:
 *     Filters observed events and command outcomes using case-insensitive search words
 *     combined with an event-type or command-status selection. Preserves record order
 *     and leaves the session's retained records unchanged.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.Contracts;
using Formula9.Session;

namespace Formula9.UI
{
    // Every search word must match a displayed ID, type, status or failure code. Filtering
    // preserves the journal's order and never changes the records held by the session.
    public static class ReportFilter
    {
        public static IReadOnlyList<Event> Events(IEnumerable<Event> records, string search, string eventType = null)
        {
            var words = Words(search);
            return records.Where(record => Selected(record.EventType, eventType) &&
                Matches(words, record.EventId, record.EventType, record.EntityId)).ToList();
        }

        public static IReadOnlyList<CommandOutcome> Commands(IEnumerable<CommandOutcome> records, string search, string status = null)
        {
            var words = Words(search);
            return records.Where(record => Selected(record.Status, status) &&
                Matches(words, record.CommandId, record.TargetType, record.TargetId, record.Status, record.FailureCode)).ToList();
        }

        private static string[] Words(string search) =>
            (search ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

        private static bool Selected(string value, string selected) =>
            string.IsNullOrWhiteSpace(selected) || string.Equals(value, selected.Trim(), StringComparison.OrdinalIgnoreCase);

        private static bool Matches(string[] words, params string[] fields) =>
            words.All(word => fields.Any(field => field != null && field.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0));
    }
}
