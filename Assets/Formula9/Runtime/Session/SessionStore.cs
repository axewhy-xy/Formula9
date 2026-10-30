/*
 * The selected training session's state as the Unity client shows it.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Part of the Unity client's session layer. A port of the Qt client's
 *     TrainingDomain::SessionStore. It keeps the latest accepted snapshot and the most recent 1,000
 *     events, and refuses anything that would move the view backwards or skip events (an older
 *     epoch, a stale or reused snapshot, an event gap), so every screen reads one consistent state.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.Contracts;

namespace Formula9.Session
{
    // -----------------------------------------------------------------------------------
    // Class: SessionStore
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class holds the selected session's latest accepted snapshot and recent event log.
    //
    //     Attributes: SelectedSessionId, Snapshot, Events (oldest first, at most MaxRecentEvents)
    //     and LastEventSequence, the sequence number of the last event accepted.
    public sealed class SessionStore
    {
        public const int MaxRecentEvents = 1000;

        private readonly List<Event> events = new List<Event>();

        public string SelectedSessionId { get; private set; }
        public FullSnapshot Snapshot { get; private set; }
        public IReadOnlyList<Event> Events => events;
        public long LastEventSequence { get; private set; }

        // -----------------------------------------------------------------------------------
        // Function: Select
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function selects a session. Switching sessions clears everything; selecting the
        //     same one keeps its state.
        //
        // Args:
        //     sessionId (string): The session to select, or null for none.
        public void Select(string sessionId)
        {
            if (sessionId == SelectedSessionId) return;
            SelectedSessionId = sessionId;
            Snapshot = null;
            events.Clear();
            LastEventSequence = 0;
        }

        // -----------------------------------------------------------------------------------
        // Function: AcceptSnapshot
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function accepts a newer snapshot of the selected session. Within one epoch the
        //     event sequence, tick and aggregate revision may not go backwards, the authority may
        //     not change, and a snapshot ID may not be reused for different state; revisions of the
        //     scenario, scene and transform may never change. The event log restarts on a new epoch
        //     or when asked.
        //
        // Args:
        //     candidate (FullSnapshot): The snapshot.
        //     resetEvents (bool): True after a resync, to restart the event log from this snapshot.
        //
        // Returns:
        //     string: Null when accepted, otherwise why it was refused.
        public string AcceptSnapshot(FullSnapshot candidate, bool resetEvents)
        {
            if (string.IsNullOrEmpty(SelectedSessionId) || candidate.SessionId != SelectedSessionId) return "unselected session";
            var current = Snapshot;
            if (current != null)
            {
                if (candidate.SessionEpoch < current.SessionEpoch) return "older session epoch";
                if (candidate.Session.ScenarioRevisionId != current.Session.ScenarioRevisionId || candidate.SceneRevision != current.SceneRevision
                    || candidate.TransformRevision != current.TransformRevision || !SameSpatial(candidate.Spatial, current.Spatial))
                    return "incompatible session revisions";
                if (candidate.SessionEpoch == current.SessionEpoch)
                {
                    if (candidate.EventSequence < current.EventSequence || candidate.Tick < current.Tick
                        || candidate.Session.AuthorityId != current.Session.AuthorityId
                        || candidate.Session.AggregateRevision < current.Session.AggregateRevision)
                        return "stale snapshot";
                    if (candidate.SnapshotId == current.SnapshotId
                        && (candidate.EventSequence != current.EventSequence || candidate.Tick != current.Tick))
                        return "snapshot ID reused for different state";
                }
            }
            if (resetEvents || current == null || candidate.SessionEpoch != current.SessionEpoch)
            {
                events.Clear();
                LastEventSequence = candidate.EventSequence;
            }
            Snapshot = candidate;
            return null;
        }

        // -----------------------------------------------------------------------------------
        // Function: AcceptEvents
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function appends the next contiguous events. Re-sent events the log already
        //     covers are ignored unless they contradict what was received. The batch is all or
        //     nothing.
        //
        // Args:
        //     sessionId (string): The session the batch is for.
        //     epoch (long): The batch's session epoch.
        //     batch (IReadOnlyList<Event>): The events, in sequence order.
        //
        // Returns:
        //     string: Null when accepted, otherwise why the batch was refused.
        public string AcceptEvents(string sessionId, long epoch, IReadOnlyList<Event> batch)
        {
            if (Snapshot == null || sessionId != SelectedSessionId || epoch != Snapshot.SessionEpoch) return "event session mismatch";
            var cursor = LastEventSequence;
            var seen = new HashSet<string>(events.Select(e => e.EventId));
            var retained = events.ToDictionary(e => e.EventSequence, e => e.EventId);
            var additions = new List<Event>();
            foreach (var e in batch)
            {
                if (e.EventSequence <= cursor)
                {
                    // Older than the retained window: covered by the cursor, but no longer checkable.
                    if (retained.TryGetValue(e.EventSequence, out var id) && id != e.EventId) return "conflicting duplicate event";
                    continue;
                }
                if (e.EventSequence != cursor + 1 || !seen.Add(e.EventId)) return "event sequence gap or duplicate ID";
                additions.Add(e);
                retained[e.EventSequence] = e.EventId;
                cursor = e.EventSequence;
            }
            events.AddRange(additions);
            if (events.Count > MaxRecentEvents) events.RemoveRange(0, events.Count - MaxRecentEvents);
            LastEventSequence = cursor;
            return null;
        }

        // -----------------------------------------------------------------------------------
        // Function: SameSpatial
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function tells whether two snapshots use the same spatial scene and revisions;
        //     two planar snapshots also match.
        //
        // Args:
        //     a (SpatialReference): The first reference, or null.
        //     b (SpatialReference): The second reference, or null.
        //
        // Returns:
        //     bool: True when they match.
        private static bool SameSpatial(SpatialReference a, SpatialReference b)
        {
            if (a == null || b == null) return a == b;
            return a.SceneId == b.SceneId && a.SceneRevision == b.SceneRevision && a.SpatialRevision == b.SpatialRevision
                   && a.TransformRevision == b.TransformRevision && a.ManifestSha256 == b.ManifestSha256;
        }
    }
}
