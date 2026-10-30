/*
 * Plays the training alert sounds in the Unity client.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Part of the native semantic audio work (NAT-M2-07). TrainingAlerts decides which events
 *     alert; this component only turns a cue name into a sound. The clips are the Qt client's
 *     Assets/Audio/alert_*.wav, made by its generate_alert_cues.py, so both clients sound the same.
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
    //     Attributes: SoundFiles maps each cue name to its clip (file name without .wav).
    public sealed class AlertPlayer : MonoBehaviour
    {
        // Cue name -> clip in Resources/Formula9/Audio (file name without .wav).
        public static readonly IReadOnlyDictionary<string, string> SoundFiles = new Dictionary<string, string>
        {
            ["incident_alert"] = "alert_incident",
            ["command_rejected"] = "alert_command_rejected",
            ["needs_reconfirmation"] = "alert_needs_reconfirmation",
            ["session_recovered"] = "alert_session_recovered",
        };

        private readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
        private AudioSource source;

        // -----------------------------------------------------------------------------------
        // Function: Awake
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function creates the 2D audio source the alerts play through. Alerts are
        //     interface sounds, not part of the 3D world, so the source has no spatial blend.
        private void Awake()
        {
            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0; // alerts are interface sounds, not part of the 3D world
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
            if (!SoundFiles.TryGetValue(cue, out var file)) return;
            if (!clips.TryGetValue(cue, out var clip))
                clips[cue] = clip = Resources.Load<AudioClip>($"Formula9/Audio/{file}");
            if (clip != null) source.PlayOneShot(clip);
        }
    }
}
