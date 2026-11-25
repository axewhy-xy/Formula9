using System;
using System.Globalization;
using Newtonsoft.Json;
using UnityEngine;

namespace Formula9.World
{
    // The sun, sky and shadows of the 3D world, read from Resources/Formula9/world_look.json so the look
    // can be tuned without code. Colours are #RRGGBB, angles degrees.
    public sealed class WorldLook
    {
        public const string ResourcePath = "Formula9/world_look";

        [JsonProperty("sun")] public SunSpec Sun = new SunSpec();
        [JsonProperty("ambient")] public AmbientSpec Ambient = new AmbientSpec();
        [JsonProperty("shadows")] public ShadowSpec Shadows = new ShadowSpec();
        [JsonProperty("sky")] public SkySpec Sky = new SkySpec();
        [JsonProperty("haze")] public HazeSpec Haze = new HazeSpec();
        // How much light buildings give off themselves, so walls in deep shadow keep some colour
        [JsonProperty("buildingSelfLight")] public float BuildingSelfLight = 0.06f;
        // How much darker the unlit ground is in shadow, 0 to 1
        [JsonProperty("groundShadowDarkness")] public float GroundShadowDarkness = 0.45f;

        public sealed class SunSpec
        {
            [JsonProperty("color")] public string Color = "#FFF2DE";
            [JsonProperty("intensity")] public float Intensity = 1.1f;
            // Height above the horizon and compass direction it shines towards
            [JsonProperty("pitchDeg")] public float PitchDeg = 45;
            [JsonProperty("yawDeg")] public float YawDeg = -30;
            // 0 for no shadows, 1 for black ones
            [JsonProperty("shadowStrength")] public float ShadowStrength = 0.75f;
        }

        // Light from the sky above, the horizon and the ground below
        public sealed class AmbientSpec
        {
            [JsonProperty("sky")] public string Sky = "#A8C3E0";
            [JsonProperty("horizon")] public string Horizon = "#C8CCCB";
            [JsonProperty("ground")] public string Ground = "#8A8780";
            [JsonProperty("intensity")] public float Intensity = 1;
        }

        public sealed class ShadowSpec
        {
            // How far from the camera shadows are drawn, in metres
            [JsonProperty("distanceM")] public float DistanceM = 900;
            [JsonProperty("cascades")] public int Cascades = 4;
            // Low, Medium, High or VeryHigh: sharper, steadier shadow edges cost graphics memory
            [JsonProperty("resolution")] public string Resolution = "VeryHigh";
        }

        // Distant things fading towards the sky's colour, as air does
        public sealed class HazeSpec
        {
            [JsonProperty("enabled")] public bool Enabled;
            [JsonProperty("color")] public string Color = "#D3E0EA";
            // Exponential-squared fog: at d metres, exp(-(density * d)²) of the colour is left
            [JsonProperty("density")] public float Density = 0.00022f;
        }

        public sealed class SkySpec
        {
            [JsonProperty("top")] public string Top = "#4D87C7";
            [JsonProperty("horizon")] public string Horizon = "#D3E3EF";
            [JsonProperty("below")] public string Below = "#B9C2C6";
            // 0 for a clear sky, 1 for overcast
            [JsonProperty("cloudCover")] public float CloudCover = 0.45f;
            [JsonProperty("cloudColor")] public string CloudColor = "#FFFFFF";
        }

        public static WorldLook Parse(string json)
        {
            var settings = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace };
            var look = JsonConvert.DeserializeObject<WorldLook>(json, settings) ?? new WorldLook();
            foreach (var hex in new[] { look.Sun.Color, look.Ambient.Sky, look.Ambient.Horizon, look.Ambient.Ground, look.Sky.Top, look.Sky.Horizon, look.Sky.Below, look.Sky.CloudColor, look.Haze.Color })
                Color(hex);
            return look;
        }

        private static WorldLook loaded;

        // The project's look, read once; the defaults above if the file is missing or broken
        public static WorldLook Default
        {
            get
            {
                if (loaded != null) return loaded;
                var asset = Resources.Load<TextAsset>(ResourcePath);
                if (asset == null) return loaded = new WorldLook();
                try { return loaded = Parse(asset.text); }
                catch (Exception e)
                {
                    // A broken file should not stop the world showing
                    Debug.LogError($"{ResourcePath}.json could not be read, so the world uses its default look: {e.Message}");
                    return loaded = new WorldLook();
                }
            }
        }

        // "#RRGGBB" as a Unity colour in sRGB, as RenderSettings, lights and material colours take it
        public static Color Color(string hex)
        {
            var text = (hex ?? "").TrimStart('#');
            if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                throw new FormatException($"'{hex}' is not a colour like #A1B2C3");
            return new Color(((value >> 16) & 255) / 255f, ((value >> 8) & 255) / 255f, (value & 255) / 255f);
        }
    }
}
