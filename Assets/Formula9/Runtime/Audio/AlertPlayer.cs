using System.Collections.Generic;
using UnityEngine;

namespace Formula9.Audio
{
    // Plays alert cues by name. The sounds are the Qt client's Assets/Audio/alert_*.wav, made by
    // its generate_alert_cues.py, so both clients sound the same. Unknown cues stay silent.
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

        private void Awake()
        {
            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0; // alerts are interface sounds, not part of the 3D world
        }

        public void Play(string cue)
        {
            if (!SoundFiles.TryGetValue(cue, out var file)) return;
            if (!clips.TryGetValue(cue, out var clip))
                clips[cue] = clip = Resources.Load<AudioClip>($"Formula9/Audio/{file}");
            if (clip != null) source.PlayOneShot(clip);
        }
    }
}
