using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace HexLive.UnityDebug.Editor
{
    /// <summary>
    /// HV-002 / §171.14: every emoji the UI prints comes from ONE baked atlas
    /// (Tools/emoji/bake_emoji_atlas.py), wired into the panels' text settings as
    /// the emoji fallback. Without it WebGL showed empty boxes (no system emoji
    /// font in the browser) and macOS/Windows each drew their own style.
    /// Results are LOGGED, never shown in a dialog (CLAUDE.md).
    /// </summary>
    public static class EmojiSpriteAssetBuilder
    {
        private const string AtlasPath = "Assets/Resources/HexLive/UI/EmojiAtlas.png";
        private const string ManifestPath = "Assets/Resources/HexLive/UI/EmojiAtlas.json";
        private const string SpriteAssetPath = "Assets/Resources/HexLive/UI/EmojiSpriteAsset.asset";
        private const string TextSettingsPath = "Assets/Resources/HexLive/UI/HexLivePanelTextSettings.asset";
        private const string PanelSettingsPath = "Assets/Resources/HexLive/DebugPanelSettings.asset";

        [Serializable] private sealed class Manifest { public int atlasSize; public int glyph; public Entry[] sprites; }
        [Serializable] private sealed class Entry { public uint unicode; public string sequence; public int x, y, w, h; }

        [MenuItem("HexLive/UI/Build Emoji Sprite Asset")]
        public static void Build()
        {
            var manifest = JsonUtility.FromJson<Manifest>(System.IO.File.ReadAllText(ManifestPath));
            ConfigureAtlasImport();
            var atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(AtlasPath)
                ?? throw new InvalidOperationException("Emoji atlas missing: run Tools/emoji/bake_emoji_atlas.py");

            var sprites = AssetDatabase.LoadAssetAtPath<SpriteAsset>(SpriteAssetPath);
            if (sprites == null)
            {
                sprites = ScriptableObject.CreateInstance<SpriteAsset>();
                AssetDatabase.CreateAsset(sprites, SpriteAssetPath);
            }
            sprites.name = "EmojiSpriteAsset";
            sprites.spriteGlyphTable.Clear();
            sprites.spriteCharacterTable.Clear();
            var size = manifest.glyph;
            // Sits on the baseline like a capital letter and advances one em.
            var metrics = new GlyphMetrics(size, size, 0, size * 0.82f, size);
            for (var i = 0; i < manifest.sprites.Length; i++)
            {
                var e = manifest.sprites[i];
                var glyph = new SpriteGlyph((uint)i, metrics, new GlyphRect(e.x, e.y, e.w, e.h), 1f, 0);
                var character = new SpriteCharacter(e.unicode, sprites, glyph) { name = "u" + e.unicode.ToString("X4") };
                sprites.spriteGlyphTable.Add(glyph);
                sprites.spriteCharacterTable.Add(character);
            }

            var so = new SerializedObject(sprites);
            SetObject(so, atlas, "m_SpriteAtlasTexture", "spriteSheet");
            var face = new FaceInfo { pointSize = size, scale = 1f, lineHeight = size, ascentLine = size * 0.82f,
                baseline = 0f, descentLine = -size * 0.18f, capLine = size * 0.7f, meanLine = size * 0.5f };
            // Order matters: applying the SerializedObject writes back its whole snapshot,
            // so the face (set via reflection) goes in AFTER, or it reverts to zero size.
            so.ApplyModifiedPropertiesWithoutUndo();
            SetFaceInfo(sprites, face);
            var shader = Shader.Find("Text/Sprite") ?? Shader.Find("TextCore/Sprite") ?? Shader.Find("TextMeshPro/Sprite");
            if (shader != null)
            {
                var material = sprites.material != null ? sprites.material : new Material(shader) { name = "EmojiSpriteAsset Material" };
                material.mainTexture = atlas;
                if (sprites.material == null)
                {
                    AssetDatabase.AddObjectToAsset(material, sprites);
                    sprites.material = material;
                }
            }
            sprites.UpdateLookupTables();
            EditorUtility.SetDirty(sprites);

            var settings = AssetDatabase.LoadAssetAtPath<PanelTextSettings>(TextSettingsPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<PanelTextSettings>();
                AssetDatabase.CreateAsset(settings, TextSettingsPath);
            }
            settings.enableEmojiSupport = true;
            settings.emojiFallbackTextAssets = new List<UnityEngine.TextCore.Text.TextAsset> { sprites };
            settings.defaultSpriteAsset = sprites;
            EditorUtility.SetDirty(settings);

            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (panel != null && panel.textSettings != settings)
            {
                panel.textSettings = settings;
                EditorUtility.SetDirty(panel);
            }
            AssetDatabase.SaveAssets();

            var missing = manifest.sprites.Where(e => SpriteAsset.SearchForSpriteByUnicode(sprites, e.unicode, false, out _) == null).ToArray();
            Debug.Log($"[Emoji] sprite asset: {manifest.sprites.Length} emoji from {AtlasPath}; " +
                      $"lookup misses: {missing.Length}; panel text settings: {(panel != null ? panel.textSettings?.name : "no panel")}");
        }

        private static void ConfigureAtlasImport()
        {
            if (AssetImporter.GetAtPath(AtlasPath) is not TextureImporter importer) return;
            var changed = importer.textureType != TextureImporterType.Default || importer.mipmapEnabled ||
                          importer.alphaIsTransparency != true || importer.npotScale != TextureImporterNPOTScale.None ||
                          importer.isReadable;
            importer.textureType = TextureImporterType.Default;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.isReadable = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            // Colour emoji must stay colour on every platform; 1024² RGBA32 is 4 MB.
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            if (changed) importer.SaveAndReimport();
        }

        /// <summary>The atlas/face fields are internal in TextCore — set them through serialization by any known name.</summary>
        private static void SetObject(SerializedObject so, UnityEngine.Object value, params string[] names)
        {
            foreach (var name in names)
            {
                var property = so.FindProperty(name);
                if (property != null) { property.objectReferenceValue = value; return; }
            }
            var it = so.GetIterator();
            while (it.NextVisible(true))
                if (it.propertyType == SerializedPropertyType.ObjectReference && it.type.Contains("Texture"))
                { it.objectReferenceValue = value; return; }
            throw new InvalidOperationException("SpriteAsset has no texture field");
        }

        private static void SetFaceInfo(SpriteAsset asset, FaceInfo face)
        {
            var property = typeof(SpriteAsset).GetProperty("faceInfo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var setter = property?.GetSetMethod(true);
            if (setter != null) { setter.Invoke(asset, new object[] { face }); return; }
            var field = typeof(SpriteAsset).GetField("m_FaceInfo", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? typeof(UnityEngine.TextCore.Text.TextAsset).GetField("m_FaceInfo", BindingFlags.Instance | BindingFlags.NonPublic);
            field?.SetValue(asset, face);
        }
    }
}
