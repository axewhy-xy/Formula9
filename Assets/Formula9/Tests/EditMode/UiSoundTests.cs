/*
 * Unit tests for UiSounds.cs.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Checks amee's sound review as applied to the Unity controls: which buttons play the hover
 *     sound (including on the real layout), the Start and map point sounds, the dropdown sounds and
 *     the values that stay silent, and that every sound is a real clip from the web app.
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
    //     This class tests the interface sounds with a fake player and clock.
    public class UiSoundTests
    {
        private List<UiSound> played;
        private float clock;
        private UiSounds sounds;

        // -----------------------------------------------------------------------------------
        // Function: SetUp
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function gives each test a fresh list of played sounds and a clock at zero.
        [SetUp]
        public void SetUp()
        {
            played = new List<UiSound>();
            clock = 0;
            sounds = new UiSounds(played.Add, () => clock);
        }

        // -----------------------------------------------------------------------------------
        // Function: Button
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function makes a button.
        //
        // Args:
        //     text (string): Its text.
        //     primary (bool): True for a big primary button.
        //     enabled (bool): False for a disabled button.
        //
        // Returns:
        //     Button: The button.
        private static Button Button(string text, bool primary = false, bool enabled = true)
        {
            var button = new Button { text = text };
            if (primary) button.AddToClassList("primary");
            button.SetEnabled(enabled);
            return button;
        }

        // -----------------------------------------------------------------------------------
        // Function: SmallButtonsPlayTheHoverSoundAndBigOnesStaySilent
        // -----------------------------------------------------------------------------------
        // Description:
        //     Only enabled, non-primary buttons play the hover sound.
        [Test]
        public void SmallButtonsPlayTheHoverSoundAndBigOnesStaySilent()
        {
            sounds.Hovered(Button("Pause"));
            sounds.Hovered(Button("Start scenario", primary: true));
            sounds.Hovered(Button("Resume", enabled: false));
            CollectionAssert.AreEqual(new[] { UiSound.ButtonHover }, played);
        }

        // -----------------------------------------------------------------------------------
        // Function: TheRealLayoutsBigButtonsAreTheOnesWithoutHoverSound
        // -----------------------------------------------------------------------------------
        // Description:
        //     In the real simulator layout only Create and Join are silent, Start scenario plays
        //     the hover sound, and the login page is outside it.
        [Test]
        public void TheRealLayoutsBigButtonsAreTheOnesWithoutHoverSound()
        {
            var root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Formula9/UI/Formula9App.uxml").CloneTree();
            var shell = root.Q("training-host");
            var silent = shell.Query<Button>().ToList().Where(b => !UiSounds.HoverPlays(b)).Select(b => b.name);
            CollectionAssert.AreEquivalent(new[] { "create", "join" }, silent);
            Assert.IsTrue(UiSounds.HoverPlays(shell.Q<Button>("start")), "amee lists start among the hover buttons");
            Assert.IsNull(shell.Q<Button>("submit"), "the login page is outside the simulator");
        }

        // -----------------------------------------------------------------------------------
        // Function: HoverIsResolvedToTheEnclosingButtonOncePerButton
        // -----------------------------------------------------------------------------------
        // Description:
        //     Pointer events reach the root from whatever is under the pointer, such as a label
        //     inside a nav button. The hover sound plays once per entry into a button, and again
        //     after leaving it.
        [Test]
        public void HoverIsResolvedToTheEnclosingButtonOncePerButton()
        {
            var nav = Button("");
            var label = new Label("Route Planning"); nav.Add(label);
            var row = new VisualElement();
            sounds.PointerOver(label);
            sounds.PointerOver(nav);
            sounds.PointerOver(row);
            sounds.PointerOver(nav);
            CollectionAssert.AreEqual(new[] { UiSound.ButtonHover, UiSound.ButtonHover }, played, "entering again after leaving plays again");
        }

        // -----------------------------------------------------------------------------------
        // Function: DropdownsAreFoundFromThePartPressed
        // -----------------------------------------------------------------------------------
        // Description:
        //     A press inside a dropdown opens it with a sound, and only a dropdown's value change
        //     plays, not a text field's.
        [Test]
        public void DropdownsAreFoundFromThePartPressed()
        {
            var field = new DropdownField("Route", new List<string> { "a", "b" }, 0);
            var inner = field.Q<VisualElement>(className: DropdownField.inputUssClassName);
            sounds.PointerDown(inner ?? field);
            sounds.ValueChanged(field);
            sounds.ValueChanged(new TextField());
            CollectionAssert.AreEqual(new[] { UiSound.Dropdown, UiSound.Dropdown }, played);
        }

        // -----------------------------------------------------------------------------------
        // Function: StartAndMapPointsHaveTheirOwnSounds
        // -----------------------------------------------------------------------------------
        // Description:
        //     Start scenario and picking a map point play their own sounds.
        [Test]
        public void StartAndMapPointsHaveTheirOwnSounds()
        {
            sounds.StartClicked();
            sounds.PointPicked();
            CollectionAssert.AreEqual(new[] { UiSound.StartClick, UiSound.MapPoint }, played);
        }

        // -----------------------------------------------------------------------------------
        // Function: DropdownsSoundWhenOpenedAndWhenTheUserChooses
        // -----------------------------------------------------------------------------------
        // Description:
        //     A dropdown sounds when opened and when the user chooses.
        [Test]
        public void DropdownsSoundWhenOpenedAndWhenTheUserChooses()
        {
            var field = new DropdownField("Route", new List<string> { "a", "b" }, 0);
            sounds.DropdownOpened(field);
            sounds.DropdownChanged(field);
            CollectionAssert.AreEqual(new[] { UiSound.Dropdown, UiSound.Dropdown }, played);
        }

        // -----------------------------------------------------------------------------------
        // Function: ValuesTheAppSetsStaySilent
        // -----------------------------------------------------------------------------------
        // Description:
        //     A change nobody opened the dropdown for is silent, an opening long ago does not claim
        //     a later change, and only one choice plays per opening.
        [Test]
        public void ValuesTheAppSetsStaySilent()
        {
            var field = new DropdownField("Incident", new List<string> { "a", "b" }, 0);
            sounds.DropdownChanged(field);
            Assert.IsEmpty(played, "a change nobody opened the dropdown for");

            sounds.DropdownOpened(field);
            clock = UiSounds.ChoiceWindowSeconds + 1;
            sounds.DropdownChanged(field);
            CollectionAssert.AreEqual(new[] { UiSound.Dropdown }, played, "an open long ago does not claim a later app change");

            sounds.DropdownChanged(field);
            Assert.AreEqual(1, played.Count, "one choice per opening");
        }

        // -----------------------------------------------------------------------------------
        // Function: ADisabledDropdownIsSilent
        // -----------------------------------------------------------------------------------
        // Description:
        //     Pressing a disabled dropdown plays nothing.
        [Test]
        public void ADisabledDropdownIsSilent()
        {
            var field = new DropdownField("Option", new List<string> { "a" }, 0);
            field.SetEnabled(false);
            sounds.DropdownOpened(field);
            Assert.IsEmpty(played);
        }

        // -----------------------------------------------------------------------------------
        // Function: EverySoundIsARealClipFromTheWebApp
        // -----------------------------------------------------------------------------------
        // Description:
        //     Every sound loads as a real clip, and the dropdown reuses the map point clip.
        [Test]
        public void EverySoundIsARealClipFromTheWebApp()
        {
            foreach (var file in UiSounds.Files.Values.Distinct())
            {
                var clip = Resources.Load<AudioClip>($"Formula9/Audio/{file}");
                Assert.IsNotNull(clip, file);
                Assert.Greater(clip.length, 0.02f, file);
            }
            Assert.AreEqual(UiSounds.Files[UiSound.MapPoint], UiSounds.Files[UiSound.Dropdown], "the dropdown is a variation of the map sound");
        }
    }
}
