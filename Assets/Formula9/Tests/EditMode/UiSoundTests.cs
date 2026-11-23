/*
 * Unit tests for UiSounds.cs.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 22/11/2026
 *
 * Description:
 *     Checks the sound consultation as applied to the Unity controls: the major buttons exist in
 *     the real layout, the major button and map point sounds play, and every sound is a real clip.
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
