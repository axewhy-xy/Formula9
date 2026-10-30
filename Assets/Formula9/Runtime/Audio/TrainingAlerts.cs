/*
 * Decides which training events play an alert sound.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Part of NAT-M2-07. A port of the Qt client's AlertGate and TrainingAlerts. Only events listed
 *     in the shared contracts/v2/alert_cues.json (embedded by the generator as AlertCues) play a
 *     sound; each event ID plays at most once, even when a reconnect replays it; history never
 *     plays; and muting still marks events as seen, so unmuting causes no burst of old alerts.
 */

using System;
using System.Collections.Generic;
using Formula9.Contracts;

namespace Formula9.Audio
{
    // -----------------------------------------------------------------------------------
    // Class: AlertGate
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class decides which training events play a sound, and which sound. Only meaningful
    //     events alert; routine events such as COMMAND_APPLIED stay silent. Each durable event ID
    //     alerts at most once, including after a reconnect replays it. History playback never
    //     alerts and is not remembered as seen. Muting silences alerts but still marks events as
    //     seen. Remembered IDs are bounded; the oldest are forgotten first.
    //
    //     Attributes: Muted silences alerts; DefaultMemoryLimit is how many event IDs are
    //     remembered.
    public sealed class AlertGate
    {
        public const int DefaultMemoryLimit = 1024;

        private readonly IReadOnlyDictionary<string, string> cues;
        private readonly int memoryLimit;
        private readonly HashSet<string> seen = new HashSet<string>();
        private readonly Queue<string> order = new Queue<string>();

        public bool Muted { get; set; }

        // -----------------------------------------------------------------------------------
        // Function: AlertGate (constructor)
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function sets up the gate with the cue table and how many event IDs to remember.
        //
        // Args:
        //     cues (IReadOnlyDictionary<string, string>): Event key (entity type/event type) to cue
        //         name; AlertCues.Default when null.
        //     memoryLimit (int): How many event IDs to remember before forgetting the oldest.
        public AlertGate(IReadOnlyDictionary<string, string> cues = null, int memoryLimit = DefaultMemoryLimit)
        {
            this.cues = cues ?? AlertCues.Default;
            this.memoryLimit = memoryLimit;
        }

        // -----------------------------------------------------------------------------------
        // Function: OnEvent
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function decides whether one event alerts. A live event is remembered as seen
        //     even when it has no cue or alerts are muted.
        //
        // Args:
        //     e (Event): The training event.
        //     live (bool): False for history playback, which never alerts.
        //
        // Returns:
        //     string: The cue to play, or null for no sound.
        public string OnEvent(Event e, bool live)
        {
            if (!live || !seen.Add(e.EventId)) return null;
            order.Enqueue(e.EventId);
            while (order.Count > memoryLimit) seen.Remove(order.Dequeue());
            if (!cues.TryGetValue($"{e.EntityType}/{e.EventType}", out var cue)) return null;
            return Muted ? null : cue;
        }
    }

    // -----------------------------------------------------------------------------------
    // Class: TrainingAlerts
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class passes accepted training events through the gate and plays each returned cue.
    //     The player is injected, so the rules are tested without audio.
    //
    //     Attributes: Muted reads or changes the gate's mute.
    public sealed class TrainingAlerts
    {
        private readonly Action<string> play;
        private readonly AlertGate gate;

        // -----------------------------------------------------------------------------------
        // Function: TrainingAlerts (constructor)
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function connects the gate to the player.
        //
        // Args:
        //     play (Action<string>): Plays a cue, normally AlertPlayer.Play.
        //     gate (AlertGate): The rules to apply; a new AlertGate when null.
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

        // -----------------------------------------------------------------------------------
        // Function: OnEvents
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function plays the cue of every event in a batch that the gate lets through, in
        //     order.
        //
        // Args:
        //     events (IEnumerable<Event>): Events the session store accepted, or a recovery
        //         appended.
        //     live (bool): False for history playback.
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
