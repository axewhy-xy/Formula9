using System.Collections.Generic;
using UnityEngine;

namespace Formula9.Audio
{
    // Where client preferences live. The app uses PlayerPrefs; tests use MemoryPreferences so they
    // never touch the real user's settings.
    public interface IPreferences
    {
        bool GetBool(string key, bool fallback);
        void SetBool(string key, bool value);
    }

    // Settings kept across restarts in Unity's PlayerPrefs; used by the app.
    public sealed class PlayerPreferences : IPreferences
    {
        public bool GetBool(string key, bool fallback) => PlayerPrefs.GetInt(key, fallback ? 1 : 0) != 0;

        public void SetBool(string key, bool value)
        {
            PlayerPrefs.SetInt(key, value ? 1 : 0);
            PlayerPrefs.Save();
        }
    }

    // Settings kept in memory only, so tests never touch the real PlayerPrefs.
    public sealed class MemoryPreferences : IPreferences
    {
        public readonly Dictionary<string, bool> Values = new Dictionary<string, bool>();
        public bool GetBool(string key, bool fallback) => Values.TryGetValue(key, out var value) ? value : fallback;
        public void SetBool(string key, bool value) => Values[key] = value;
    }

    // The Mute alerts choice, kept across restarts like the Qt client's training/muteAlerts.
    public sealed class MuteSetting
    {
        public const string Key = "training.muteAlerts";
        private readonly IPreferences preferences;
        private readonly TrainingAlerts alerts;

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
