using System;
using System.Collections.Generic;
using Formula9.Contracts;

namespace Formula9.Audio
{
    // Decides which training events play a sound, and which sound. Port of the Qt AlertGate.
    //   - Only meaningful events alert; routine events (e.g. COMMAND_APPLIED) stay silent.
    //   - Each durable event ID alerts at most once, including after a reconnect replays it.
    //   - History playback never alerts and is not remembered as seen.
    //   - Muting silences alerts but still marks events as seen, so unmuting causes no burst.
    //   - Remembered IDs are bounded; the oldest are forgotten first.
    public sealed class AlertGate
    {
        public const int DefaultMemoryLimit = 1024;

        private readonly IReadOnlyDictionary<string, string> cues;
        private readonly int memoryLimit;
        private readonly HashSet<string> seen = new HashSet<string>();
        private readonly Queue<string> order = new Queue<string>();

        public bool Muted { get; set; }

        public AlertGate(IReadOnlyDictionary<string, string> cues = null, int memoryLimit = DefaultMemoryLimit)
        {
            this.cues = cues ?? AlertCues.Default;
            this.memoryLimit = memoryLimit;
        }

        // Returns the cue to play, or null for no sound.
        public string OnEvent(Event e, bool live)
        {
            if (!live || !seen.Add(e.EventId)) return null;
            order.Enqueue(e.EventId);
            while (order.Count > memoryLimit) seen.Remove(order.Dequeue());
            if (!cues.TryGetValue($"{e.EntityType}/{e.EventType}", out var cue)) return null;
            return Muted ? null : cue;
        }
    }

    // Passes accepted training events through the gate and plays each returned cue. The player is
    // injected, so the rules are tested without audio. Port of the Qt TrainingAlerts.
    public sealed class TrainingAlerts
    {
        private readonly Action<string> play;
        private readonly AlertGate gate;

        public TrainingAlerts(Action<string> play, AlertGate gate = null)
        {
            this.play = play;
            this.gate = gate ?? new AlertGate();
        }

        public bool Muted
        {
            get => gate.Muted;
            set => gate.Muted = value;
        }

        public void OnEvents(IEnumerable<Event> events, bool live)
        {
            foreach (var e in events)
            {
                var cue = gate.OnEvent(e, live);
                if (cue != null) play?.Invoke(cue);
            }
        }
    }
}
