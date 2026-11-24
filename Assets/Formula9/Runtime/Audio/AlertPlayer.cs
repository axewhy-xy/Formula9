/*
 * Plays the training alert sounds in the Unity client.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 24/11/2026
 *
 * Description:
 *     Part of the native semantic audio work (NAT-M2-07). TrainingAlerts decides which events
 *     alert; this component only turns a cue name into a sound. The incident, battery and
 *     disconnect clips are from amee's sound consultation with Vuk; the quieter incident alert
 *     follows the audio professor's review; the other alerts are still the Qt client's
 *     Assets/Audio/alert_*.wav. Reconnecting plays only session_recovered, not a cue of its own.
 */

using System.Collections.Generic;
using UnityEngine;

namespace Formula9.Audio
{
    // -----------------------------------------------------------------------------------
    // Class: AlertPlayer
    // -----------------------------------------------------------------------------------
    // Description:
    //     This component plays alert cues by name. Each clip is loaded from
    //     Resources/Formula9/Audio the first time its cue plays and then kept. Unknown cues stay
    //     silent, so a cue added to the contract before its sound exists cannot break the client.
    //
    //     Attributes: SoundFiles maps each cue name to its clip (file name without .wav);
    //     ClientSoundFiles does the same for cues the client raises itself, such as battery low,
    //     which no backend event carries; Volumes lowers the cues that play below full volume.
    public sealed class AlertPlayer : MonoBehaviour
    {
        // Cue name -> clip in Resources/Formula9/Audio (file name without .wav).
        public static readonly IReadOnlyDictionary<string, string> SoundFiles = new Dictionary<string, string>
        {
            ["incident_alert"] = "ui_accident",
            ["command_rejected"] = "alert_command_rejected",
            ["needs_reconfirmation"] = "alert_needs_reconfirmation",
            ["session_recovered"] = "alert_session_recovered",
        };

        // Cue name -> clip for cues the client raises from snapshots, not from the cue table.
        public static readonly IReadOnlyDictionary<string, string> ClientSoundFiles = new Dictionary<string, string>
        {
            [BatteryWatch.Cue] = "ui_battery_low",
            [DisconnectCue] = "ui_dull_01",  // amee: dial 1 is disconnect
        };

        // Cue name -> volume for cues quieter than full volume. The audio professor found the
        // incident alert too loud next to the others.
        public static readonly IReadOnlyDictionary<string, float> Volumes = new Dictionary<string, float>
        {
            ["incident_alert"] = 0.5f,
        };

        // Played when the backend stops answering a joined session. Reconnecting has no cue of its own:
        // the backend's SESSION_RECOVERED event plays session_recovered then.
        public const string DisconnectCue = "disconnect";

        private readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
        private AudioSource source;

        // -----------------------------------------------------------------------------------
        // Function: Awake
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function creates the 2D audio source the alerts play through. Alerts are
        //     interface sounds, not part of the 3D world, so the source has no spatial blend. It
        //     also loads every alert clip up front: a clip whose audio is not loaded yet plays
        //     nothing the first time, so each alert's first cue would otherwise be silent.
        private void Awake()
        {
            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0; // alerts are interface sounds, not part of the 3D world
            foreach (var cues in new[] { SoundFiles, ClientSoundFiles })
                foreach (var cue in cues)
                    clips[cue.Key] = LoadClip(cue.Value);
        }

        // -----------------------------------------------------------------------------------
        // Function: LoadClip
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function loads a clip from Resources/Formula9/Audio with its audio data ready.
        //     The clips are imported without Preload Audio Data, and the first PlayOneShot of a
        //     clip still unloaded is silent, so the data is loaded here, before it is needed.
        //
        // Args:
        //     file (string): The file name without .wav.
        //
        // Returns:
        //     AudioClip: The loaded clip, or null when there is no such clip.
        internal static AudioClip LoadClip(string file)
        {
            var clip = Resources.Load<AudioClip>($"Formula9/Audio/{file}");
            if (clip != null && clip.loadState != AudioDataLoadState.Loaded) clip.LoadAudioData();
            return clip;
        }

        // -----------------------------------------------------------------------------------
        // Function: Play
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function plays the clip for a cue. PlayOneShot lets alerts that arrive together
        //     overlap instead of cutting each other off.
        //
        // Args:
        //     cue (string): The cue name from AlertCues, for example incident_alert.
        public void Play(string cue)
        {
            if (!SoundFiles.TryGetValue(cue, out var file) && !ClientSoundFiles.TryGetValue(cue, out file)) return;
            if (!clips.TryGetValue(cue, out var clip))
                clips[cue] = clip = LoadClip(file);
            if (clip != null) source.PlayOneShot(clip, Volumes.TryGetValue(cue, out var volume) ? volume : 1f);
        }
    }
}
