using System.Collections.Generic;
using System.Linq;
using Formula9.Audio;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Formula9.Tests
{
    // amee's sound review applied to the Unity controls (see UiSounds).
    public class UiSoundTests
    {
        private List<UiSound> played;
        private float clock;
        private UiSounds sounds;

        [SetUp]
        public void SetUp()
        {
            played = new List<UiSound>();
            clock = 0;
            sounds = new UiSounds(played.Add, () => clock);
        }

        private static Button Button(string text, bool primary = false, bool enabled = true)
        {
            var button = new Button { text = text };
            if (primary) button.AddToClassList("primary");
            button.SetEnabled(enabled);
            return button;
        }

        [Test]
        public void SmallButtonsPlayTheHoverSoundAndBigOnesStaySilent()
        {
            sounds.Hovered(Button("Pause"));
            sounds.Hovered(Button("Start scenario", primary: true));
            sounds.Hovered(Button("Resume", enabled: false));
            var login = Button("LOGIN"); login.AddToClassList("login-button");
            sounds.Hovered(login);
            sounds.Hovered(new Button { name = UiSounds.StartButtonName, text = "Start scenario" });
            CollectionAssert.AreEqual(new[] { UiSound.ButtonHover }, played);
        }

        [Test]
        public void TheRealLayoutsBigButtonsAreTheOnesWithoutHoverSound()
        {
            var root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Formula9/UI/Formula9App.uxml").CloneTree();
            var silent = root.Query<Button>().ToList().Where(b => !UiSounds.HoverPlays(b)).Select(b => b.name);
            CollectionAssert.AreEquivalent(new[] { "submit", "create", "join", "start" }, silent);
        }

        // Pointer events reach the root from whatever is under the pointer: an icon or label inside
        // a nav button, or a button the screens built after Attach.
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

        [Test]
        public void StartAndMapPointsHaveTheirOwnSounds()
        {
            sounds.StartClicked();
            sounds.PointPicked();
            CollectionAssert.AreEqual(new[] { UiSound.StartClick, UiSound.MapPoint }, played);
        }

        [Test]
        public void DropdownsSoundWhenOpenedAndWhenTheUserChooses()
        {
            var field = new DropdownField("Route", new List<string> { "a", "b" }, 0);
            sounds.DropdownOpened(field);
            sounds.DropdownChanged(field);
            CollectionAssert.AreEqual(new[] { UiSound.Dropdown, UiSound.Dropdown }, played);
        }

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

        [Test]
        public void ADisabledDropdownIsSilent()
        {
            var field = new DropdownField("Option", new List<string> { "a" }, 0);
            field.SetEnabled(false);
            sounds.DropdownOpened(field);
            Assert.IsEmpty(played);
        }

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
