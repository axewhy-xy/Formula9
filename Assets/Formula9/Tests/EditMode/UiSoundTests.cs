/*
 * Unit tests for UiSounds.cs.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 23/11/2026
 *
 * Description:
 *     Checks the sound consultation as applied to the Unity controls: the major buttons exist in
 *     the real layout, the major button and map point sounds play, a greyed-out button plays the
 *     refused sound unless the app is busy, a real press reaches it through the root, and every
 *     sound is a real clip.
 */

using System.Collections.Generic;
using System.Linq;
using Formula9.Audio;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Formula9.Tests
{
    // -----------------------------------------------------------------------------------
    // Class: UiSoundTests
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class tests the interface sounds with a fake player.
    public class UiSoundTests
    {
        private List<UiSound> played;
        private UiSounds sounds;

        // -----------------------------------------------------------------------------------
        // Function: SetUp
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function gives each test a fresh list of played sounds.
        [SetUp]
        public void SetUp()
        {
            played = new List<UiSound>();
            sounds = new UiSounds(played.Add);
        }

        // -----------------------------------------------------------------------------------
        // Function: EveryMajorButtonIsInTheRealLayout
        // -----------------------------------------------------------------------------------
        // Description:
        //     Each major button the app hooks the click onto exists in the simulator layout, so
        //     a renamed button fails here instead of going silent.
        [Test]
        public void EveryMajorButtonIsInTheRealLayout()
        {
            var root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Formula9/UI/Formula9App.uxml").CloneTree();
            foreach (var name in UiSounds.MajorButtons)
                Assert.IsNotNull(root.Q<Button>(name), name);
        }

        // -----------------------------------------------------------------------------------
        // Function: MajorButtonsAndMapPointsHaveTheirOwnSounds
        // -----------------------------------------------------------------------------------
        // Description:
        //     A major button and picking a map point play their own sounds.
        [Test]
        public void MajorButtonsAndMapPointsHaveTheirOwnSounds()
        {
            sounds.MajorPressed();
            sounds.PointPicked();
            CollectionAssert.AreEqual(new[] { UiSound.MajorButton, UiSound.MapPoint }, played);
        }

        // -----------------------------------------------------------------------------------
        // Function: PressingAGreyedOutButtonPlaysTheRefusedSound
        // -----------------------------------------------------------------------------------
        // Description:
        //     A press on a disabled button, or on its label, plays the refused sound, and so does a
        //     press on a button greyed out by its disabled panel; a press on an enabled button, or on
        //     no button, stays silent.
        [Test]
        public void PressingAGreyedOutButtonPlaysTheRefusedSound()
        {
            var panel = new VisualElement();
            var off = new Button { text = "Apply" };
            var label = new Label("inside");
            off.Add(label);
            off.SetEnabled(false);
            var on = new Button();
            panel.Add(off); panel.Add(on);

            sounds.Pressed(off);
            sounds.Pressed(label);
            CollectionAssert.AreEqual(new[] { UiSound.Refused, UiSound.Refused }, played);

            played.Clear();
            sounds.Pressed(on);
            sounds.Pressed(panel);
            sounds.Pressed(null);
            CollectionAssert.IsEmpty(played);

            panel.SetEnabled(false);
            sounds.Pressed(on);
            CollectionAssert.AreEqual(new[] { UiSound.Refused }, played, "a button greyed out by its panel counts too");
        }

        // -----------------------------------------------------------------------------------
        // Function: AGreyedOutButtonStaysSilentWhileTheAppIsBusy
        // -----------------------------------------------------------------------------------
        // Description:
        //     A button greyed out while a command, login or preview is being sent is waiting, not
        //     refusing, so a second click on it (Apply double-clicked) stays silent until the app is
        //     done.
        [Test]
        public void AGreyedOutButtonStaysSilentWhileTheAppIsBusy()
        {
            var busy = true;
            var waiting = new UiSounds(played.Add, () => busy);
            var apply = new Button();
            apply.SetEnabled(false);

            waiting.Pressed(apply);
            CollectionAssert.IsEmpty(played);

            busy = false;
            waiting.Pressed(apply);
            CollectionAssert.AreEqual(new[] { UiSound.Refused }, played);
        }

        // -----------------------------------------------------------------------------------
        // Function: ARealPressOnAGreyedOutButtonReachesTheRoot
        // -----------------------------------------------------------------------------------
        // Description:
        //     Through a real panel, a left press on a greyed-out button (or its label) is caught
        //     by the root's listener and plays the refused sound; a right or middle click does not,
        //     and after Unlisten nothing plays.
        [Test]
        public void ARealPressOnAGreyedOutButtonReachesTheRoot()
        {
            var host = new GameObject("UiSoundTests panel");
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            try
            {
                host.SetActive(false);
                var document = host.AddComponent<UIDocument>();
                document.panelSettings = settings;
                host.SetActive(true);
                var root = document.rootVisualElement;
                Assert.IsNotNull(root?.panel, "the document has a panel");

                var off = new Button { text = "Apply" };
                var label = new Label("inside");
                off.Add(label);
                off.SetEnabled(false);
                root.Add(off);
                sounds.Listen(root);

                Press(off, 0);
                Press(label, 0);
                CollectionAssert.AreEqual(new[] { UiSound.Refused, UiSound.Refused }, played);

                played.Clear();
                Press(off, 1);
                Press(off, 2);
                CollectionAssert.IsEmpty(played, "a right or middle click is not a press");

                sounds.Unlisten(root);
                Press(off, 0);
                CollectionAssert.IsEmpty(played, "Unlisten stops the sound");
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(settings);
            }
        }

        // Sends a pointer press with the given mouse button straight to an element.
        private static void Press(VisualElement target, int button)
        {
            var mouse = new Event { type = EventType.MouseDown, button = button, mousePosition = Vector2.zero };
            using (var press = PointerDownEvent.GetPooled(mouse))
            {
                press.target = target;
                target.SendEvent(press);
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: EverySoundIsARealClip
        // -----------------------------------------------------------------------------------
        // Description:
        //     Every sound loads as a real clip.
        [Test]
        public void EverySoundIsARealClip()
        {
            foreach (var file in UiSounds.Files.Values.Distinct())
            {
                var clip = Resources.Load<AudioClip>($"Formula9/Audio/{file}");
                Assert.IsNotNull(clip, file);
                Assert.Greater(clip.length, 0.02f, file);
            }
        }
    }
}
