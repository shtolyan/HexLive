#if UNITY_EDITOR
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace HexLive.Tests
{
    public sealed class PeopleSkinNormalTests
    {
        [TestCase("Marta")]
        [TestCase("Kshishtof")]
        public void AllComplexionsKeepTheAuthoredNormalAtlas(string actor)
        {
            const string root = "Assets/HexLiveContent/People";
            var path = root + "/Source/Textures/" + actor + "_SkinNormal.png";
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            Assert.That(texture, Is.Not.Null);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            Assert.That(importer.textureType, Is.EqualTo(TextureImporterType.NormalMap));
            Assert.That(importer.sRGBTexture, Is.False);
            Assert.That(importer.mipmapEnabled, Is.True);
            Assert.That(importer.maxTextureSize, Is.EqualTo(1024));
            var paths = Directory.GetFiles(root + "/Materials", "Skin_" + actor + "_*.mat");
            Assert.That(paths.Length, Is.GreaterThan(0));
            foreach (var materialPath in paths)
                Check(AssetDatabase.LoadAssetAtPath<Material>(materialPath.Replace('\\', '/')), texture);
            Check(AssetDatabase.LoadAssetAtPath<Material>(root + "/Materials/" + actor + "_Atlas__Base.mat"), texture);
        }

        private static void Check(Material material, Texture texture)
        {
            Assert.That(material, Is.Not.Null);
            Assert.That(material.GetTexture("_BumpMap"), Is.SameAs(texture), material.name);
            Assert.That(material.IsKeywordEnabled("_NORMALMAP"), Is.True, material.name);
            // Original per-surface strengths are already encoded in the atlas.
            Assert.That(material.GetFloat("_BumpScale"), Is.EqualTo(1f), material.name);
        }
    }
}
#endif
