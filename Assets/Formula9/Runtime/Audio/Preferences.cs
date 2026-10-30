/*
 * Client preferences and the Mute alerts setting.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Part of NAT-M2-07. Mute alerts is kept across restarts, like the Qt client's
 *     training/muteAlerts. Preferences are read through IPreferences so the app can use Unity's
 *     PlayerPrefs while tests use memory only and never touch the real user's settings.
 */

using System.Collections.Generic;
using UnityEngine;

namespace Formula9.Audio
{
    // -----------------------------------------------------------------------------------
    // Class: IPreferences
    // -----------------------------------------------------------------------------------
    // Description:
    //     This interface is where client preferences live. The app uses PlayerPreferences; tests
    //     use MemoryPreferences.
    public interface IPreferences
    {
        bool GetBool(string key, bool fallback);
        void SetBool(string key, bool value);
    }

    // -----------------------------------------------------------------------------------
    // Class: PlayerPreferences
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class keeps settings across restarts in Unity's PlayerPrefs. The app uses it.
    public sealed class PlayerPreferences : IPreferences
    {
        // -----------------------------------------------------------------------------------
        // Function: PlayerPreferences.GetBool
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reads a saved yes/no setting.
        //
        // Args:
        //     key (string): The setting name.
        //     fallback (bool): The value to use when nothing is saved.
        //
        // Returns:
        //     bool: The saved value, or fallback.
        public bool GetBool(string key, bool fallback) => PlayerPrefs.GetInt(key, fallback ? 1 : 0) != 0;

        // -----------------------------------------------------------------------------------
        // Function: PlayerPreferences.SetBool
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function saves a yes/no setting and writes it to disk at once, so it survives a
        //     crash.
        //
        // Args:
        //     key (string): The setting name.
        //     value (bool): The value to save.
        public void SetBool(string key, bool value)
        {
            PlayerPrefs.SetInt(key, value ? 1 : 0);
            PlayerPrefs.Save();
        }
    }

    // -----------------------------------------------------------------------------------
    // Class: MemoryPreferences
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class keeps settings in memory only, so tests never touch the real PlayerPrefs.
    //     GetBool and SetBool read and write Values, which tests can inspect.
    public sealed class MemoryPreferences : IPreferences
    {
        public readonly Dictionary<string, bool> Values = new Dictionary<string, bool>();
        public bool GetBool(string key, bool fallback) => Values.TryGetValue(key, out var value) ? value : fallback;
        public void SetBool(string key, bool value) => Values[key] = value;
    }

    // -----------------------------------------------------------------------------------
    // Class: MuteSetting
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class is the Mute alerts choice. It applies the saved choice to TrainingAlerts when
    //     created, and saves every change.
    //
    //     Attributes: Key is the preference name (training.muteAlerts); Muted reads or changes the
    //     choice.
    public sealed class MuteSetting
    {
        public const string Key = "training.muteAlerts";
        private readonly IPreferences preferences;
        private readonly TrainingAlerts alerts;

        // -----------------------------------------------------------------------------------
        // Function: MuteSetting (constructor)
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function restores the saved Mute alerts choice onto the alerts. Unmuted is the
        //     default.
        //
        // Args:
        //     preferences (IPreferences): Where the choice is saved.
        //     alerts (TrainingAlerts): The alerts the choice applies to.
        public MuteSetting(IPreferences preferences, TrainingAlerts alerts)
        {
            this.preferences = preferences;
            this.alerts = alerts;
            alerts.Muted = preferences.GetBool(Key, false);
        }

        public bool Muted
        {
            get => alerts.Muted;
            set
            {
                alerts.Muted = value;
                preferences.SetBool(Key, value);
            }
        }
    }
}
