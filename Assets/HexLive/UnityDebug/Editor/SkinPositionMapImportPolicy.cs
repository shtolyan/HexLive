using System;
using System.IO;
using UnityEditor;

namespace HexLive.UnityDebug.Editor
{
    // These EXRs contain signed mesh-space coordinates, not display colours.
    // Automatic WebGL import selects RGBA32 and silently clamps them to [0,1].
    public sealed class SkinPositionMapImportPolicy : AssetPostprocessor
    {
        private void OnPreprocessTexture()
        {
            var name = Path.GetFileName(assetPath);
            if (assetPath.Contains("/PaintMaps/") &&
                name.StartsWith("skinpos_", StringComparison.Ordinal) &&
                name.EndsWith(".exr", StringComparison.OrdinalIgnoreCase))
                Configure((TextureImporter)assetImporter);
        }

        public static void Configure(TextureImporter importer)
        {
            importer.sRGBTexture = false;
            importer.mipmapEnabled = false;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            var defaults = importer.GetDefaultPlatformTextureSettings();
            defaults.format = TextureImporterFormat.RGBAHalf;
            defaults.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SetPlatformTextureSettings(defaults);
            foreach (var platform in new[] { "Standalone", "WebGL" })
            {
                var settings = importer.GetPlatformTextureSettings(platform);
                settings.name = platform;
                settings.overridden = true;
                settings.format = TextureImporterFormat.RGBAHalf;
                settings.maxTextureSize = importer.maxTextureSize;
                settings.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SetPlatformTextureSettings(settings);
            }
        }
    }
}
