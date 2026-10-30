using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Formula9.Audio
{
    public enum UiSound { ButtonHover, StartClick, MapPoint, Dropdown }

    // amee's sound review, as the Formula_9 web app applies it (uiAudio.ts), mapped onto the Unity
    // controls:
    //   - TileHover 2 on hover for every enabled button except the big primary ones; no click sound.
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

        public static bool HoverPlays(Button button) => button.enabledInHierarchy && !button.ClassListContains("primary");

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

        // Hooks every button and dropdown already in the tree. Call it once the screens are built.
        public void Attach(VisualElement root)
        {
            foreach (var button in root.Query<Button>().ToList())
                button.RegisterCallback<PointerEnterEvent>(_ => Hovered(button));
            foreach (var field in root.Query<DropdownField>().ToList())
            {
                // The field's own input handles the press, so listen on the way down.
                field.RegisterCallback<PointerDownEvent>(_ => DropdownOpened(field), TrickleDown.TrickleDown);
                field.RegisterValueChangedCallback(_ => DropdownChanged(field));
            }
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
