/*
 * Plays the battery critical sound when a shuttle's battery gauge turns red.
 *
 * Author: Choo Jian Wei
 * Date Created: 03/11/2026
 * Last Modified: 24/11/2026
 *
 * Description:
 *     The backend sends no event when a battery runs low, only BATTERY_EXHAUSTED once it is empty,
 *     so the client watches each snapshot instead. The audio professor asked for one battery sound,
 *     for the critical warning only, so a shuttle sounds once when its gauge turns red, and again
 *     only after it has charged back out of the red. Like the training alerts,
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
    //     Attributes: RedPercent, where both battery gauges turn red, kept above the energy
    //     model's 15% reserve because the safety return turns a shuttle back before it; Cue, the
    //     cue name AlertPlayer plays.
    public sealed class BatteryWatch
    {
        public const double RedPercent = 20;
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
        // Function: IsRed (percent)
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function tells whether a battery gauge shows red. Fleet Overview and the
        //     Charging page both colour their gauges with it, so the sound plays exactly when the
        //     operator sees the bar turn red.
        //
        // Args:
        //     percent (double): The battery, in percent of capacity.
        //
        // Returns:
        //     bool: True below RedPercent.
        public static bool IsRed(double percent) => percent < RedPercent;

        // -----------------------------------------------------------------------------------
        // Function: IsRed (vehicle)
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function tells whether a shuttle's battery gauge shows red. A shuttle with no
        //     battery capacity has no gauge to turn red.
        //
        // Args:
        //     vehicle (FleetVehicle): The shuttle.
        //
        // Returns:
        //     bool: True when its gauge is red.
        public static bool IsRed(FleetVehicle vehicle) =>
            vehicle.BatteryCapacityKwh > 0 && IsRed(100 * vehicle.BatteryKwh / vehicle.BatteryCapacityKwh);

        // -----------------------------------------------------------------------------------
        // Function: HoldNextCue
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function makes a drop seen in the next snapshot sound one snapshot later. The
        //     session recovered alert plays when an outage ends, so a battery that ran low during
        //     it would otherwise sound on top of it.
        public void HoldNextCue() => holding = true;

        // -----------------------------------------------------------------------------------
        // Function: OnSnapshot
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function checks every shuttle in a snapshot and plays the sound once if any
        //     shuttle's gauge has just turned red. The first snapshot of a session only records
        //     which shuttles are already red.
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
                if (!IsRed(vehicle)) low.Remove(vehicle.VehicleId);
                else if (low.Add(vehicle.VehicleId)) dropped = true;
            }
            var sound = (dropped && !joined) || held;
            held = holding && sound;
            holding = false;
            if (sound && !held && !muted()) play?.Invoke(Cue);
        }
    }
}
