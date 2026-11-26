using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Formula9.World;
using UnityEditor;
using UnityEngine;

namespace Formula9.EditorTools
{
    // Formula9 > Generate Building Textures: paints every style in Resources/Formula9/building_styles.json
    // and saves them as the four texture arrays CityView loads (ground-floor walls and facades, each with a
    // normal map), one slice per style in the file's order, and building_textures_signature.txt recording what they
    // were painted from. Run it again after editing the styles file.
    public static class BuildingTextureGenerator
    {
        public const string StylesPath = "Assets/Formula9/Resources/Formula9/building_styles.json";
        public const string Folder = "Assets/Formula9/Resources/Formula9/Textures";
        // Slices side by side in each saved image; Unity cuts them into the array's slices
        private const int Columns = 4;

        [MenuItem("Formula9/Generate Building Textures")]
        public static void GenerateFromMenu()
        {
            try
            {
                Generate((step, share) => EditorUtility.DisplayCancelableProgressBar("Generating building textures", step, share));
                Debug.Log("Building textures generated from " + StylesPath);
            }
            catch (OperationCanceledException) { Debug.Log("Building texture generation cancelled"); }
            finally { EditorUtility.ClearProgressBar(); }
        }

        // For -batchmode -executeMethod Formula9.EditorTools.BuildingTextureGenerator.GenerateFromCommandLine
        public static void GenerateFromCommandLine() => Generate((step, _) => { Console.WriteLine(step); return false; });

        // progress returns true to cancel
        public static void Generate(Func<string, float, bool> progress)
        {
            var styles = BuildingStyles.Parse(File.ReadAllText(StylesPath));
            var size = styles.TextureSize;
            var walls = new List<FacadePainter.Painted>();
            var facades = new List<FacadePainter.Painted>();
            for (var i = 0; i < styles.Styles.Count; i++)
            {
                var style = styles.Styles[i];
                if (progress($"Painting {style.Name}", (float)i / styles.Styles.Count)) throw new OperationCanceledException();
                walls.Add(FacadePainter.PaintWall(style, size, styles.NormalStrength, styles.RecessShade));
                facades.Add(FacadePainter.PaintFacade(style, size, styles.NormalStrength, styles.RecessShade));
            }
            progress("Saving", 1);
            var columns = Math.Min(Columns, styles.Styles.Count);
            Save("building_walls", walls.Select(p => p.Color).ToList(), size, columns, false);
            Save("building_wall_normals", walls.Select(p => p.Normal).ToList(), size, columns, true);
            Save("building_facades", facades.Select(p => p.Color).ToList(), size, columns, false);
            Save("building_facade_normals", facades.Select(p => p.Normal).ToList(), size, columns, true);
            // What these textures were painted from, so CityView can tell when they no longer match the file
            var signature = $"{Folder}/{CityView.BuildingTextureSignature}.txt";
            File.WriteAllText(signature, styles.TextureSignature() + "\n");
            AssetDatabase.ImportAsset(signature, ImportAssetOptions.ForceSynchronousImport);
        }

        private static void Save(string name, List<byte[]> slices, int size, int columns, bool normal)
        {
            var (width, height, pixels) = FacadePainter.Atlas(slices, size, columns);
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixelData(pixels, 0);
                texture.Apply(false);
                Directory.CreateDirectory(Folder);
                File.WriteAllBytes(Path.Combine(Folder, name + ".png"), texture.EncodeToPNG());
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
            var path = $"{Folder}/{name}.png";
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            settings.textureType = TextureImporterType.Default;
            settings.textureShape = TextureImporterShape.Texture2DArray;
            settings.flipbookColumns = columns;
            settings.flipbookRows = height / size;
            settings.wrapMode = TextureWrapMode.Repeat;
            settings.filterMode = FilterMode.Trilinear;
            settings.aniso = 8;
            settings.mipmapEnabled = true;
            // Colours are sRGB with the paint mask in alpha; normal maps are plain numbers with no alpha
            settings.sRGBTexture = !normal;
            settings.alphaSource = normal ? TextureImporterAlphaSource.None : TextureImporterAlphaSource.FromInput;
            settings.alphaIsTransparency = false;
            importer.SetTextureSettings(settings);
            importer.maxTextureSize = 16384;
            // Full resolution at every quality level: a low level's texture limit would blur the windows
            importer.ignoreMipmapLimit = true;
            importer.textureCompression = normal ? TextureImporterCompression.CompressedHQ : TextureImporterCompression.Compressed;
            importer.SaveAndReimport();
        }
    }
}
