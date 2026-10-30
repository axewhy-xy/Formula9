/*
 * Interface sounds from amee's sound review.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Applies amee's review the same way the Formula_9 web app does (uiAudio.ts), mapped onto the
 *     Unity controls: TileHover 2 on hover for the enabled buttons of the main simulator, except
 *     the big primary ones, with no click sound; UI_Hover 6 when Start scenario is clicked; UI
 *     Hover New 1 when a node is picked for a route; and a higher-pitched UI Hover New 1 when a
 *     dropdown opens and when the user chooses an option. Hovering options and values set by the
 *     app itself stay silent. Interface sounds are separate from training alerts, so Mute alerts
 *     does not silence them.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Formula9.Audio
{
    // -----------------------------------------------------------------------------------
    // Class: UiSound
    // -----------------------------------------------------------------------------------
    // Description:
    //     This enum lists the interface sounds amee's review assigns. UiSounds.Files maps each to
    //     its clip.
    public enum UiSound { ButtonHover, StartClick, MapPoint, Dropdown }

    // -----------------------------------------------------------------------------------
    // Class: UiSounds
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class decides when an interface sound plays. The player and the clock are injected,
    //     so the rules are tested without audio.
    //
    //     Attributes: Volume (0.35, as in the web app); DropdownPitch (1.15); ChoiceWindowSeconds,
    //     how long after opening a dropdown a change still counts as the user's choice; Files,
    //     sound to clip in Resources/Formula9/Audio (copies of the web app's Assets/Audio files);
    //     BigButtonClass, the class of the big buttons that stay silent on hover.
    public sealed class UiSounds
    {
        public const float Volume = 0.35f;
        public const float DropdownPitch = 1.15f;
        // A choice counts as the user's only shortly after they opened that dropdown.
        public const float ChoiceWindowSeconds = 30;

        // Sound -> clip in Resources/Formula9/Audio (copies of the web app's Assets/Audio files).
        public static readonly IReadOnlyDictionary<UiSound, string> Files = new Dictionary<UiSound, string>
        {
            [UiSound.ButtonHover] = "ui_button_hover",  // TileHover 2.wav
            [UiSound.StartClick] = "ui_start_click",    // UI_Hover 6.wav
            [UiSound.MapPoint] = "ui_map_point",        // UI Hover New 1.wav
            [UiSound.Dropdown] = "ui_map_point",        // the same sound, played at DropdownPitch
        };

        private readonly Action<UiSound> play;
        private readonly Func<float> now;
        private readonly Dictionary<DropdownField, float> opened = new Dictionary<DropdownField, float>();

        // -----------------------------------------------------------------------------------
        // Function: UiSounds (constructor)
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function connects the rules to a player and a clock.
        //
        // Args:
        //     play (Action<UiSound>): Plays a sound, normally UiSoundPlayer.Play.
        //     now (Func<float>): The current time in seconds; the real-time clock when null.
        public UiSounds(Action<UiSound> play, Func<float> now = null)
        {
            this.play = play;
            this.now = now ?? (() => Time.realtimeSinceStartup);
        }

        // The large call-to-action buttons stay silent on hover, as in the web app.
        public const string BigButtonClass = "primary";

        private Button hovered;

        // -----------------------------------------------------------------------------------
        // Function: HoverPlays
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function tells whether hovering a button plays the hover sound: it must be
        //     enabled and not one of the big primary buttons.
        //
        // Args:
        //     button (Button): The hovered button.
        //
        // Returns:
        //     bool: True when the hover sound plays.
        public static bool HoverPlays(Button button) =>
            button.enabledInHierarchy && !button.ClassListContains(BigButtonClass);

        // -----------------------------------------------------------------------------------
        // Function: Hovered
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function plays the hover sound for a button if HoverPlays allows it.
        //
        // Args:
        //     button (Button): The button the pointer entered.
        public void Hovered(Button button)
        {
            if (HoverPlays(button)) play?.Invoke(UiSound.ButtonHover);
        }

        // -----------------------------------------------------------------------------------
        // Function: StartClicked
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function plays the Start scenario click sound (UI_Hover 6).
        public void StartClicked() => play?.Invoke(UiSound.StartClick);

        // -----------------------------------------------------------------------------------
        // Function: PointPicked
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function plays the map point sound (UI Hover New 1) when a node is picked for a
        //     route.
        public void PointPicked() => play?.Invoke(UiSound.MapPoint);

        // -----------------------------------------------------------------------------------
        // Function: DropdownOpened
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function plays the dropdown sound when the user opens an enabled dropdown, and
        //     records when, so the choice that follows can be told apart from values the app sets.
        //
        // Args:
        //     field (DropdownField): The dropdown that was pressed.
        public void DropdownOpened(DropdownField field)
        {
            if (!field.enabledInHierarchy) return;
            opened[field] = now();
            play?.Invoke(UiSound.Dropdown);
        }

        // -----------------------------------------------------------------------------------
        // Function: DropdownChanged
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function plays the dropdown sound when a dropdown's value changes shortly after
        //     the user opened it, once per opening. The app also sets values while it renders
        //     snapshots, and those must stay silent.
        //
        // Args:
        //     field (DropdownField): The dropdown whose value changed.
        public void DropdownChanged(DropdownField field)
        {
            if (!opened.TryGetValue(field, out var at)) return;
            opened.Remove(field);
            if (now() - at <= ChoiceWindowSeconds) play?.Invoke(UiSound.Dropdown);
        }

        // -----------------------------------------------------------------------------------
        // Function: Attach
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function hooks the sounds onto a screen. It listens once on the root, so
        //     controls the screens build later (charging decisions, map zoom buttons) are covered
        //     too. Pointer over and down are heard on the way down the tree, because a control's
        //     own input may handle them.
        //
        // Args:
        //     root (VisualElement): The main simulator's root element.
        public void Attach(VisualElement root)
        {
            root.RegisterCallback<PointerOverEvent>(e => PointerOver(e.target), TrickleDown.TrickleDown);
            // The field's own input handles the press, so listen on the way down.
            root.RegisterCallback<PointerDownEvent>(e => PointerDown(e.target), TrickleDown.TrickleDown);
            root.RegisterCallback<ChangeEvent<string>>(e => ValueChanged(e.target));
        }

        // -----------------------------------------------------------------------------------
        // Function: PointerOver
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function handles the pointer moving over any element. The element is resolved to
        //     the button that contains it (an icon or label inside a nav button counts as the
        //     button), and the hover sound plays once per entry into that button.
        //
        // Args:
        //     target (IEventHandler): The element under the pointer.
        public void PointerOver(IEventHandler target)
        {
            var button = Enclosing<Button>(target);
            if (button == hovered) return;
            hovered = button;
            if (button != null) Hovered(button);
        }

        // -----------------------------------------------------------------------------------
        // Function: PointerDown
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function handles a press on any element and treats a press inside a dropdown as
        //     opening it.
        //
        // Args:
        //     target (IEventHandler): The element pressed.
        public void PointerDown(IEventHandler target)
        {
            var field = Enclosing<DropdownField>(target);
            if (field != null) DropdownOpened(field);
        }

        // -----------------------------------------------------------------------------------
        // Function: ValueChanged
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function handles a text value change anywhere under the root and passes on those
        //     that come from a dropdown.
        //
        // Args:
        //     target (IEventHandler): The element whose value changed.
        public void ValueChanged(IEventHandler target)
        {
            if (target is DropdownField field) DropdownChanged(field);
        }

        // -----------------------------------------------------------------------------------
        // Function: Enclosing
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function finds the nearest element of a given type at or above an element.
        //
        // Args:
        //     target (IEventHandler): Where to start looking.
        //
        // Returns:
        //     T: The element found, or null when none contains target.
        private static T Enclosing<T>(IEventHandler target) where T : VisualElement
        {
            for (var element = target as VisualElement; element != null; element = element.parent)
                if (element is T match) return match;
            return null;
        }
    }

    // -----------------------------------------------------------------------------------
    // Class: UiSoundPlayer
    // -----------------------------------------------------------------------------------
    // Description:
    //     This component plays the interface sounds. The dropdown variant needs its own pitch, so
    //     it has its own audio source; PlayOneShot lets quick repeats overlap instead of cutting
    //     each other off.
    public sealed class UiSoundPlayer : MonoBehaviour
    {
        private readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
        private AudioSource normal, pitched;

        // -----------------------------------------------------------------------------------
        // Function: Awake
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function creates the two audio sources: one at normal pitch and one at
        //     DropdownPitch.
        private void Awake()
        {
            normal = Source(1);
            pitched = Source(UiSounds.DropdownPitch);
        }

        // -----------------------------------------------------------------------------------
        // Function: Source
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function adds a 2D audio source that does not play on its own.
        //
        // Args:
        //     pitch (float): The playback pitch.
        //
        // Returns:
        //     AudioSource: The new source.
        private AudioSource Source(float pitch)
        {
            var source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0;
            source.pitch = pitch;
            return source;
        }

        // -----------------------------------------------------------------------------------
        // Function: Play
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function plays one interface sound at the review's volume. Each clip is loaded
        //     the first time it plays and then kept; a missing clip stays silent.
        //
        // Args:
        //     sound (UiSound): The sound to play.
        public void Play(UiSound sound)
        {
            var file = UiSounds.Files[sound];
            if (!clips.TryGetValue(file, out var clip)) clips[file] = clip = Resources.Load<AudioClip>($"Formula9/Audio/{file}");
            if (clip != null) (sound == UiSound.Dropdown ? pitched : normal).PlayOneShot(clip, UiSounds.Volume);
        }
    }
}
