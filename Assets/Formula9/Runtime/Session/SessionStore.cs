using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.Contracts;

namespace Formula9.Session
{
    // The selected session's latest accepted snapshot and recent event log. Port of the Qt
    // client's TrainingDomain::SessionStore: it refuses anything that would move the view
    // backwards or skip events, so every screen reads one consistent state.
    public sealed class SessionStore
    {
        public const int MaxRecentEvents = 1000;

        private readonly List<Event> events = new List<Event>();

        public string SelectedSessionId { get; private set; }
        public FullSnapshot Snapshot { get; private set; }
        public IReadOnlyList<Event> Events => events;
        public long LastEventSequence { get; private set; }

        // Switching sessions clears everything; selecting the same one keeps its state.
        public void Select(string sessionId)
        {
            if (sessionId == SelectedSessionId) return;
            SelectedSessionId = sessionId;
            Snapshot = null;
            events.Clear();
            LastEventSequence = 0;
        }

        // Returns null on success, otherwise why the snapshot was refused.
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

        // Appends the next contiguous events. Re-sent events already covered are ignored unless
        // they contradict what was received. Returns null on success, otherwise the reason.
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

        private static bool SameSpatial(SpatialReference a, SpatialReference b)
        {
            if (a == null || b == null) return a == b;
            return a.SceneId == b.SceneId && a.SceneRevision == b.SceneRevision && a.SpatialRevision == b.SpatialRevision
                   && a.TransformRevision == b.TransformRevision && a.ManifestSha256 == b.ManifestSha256;
        }
    }
}
