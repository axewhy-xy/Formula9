/*
 * The training world's day and weather.
 *
 * Author: Rishika
 * Date Created: 26/11/2026
 * Last Modified: 26/11/2026
 *
 * Description:
 *     Part of the Unity client's 3D world. Moves the sun across the sky for the time of day (worked
 *     out for Toa Payoh, so sunrise, noon and sunset fall when they do in Singapore), colours the sky,
 *     light, ambient light, haze and unlit ground from the hour keys in world_look.json, and lays the
 *     weather over the top: cloud cover, a grey overcast sky, wind that drifts the clouds, and mist.
 *     Nothing about any hour is written here; the keys say how each hour looks.
 */

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Formula9.World
{
    // How the sky and its light look at one moment, before or after the weather
    public struct SkyState
    {
        public Color SkyTop, SkyHorizon, SkyBelow, CloudColor;
        public Color LightColor;
        public float LightIntensity, ShadowStrength;
        public Color AmbientSky, AmbientHorizon, AmbientGround;
        public float AmbientIntensity;
        public Color HazeColor, GroundLight;
        public float Stars;

        public static SkyState From(WorldLook.SkyKey key) => new SkyState
        {
            SkyTop = WorldLook.Color(key.SkyTop),
            SkyHorizon = WorldLook.Color(key.SkyHorizon),
            SkyBelow = WorldLook.Color(key.SkyBelow),
            CloudColor = WorldLook.Color(key.CloudColor),
            LightColor = WorldLook.Color(key.LightColor),
            LightIntensity = key.LightIntensity,
            ShadowStrength = key.ShadowStrength,
            AmbientSky = WorldLook.Color(key.AmbientSky),
            AmbientHorizon = WorldLook.Color(key.AmbientHorizon),
            AmbientGround = WorldLook.Color(key.AmbientGround),
            AmbientIntensity = key.AmbientIntensity,
            HazeColor = WorldLook.Color(key.HazeColor),
            GroundLight = WorldLook.Color(key.GroundLight),
            Stars = key.Stars,
        };

        // The fixed look of the sun, ambient, sky and haze sections, for a file with no day keys
        public static SkyState From(WorldLook look) => new SkyState
        {
            SkyTop = WorldLook.Color(look.Sky.Top),
            SkyHorizon = WorldLook.Color(look.Sky.Horizon),
            SkyBelow = WorldLook.Color(look.Sky.Below),
            CloudColor = WorldLook.Color(look.Sky.CloudColor),
            LightColor = WorldLook.Color(look.Sun.Color),
            LightIntensity = look.Sun.Intensity,
            ShadowStrength = look.Sun.ShadowStrength,
            AmbientSky = WorldLook.Color(look.Ambient.Sky),
            AmbientHorizon = WorldLook.Color(look.Ambient.Horizon),
            AmbientGround = WorldLook.Color(look.Ambient.Ground),
            AmbientIntensity = look.Ambient.Intensity,
            HazeColor = WorldLook.Color(look.Haze.Color),
            GroundLight = Color.white,
            Stars = 0,
        };

        public static SkyState Lerp(SkyState a, SkyState b, float t) => new SkyState
        {
            SkyTop = Color.Lerp(a.SkyTop, b.SkyTop, t),
            SkyHorizon = Color.Lerp(a.SkyHorizon, b.SkyHorizon, t),
            SkyBelow = Color.Lerp(a.SkyBelow, b.SkyBelow, t),
            CloudColor = Color.Lerp(a.CloudColor, b.CloudColor, t),
            LightColor = Color.Lerp(a.LightColor, b.LightColor, t),
            LightIntensity = Mathf.Lerp(a.LightIntensity, b.LightIntensity, t),
            ShadowStrength = Mathf.Lerp(a.ShadowStrength, b.ShadowStrength, t),
            AmbientSky = Color.Lerp(a.AmbientSky, b.AmbientSky, t),
            AmbientHorizon = Color.Lerp(a.AmbientHorizon, b.AmbientHorizon, t),
            AmbientGround = Color.Lerp(a.AmbientGround, b.AmbientGround, t),
            AmbientIntensity = Mathf.Lerp(a.AmbientIntensity, b.AmbientIntensity, t),
            HazeColor = Color.Lerp(a.HazeColor, b.HazeColor, t),
            GroundLight = Color.Lerp(a.GroundLight, b.GroundLight, t),
            Stars = Mathf.Lerp(a.Stars, b.Stars, t),
        };
    }

    // The weather now. A weather system sets DayCycle.Weather; the world eases into it.
    public sealed class WeatherState
    {
        public float CloudCover, Overcast, WindTowardsDeg, WindSpeedMps, Fog, Rain;

        public static WeatherState From(WorldLook.WeatherSpec spec) => new WeatherState
        {
            CloudCover = spec.CloudCover, Overcast = spec.Overcast, WindTowardsDeg = spec.WindTowardsDeg,
            WindSpeedMps = spec.WindSpeedMps, Fog = spec.Fog, Rain = spec.Rain,
        };

        public WeatherState Copy() => (WeatherState)MemberwiseClone();

        // The wind as metres a second east (x) and north (y)
        public Vector2 Wind => new Vector2(Mathf.Sin(WindTowardsDeg * Mathf.Deg2Rad), Mathf.Cos(WindTowardsDeg * Mathf.Deg2Rad)) * WindSpeedMps;

        // How grey the sky is, rain counting towards it
        public float Gloom => Mathf.Clamp01(Overcast + Rain * 0.6f);
    }

    // -----------------------------------------------------------------------------------
    // Class: DaySky
    // -----------------------------------------------------------------------------------
    // Description:
    //     The arithmetic of the day, kept apart from Unity objects so it can be tested: blending the
    //     hour keys, where the sun is, and what the weather does to the sky.
    public static class DaySky
    {
        // -----------------------------------------------------------------------------------
        // Function: Evaluate
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function blends the two keys either side of the hour, wrapping around midnight.
        //
        // Args:
        //     keys (IReadOnlyList<SkyKey>): The day's keys, sorted by hour; at least one.
        //     hour (float): The time of day, 0 to 24.
        //
        // Returns:
        //     SkyState: How the sky looks at that hour.
        public static SkyState Evaluate(IReadOnlyList<WorldLook.SkyKey> keys, float hour)
        {
            if (keys == null || keys.Count == 0) throw new ArgumentException("the day needs at least one key", nameof(keys));
            if (keys.Count == 1) return SkyState.From(keys[0]);
            hour = Mathf.Repeat(hour, 24);
            // The last key at or before the hour; before the first key, the day's last key
            var before = keys.Count - 1;
            for (var i = 0; i < keys.Count; i++) if (keys[i].Hour <= hour) before = i;
            var after = (before + 1) % keys.Count;
            var span = Mathf.Repeat(keys[after].Hour - keys[before].Hour, 24);
            if (span <= 0) return SkyState.From(keys[before]);
            var t = Mathf.Clamp01(Mathf.Repeat(hour - keys[before].Hour, 24) / span);
            return SkyState.Lerp(SkyState.From(keys[before]), SkyState.From(keys[after]), t);
        }

        // -----------------------------------------------------------------------------------
        // Function: SunDirection
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function works out which way the sun is (NOAA's solar position approximation,
        //     good to well under a degree). Unity's axes: x east, y up, z north.
        //
        // Args:
        //     spec (DayCycleSpec): The place and time zone.
        //     dayOfYear (int): 1 to 366.
        //     hour (float): Local clock time, 0 to 24.
        //
        // Returns:
        //     Vector3: A unit vector towards the sun; below the horizon at night.
        public static Vector3 SunDirection(WorldLook.DayCycleSpec spec, int dayOfYear, float hour)
        {
            var year = 2 * Math.PI / 365 * (dayOfYear - 1 + (hour - 12) / 24.0);
            var equationMin = 229.18 * (0.000075 + 0.001868 * Math.Cos(year) - 0.032077 * Math.Sin(year) - 0.014615 * Math.Cos(2 * year) - 0.040849 * Math.Sin(2 * year));
            var declination = 0.006918 - 0.399912 * Math.Cos(year) + 0.070257 * Math.Sin(year) - 0.006758 * Math.Cos(2 * year)
                              + 0.000907 * Math.Sin(2 * year) - 0.002697 * Math.Cos(3 * year) + 0.00148 * Math.Sin(3 * year);
            var solarMin = hour * 60 + equationMin + 4 * spec.LongitudeDeg - 60 * spec.UtcOffsetHours;
            var hourAngle = (solarMin / 4 - 180) * Math.PI / 180;
            var latitude = spec.LatitudeDeg * Math.PI / 180;
            var east = -Math.Cos(declination) * Math.Sin(hourAngle);
            var north = Math.Cos(latitude) * Math.Sin(declination) - Math.Sin(latitude) * Math.Cos(declination) * Math.Cos(hourAngle);
            var up = Math.Sin(latitude) * Math.Sin(declination) + Math.Cos(latitude) * Math.Cos(declination) * Math.Cos(hourAngle);
            return new Vector3((float)east, (float)up, (float)north).normalized;
        }

        // -----------------------------------------------------------------------------------
        // Function: LightDirection
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function picks where the world's one light comes from: the sun by day, the moon
        //     (taken as opposite the sun) by night, never lower than minPitchDeg so shadows stay short
        //     enough to read. fade drops to 0 as either crosses the horizon, hiding the swap.
        //
        // Args:
        //     toSun (Vector3): Towards the sun.
        //     minPitchDeg (float): The lowest the light comes from.
        //     fade (out float): 0 to 1, how much of the light shows.
        //
        // Returns:
        //     Vector3: A unit vector towards the light.
        public static Vector3 LightDirection(Vector3 toSun, float minPitchDeg, out float fade)
        {
            var toLight = toSun.y >= 0 ? toSun : -toSun;
            fade = Mathf.SmoothStep(0, 1, Mathf.Abs(toSun.y) / Mathf.Sin(5 * Mathf.Deg2Rad));
            var flat = new Vector3(toLight.x, 0, toLight.z);
            if (flat.sqrMagnitude < 1e-8f) return Vector3.up;
            var pitch = Mathf.Max(Mathf.Asin(Mathf.Clamp(toLight.y, -1, 1)), minPitchDeg * Mathf.Deg2Rad);
            return flat.normalized * Mathf.Cos(pitch) + Vector3.up * Mathf.Sin(pitch);
        }

        // -----------------------------------------------------------------------------------
        // Function: WithWeather
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function lays the weather over the time of day's look: an overcast or rainy sky
        //     turns grey, hides the sun and softens shadows; cloud hides the stars.
        //
        // Args:
        //     sky (SkyState): The time of day's look.
        //     weather (WeatherState): The weather.
        //
        // Returns:
        //     SkyState: The look under that weather.
        public static SkyState WithWeather(SkyState sky, WeatherState weather)
        {
            var gloom = weather.Gloom;
            Color Grey(Color c, float shade) { var g = c.grayscale * shade; return new Color(g, g, g, 1); }
            sky.SkyTop = Color.Lerp(sky.SkyTop, Grey(sky.SkyTop, 1.05f), gloom * 0.8f);
            sky.SkyHorizon = Color.Lerp(sky.SkyHorizon, Grey(sky.SkyHorizon, 0.95f), gloom * 0.8f);
            sky.CloudColor = Color.Lerp(sky.CloudColor, Grey(sky.CloudColor, 0.6f), gloom);
            sky.HazeColor = Color.Lerp(sky.HazeColor, Grey(sky.HazeColor, 0.9f), gloom * 0.8f);
            sky.LightIntensity *= 1 - 0.75f * gloom;
            sky.ShadowStrength *= 1 - 0.8f * gloom;
            sky.AmbientIntensity *= 1 - 0.2f * gloom;
            sky.Stars *= 1 - Mathf.Clamp01(Mathf.Max(weather.CloudCover, gloom));
            return sky;
        }
    }

    // -----------------------------------------------------------------------------------
    // Class: DayCycle
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class sits on the world's light and, each frame, sets the sun, sky, ambient light,
    //     haze and unlit ground for the session's time of day and the weather.
    //
    // Attributes:
    //     PreviewHour (float): Set in the Inspector (0 to 24) to see an hour; -1 follows the session.
    //     Weather (WeatherState): The weather to ease towards.
    public sealed class DayCycle : MonoBehaviour
    {
        // The backend's ticks a session second
        public const double SessionTicksPerSecond = 20;
        // About how long a change of weather takes to show fully
        public const float WeatherEaseSeconds = 8;
        // Roughly the clouds' height over the sky shader's layer scale: metres one unit of cloud offset is
        private const float CloudLayerUnitM = 900;
        // The light turns in steps this big rather than a little every frame: each turn redraws the
        // shadow maps from a new angle, and turning every frame makes shadow edges and walls shimmer
        private const float LightStepDeg = 0.25f;

        [Tooltip("0 to 24 to look at that hour; -1 to follow the session's clock")]
        [Range(-1, 24)] public float PreviewHour = -1;

        public WeatherState Weather { get; set; }
        // The time of day shown, 0 to 24
        public float Hour { get; private set; }

        private Light sun;
        private Material sky;
        private WorldLook look;
        private SkyState fixedLook;
        private int dayOfYear;
        private WeatherState shown;
        private Vector2 cloudOffset, cloudOffsetFine;
        private bool lightPlaced;
        private double sessionSeconds = double.NaN;
        private float sessionAt;

        // -----------------------------------------------------------------------------------
        // Function: Init
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function takes the light and sky to drive and shows the start hour straight away,
        //     so the world looks right before its first frame (and in Edit Mode renders).
        public void Init(Light light, Material skyMaterial, WorldLook worldLook)
        {
            sun = light;
            sky = skyMaterial;
            look = worldLook;
            fixedLook = SkyState.From(look);
            var spec = look.DayCycle;
            dayOfYear = spec.DayOfYear >= 1 && spec.DayOfYear <= 366 ? spec.DayOfYear : DateTime.Now.DayOfYear;
            Weather = WeatherState.From(look.Weather);
            shown = Weather.Copy();
            lightPlaced = false;
            Apply(Mathf.Repeat(spec.StartHour, 24));
        }

        // The session's clock, from each snapshot's tick
        public void SetSessionTick(long tick)
        {
            sessionSeconds = tick / SessionTicksPerSecond;
            sessionAt = Time.unscaledTime;
        }

        // No session joined: the start hour holds
        public void ClearSession() => sessionSeconds = double.NaN;

        // The time of day sessionSeconds into a session
        public static float HourAt(WorldLook.DayCycleSpec spec, double sessionSeconds) =>
            (float)(((spec.StartHour + sessionSeconds * spec.TimeScale / 3600) % 24 + 24) % 24);

        // The hour as a clock reads it, as in "7:05 pm"
        public static string Clock(float hour)
        {
            var minutes = (int)Math.Floor(Mathf.Repeat(hour, 24) * 60) % (24 * 60);
            var h = minutes / 60;
            return $"{(h + 11) % 12 + 1}:{minutes % 60:00} {(h < 12 ? "am" : "pm")}";
        }

        // A typical Singapore day, in °C: coolest just before dawn, warmest in the early afternoon
        private const float CoolestC = 25, WarmestC = 32, CoolestHour = 6, WarmestHour = 14;
        // How much of the day's warming a fully grey or rainy sky holds back
        private const float GloomCooling = 0.6f;

        // -----------------------------------------------------------------------------------
        // Function: TemperatureAt
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function gives the air temperature the training world shows at an hour. It warms
        //     from dawn to the early afternoon and cools through the evening and night, as a typical
        //     Singapore day does, and cloud and rain hold back some of the day's warming. It is the
        //     world's own weather, made up like its sky, not a reading or a forecast.
        //
        // Args:
        //     hour (float): The time of day, 0 to 24.
        //     weather (WeatherState): The weather; null for a clear sky.
        //
        // Returns:
        //     float: The temperature in °C.
        public static float TemperatureAt(float hour, WeatherState weather)
        {
            hour = Mathf.Repeat(hour, 24);
            // How warm the hour is, 0 (coolest) to 1 (warmest), easing in and out at both ends
            float warmth;
            if (hour >= CoolestHour && hour < WarmestHour)
                warmth = (1 - Mathf.Cos(Mathf.PI * (hour - CoolestHour) / (WarmestHour - CoolestHour))) / 2;
            else
                warmth = (1 + Mathf.Cos(Mathf.PI * Mathf.Repeat(hour - WarmestHour, 24) / (24 - WarmestHour + CoolestHour))) / 2;
            if (weather != null) warmth *= 1 - GloomCooling * weather.Gloom;
            return CoolestC + (WarmestC - CoolestC) * warmth;
        }

        // The temperature now, under the weather shown
        public float TemperatureC => TemperatureAt(Hour, shown);

        private void Update()
        {
            if (look == null) return;
            var dt = Time.unscaledDeltaTime;
            // Ease towards the weather asked for
            var ease = 1 - Mathf.Exp(-dt / WeatherEaseSeconds * 3);
            var target = Weather ?? shown;
            var wind = Vector2.Lerp(shown.Wind, target.Wind, ease);
            shown.CloudCover = Mathf.Lerp(shown.CloudCover, target.CloudCover, ease);
            shown.Overcast = Mathf.Lerp(shown.Overcast, target.Overcast, ease);
            shown.Fog = Mathf.Lerp(shown.Fog, target.Fog, ease);
            shown.Rain = Mathf.Lerp(shown.Rain, target.Rain, ease);
            shown.WindSpeedMps = wind.magnitude;
            if (wind.sqrMagnitude > 1e-6f) shown.WindTowardsDeg = Mathf.Atan2(wind.x, wind.y) * Mathf.Rad2Deg;
            // Clouds drift with the wind; a finer layer drifts a little faster and off-line, so they change shape as they go
            cloudOffset -= wind * dt / CloudLayerUnitM;
            cloudOffsetFine -= new Vector2(wind.x * 1.3f - wind.y * 0.2f, wind.y * 1.3f + wind.x * 0.2f) * dt / CloudLayerUnitM;
            // Between snapshots the clock runs on, but not far: a paused session holds its hour
            float hour;
            if (PreviewHour >= 0) hour = PreviewHour;
            else if (double.IsNaN(sessionSeconds)) hour = look.DayCycle.StartHour;
            else hour = HourAt(look.DayCycle, sessionSeconds + Math.Min(Time.unscaledTime - sessionAt, 1.0));
            Apply(hour);
        }

        // -----------------------------------------------------------------------------------
        // Function: Apply
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function shows an hour under the weather now.
        //
        // Args:
        //     hour (float): The time of day, 0 to 24.
        public void Apply(float hour)
        {
            Hour = Mathf.Repeat(hour, 24);
            var spec = look.DayCycle;
            var keyed = spec.Keys.Count > 0;
            var state = DaySky.WithWeather(keyed ? DaySky.Evaluate(spec.Keys, Hour) : fixedLook, shown);
            Vector3 toSun, toLight;
            float fade;
            if (keyed)
            {
                toSun = DaySky.SunDirection(spec, dayOfYear, Hour);
                toLight = DaySky.LightDirection(toSun, spec.MinLightPitchDeg, out fade);
            }
            else
            {
                // The fixed sun of a file with no day keys
                toLight = toSun = -(Quaternion.Euler(look.Sun.PitchDeg, look.Sun.YawDeg, 0) * Vector3.forward);
                fade = 1;
            }
            Shader.SetGlobalVector("_Formula9AsphaltLightDirection", new Vector4(toLight.x, toLight.y, toLight.z, 0f));

            if (sun != null)
            {
                var rotation = Quaternion.LookRotation(-toLight, Mathf.Abs(toLight.y) > 0.999f ? Vector3.forward : Vector3.up);
                if (!lightPlaced || Quaternion.Angle(sun.transform.rotation, rotation) >= LightStepDeg)
                {
                    sun.transform.rotation = rotation;
                    lightPlaced = true;
                }
                sun.color = state.LightColor;
                sun.intensity = state.LightIntensity * fade;
                sun.shadowStrength = Mathf.Clamp01(state.ShadowStrength);
                sun.shadows = sun.intensity > 0.005f ? LightShadows.Soft : LightShadows.None;
            }

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = state.AmbientSky * state.AmbientIntensity;
            RenderSettings.ambientEquatorColor = state.AmbientHorizon * state.AmbientIntensity;
            RenderSettings.ambientGroundColor = state.AmbientGround * state.AmbientIntensity;
            var mist = Mathf.Clamp01(shown.Fog) * 0.004f;
            RenderSettings.fog = look.Haze.Enabled || mist > 0.00001f;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = (look.Haze.Enabled ? look.Haze.Density : 0) + mist;
            RenderSettings.fogColor = state.HazeColor;

            // The unlit map tiles and roads: dimmed by night, their shadows as faint as the light casting them
            var ground = state.GroundLight;
            Shader.SetGlobalVector("_Formula9GroundDim", new Vector4(1 - ground.r, 1 - ground.g, 1 - ground.b, 0));
            var shadowLight = Mathf.Clamp01(state.LightIntensity * fade / Mathf.Max(0.01f, look.Sun.Intensity));
            Shader.SetGlobalFloat("_Formula9ShadowDarkness", look.GroundShadowDarkness * shadowLight * Mathf.Clamp01(state.ShadowStrength / Mathf.Max(0.01f, look.Sun.ShadowStrength)));

            if (sky != null)
            {
                sky.SetColor("_Top", state.SkyTop);
                sky.SetColor("_Horizon", state.SkyHorizon);
                sky.SetColor("_Below", state.SkyBelow);
                sky.SetColor("_CloudColor", state.CloudColor);
                sky.SetFloat("_CloudCover", Mathf.Clamp01(Mathf.Max(shown.CloudCover, shown.Gloom)));
                sky.SetFloat("_CloudShade", 0.3f + 0.7f * shown.Gloom);
                sky.SetVector("_CloudOffset", new Vector4(cloudOffset.x, cloudOffset.y, cloudOffsetFine.x, cloudOffsetFine.y));
                // The sun's glow and disc fade as it sets and behind an overcast sky; the moon shows by night
                var sunShows = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(-0.12f, 0.03f, toSun.y)) * (1 - 0.9f * shown.Gloom);
                var sunGlow = state.LightColor;
                sunGlow.a = keyed ? sunShows : 0;
                sky.SetColor("_SunColor", sunGlow);
                sky.SetVector("_SunDir", toSun);
                var moonShows = keyed ? Mathf.Clamp01(-toSun.y * 6) * (1 - shown.Gloom) * (1 - 0.7f * shown.CloudCover) : 0;
                var toMoon = -toSun;
                sky.SetVector("_MoonDir", new Vector4(toMoon.x, toMoon.y, toMoon.z, moonShows));
                sky.SetFloat("_Stars", Mathf.Clamp01(state.Stars));
            }
        }
    }
}
