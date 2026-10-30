using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Formula9.Audio
{
    // The interface sounds amee's review assigns; Files maps each to its clip.
    public enum UiSound { ButtonHover, StartClick, MapPoint, Dropdown }

    // amee's sound review, as the Formula_9 web app applies it (uiAudio.ts), mapped onto the Unity
    // controls:
    //   - TileHover 2 on hover for the enabled buttons of the main simulator (not the login page),
    //     except the big primary ones; no click sound. Start scenario is one of amee's hover buttons.
    //   - UI_Hover 6 when Start scenario is clicked.
    //   - UI Hover New 1 when a node is picked on the route planning graph.
    //   - A higher-pitched UI Hover New 1 when a dropdown opens and when an option is chosen;
    //     hovering options and values set by the app itself stay silent.
    // Interface sounds are separate from training alerts, so Mute alerts does not silence them.
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

        public UiSounds(Action<UiSound> play, Func<float> now = null)
        {
            this.play = play;
            this.now = now ?? (() => Time.realtimeSinceStartup);
        }

        // The large call-to-action buttons stay silent on hover, as in the web app.
        public const string BigButtonClass = "primary";

        private Button hovered;

        public static bool HoverPlays(Button button) =>
            button.enabledInHierarchy && !button.ClassListContains(BigButtonClass);

        public void Hovered(Button button)
        {
            if (HoverPlays(button)) play?.Invoke(UiSound.ButtonHover);
        }

        public void StartClicked() => play?.Invoke(UiSound.StartClick);

        public void PointPicked() => play?.Invoke(UiSound.MapPoint);

        public void DropdownOpened(DropdownField field)
        {
            if (!field.enabledInHierarchy) return;
            opened[field] = now();
            play?.Invoke(UiSound.Dropdown);
        }

        // Only a change that follows the user opening this dropdown plays; the app also sets values
        // while it renders snapshots, and those must stay silent.
        public void DropdownChanged(DropdownField field)
        {
            if (!opened.TryGetValue(field, out var at)) return;
            opened.Remove(field);
            if (now() - at <= ChoiceWindowSeconds) play?.Invoke(UiSound.Dropdown);
        }

        // Listens once on the main simulator's root, so controls the screens build later (charging decisions, map
        // zoom buttons) are covered too. Pointer over/down bubble up from icons and labels inside a
        // control, so each is resolved to the button or dropdown that contains it.
        public void Attach(VisualElement root)
        {
            root.RegisterCallback<PointerOverEvent>(e => PointerOver(e.target), TrickleDown.TrickleDown);
            // The field's own input handles the press, so listen on the way down.
            root.RegisterCallback<PointerDownEvent>(e => PointerDown(e.target), TrickleDown.TrickleDown);
            root.RegisterCallback<ChangeEvent<string>>(e => ValueChanged(e.target));
        }

        public void PointerOver(IEventHandler target)
        {
            var button = Enclosing<Button>(target);
            if (button == hovered) return;
            hovered = button;
            if (button != null) Hovered(button);
        }

        public void PointerDown(IEventHandler target)
        {
            var field = Enclosing<DropdownField>(target);
            if (field != null) DropdownOpened(field);
        }

        public void ValueChanged(IEventHandler target)
        {
            if (target is DropdownField field) DropdownChanged(field);
        }

        private static T Enclosing<T>(IEventHandler target) where T : VisualElement
        {
            for (var element = target as VisualElement; element != null; element = element.parent)
                if (element is T match) return match;
            return null;
        }
    }

    // Plays interface sounds. The dropdown variant needs its own pitch, so it has its own source;
    // PlayOneShot lets quick repeats overlap instead of cutting each other off.
    public sealed class UiSoundPlayer : MonoBehaviour
    {
        private readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
        private AudioSource normal, pitched;

        private void Awake()
        {
            normal = Source(1);
            pitched = Source(UiSounds.DropdownPitch);
        }

        private AudioSource Source(float pitch)
        {
            var source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0;
            source.pitch = pitch;
            return source;
        }

        public void Play(UiSound sound)
        {
            var file = UiSounds.Files[sound];
            if (!clips.TryGetValue(file, out var clip)) clips[file] = clip = Resources.Load<AudioClip>($"Formula9/Audio/{file}");
            if (clip != null) (sound == UiSound.Dropdown ? pitched : normal).PlayOneShot(clip, UiSounds.Volume);
        }
    }
}
