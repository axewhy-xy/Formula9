/*
 * Unit tests for the day and weather (DayCycle.cs, WorldLook.cs, WorldLighting.cs).
 *
 * Author: Rishika
 * Date Created: 26/11/2026
 * Last Modified: 26/11/2026
 *
 * Description:
 *     Checks that the sun rises in the east around 7am and sets around 7pm over Toa Payoh, stands
 *     high at about 1pm, that the light swaps to the moon at night without ever coming in lower than
 *     the file allows, that the hour keys blend between neighbours and around midnight, that the
 *     session's clock runs the hour at the file's pace, that an overcast sky dims the light and greys
 *     the sky, that a key with a bad hour is turned down, that the project's file has keys for the
 *     whole day, and that the world's lighting dims the unlit ground at night and puts it back after.
 */

using System;
using System.Collections.Generic;
using Formula9.World;
using NUnit.Framework;
using UnityEngine;

namespace Formula9.Tests
{
    // -----------------------------------------------------------------------------------
    // Class: DayCycleTests
    // -----------------------------------------------------------------------------------
    public sealed class DayCycleTests
    {
        private static readonly WorldLook.DayCycleSpec ToaPayoh = new WorldLook.DayCycleSpec();

        private static float Elevation(int day, float hour) => Mathf.Asin(DaySky.SunDirection(ToaPayoh, day, hour).y) * Mathf.Rad2Deg;

        [TestCase(1)]
        [TestCase(172)]
        [TestCase(330)]
        public void TheSunRisesAroundSevenAndSetsAroundSevenOverToaPayoh(int day)
        {
            Assert.Less(Elevation(day, 6.5f), 0, "dark at 6:30");
            Assert.Greater(Elevation(day, 7.5f), 0, "up by 7:30");
            Assert.Greater(Elevation(day, 18.5f), 0, "still up at 6:30pm");
            Assert.Less(Elevation(day, 19.5f), 0, "down by 7:30pm");
            Assert.Greater(Elevation(day, 13), 60, "near overhead at 1pm, as near the equator");
            Assert.Greater(DaySky.SunDirection(ToaPayoh, day, 8).x, 0.5f, "in the east in the morning");
            Assert.Less(DaySky.SunDirection(ToaPayoh, day, 17).x, -0.5f, "in the west in the afternoon");
        }

        [Test]
        public void TheLightIsTheMoonAtNightAndNeverComesInTooLow()
        {
            var toSun = DaySky.SunDirection(ToaPayoh, 330, 23);
            var toLight = DaySky.LightDirection(toSun, 10, out var fade);
            Assert.Greater(toLight.y, 0, "the moon lights the city from above at night");
            Assert.AreEqual(1, fade, 1e-4f);
            var low = DaySky.LightDirection(DaySky.SunDirection(ToaPayoh, 330, 7.1f), 10, out var lowFade);
            Assert.GreaterOrEqual(Mathf.Asin(low.y) * Mathf.Rad2Deg, 10 - 1e-3f, "shadows no longer than a 10° light casts");
            Assert.Less(lowFade, 1, "faded while the sun is near the horizon");
            DaySky.LightDirection(new Vector3(1, 0, 0), 10, out var swap);
            Assert.AreEqual(0, swap, 1e-4f, "dark at the moment sun and moon swap");
        }

        private static WorldLook.SkyKey Key(float hour, string top, float light) => new WorldLook.SkyKey { Hour = hour, SkyTop = top, LightIntensity = light };

        [Test]
        public void KeysBlendBetweenNeighboursAndAroundMidnight()
        {
            var keys = new List<WorldLook.SkyKey> { Key(6, "#000000", 0), Key(12, "#FFFFFF", 1), Key(18, "#000000", 0.2f) };
            Assert.AreEqual(0.5f, DaySky.Evaluate(keys, 9).LightIntensity, 1e-4f);
            Assert.AreEqual(1, DaySky.Evaluate(keys, 12).SkyTop.r, 1e-4f);
            // 18:00 to 6:00 is twelve hours over midnight: midnight is halfway
            Assert.AreEqual(0.1f, DaySky.Evaluate(keys, 0).LightIntensity, 1e-4f);
            Assert.AreEqual(0.1f, DaySky.Evaluate(keys, 24).LightIntensity, 1e-4f);
            Assert.AreEqual(0.15f, DaySky.Evaluate(keys, 21).LightIntensity, 1e-4f);
            Assert.AreEqual(0.7f, DaySky.Evaluate(new[] { Key(3, "#000000", 0.7f) }, 15).LightIntensity, 1e-4f);
        }

        [Test]
        public void TheSessionClockRunsTheHourAtTheFilesPace()
        {
            var spec = new WorldLook.DayCycleSpec { StartHour = 22, TimeScale = 24 };
            Assert.AreEqual(22, DayCycle.HourAt(spec, 0), 1e-4f);
            // An hour of session at 24 times is a whole day; a quarter of it is six hours, past midnight
            Assert.AreEqual(4, DayCycle.HourAt(spec, 900), 1e-3f);
            Assert.AreEqual(22, DayCycle.HourAt(spec, 3600), 1e-3f);
        }

        [TestCase(0f, "12:00 am")]
        [TestCase(7.25f, "7:15 am")]
        [TestCase(12.5f, "12:30 pm")]
        [TestCase(19.09f, "7:05 pm")]
        [TestCase(23.999f, "11:59 pm")]
        public void TheClockReadsLikeAClock(float hour, string text) => Assert.AreEqual(text, DayCycle.Clock(hour));

        [Test]
        public void AnOvercastSkyDimsTheLightAndGreysTheSky()
        {
            var clear = SkyState.From(new WorldLook.SkyKey { SkyTop = "#2D86E0", LightIntensity = 1, ShadowStrength = 0.8f, Stars = 1 });
            var grey = DaySky.WithWeather(clear, new WeatherState { Overcast = 1, CloudCover = 1 });
            Assert.Less(grey.LightIntensity, 0.5f);
            Assert.Less(grey.ShadowStrength, 0.3f);
            Assert.AreEqual(0, grey.Stars, 1e-4f, "cloud hides the stars");
            Color.RGBToHSV(clear.SkyTop, out _, out var clearSaturation, out _);
            Color.RGBToHSV(grey.SkyTop, out _, out var greySaturation, out _);
            Assert.Less(greySaturation, clearSaturation * 0.5f);
            var calm = DaySky.WithWeather(clear, new WeatherState());
            Assert.AreEqual(clear.LightIntensity, calm.LightIntensity, 1e-5f, "a clear day changes nothing");
        }

        [Test]
        public void AKeyWithABadHourIsTurnedDown()
        {
            Assert.Throws<FormatException>(() => WorldLook.Parse("{\"dayCycle\":{\"keys\":[{\"hour\":25}]}}"));
            Assert.Throws<FormatException>(() => WorldLook.Parse("{\"dayCycle\":{\"keys\":[{\"hour\":3,\"skyTop\":\"blue\"}]}}"));
            var look = WorldLook.Parse("{\"dayCycle\":{\"keys\":[{\"hour\":20},{\"hour\":4}]}}");
            Assert.AreEqual(4, look.DayCycle.Keys[0].Hour, "kept in order of the hour");
        }

        [Test]
        public void TheProjectsLookHasKeysForTheWholeDay()
        {
            var asset = Resources.Load<TextAsset>(WorldLook.ResourcePath);
            Assert.IsNotNull(asset);
            var look = WorldLook.Parse(asset.text);
            Assert.GreaterOrEqual(look.DayCycle.Keys.Count, 4);
            var noon = DaySky.Evaluate(look.DayCycle.Keys, 13);
            var midnight = DaySky.Evaluate(look.DayCycle.Keys, 0);
            Assert.Greater(noon.SkyTop.grayscale, midnight.SkyTop.grayscale + 0.2f, "brighter by day than at night");
            Assert.Greater(midnight.Stars, 0.5f, "stars at midnight");
            Assert.Less(midnight.GroundLight.grayscale, 0.5f, "the unlit ground darkens at night");
        }

        [Test]
        public void TheWorldsLightingDimsTheGroundAtNightAndPutsItBack()
        {
            var rig = new GameObject("Day rig");
            try
            {
                var cycle = WorldLighting.Apply(rig.transform).GetComponent<DayCycle>();
                Assert.IsNotNull(cycle);
                cycle.Apply(1);
                Assert.Greater(Shader.GetGlobalVector("_Formula9GroundDim").x, 0.3f);
                cycle.Apply(13);
                Assert.Less(Shader.GetGlobalVector("_Formula9GroundDim").x, 0.05f);
                cycle.Apply(1);
            }
            finally { UnityEngine.Object.DestroyImmediate(rig); }
            Assert.AreEqual(Vector4.zero, Shader.GetGlobalVector("_Formula9GroundDim"), "the ground at full brightness once the world goes");
        }
    }
}
