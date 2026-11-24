/*
 * Interface sounds from amee's sound consultation with Vuk and the audio professor's review.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 24/11/2026
 *
 * Description:
 *     Applies the consultation's rules to the Unity controls: buttons make no hover sound and most
 *     presses stay silent. Start scenario, Start and watch in 3D and Resume simulation play the start
 *     session click; Approve route (a re-route), Route Planning's Confirm (keep, hold or assist) and the
 *     Charging page's Apply play the confirmation sound; picking a point for a route
 *     plays the map point sound. Clicking a button that is greyed out plays the command rejected
 *     sound, so the app says no out loud, unless the app is busy sending something: a button greyed
 *     out only while a command, login or preview is on its way has not refused anything. Interface sounds are separate from training alerts, so
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
    public enum UiSound { StartSession, Confirm, MapPoint, Refused }

    // -----------------------------------------------------------------------------------
    // Class: UiSounds
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class decides when an interface sound plays. The player is injected, so the rules
    //     are tested without audio.
    //
    //     Attributes: Volume (0.35, as in the web app); Volumes, the sounds played louder;
    //     StartButtons, the buttons that start or
    //     resume the session; ConfirmButtons, the buttons that confirm a decision; Files, sound to
    //     clip in Resources/Formula9/Audio.
    public sealed class UiSounds
    {
        public const float Volume = 0.35f;

        // Sounds louder than Volume. The confirmation chime is soft and was hard to hear at 0.35.
        public static readonly IReadOnlyDictionary<UiSound, float> Volumes = new Dictionary<UiSound, float>
        {
            [UiSound.Confirm] = 1f,
        };

        // -----------------------------------------------------------------------------------
        // Function: VolumeOf
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function gives the volume a sound plays at: its own from Volumes, else Volume.
        //
        // Args:
        //     sound (UiSound): The sound.
        //
        // Returns:
        //     float: Its volume, 0 to 1.
        public static float VolumeOf(UiSound sound) => Volumes.TryGetValue(sound, out var volume) ? volume : Volume;

        // The audio professor's review: starting and resuming the session share one click, and every
        // other confirmation plays the confirmation sound. Every other press stays silent.
        public static readonly IReadOnlyList<string> StartButtons = new[] { "start", "route-start", "resume" };
        public static readonly IReadOnlyList<string> ConfirmButtons = new[] { "route-approve", "route-confirm-send", "charge-apply" };

        // Sound -> clip in Resources/Formula9/Audio.
        public static readonly IReadOnlyDictionary<UiSound, string> Files = new Dictionary<UiSound, string>
        {
            [UiSound.StartSession] = "ui_start_session", // UI Button Click-Dull.wav
            [UiSound.Confirm] = "ui_confirmation",      // UI Confirmation_2.wav
            [UiSound.MapPoint] = "ui_map_point",        // UI Hover New 1.wav
            [UiSound.Refused] = "alert_command_rejected", // the training alert's clip, reused for a greyed-out button
        };

        private readonly Action<UiSound> play;
        private readonly Func<bool> busy;
        private readonly EventCallback<PointerDownEvent> onPointerDown;

        // -----------------------------------------------------------------------------------
        // Function: UiSounds (constructor)
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function connects the rules to a player.
        //
        // Args:
        //     play (Action<UiSound>): Plays a sound, normally UiSoundPlayer.Play.
        //     busy (Func<bool>): True while a command, login or preview is being sent; greyed-out
        //         presses stay silent then, even on a button greyed out for a real reason. Optional.
        public UiSounds(Action<UiSound> play, Func<bool> busy = null)
        {
            this.play = play;
            this.busy = busy;
            onPointerDown = e => { if (e.button == 0) Pressed(e.target as VisualElement); };
        }

        // -----------------------------------------------------------------------------------
        // Function: Listen
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function catches presses under a root. A greyed-out button gets no events of its
        //     own, so presses are caught on the way down from the root. Only the primary button (or
        //     a touch) counts; a right or middle click is not a press.
        //
        // Args:
        //     root (VisualElement): The screen's root element.
        public void Listen(VisualElement root) => root.RegisterCallback(onPointerDown, TrickleDown.TrickleDown);

        // -----------------------------------------------------------------------------------
        // Function: Unlisten
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function stops catching presses under a root, undoing Listen. A null root (one
        //     already gone) is ignored.
        //
        // Args:
        //     root (VisualElement): The root passed to Listen.
        public void Unlisten(VisualElement root) => root?.UnregisterCallback(onPointerDown, TrickleDown.TrickleDown);

        // -----------------------------------------------------------------------------------
        // Function: StartPressed
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function plays the start session click for a start or resume button.
        public void StartPressed() => play?.Invoke(UiSound.StartSession);

        // -----------------------------------------------------------------------------------
        // Function: ConfirmPressed
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function plays the confirmation sound for a button that confirms a decision.
        public void ConfirmPressed() => play?.Invoke(UiSound.Confirm);

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
        //     disabled button gets no click of its own, so Listen calls this for every press, with
        //     the element under the pointer. Nothing plays while the app is busy, since a button
        //     may be greyed out only until the work in flight finishes.
        //
        // Args:
        //     target (VisualElement): The element pressed, or one inside a button such as its label.
        public void Pressed(VisualElement target)
        {
            if (busy?.Invoke() == true) return;
            if (DisabledButton(target)) play?.Invoke(UiSound.Refused);
        }

        // -----------------------------------------------------------------------------------
        // Function: DisabledButton
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function tells whether an element is, or sits inside, a button that is greyed out.
        //     Only buttons count: they are what send commands, so they are what can be refused. A
        //     greyed-out field, toggle or dropdown stays silent.
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
        //     This function adds a 2D audio source that does not play on its own, and loads every
        //     interface clip up front: a clip whose audio is not loaded yet plays nothing the first
        //     time, so each sound's first press would otherwise be silent.
        private void Awake()
        {
            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0;
            foreach (var file in UiSounds.Files.Values)
                if (!clips.ContainsKey(file)) clips[file] = AlertPlayer.LoadClip(file);
        }

        // -----------------------------------------------------------------------------------
        // Function: Play
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function plays one interface sound at its volume (VolumeOf). The clips were loaded in
        //     Awake; a missing clip stays silent.
        //
        // Args:
        //     sound (UiSound): The sound to play.
        public void Play(UiSound sound)
        {
            var file = UiSounds.Files[sound];
            if (!clips.TryGetValue(file, out var clip)) clips[file] = clip = AlertPlayer.LoadClip(file);
            if (clip != null) source.PlayOneShot(clip, UiSounds.VolumeOf(sound));
        }
    }
}
