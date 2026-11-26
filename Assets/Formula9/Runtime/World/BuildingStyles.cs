using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;

namespace Formula9.World
{
    // How every kind of building looks, read from Resources/Formula9/building_styles.json so new areas and
    // new kinds of building need an entry there, not code. A style is one wall texture (the ground floor),
    // one facade texture (the floors above it) and the colours a building of that style is painted in.
    // Rules pick a style from a building's OSM type and height; the first rule that matches wins.
    // The Formula9 > Generate Building Textures menu paints the textures from the same file, and
    // CityBuilding.shader reads one slice of each texture array per style, in the order listed here.
    public sealed class BuildingStyles
    {
        // CityBuilding.shader holds per-style settings in arrays of this length
        public const int MaxStyles = 32;
        public const string ResourcePath = "Formula9/building_styles";

        [JsonProperty("textureSize")] public int TextureSize = 1024;
        // Facade bumps are drawn this strongly; 0 is flat
        [JsonProperty("normalStrength")] public float NormalStrength = 1;
        // How much darker the edges of recesses (windows, corridors, joints) are painted, 0 to 1
        [JsonProperty("recessShade")] public float RecessShade = 0.45f;
        // Footprint corners sticking out less than this are dropped, so traced outlines read as clean blocks
        [JsonProperty("simplifyM")] public float SimplifyM;
        // Every building colour's saturation is multiplied by this: above 1 for stronger colours
        [JsonProperty("colorSaturation")] public float ColorSaturation = 1;
        [JsonProperty("styles")] public List<BuildingStyle> Styles = new List<BuildingStyle>();
        [JsonProperty("rules")] public List<StyleRule> Rules = new List<StyleRule>();
        [JsonProperty("defaultStyle")] public string DefaultStyle;

        public static BuildingStyles Parse(string json)
        {
            // Replace, so a list in the file takes the place of its default instead of being added to it
            var settings = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace };
            var styles = JsonConvert.DeserializeObject<BuildingStyles>(json, settings) ?? throw new FormatException("building styles are empty");
            styles.Check();
            return styles;
        }

#if UNITY_2017_1_OR_NEWER
        private static BuildingStyles loaded;

        // The project's styles, read once. A missing or broken file is logged and the city falls back to
        // one plain style, so a typo in the file leaves plain buildings rather than no city at all.
        public static BuildingStyles Default
        {
            get
            {
                if (loaded != null) return loaded;
                try
                {
                    var asset = UnityEngine.Resources.Load<UnityEngine.TextAsset>(ResourcePath);
                    if (asset == null) throw new InvalidOperationException($"Resources/{ResourcePath}.json is missing");
                    return loaded = Parse(asset.text);
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogError($"Building styles could not be read, so buildings are plain colour: {e.Message}");
                    return loaded = Fallback();
                }
            }
        }
#endif

        // One untextured style for every building, for when the styles file cannot be used
        public static BuildingStyles Fallback()
        {
            var styles = Parse("{\"defaultStyle\":\"plain\",\"styles\":[{\"name\":\"plain\",\"bodyColors\":[\"#D9D4C9\"],\"roofColors\":[\"#B8B4AC\"]}]}");
            styles.IsFallback = true;
            return styles;
        }

        // True for the built-in plain style, which has no textures to show
        [JsonIgnore] public bool IsFallback { get; private set; }

        // What the texture arrays are painted from, as a short hash: the painting settings and, slice by
        // slice, each style's name, wall and facade. Generate Building Textures saves it beside the
        // textures, so textures painted from an older file (styles reordered, a tile resized) are noticed
        // at load instead of showing stretched or wrong facades. Colours outside the textures do not count.
        public string TextureSignature()
        {
            var painted = new
            {
                TextureSize,
                NormalStrength,
                RecessShade,
                Slices = Styles.Select(s => new { s.Name, s.Wall, s.Facade }).ToList(),
            };
            var json = JsonConvert.SerializeObject(painted, Formatting.None);
            unchecked
            {
                // FNV-1a, 64 bits
                var hash = 14695981039346656037UL;
                foreach (var c in json) { hash ^= c; hash *= 1099511628211UL; }
                return hash.ToString("x16", CultureInfo.InvariantCulture);
            }
        }

        public int IndexOf(string name)
        {
            var index = Styles.FindIndex(s => s.Name == name);
            if (index < 0) throw new KeyNotFoundException($"no building style called '{name}'");
            return index;
        }

        // The index of the style a building of this OSM type and height takes; where a rule offers several,
        // the building's ID picks one, the same every time
        public int StyleFor(string type, double heightM, string buildingId = "")
        {
            foreach (var rule in Rules)
                if (rule.Matches(type, heightM))
                    return IndexOf(rule.Choices.Count == 0 ? rule.Style : Pick(rule.Choices, buildingId, 4));
            return IndexOf(DefaultStyle);
        }

        // A colour from the palette, the same for the same building every time, so neighbours differ
        public static string Pick(IReadOnlyList<string> palette, string buildingId, uint salt)
        {
            if (palette == null || palette.Count == 0) return "#FFFFFF";
            var hash = 2166136261u ^ salt;
            foreach (var c in buildingId ?? "") hash = (hash ^ c) * 16777619u;
            return palette[(int)(hash % (uint)palette.Count)];
        }

        // Hex "#RRGGBB" to 0..1 sRGB components
        public static (float R, float G, float B) Rgb(string hex)
        {
            var text = (hex ?? "").TrimStart('#');
            if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                throw new FormatException($"'{hex}' is not a colour like #A1B2C3");
            return (((value >> 16) & 255) / 255f, ((value >> 8) & 255) / 255f, (value & 255) / 255f);
        }

        private void Check()
        {
            if (Styles.Count == 0) throw new FormatException("building styles list no styles");
            if (Styles.Count > MaxStyles) throw new FormatException($"at most {MaxStyles} building styles fit the shader");
            if (TextureSize < 64 || (TextureSize & (TextureSize - 1)) != 0) throw new FormatException("textureSize must be a power of two of at least 64");
            var duplicate = Styles.GroupBy(s => s.Name).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null) throw new FormatException($"two building styles are called '{duplicate.Key}'");
            IndexOf(DefaultStyle);
            foreach (var rule in Rules)
            {
                if (rule.Choices.Count == 0) IndexOf(rule.Style);
                foreach (var choice in rule.Choices) IndexOf(choice);
            }
            foreach (var style in Styles) style.Check();
        }
    }

    public sealed class StyleRule
    {
        // OSM building types this rule covers; empty covers every type
        [JsonProperty("types")] public List<string> Types = new List<string>();
        [JsonProperty("minHeightM")] public double MinHeightM;
        [JsonProperty("maxHeightM")] public double MaxHeightM = double.PositiveInfinity;
        [JsonProperty("style")] public string Style;
        // Several styles, one picked per building, for variety; used instead of style
        [JsonProperty("styles")] public List<string> Choices = new List<string>();

        public bool Matches(string type, double heightM) =>
            (Types.Count == 0 || Types.Contains(type)) && heightM >= MinHeightM && heightM < MaxHeightM;
    }

    public sealed class BuildingStyle
    {
        [JsonProperty("name")] public string Name;
        // A building's main colour comes from here, painted over every part of the textures marked "body"
        [JsonProperty("bodyColors")] public List<string> BodyColors = new List<string> { "#FFFFFF" };
        // and its second colour, for parts marked "accent", such as an HDB block's coloured stripes
        [JsonProperty("accentColors")] public List<string> AccentColors = new List<string> { "#FFFFFF" };
        // and its flat roof's colour; empty roofs take the body colour
        [JsonProperty("roofColors")] public List<string> RoofColors = new List<string>();
        // Below this the wall has no windows, like an HDB void deck or a shop front
        [JsonProperty("groundFloorM")] public float GroundFloorM = 3.5f;
        // The plain wall of the ground floor
        [JsonProperty("wall")] public WallSpec Wall = new WallSpec();
        // The floors above it
        [JsonProperty("facade")] public FacadeSpec Facade = new FacadeSpec();
        // A low wall round the roof's edge; null for none
        [JsonProperty("parapet")] public ParapetSpec Parapet;
        // Boxes standing on the roof, such as lift motor rooms, water tanks and air-conditioning plant
        [JsonProperty("rooftop")] public List<RooftopSpec> Rooftop = new List<RooftopSpec>();
        // An open ground floor behind columns, such as an HDB void deck, a shop's five-foot way or a
        // recessed lobby; its back wall is the wall texture. Null for a solid ground floor
        [JsonProperty("voidDeck")] public VoidDeckSpec VoidDeck;

        // What one copy of each texture covers in metres
        public (float W, float H) WallTileM => (Wall.TileM[0], Wall.TileM[1]);
        public (float W, float H) FacadeTileM => (Facade.Bays * Facade.BayM, Facade.Floors * Facade.FloorM);

        internal void Check()
        {
            if (string.IsNullOrEmpty(Name)) throw new FormatException("a building style has no name");
            if (Wall.TileM == null || Wall.TileM.Length != 2 || Wall.TileM.Any(m => m <= 0)) throw new FormatException($"{Name}: wall.tileM must be [width, height] in metres");
            if (Facade.Bays < 1 || Facade.Floors < 1 || Facade.BayM <= 0 || Facade.FloorM <= 0) throw new FormatException($"{Name}: the facade needs bays, floors, bayM and floorM");
            foreach (var hex in BodyColors.Concat(AccentColors).Concat(RoofColors)) BuildingStyles.Rgb(hex);
            var named = Rooftop.Select(r => r.Color).Concat(Parapet == null ? Enumerable.Empty<string>() : new[] { Parapet.Color })
                .Concat(VoidDeck == null ? Enumerable.Empty<string>() : new[] { VoidDeck.CeilingColor, VoidDeck.ColumnColor });
            foreach (var color in named.Where(c => c != "body" && c != "accent" && c != "roof")) BuildingStyles.Rgb(color);
            var windows = new[] { Facade.Windows, Wall.Windows }.Where(w => w != null);
            foreach (var covering in windows.SelectMany(w => w.Coverings))
                foreach (var hex in covering.Colors.Concat(covering.PatternColors)) BuildingStyles.Rgb(hex);
            if (Rooftop.Any(r => r.SizeM == null || r.SizeM.Length != 3 || r.SizeM.Any(m => m <= 0))) throw new FormatException($"{Name}: every rooftop sizeM must be [width, depth, height] in metres");
        }
    }

    // Colours here are body, accent or roof (the building's own) or #RRGGBB
    public sealed class ParapetSpec
    {
        [JsonProperty("heightM")] public float HeightM = 1.1f;
        [JsonProperty("thicknessM")] public float ThicknessM = 0.3f;
        [JsonProperty("color")] public string Color = "body";
    }

    public sealed class VoidDeckSpec
    {
        // How far the back wall stands behind the building's outline
        [JsonProperty("depthM")] public float DepthM = 1.8f;
        // Columns along the outline: their width and the most space between them
        [JsonProperty("columnM")] public float ColumnM = 0.7f;
        [JsonProperty("spacingM")] public float SpacingM = 6;
        [JsonProperty("columnColor")] public string ColumnColor = "body";
        [JsonProperty("ceilingColor")] public string CeilingColor = "#E6E4DE";
        // How much darker the back wall is, being out of the sky's light, 0 to 1
        [JsonProperty("shade")] public float Shade = 0.25f;
        // Buildings lower than this keep a solid ground floor
        [JsonProperty("minHeightM")] public float MinHeightM = 8;
    }

    public sealed class RooftopSpec
    {
        [JsonProperty("name")] public string Name;
        // Width (along the building's longest wall), depth and height
        [JsonProperty("sizeM")] public float[] SizeM = { 3, 3, 2.5f };
        // How many on every roof, plus how many more for every 1000 m² of roof
        [JsonProperty("count")] public int Count = 1;
        [JsonProperty("perThousandM2")] public float PerThousandM2;
        // Roofs smaller than this get none
        [JsonProperty("minRoofM2")] public float MinRoofM2 = 100;
        [JsonProperty("color")] public string Color = "body";
    }

    // A surface: a colour and a pattern, such as plaster, bricks, tiles or concrete panels
    public sealed class SurfaceSpec
    {
        // plaster, brick, tiles or panels
        [JsonProperty("pattern")] public string Pattern = "plaster";
        [JsonProperty("color")] public string Color = "#E6E6E6";
        // Which of the building's colours paints it: body, accent or none (the texture's own colour)
        [JsonProperty("tint")] public string Tint = "body";
        // Bricks, tiles or panels: one unit's size in metres, the joint's width and colour
        [JsonProperty("cellM")] public float[] CellM = { 0.25f, 0.075f };
        [JsonProperty("jointM")] public float JointM = 0.01f;
        [JsonProperty("jointColor")] public string JointColor = "#B0B0B0";
        // How much each brick, tile or panel differs from the next, 0 to 1
        [JsonProperty("variation")] public float Variation = 0.05f;
        // Large soft stains and fine grain, 0 to 1
        [JsonProperty("grime")] public float Grime = 0.05f;
        [JsonProperty("grain")] public float Grain = 0.02f;
    }

    // The ground floor's wall. With windows it is painted like one floor of a facade, its bays across the
    // tile, for doors, shopfronts and lobby glass
    public sealed class WallSpec
    {
        [JsonProperty("tileM")] public float[] TileM = { 2, 2 };
        [JsonProperty("surface")] public SurfaceSpec Surface = new SurfaceSpec();
        [JsonProperty("bays")] public int Bays = 1;
        [JsonProperty("windows")] public WindowSpec Windows;
        [JsonProperty("bands")] public List<BandSpec> Bands = new List<BandSpec>();
        [JsonProperty("piers")] public List<PierSpec> Piers = new List<PierSpec>();
    }

    // The floors above the ground floor, as a grid of bays (columns) and floors; one texture is Bays by Floors
    public sealed class FacadeSpec
    {
        [JsonProperty("bays")] public int Bays = 3;
        [JsonProperty("floors")] public int Floors = 2;
        [JsonProperty("bayM")] public float BayM = 3;
        [JsonProperty("floorM")] public float FloorM = 3;
        [JsonProperty("surface")] public SurfaceSpec Surface = new SurfaceSpec();
        [JsonProperty("windows")] public WindowSpec Windows = new WindowSpec();
        // Horizontal strips repeated on every floor, such as slab edges and corridor parapets
        [JsonProperty("bands")] public List<BandSpec> Bands = new List<BandSpec>();
        // Vertical strips at the left of some bays, such as columns or accent stripes
        [JsonProperty("piers")] public List<PierSpec> Piers = new List<PierSpec>();
        // A railing in front of the lower part of the window, for balconies; null for none
        [JsonProperty("balcony")] public BalconySpec Balcony;
        // Rain streaks below each window sill, 0 to 1
        [JsonProperty("streaks")] public float Streaks = 0.1f;
    }

    public sealed class WindowSpec
    {
        // Windows side by side in each bay
        [JsonProperty("perBay")] public int PerBay = 1;
        [JsonProperty("widthM")] public float WidthM = 1.5f;
        [JsonProperty("heightM")] public float HeightM = 1.4f;
        // From the floor to the bottom of the window
        [JsonProperty("sillM")] public float SillM = 0.9f;
        [JsonProperty("frameM")] public float FrameM = 0.05f;
        [JsonProperty("frameColor")] public string FrameColor = "#8A9096";
        // How far the glass sits back from the wall
        [JsonProperty("depthM")] public float DepthM = 0.1f;
        // Bars across the glass: vertical ones splitting the width, horizontal ones the height
        [JsonProperty("mullionsX")] public int MullionsX = 1;
        [JsonProperty("mullionsY")] public int MullionsY = 0;
        [JsonProperty("glassColor")] public string GlassColor = "#7C8C9A";
        // How much panes differ in shade, 0 to 1
        [JsonProperty("glassVariation")] public float GlassVariation = 0.15f;
        // How much lighter the top of each pane is than its bottom, like sky reflected in it
        [JsonProperty("reflection")] public float Reflection = 0.15f;
        // The share of windows with something behind the glass, and what: one of the coverings, picked
        // per window by weight. Without coverings, plain curtains in the curtain colours
        [JsonProperty("curtainChance")] public float CurtainChance = 0.2f;
        [JsonProperty("curtainColors")] public List<string> CurtainColors = new List<string> { "#D9D2C3" };
        [JsonProperty("coverings")] public List<CoveringSpec> Coverings = new List<CoveringSpec>();
        // A ledge under each window, sticking out of the wall; 0 for none
        [JsonProperty("sillLedgeM")] public float SillLedgeM = 0.05f;
        [JsonProperty("sillColor")] public string SillColor = "#D8D8D8";
        // The ledge's height and how far past the window it runs each side: a tall one is an air-con ledge
        [JsonProperty("ledgeHeightM")] public float LedgeHeightM = 0.07f;
        [JsonProperty("ledgeOverhangM")] public float LedgeOverhangM = 0.05f;
    }

    // What hangs behind a window's glass
    public sealed class CoveringSpec
    {
        // curtain (soft folds), stripes, checks, print (small dots), roller (a blind), venetian (slats),
        // or dark (an open window into an unlit room)
        [JsonProperty("kind")] public string Kind = "curtain";
        // How often it is picked against the window's other coverings
        [JsonProperty("weight")] public float Weight = 1;
        // Its main colour and, for stripes, checks and prints, the second colour, each picked per window
        [JsonProperty("colors")] public List<string> Colors = new List<string> { "#D9D2C3" };
        [JsonProperty("patternColors")] public List<string> PatternColors = new List<string> { "#FFFFFF" };
        // The size of one stripe, check, dot spacing or slat
        [JsonProperty("patternM")] public float PatternM = 0.08f;
        // How much of the window it covers, picked per window between the two: 1 is fully closed
        [JsonProperty("drawn")] public float[] Drawn = { 1, 1 };
        // Where it is drawn from: both sides (open in the middle), left, right, or top (blinds)
        [JsonProperty("from")] public string From = "both";
    }

    public sealed class BandSpec
    {
        // From the bottom of the floor
        [JsonProperty("fromM")] public float FromM;
        [JsonProperty("heightM")] public float HeightM = 0.3f;
        // How far it sticks out of the wall; less than 0 sets it back
        [JsonProperty("depthM")] public float DepthM = 0.05f;
        [JsonProperty("surface")] public SurfaceSpec Surface = new SurfaceSpec();
        // Only across these bays (0 is the first), for panels of colour; empty for the whole width
        [JsonProperty("bays")] public List<int> Bays = new List<int>();
        // Only on these floors of the texture (0 is the lowest); empty for every floor
        [JsonProperty("floors")] public List<int> Floors = new List<int>();
    }

    public sealed class PierSpec
    {
        // At the left edge of every this many bays, starting with the first
        [JsonProperty("everyBays")] public int EveryBays = 1;
        // From the bay's left edge
        [JsonProperty("offsetM")] public float OffsetM;
        [JsonProperty("widthM")] public float WidthM = 0.4f;
        [JsonProperty("depthM")] public float DepthM = 0.1f;
        [JsonProperty("surface")] public SurfaceSpec Surface = new SurfaceSpec();
    }

    public sealed class BalconySpec
    {
        [JsonProperty("heightM")] public float HeightM = 1.0f;
        // From the bay's centre, how wide; 0 for the whole bay
        [JsonProperty("widthM")] public float WidthM;
        // solid (a low wall), glass or bars
        [JsonProperty("railing")] public string Railing = "solid";
        [JsonProperty("depthM")] public float DepthM = 0.15f;
        [JsonProperty("surface")] public SurfaceSpec Surface = new SurfaceSpec();
        // Glass or bars: their colour; bars: their spacing and width
        [JsonProperty("railColor")] public string RailColor = "#9AA5AE";
        [JsonProperty("barSpacingM")] public float BarSpacingM = 0.12f;
        [JsonProperty("barM")] public float BarM = 0.02f;
    }
}
