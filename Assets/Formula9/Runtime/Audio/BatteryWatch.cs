/*
 * Plays the battery low sound when a shuttle's battery runs low.
 *
 * Author: Choo Jian Wei
 * Date Created: 03/11/2026
 * Last Modified: 03/11/2026
 *
 * Description:
 *     The backend sends no event when a battery runs low, only BATTERY_EXHAUSTED once it is empty,
 *     so the client watches each snapshot instead. A shuttle sounds once when its gauge first
 *     reads LowPercent, and again only after it has charged back above it. Like the training alerts,
 *     what was already true when the session was joined stays silent, and Mute alerts silences it.
 */

using System;
using System.Collections.Generic;
using Formula9.Contracts;

namespace Formula9.Audio
{
    // -----------------------------------------------------------------------------------
    // Class: BatteryWatch
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class decides when the battery low sound plays. The player and the mute check are
    //     injected, so the rules are tested without audio.
    //
    //     Attributes: LowPercent, the Fleet Overview gauge's red threshold, kept above the
    //     energy model's 15% reserve because the safety return turns a shuttle back before it;
    //     Cue, the cue name AlertPlayer plays.
    public sealed class BatteryWatch
    {
        public const double LowPercent = 20;
        public const string Cue = "battery_low";

        private readonly Action<string> play;
        private readonly Func<bool> muted;
        private readonly HashSet<string> low = new HashSet<string>();
        private string sessionId;
        private bool holding, held;

        // -----------------------------------------------------------------------------------
        // Function: BatteryWatch (constructor)
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function connects the rules to a player and the Mute alerts setting.
        //
        // Args:
        //     play (Action<string>): Plays a cue, normally AlertPlayer.Play.
        //     muted (Func<bool>): True while Mute alerts is on; never muted when null.
        public BatteryWatch(Action<string> play, Func<bool> muted = null)
        {
            this.play = play;
            this.muted = muted ?? (() => false);
        }

        // -----------------------------------------------------------------------------------
        // Function: IsLow
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function tells whether a shuttle's battery reads LowPercent or less on screen.
        //     Both gauges format with "0", which rounds halves up, so a battery below LowPercent + 0.5
        //     already shows LowPercent, and the sound plays when the operator first sees that number.
        //
        // Args:
        //     vehicle (FleetVehicle): The shuttle.
        //
        // Returns:
        //     bool: True when its battery is low.
        public static bool IsLow(FleetVehicle vehicle) =>
            vehicle.BatteryCapacityKwh > 0 && 100 * vehicle.BatteryKwh / vehicle.BatteryCapacityKwh < LowPercent + 0.5;

        // -----------------------------------------------------------------------------------
        // Function: HoldNextCue
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function makes a drop seen in the next snapshot sound one snapshot later. The
        //     reconnect cue plays on the snapshot that ends an outage, so a battery that ran low
        //     during it would otherwise sound on top of it.
        public void HoldNextCue() => holding = true;

        // -----------------------------------------------------------------------------------
        // Function: OnSnapshot
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function checks every shuttle in a snapshot and plays the sound once if any
        //     shuttle has just dropped to LowPercent on screen. The first snapshot of a session only
        //     records which shuttles are already low.
        //
        // Args:
        //     snapshot (FullSnapshot): The snapshot the session accepted; ignored when null.
        public void OnSnapshot(FullSnapshot snapshot)
        {
            if (snapshot?.Vehicles == null) return;
            var joined = snapshot.SessionId != sessionId;
            if (joined) { sessionId = snapshot.SessionId; low.Clear(); held = false; }
            var dropped = false;
            foreach (var vehicle in snapshot.Vehicles)
            {
                if (!IsLow(vehicle)) low.Remove(vehicle.VehicleId);
                else if (low.Add(vehicle.VehicleId)) dropped = true;
            }
            var sound = (dropped && !joined) || held;
            held = holding && sound;
            holding = false;
            if (sound && !held && !muted()) play?.Invoke(Cue);
        }
    }
}
