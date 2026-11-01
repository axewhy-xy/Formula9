#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

// Ensure the supplied *_normal.png files are imported as tangent-space normal
// maps rather than sRGB colour images; ordinary shelter albedo stays as colour.
public sealed class BusStopNormalImporter : AssetPostprocessor
{
    void OnPreprocessTexture()
    {
        const string root = "Assets/Formula9/Resources/Formula9/Textures/";
        if (!assetPath.StartsWith(root, System.StringComparison.Ordinal)) return;
        var importer = (TextureImporter)assetImporter;
        importer.wrapMode = TextureWrapMode.Repeat;
        importer.mipmapEnabled = true;
        if (assetPath.EndsWith("_normal.png", System.StringComparison.OrdinalIgnoreCase))
        {
            importer.textureType = TextureImporterType.NormalMap;
            importer.sRGBTexture = false;
        }
    }
}
#endif
