using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.World;

namespace Formula9.EditorTools
{
    // Paints a building style's textures from its BuildingStyle: the ground-floor wall and the facade above
    // it, each as colour (with the building-colour mask in alpha) and a tangent-space normal map. Everything
    // is laid out in metres, so a window is the size the style says however large the texture is, and every
    // texture tiles seamlessly. Plain C#, so it can run outside Unity too.
    public static class FacadePainter
    {
        // Alpha in the colour texture: which of the building's colours paints a pixel
        public const byte MaskBody = 255, MaskAccent = 128, MaskNone = 0;
        // How deep brick and tile joints are, in metres
        private const float JointDepthM = 0.006f;

        // A painted texture: colour as RGBA bytes and the normal map as RGBA bytes, rows from the bottom
        public sealed class Painted
        {
            public int Width, Height;
            public byte[] Color, Normal;
        }

        public static Painted PaintWall(BuildingStyle style, int size, float normalStrength, float recessShade = 0.45f)
        {
            var wall = style.Wall;
            var (w, h) = style.WallTileM;
            if (wall.Windows != null)
            {
                // One floor of bays across the tile: doors, shopfronts or lobby glass
                var floor = new FacadeSpec
                {
                    Bays = Math.Max(1, wall.Bays), Floors = 1, BayM = w / Math.Max(1, wall.Bays), FloorM = h,
                    Surface = wall.Surface, Windows = wall.Windows, Bands = wall.Bands, Piers = wall.Piers, Streaks = 0,
                };
                return Paint(floor, w, h, size, normalStrength, Seed(style.Name + "/wall"), recessShade);
            }
            var canvas = new Canvas(size, size, w, h, Seed(style.Name + "/wall"));
            canvas.FillSurface(wall.Surface, 0, 0, w, h, 0, 1);
            return canvas.Finish(normalStrength, recessShade);
        }

        public static Painted PaintFacade(BuildingStyle style, int size, float normalStrength, float recessShade = 0.45f)
        {
            var (w, h) = style.FacadeTileM;
            return Paint(style.Facade, w, h, size, normalStrength, Seed(style.Name + "/facade"), recessShade);
        }

        private static Painted Paint(FacadeSpec facade, float w, float h, int size, float normalStrength, int seed, float recessShade)
        {
            var canvas = new Canvas(size, size, w, h, seed);
            canvas.FillSurface(facade.Surface, 0, 0, w, h, 0, 1);
            for (var floor = 0; floor < facade.Floors; floor++)
                foreach (var band in facade.Bands)
                {
                    if (band.Floors.Count > 0 && !band.Floors.Contains(floor)) continue;
                    var y = floor * facade.FloorM + band.FromM;
                    if (band.Bays.Count == 0) canvas.FillSurface(band.Surface, 0, y, w, y + band.HeightM, band.DepthM, 2 + floor);
                    else
                        foreach (var bay in band.Bays.Where(b => b >= 0 && b < facade.Bays))
                            canvas.FillSurface(band.Surface, bay * facade.BayM, y, (bay + 1) * facade.BayM, y + band.HeightM, band.DepthM, 2 + floor);
                }
            for (var bay = 0; bay < facade.Bays; bay++)
                foreach (var pier in facade.Piers)
                {
                    if (bay % Math.Max(1, pier.EveryBays) != 0) continue;
                    var x = bay * facade.BayM + pier.OffsetM;
                    canvas.FillSurface(pier.Surface, x, 0, x + pier.WidthM, h, pier.DepthM, 100 + bay);
                }
            var windows = new List<(float X0, float Y0, float X1, float Y1)>();
            var spec = facade.Windows;
            for (var floor = 0; floor < facade.Floors; floor++)
                for (var bay = 0; bay < facade.Bays; bay++)
                    for (var k = 0; k < Math.Max(1, spec.PerBay); k++)
                    {
                        var slot = facade.BayM / Math.Max(1, spec.PerBay);
                        var centre = bay * facade.BayM + (k + 0.5f) * slot;
                        var y0 = floor * facade.FloorM + spec.SillM;
                        var rect = (centre - spec.WidthM / 2, y0, centre + spec.WidthM / 2, y0 + spec.HeightM);
                        windows.Add(rect);
                        canvas.Window(spec, rect, (floor * 97 + bay) * 13 + k);
                    }
            if (facade.Balcony != null)
                for (var floor = 0; floor < facade.Floors; floor++)
                    for (var bay = 0; bay < facade.Bays; bay++)
                        canvas.Balcony(facade.Balcony, bay * facade.BayM, facade.BayM, floor * facade.FloorM, 300 + floor * 31 + bay);
            if (facade.Streaks > 0)
                foreach (var (x0, y0, x1, _) in windows)
                    canvas.Streaks(x0, x1, y0 - spec.LedgeHeightM, facade.Streaks);
            return canvas.Finish(normalStrength, recessShade);
        }

        // Puts several equal textures side by side into one, Columns across, the first at the top left:
        // the layout Unity reads a Texture2DArray's slices from. Gaps repeat the first texture.
        public static (int Width, int Height, byte[] Pixels) Atlas(IReadOnlyList<byte[]> slices, int size, int columns)
        {
            var rows = (slices.Count + columns - 1) / columns;
            int width = size * columns, height = size * rows;
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < rows * columns; i++)
            {
                var source = slices[i < slices.Count ? i : 0];
                int column = i % columns, rowFromTop = i / columns;
                // Rows are stored from the bottom, so the top row of slices is the last in memory
                var baseY = (rows - 1 - rowFromTop) * size;
                for (var y = 0; y < size; y++)
                    Buffer.BlockCopy(source, y * size * 4, pixels, ((baseY + y) * width + column * size) * 4, size * 4);
            }
            return (width, height, pixels);
        }

        public static int Seed(string text)
        {
            var hash = 2166136261u;
            foreach (var c in text) hash = (hash ^ c) * 16777619u;
            return (int)(hash & 0x7fffffff);
        }

        // 0 to 1, the same for the same numbers
        private static float Hash(int a, int b, int seed)
        {
            unchecked
            {
                var h = (uint)(a * 374761393 + b * 668265263 + seed * 2147483647);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xffffff) / 16777216f;
            }
        }

        // Smooth noise that repeats every period lattice cells, so it tiles
        private static float Noise(float u, float v, int periodU, int periodV, int seed)
        {
            float x = u * periodU, y = v * periodV;
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            int Wrap(int n, int p) => ((n % p) + p) % p;
            float At(int i, int j) => Hash(Wrap(i, periodU), Wrap(j, periodV), seed);
            var top = At(x0, y0) + (At(x0 + 1, y0) - At(x0, y0)) * fx;
            var bottom = At(x0, y0 + 1) + (At(x0 + 1, y0 + 1) - At(x0, y0 + 1)) * fx;
            return top + (bottom - top) * fy;
        }

        // Layers of noise, -1 to 1; base cells at least this many metres across
        private static float Fbm(float u, float v, float tileW, float tileH, float cellM, int seed)
        {
            float sum = 0, amplitude = 0.5f, total = 0;
            for (var octave = 0; octave < 5; octave++)
            {
                var size = cellM / (1 << octave);
                int pu = Math.Max(1, (int)Math.Round(tileW / size)), pv = Math.Max(1, (int)Math.Round(tileH / size));
                sum += amplitude * (Noise(u, v, pu, pv, seed + octave * 101) * 2 - 1);
                total += amplitude;
                amplitude *= 0.5f;
            }
            return sum / total;
        }

        private sealed class Canvas
        {
            private readonly int width, height, seed;
            private readonly float tileW, tileH;
            private readonly float[] r, g, b, depth;
            private readonly byte[] mask;
            // Glass and frames, where rain streaks do not show
            private readonly bool[] glazed;

            public Canvas(int width, int height, float tileW, float tileH, int seed)
            {
                this.width = width; this.height = height; this.tileW = tileW; this.tileH = tileH; this.seed = seed;
                var n = width * height;
                r = new float[n]; g = new float[n]; b = new float[n]; depth = new float[n];
                mask = new byte[n]; glazed = new bool[n];
            }

            private float MetresX(int x) => (x + 0.5f) * tileW / width;
            private float MetresY(int y) => (y + 0.5f) * tileH / height;

            // Every pixel whose centre is in the rectangle (metres, wrapping round the tile)
            private IEnumerable<(int X, int Y, int I)> Pixels(float x0, float y0, float x1, float y1)
            {
                int px0 = (int)Math.Round(x0 / tileW * width), px1 = (int)Math.Round(x1 / tileW * width);
                int py0 = (int)Math.Round(y0 / tileH * height), py1 = (int)Math.Round(y1 / tileH * height);
                for (var py = py0; py < py1; py++)
                {
                    var y = ((py % height) + height) % height;
                    for (var px = px0; px < px1; px++)
                    {
                        var x = ((px % width) + width) % width;
                        yield return (x, y, y * width + x);
                    }
                }
            }

            private void Set(int i, (float R, float G, float B) color, byte paint, float d)
            {
                r[i] = color.R; g[i] = color.G; b[i] = color.B; mask[i] = paint; depth[i] = d;
            }

            private static byte MaskFor(string tint) =>
                tint == "accent" ? MaskAccent : tint == "none" ? MaskNone : MaskBody;

            public void FillSurface(SurfaceSpec surface, float x0, float y0, float x1, float y1, float d, int salt)
            {
                var baseColor = BuildingStyles.Rgb(surface.Color);
                var joint = BuildingStyles.Rgb(surface.JointColor);
                var paint = MaskFor(surface.Tint);
                var s = seed + salt * 7919;
                // Cells resized a little so a whole number of them fit the tile, which keeps it seamless
                float cellW = Fit(tileW, surface.CellM?.Length > 0 ? surface.CellM[0] : 1);
                var rowsPerTile = Math.Max(1, (int)Math.Round(tileH / (surface.CellM?.Length > 1 ? surface.CellM[1] : 1)));
                if (surface.Pattern == "brick" && rowsPerTile % 2 == 1) rowsPerTile++;
                var cellH = tileH / rowsPerTile;
                foreach (var (x, y, i) in Pixels(x0, y0, x1, y1))
                {
                    float mx = MetresX(x), my = MetresY(y), u = mx / tileW, v = my / tileH;
                    var color = baseColor;
                    var surfaceDepth = d;
                    if (surface.Pattern != "plaster")
                    {
                        var row = (int)Math.Floor(my / cellH);
                        var shift = surface.Pattern == "brick" && row % 2 == 1 ? cellW / 2 : 0;
                        var cx = mx + shift;
                        var column = (int)Math.Floor(cx / cellW);
                        float inX = cx - column * cellW, inY = my - row * cellH;
                        var half = surface.JointM / 2;
                        if (inX < half || inX > cellW - half || inY < half || inY > cellH - half)
                        {
                            color = joint;
                            surfaceDepth -= JointDepthM;
                        }
                        else
                        {
                            // Columns wrap with the tile, so the cell after the last is the first again
                            var columns = Math.Max(1, (int)Math.Round(tileW / cellW));
                            var shade = 1 + surface.Variation * (Hash(((column % columns) + columns) % columns, row, s) * 2 - 1);
                            color = Scale(color, shade);
                        }
                    }
                    var grime = 1 + surface.Grime * Fbm(u, v, tileW, tileH, 1.5f, s + 1);
                    var grain = 1 + surface.Grain * (Hash(x, y, s + 2) * 2 - 1);
                    Set(i, Scale(color, grime * grain), paint, surfaceDepth);
                    glazed[i] = false;
                }
            }

            public void Window(WindowSpec spec, (float X0, float Y0, float X1, float Y1) rect, int salt)
            {
                var frame = BuildingStyles.Rgb(spec.FrameColor);
                var glass = BuildingStyles.Rgb(spec.GlassColor);
                var sill = BuildingStyles.Rgb(spec.SillColor);
                var s = seed + salt * 104729;
                var (x0, y0, x1, y1) = rect;
                if (spec.SillLedgeM > 0)
                    foreach (var (_, _, i) in Pixels(x0 - spec.LedgeOverhangM, y0 - spec.LedgeHeightM, x1 + spec.LedgeOverhangM, y0))
                        Set(i, sill, MaskNone, spec.SillLedgeM);
                foreach (var (_, _, i) in Pixels(x0, y0, x1, y1))
                {
                    Set(i, frame, MaskNone, -0.01f);
                    glazed[i] = true;
                }
                float gx0 = x0 + spec.FrameM, gx1 = x1 - spec.FrameM, gy0 = y0 + spec.FrameM, gy1 = y1 - spec.FrameM;
                int panesX = spec.MullionsX + 1, panesY = spec.MullionsY + 1;
                float paneW = (gx1 - gx0) / panesX, paneH = (gy1 - gy0) / panesY;
                var covering = Hash(salt, 1, seed) < spec.CurtainChance ? Cover(spec, salt, glass) : null;
                float glassW = gx1 - gx0, glassH = gy1 - gy0;
                var windowShade = 1 + spec.GlassVariation * (Hash(salt, 3, seed) * 2 - 1);
                var mullion = spec.FrameM * 0.7f;
                foreach (var (x, y, i) in Pixels(gx0, gy0, gx1, gy1))
                {
                    float mx = MetresX(x), my = MetresY(y);
                    // Positions within the glass, wrapped back into the tile for windows that cross its edge
                    var lx = Wrap(mx - gx0, tileW);
                    var ly = Wrap(my - gy0, tileH);
                    int paneX = Math.Min(panesX - 1, (int)(lx / paneW)), paneY = Math.Min(panesY - 1, (int)(ly / paneH));
                    float inX = lx - paneX * paneW, inY = ly - paneY * paneH;
                    var onMullion = (paneX > 0 && inX < mullion / 2) || (paneX < panesX - 1 && inX > paneW - mullion / 2)
                        || (paneY > 0 && inY < mullion / 2) || (paneY < panesY - 1 && inY > paneH - mullion / 2);
                    if (onMullion) { Set(i, frame, MaskNone, -spec.DepthM + 0.02f); continue; }
                    var t = inY / paneH;
                    (float R, float G, float B) color;
                    if (covering != null && covering.Covers(lx, ly, glassW, glassH))
                        color = covering.ColorAt(lx, ly, glassW, glassH);
                    else
                    {
                        var paneShade = windowShade * (1 + spec.GlassVariation * 0.4f * (Hash(salt * 7 + paneX, paneY, seed) * 2 - 1));
                        color = Scale(glass, paneShade * (1 + spec.Reflection * (t - 0.5f) * 2));
                    }
                    Set(i, color, MaskNone, -spec.DepthM);
                }
            }

            // One window's covering, chosen from the spec by weight, with its colours and how far it is drawn
            private Covering Cover(WindowSpec spec, int salt, (float R, float G, float B) glass)
            {
                if (spec.Coverings.Count == 0)
                {
                    var plain = new CoveringSpec { Colors = spec.CurtainColors.Count > 0 ? spec.CurtainColors : new List<string> { "#D9D2C3" } };
                    return new Covering(plain, Pick(plain.Colors, salt, 2), (1, 1, 1), 1, seed);
                }
                var total = spec.Coverings.Sum(c => Math.Max(0, c.Weight));
                var roll = Hash(salt, 4, seed) * total;
                var chosen = spec.Coverings[spec.Coverings.Count - 1];
                foreach (var c in spec.Coverings)
                {
                    roll -= Math.Max(0, c.Weight);
                    if (roll < 0) { chosen = c; break; }
                }
                var drawn = chosen.Drawn?.Length == 2 ? chosen.Drawn[0] + (chosen.Drawn[1] - chosen.Drawn[0]) * Hash(salt, 5, seed) : 1;
                return new Covering(chosen, Pick(chosen.Colors, salt, 2), Pick(chosen.PatternColors, salt, 6), Clamp01(drawn), seed);
            }

            private (float R, float G, float B) Pick(List<string> colors, int salt, int which) =>
                colors == null || colors.Count == 0 ? (0.85f, 0.82f, 0.76f) : BuildingStyles.Rgb(colors[(int)(Hash(salt, which, seed) * colors.Count) % colors.Count]);

            public void Balcony(BalconySpec spec, float bayX, float bayW, float floorY, int salt)
            {
                var w = spec.WidthM > 0 ? Math.Min(spec.WidthM, bayW) : bayW;
                float x0 = bayX + (bayW - w) / 2, x1 = x0 + w, y1 = floorY + spec.HeightM;
                if (spec.Railing == "solid")
                {
                    FillSurface(spec.Surface, x0, floorY, x1, y1, spec.DepthM, salt);
                    return;
                }
                var rail = BuildingStyles.Rgb(spec.RailColor);
                var railM = Math.Max(spec.BarM, 0.03f);
                foreach (var (x, y, i) in Pixels(x0, floorY, x1, y1))
                {
                    var my = Wrap(MetresY(y) - floorY, tileH);
                    var lx = Wrap(MetresX(x) - x0, tileW);
                    var topRail = my > spec.HeightM - railM;
                    var bottomRail = my < railM;
                    var bar = spec.Railing == "bars" && lx % Math.Max(spec.BarSpacingM, 0.01f) < spec.BarM;
                    if (topRail || bottomRail || bar) { Set(i, rail, MaskNone, spec.DepthM); glazed[i] = false; }
                    else if (spec.Railing == "glass")
                    {
                        // Tinted glass over whatever is behind it
                        r[i] = r[i] * 0.6f + rail.R * 0.4f; g[i] = g[i] * 0.6f + rail.G * 0.4f; b[i] = b[i] * 0.6f + rail.B * 0.4f;
                        depth[i] = spec.DepthM;
                    }
                }
            }

            // Dark streaks running down the wall from under a sill
            public void Streaks(float x0, float x1, float topY, float amount)
            {
                const float lengthM = 1.4f;
                var s = seed + 31337;
                foreach (var (x, y, i) in Pixels(x0, topY - lengthM, x1, topY))
                {
                    if (glazed[i]) continue;
                    float u = MetresX(x) / tileW, v = MetresY(y) / tileH;
                    // Wrapped, for streaks that run off the bottom of the tile and carry on at its top
                    var below = Wrap(topY - MetresY(y), tileH) / lengthM;
                    // Soft streaks a few centimetres wide, each running down a different length
                    var column = Noise(u, 0.5f, Math.Max(1, (int)(tileW * 7)), 1, s);
                    var reach = 0.35f + 0.65f * Noise(u, 0.5f, Math.Max(1, (int)(tileW * 3)), 1, s + 1);
                    var fade = Clamp01(1 - below / reach);
                    var patchy = 0.7f + 0.3f * Noise(u, v, Math.Max(1, (int)(tileW * 7)), Math.Max(1, (int)(tileH * 2)), s + 2);
                    var streak = column * column * fade * fade * patchy * amount;
                    var shade = 1 - streak;
                    r[i] *= shade; g[i] *= shade; b[i] *= shade;
                }
            }

            public Painted Finish(float normalStrength, float recessShade)
            {
                // Recesses darker near their edges, like light not reaching into a window or a joint
                var blurred = Blur(depth, Math.Max(1, (int)(0.06f / (tileW / width))), Math.Max(1, (int)(0.06f / (tileH / height))));
                var color = new byte[width * height * 4];
                for (var i = 0; i < r.Length; i++)
                {
                    var occlusion = Clamp01(1 - 4f * Math.Max(0, blurred[i] - depth[i]));
                    occlusion = 1 - Clamp01(recessShade) * (1 - occlusion);
                    color[i * 4] = Byte(r[i] * occlusion); color[i * 4 + 1] = Byte(g[i] * occlusion); color[i * 4 + 2] = Byte(b[i] * occlusion);
                    color[i * 4 + 3] = mask[i];
                }
                // Slopes from a slightly softened depth, in metres per metre
                var soft = Blur(depth, 1, 1);
                var normal = new byte[width * height * 4];
                float pixelW = tileW / width, pixelH = tileH / height;
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        int left = y * width + (x + width - 1) % width, right = y * width + (x + 1) % width;
                        int down = ((y + height - 1) % height) * width + x, up = ((y + 1) % height) * width + x;
                        var dx = (soft[right] - soft[left]) / (2 * pixelW) * normalStrength;
                        var dy = (soft[up] - soft[down]) / (2 * pixelH) * normalStrength;
                        var length = (float)Math.Sqrt(dx * dx + dy * dy + 1);
                        var i = (y * width + x) * 4;
                        normal[i] = Byte((-dx / length) * 0.5f + 0.5f);
                        normal[i + 1] = Byte((-dy / length) * 0.5f + 0.5f);
                        normal[i + 2] = Byte((1 / length) * 0.5f + 0.5f);
                        normal[i + 3] = 255;
                    }
                return new Painted { Width = width, Height = height, Color = color, Normal = normal };
            }

            // Box blur that wraps round the tile
            private float[] Blur(float[] source, int radiusX, int radiusY)
            {
                var across = new float[source.Length];
                for (var y = 0; y < height; y++)
                {
                    float sum = 0;
                    for (var k = -radiusX; k <= radiusX; k++) sum += source[y * width + ((k % width) + width) % width];
                    for (var x = 0; x < width; x++)
                    {
                        across[y * width + x] = sum / (2 * radiusX + 1);
                        sum += source[y * width + (x + radiusX + 1) % width] - source[y * width + ((x - radiusX) % width + width) % width];
                    }
                }
                var result = new float[source.Length];
                for (var x = 0; x < width; x++)
                {
                    float sum = 0;
                    for (var k = -radiusY; k <= radiusY; k++) sum += across[(((k % height) + height) % height) * width + x];
                    for (var y = 0; y < height; y++)
                    {
                        result[y * width + x] = sum / (2 * radiusY + 1);
                        sum += across[((y + radiusY + 1) % height) * width + x] - across[(((y - radiusY) % height + height) % height) * width + x];
                    }
                }
                return result;
            }
        }

        // A covering as painted on one window, in metres from the glass's bottom left
        private sealed class Covering
        {
            private readonly CoveringSpec spec;
            private readonly (float R, float G, float B) main, pattern;
            private readonly float drawn;
            private readonly int seed;

            public Covering(CoveringSpec spec, (float R, float G, float B) main, (float R, float G, float B) pattern, float drawn, int seed)
            {
                this.spec = spec; this.main = main; this.pattern = pattern; this.drawn = drawn; this.seed = seed;
            }

            public bool Covers(float x, float y, float w, float h)
            {
                switch (spec.From)
                {
                    case "left": return x < w * drawn;
                    case "right": return x > w * (1 - drawn);
                    case "top": return y > h * (1 - drawn);
                    default: return x < w * drawn / 2 || x > w - w * drawn / 2;
                }
            }

            public (float R, float G, float B) ColorAt(float x, float y, float w, float h)
            {
                var size = Math.Max(spec.PatternM, 0.01f);
                // A little shadow where it meets the top of the frame
                var top = 1 - 0.2f * Math.Max(0, (y - (h - 0.1f)) / 0.1f);
                switch (spec.Kind)
                {
                    case "stripes":
                        return Scale((int)Math.Floor(x / size) % 2 == 0 ? main : pattern, Folds(x) * top);
                    case "checks":
                        var check = ((int)Math.Floor(x / size) + (int)Math.Floor(y / size)) % 2 == 0;
                        return Scale(check ? main : Mix(main, pattern, 0.6f), Folds(x) * top);
                    case "print":
                        // Dots on a staggered grid
                        float cx = x / size, cy = y / size + ((int)Math.Floor(x / size) % 2) * 0.5f;
                        var dx = cx - (float)Math.Floor(cx) - 0.5f; var dy = cy - (float)Math.Floor(cy) - 0.5f;
                        return Scale(dx * dx + dy * dy < 0.07f ? pattern : main, Folds(x) * top);
                    case "roller":
                        // A flat blind, a little darker at its bottom bar
                        var edge = h * (1 - drawn);
                        return Scale(main, (y - edge < 0.04f && spec.From == "top" ? 0.75f : 1) * top);
                    case "venetian":
                        var slat = (y / size) - (float)Math.Floor(y / size);
                        return Scale(main, (0.82f + 0.25f * slat) * top);
                    case "dark":
                        // An unlit room: dark, a touch lighter low down where the floor catches light
                        return Scale(main, 0.85f + 0.3f * Math.Max(0, 0.4f - y / h));
                    default:
                        return Scale(main, Folds(x) * top);
                }
            }

            private static float Folds(float x) => 1 + 0.05f * (float)Math.Sin(x / 0.09f * Math.PI);
            private static (float R, float G, float B) Mix((float R, float G, float B) a, (float R, float G, float B) b, float t) =>
                (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
        }

        private static float Fit(float tile, float cell) => tile / Math.Max(1, (float)Math.Round(tile / Math.Max(cell, 1e-3f)));
        private static float Wrap(float value, float period) => ((value % period) + period) % period;
        private static (float R, float G, float B) Scale((float R, float G, float B) c, float k) => (c.R * k, c.G * k, c.B * k);
        private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
        private static byte Byte(float v) => (byte)Math.Round(Clamp01(v) * 255);
    }
}
