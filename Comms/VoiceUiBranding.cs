using System;
using System.IO;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

// Unity objects are created lazily on the main thread and retained for the plugin lifetime.
internal static class VoiceUiBranding
{
    private const string ResourcePrefix = "VoiceChatPlugin.Resources.VoiceUi.";
    private static readonly Sprite?[] CategorySprites = new Sprite?[5];
    private static Sprite? _logo;
    private static TMP_FontAsset? _font;

    internal static Sprite Logo() => _logo ??= LoadSprite("VoiceChatPlugin.Resources.miclogo.png");

    internal static Sprite CategoryIcon(VoiceSettingsCategory category)
    {
        var index = (int)category;
        if ((uint)index >= CategorySprites.Length) throw new ArgumentOutOfRangeException(nameof(category));
        return CategorySprites[index] ??= LoadSprite(ResourcePrefix + category + ".png");
    }

    internal static TMP_FontAsset? Font(TMP_FontAsset? fallbackFont)
    {
        if (fallbackFont == null) return _font;
        if (_font == null) _font = CreateFont(fallbackFont);
        else if (fallbackFont != _font && !_font.fallbackFontAssetTable.Contains(fallbackFont))
            _font.fallbackFontAssetTable.Add(fallbackFont);
        return _font;
    }

    private static Sprite LoadSprite(string resourceName)
    {
        var sprite = VoiceChatHudState.LoadSprite(resourceName, highQuality: true);
        if (sprite == null) throw new InvalidOperationException("Failed to load voice settings artwork: " + resourceName);
        sprite.texture.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    private static TMP_FontAsset CreateFont(TMP_FontAsset fallbackFont)
    {
        using var stream = OpenResource(ResourcePrefix + "SpaceGroteskSdf.bytes");
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt64() != 0x3130304644534350UL)
            throw new InvalidDataException("Invalid Space Grotesk glyph metrics.");
        var pointSize = reader.ReadInt32();
        var padding = reader.ReadInt32();
        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        var glyphCount = reader.ReadInt32();
        reader.ReadInt32(); // Baked variable-font weight: 400.
        var face = new FaceInfo
        {
            familyName = "Space Grotesk",
            styleName = "Regular",
            pointSize = pointSize,
            scale = 1f,
            ascentLine = reader.ReadSingle(),
            descentLine = reader.ReadSingle(),
            lineHeight = reader.ReadSingle(),
            capLine = reader.ReadSingle(),
            meanLine = reader.ReadSingle(),
            underlineOffset = reader.ReadSingle(),
            underlineThickness = reader.ReadSingle(),
            superscriptOffset = pointSize * 0.5f,
            superscriptSize = 0.6f,
            subscriptOffset = -pointSize * 0.15f,
            subscriptSize = 0.6f,
            strikethroughOffset = pointSize * 0.25f,
            strikethroughThickness = 2f,
            tabWidth = pointSize * 0.5f,
        };
        var atlas = LoadAtlas(ResourcePrefix + "SpaceGroteskSdf.png");
        if (atlas.width != width || atlas.height != height)
        {
            Object.Destroy(atlas);
            throw new InvalidDataException("Space Grotesk atlas dimensions do not match glyph metrics.");
        }
        var font = ScriptableObject.CreateInstance<TMP_FontAsset>();
        font.name = "Perfect Comms Space Grotesk SDF";
        font.hideFlags = HideFlags.HideAndDontSave;
        font.version = "1.1.0";
        font.faceInfo = face;
        font.atlasPopulationMode = AtlasPopulationMode.Static;
        font.isMultiAtlasTexturesEnabled = false;
        font.atlasWidth = width;
        font.atlasHeight = height;
        font.atlasPadding = padding;
        font.atlasRenderMode = GlyphRenderMode.SDF;
        font.atlasTextures = new Il2CppReferenceArray<Texture2D>(new[] { atlas });
        font.glyphTable = new Il2CppSystem.Collections.Generic.List<Glyph>(glyphCount);
        font.characterTable = new Il2CppSystem.Collections.Generic.List<TMP_Character>(glyphCount);
        font.fallbackFontAssetTable = new Il2CppSystem.Collections.Generic.List<TMP_FontAsset>(1);
        font.fallbackFontAssetTable.Add(fallbackFont);
        for (var i = 0; i < glyphCount; i++)
        {
            var unicode = reader.ReadUInt32();
            var metrics = new GlyphMetrics(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle());
            var rect = new GlyphRect(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            var glyph = new Glyph((uint)i + 1, metrics, rect, 1f, 0);
            font.glyphTable.Add(glyph);
            font.characterTable.Add(new TMP_Character(unicode, font, glyph));
        }
        var shader = Shader.Find("TextMeshPro/Distance Field") ?? Shader.Find("TextMeshPro/Mobile/Distance Field");
        if (shader == null)
        {
            Object.Destroy(font);
            Object.Destroy(atlas);
            throw new InvalidOperationException("TextMeshPro distance-field shader is unavailable for Space Grotesk.");
        }
        font.material = new Material(shader)
        {
            name = "Perfect Comms Space Grotesk SDF Material",
            hideFlags = HideFlags.HideAndDontSave,
            mainTexture = atlas,
        };
        font.material.SetFloat("_TextureWidth", width);
        font.material.SetFloat("_TextureHeight", height);
        font.material.SetFloat("_GradientScale", padding + 1);
        font.material.SetFloat("_WeightNormal", 0f);
        font.material.SetFloat("_WeightBold", 0.75f);
        font.normalStyle = 0f;
        font.boldStyle = 0.75f;
        font.boldSpacing = 0f;
        font.ReadFontAssetDefinition();
        return font;
    }

    private static Texture2D LoadAtlas(string resourceName)
    {
        using var stream = OpenResource(resourceName);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true)
        {
            name = "Perfect Comms Space Grotesk SDF Atlas",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };
        if (texture.LoadImage(bytes.ToArray(), true)) return texture;
        Object.Destroy(texture);
        throw new InvalidDataException("Failed to decode " + resourceName);
    }

    private static Stream OpenResource(string resourceName) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
        ?? throw new FileNotFoundException("Missing voice settings resource: " + resourceName);
}
