/*
 * Interface sounds from amee's sound consultation with Vuk.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 22/11/2026
 *
 * Description:
 *     Applies the consultation's rules to the Unity controls: buttons make no hover sound and most
 *     presses stay silent. Only the major buttons (Start scenario, Start and watch in 3D, Approve
 *     route and the Charging page's Apply) play an electric click, and picking a point for a route
 *     plays the map point sound. Clicking a button that is greyed out plays the command rejected
 *     sound, so the app says no out loud. Interface sounds are separate from training alerts, so
 *     Mute alerts does not silence them.
 */

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Formula9.Audio
{
    // -----------------------------------------------------------------------------------
    // Class: UiSound
    // -----------------------------------------------------------------------------------
    // Description:
    //     This enum lists the interface sounds. UiSounds.Files maps each to its clip.
    public enum UiSound { MajorButton, MapPoint, Refused }

    // -----------------------------------------------------------------------------------
    // Class: UiSounds
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class decides when an interface sound plays. The player is injected, so the rules
    //     are tested without audio.
    //
    //     Attributes: Volume (0.35, as in the web app); MajorButtons, the names of the buttons
    //     that click; Files, sound to clip in Resources/Formula9/Audio.
    public sealed class UiSounds
    {
        public const float Volume = 0.35f;

        // The buttons the consultation counts as major; every other press stays silent.
        public static readonly IReadOnlyList<string> MajorButtons = new[] { "start", "route-start", "route-approve", "charge-apply" };

        // Sound -> clip in Resources/Formula9/Audio.
        public static readonly IReadOnlyDictionary<UiSound, string> Files = new Dictionary<UiSound, string>
        {
            [UiSound.MajorButton] = "ui_major_button",  // App SFX: UI Button Click Positive_1.wav
            [UiSound.MapPoint] = "ui_map_point",        // UI Hover New 1.wav
            [UiSound.Refused] = "alert_command_rejected", // the same sound as a command the backend refuses
        };

        private readonly Action<UiSound> play;

        // -----------------------------------------------------------------------------------
        // Function: UiSounds (constructor)
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function connects the rules to a player.
        //
        // Args:
        //     play (Action<UiSound>): Plays a sound, normally UiSoundPlayer.Play.
        public UiSounds(Action<UiSound> play)
        {
            this.play = play;
        }

        // -----------------------------------------------------------------------------------
        // Function: MajorPressed
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function plays the electric click for a major button.
        public void MajorPressed() => play?.Invoke(UiSound.MajorButton);

        // -----------------------------------------------------------------------------------
        // Function: PointPicked
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function plays the map point sound (UI Hover New 1) when a road is picked for a
        //     route.
        public void PointPicked() => play?.Invoke(UiSound.MapPoint);

        // -----------------------------------------------------------------------------------
        // Function: Pressed
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function plays the refused sound when a press lands on a greyed-out button. A
        //     disabled button gets no click of its own, so the app calls this for every press,
        //     with the element under the pointer.
        //
        // Args:
        //     target (VisualElement): The element pressed, or one inside a button such as its label.
        public void Pressed(VisualElement target)
        {
            if (DisabledButton(target)) play?.Invoke(UiSound.Refused);
        }

        // -----------------------------------------------------------------------------------
        // Function: DisabledButton
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function tells whether an element is, or sits inside, a button that is greyed out.
        //
        // Args:
        //     target (VisualElement): The element pressed.
        //
        // Returns:
        //     bool: True when the nearest button is disabled.
        public static bool DisabledButton(VisualElement target)
        {
            for (var element = target; element != null; element = element.parent)
                if (element is Button) return !element.enabledInHierarchy;
            return false;
        }
    }

    // -----------------------------------------------------------------------------------
    // Class: UiSoundPlayer
    // -----------------------------------------------------------------------------------
    // Description:
    //     This component plays the interface sounds. PlayOneShot lets quick repeats overlap
    //     instead of cutting each other off.
    public sealed class UiSoundPlayer : MonoBehaviour
    {
        private readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
        private AudioSource source;

        // -----------------------------------------------------------------------------------
        // Function: Awake
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function adds a 2D audio source that does not play on its own.
        private void Awake()
        {
            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0;
        }

        // -----------------------------------------------------------------------------------
        // Function: Play
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function plays one interface sound at the set volume. Each clip is loaded the
        //     first time it plays and then kept; a missing clip stays silent.
        //
        // Args:
        //     sound (UiSound): The sound to play.
        public void Play(UiSound sound)
        {
            var file = UiSounds.Files[sound];
            if (!clips.TryGetValue(file, out var clip)) clips[file] = clip = Resources.Load<AudioClip>($"Formula9/Audio/{file}");
            if (clip != null) source.PlayOneShot(clip, UiSounds.Volume);
        }
    }
}
