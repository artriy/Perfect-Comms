using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin;

internal static class CrewmateAvatarRenderer
{
    private const float RootScale = 0.16f;
    private const float BodyScale = 0.68f;
    private const float BasePixelsPerUnit = 32f;
    private const int BodyOrder = VCSorting.Base - 3;
    private const int BackCosmeticOrder = VCSorting.Base - 2;
    private const int CosmeticOrder = VCSorting.Base - 1;
    private const int FrontCosmeticOrder = VCSorting.Base;
    private const float IdleVelocityEpsilon = 0.0004f;
    private const int StableIdleFrameThreshold = 8;
    private const float PoseTransformEpsilon = 0.0025f;
    private const int RainbowFrameCount = 48;
    private const float RainbowHueSpeed = 0.3f;
    private const string BaseCrewmatePngBase64 = "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";

    private static readonly Dictionary<int, Sprite> BaseSpriteCache = new();
    private static readonly Dictionary<int, Sprite> RainbowSpriteCache = new();
    private static readonly Dictionary<byte, AvatarSnapshot> IdlePoseCache = new();
    private static readonly Dictionary<byte, IdlePoseCandidate> IdlePoseCandidates = new();
    private static Color32[]? templatePixels;
    private static int templateWidth;
    private static int templateHeight;
    private static Sprite? concealedBaseSprite;
    // Neutral grey used to mirror the game hiding a player's identity (camouflage / Mushroom
    // Mixup / swoop). When concealed the bar shows a grey body, no cosmetics, and no name so
    // the overlay never reveals the real color/cosmetics/name of a concealed speaker.
    private static readonly Color32 ConcealedColor = new(0x7f, 0x7f, 0x7f, 0xff);
    private static readonly Color32 ConcealedShadowColor = new(0x4a, 0x4a, 0x4a, 0xff);

    public static void TrackIdlePose(PlayerControl? pc)
    {
        try
        {
            if (!IsRightFacingIdle(pc))
            {
                ClearIdleCandidate(pc);
                return;
            }

            if (!UpdateIdleCandidate(pc!, out var candidate)) return;
            if (!candidate.Promoted && candidate.StableFrames >= StableIdleFrameThreshold)
            {
                var snapshot = CaptureCurrentPose(pc!);
                IdlePoseCache[pc!.PlayerId] = snapshot;
                candidate.Promoted = true;
            }
        }
        catch
        {
            ClearIdleCandidate(pc);
        }
    }

    public static bool TryCreate(byte playerId, PlayerControl pc, Transform parent, out GameObject? iconGO)
    {
        iconGO = null;
        if (pc?.Data == null || parent == null) return false;

        // Best effort only. Live cosmetic pose capture is allowed to fail during
        // lobby/game transitions, movement, intro, or late cosmetic loading.
        TrackIdlePose(pc);

        // Concealed (camouflaged/mixed-up/swooped) players render as a neutral grey body with no
        // rainbow animation and no cosmetic pose, mirroring how the game hides their identity.
        bool concealed = IsConcealed(pc);
        int colorId = GetPlayerColorId(pc);
        bool isRainbow = !concealed && IsRainbowColorId(colorId);
        var baseSprite = concealed
            ? GetConcealedBaseSprite()
            : isRainbow ? GetRainbowBaseSprite(0) : GetBaseSprite(colorId);
        if (baseSprite == null) return false;

        var root = new GameObject($"VC_SpriteIcon_{playerId}");
        root.transform.SetParent(parent, false);
        root.transform.localScale = Vector3.one * RootScale;
        root.transform.localPosition = Vector3.zero;

        var bodyRenderer = AddSprite(root.transform, "VC_Body_Base", baseSprite, Vector3.zero, Quaternion.identity, Vector3.one * BodyScale, Color.white, BodyOrder);
        if (isRainbow) AddRainbowBodyAnimator(bodyRenderer);
        if (!concealed && HasCachedPose(playerId, pc))
            _ = TryAddCachedPose(root.transform, playerId, pc);
        ApplySorting(root);
        VCOverlayCamera.EnsureOnTop(root);
        iconGO = root;
        return true;
    }

    public static bool HasCachedPose(byte playerId, PlayerControl? pc)
        => pc?.Data != null
           && IdlePoseCache.TryGetValue(playerId, out var snapshot)
           && snapshot.Matches(pc);

    public static bool HasCachedCosmeticPose(byte playerId, PlayerControl? pc)
        => pc?.Data != null
           && IdlePoseCache.TryGetValue(playerId, out var snapshot)
           && snapshot.Layers.Count > 0
           && snapshot.Matches(pc);

    // Add cached cosmetic layers onto an already-created (body-only) icon in place,
    // avoiding a destroy/recreate of the whole icon GameObject when cosmetics finish loading.
    internal static bool TryUpgradeWithCachedPose(GameObject? iconRoot, byte playerId, PlayerControl? pc)
    {
        if (iconRoot == null || pc?.Data == null) return false;
        if (IsConcealed(pc)) return false; // never attach real cosmetics onto a concealed body
        if (!HasCachedCosmeticPose(playerId, pc)) return false;
        if (!TryAddCachedPose(iconRoot.transform, playerId, pc)) return false;
        ApplySorting(iconRoot);
        VCOverlayCamera.EnsureOnTop(iconRoot);
        return true;
    }

    internal static void ClearCache()
    {
        IdlePoseCache.Clear();
        IdlePoseCandidates.Clear();
    }

    internal static void ClearPlayer(byte playerId)
    {
        IdlePoseCache.Remove(playerId);
        IdlePoseCandidates.Remove(playerId);
    }

    public static bool IsCustomIcon(GameObject go)
        => go != null && go.name.StartsWith("VC_SpriteIcon_");

    // Shared palette color for speaking overlays (bar + meeting), using the same
    // body-color source so both stay in parity.
    internal static Color GetPaletteColor(PlayerControl? pc)
    {
        if (pc?.Data == null) return new Color(0.18f, 0.80f, 0.44f, 1f); // voice fallback green
        if (IsConcealed(pc)) return (Color)ConcealedColor;
        // Out-of-range / modded color ids fall back to the SAME palette index (red) that the body
        // sprite uses via ClampColorId, so the ring/meeting glow never disagrees with the body
        // (previously the body recolored to red while the ring/card glowed white).
        return (Color)Palette.PlayerColors[ClampColorId(GetPlayerColorId(pc))];
    }

    // A player is "concealed" when the game hides their identity so others cannot tell who is
    // speaking. The numeric CurrentOutfitType space is shared between vanilla PlayerOutfitType and
    // Town of Us' TownOfUsAppearances: 3 = MushroomMixUp, 4 = Swooper, 6 = Camouflage — all grey
    // every body. Disguises (Shapeshift=1, Mimic=5, Morph=7) intentionally show the disguised
    // appearance and are NOT treated as concealed.
    internal static bool IsConcealed(PlayerControl? pc)
    {
        if (pc?.Data == null) return false;
        try
        {
            int outfitType = (int)pc.CurrentOutfitType;
            return outfitType == 3 || outfitType == 4 || outfitType == 6;
        }
        catch
        {
            return false;
        }
    }

    private static Sprite? GetConcealedBaseSprite()
    {
        if (concealedBaseSprite != null) return concealedBaseSprite;
        concealedBaseSprite = CreateBaseSprite(ConcealedColor, ConcealedShadowColor);
        return concealedBaseSprite;
    }

    private static bool DestroyIncompleteIcon(GameObject root)
    {
        Object.Destroy(root);
        return false;
    }

    public static void ApplySorting(GameObject go)
    {
        foreach (var sr in go.GetComponentsInChildren<SpriteRenderer>(true))
        {
            sr.sortingLayerName = VCSorting.Layer;
            sr.maskInteraction = SpriteMaskInteraction.None;
        }
    }

    private static Sprite? GetBaseSprite(int colorId)
    {
        colorId = ClampColorId(colorId);
        if (BaseSpriteCache.TryGetValue(colorId, out var cached)) return cached;
        var sprite = CreateBaseSprite(Palette.PlayerColors[colorId], Palette.ShadowColors[colorId]);
        if (sprite == null) return null;
        BaseSpriteCache[colorId] = sprite;
        return sprite;
    }

    internal static Sprite? GetRainbowBaseSprite(int frameIndex)
    {
        frameIndex %= RainbowFrameCount;
        if (frameIndex < 0) frameIndex += RainbowFrameCount;
        if (RainbowSpriteCache.TryGetValue(frameIndex, out var cached)) return cached;

        var main = RainbowBodyColor(frameIndex);
        var shadow = RainbowShadowColor(main);
        var sprite = CreateBaseSprite(main, shadow);
        if (sprite == null) return null;
        RainbowSpriteCache[frameIndex] = sprite;
        return sprite;
    }

    internal static int GetRainbowFrameIndex(float time)
    {
        float hue = Mathf.PingPong(time * RainbowHueSpeed, 1f);
        return Mathf.Clamp(Mathf.RoundToInt(hue * (RainbowFrameCount - 1)), 0, RainbowFrameCount - 1);
    }

    private static Sprite? CreateBaseSprite(Color32 main, Color32 shadow)
    {
        if (!EnsureTemplatePixels()) return null;

        var pixels = new Color32[templatePixels!.Length];
        var highlight = new Color32(0x9a, 0xca, 0xd5, 0xff);

        for (int i = 0; i < templatePixels.Length; i++)
            pixels[i] = RecolorPixel(templatePixels[i], main, shadow, highlight);

        var tex = new Texture2D(templateWidth, templateHeight, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };
        tex.SetPixels32(pixels);
        tex.Apply(false, false);

        var sprite = Sprite.Create(tex, new Rect(0, 0, templateWidth, templateHeight), new Vector2(0.5f, 0.5f), BasePixelsPerUnit);
        sprite.hideFlags |= HideFlags.HideAndDontSave;
        return sprite;
    }

    private static bool IsRainbowColorId(int colorId)
    {
        try
        {
            if (colorId < 0 || colorId >= Palette.ColorNames.Length) return false;
            if (TryIsTownOfUsRainbowColor(colorId, out bool isTownOfUsRainbow)) return isTownOfUsRainbow;
            return IsRainbowColorName(Palette.GetColorName(colorId))
                || IsRainbowColorName(Palette.ColorNames[colorId].ToString())
                || (colorId < Palette.PlayerColors.Length && IsZeroColor(Palette.PlayerColors[colorId]));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRainbowColorName(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.IndexOf("Rainbow", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool TryIsTownOfUsRainbowColor(int colorId, out bool isRainbow)
    {
        isRainbow = false;
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType("TownOfUs.Modules.RainbowMod.RainbowUtils");
                var method = type?.GetMethod(
                    "IsRainbow",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(int) },
                    null);
                if (method == null) continue;

                var result = method.Invoke(null, new object[] { colorId });
                if (result is not bool value) continue;

                isRainbow = value;
                return true;
            }
        }
        catch
        {
            isRainbow = false;
        }

        return false;
    }

    private static bool IsZeroColor(Color32 color)
        => color.r == 0 && color.g == 0 && color.b == 0;

    private static void AddRainbowBodyAnimator(SpriteRenderer bodyRenderer)
    {
        var animator = bodyRenderer.gameObject.AddComponent<RainbowBodyAnimator>();
        animator.Init(bodyRenderer);
    }

    private static Color32 RainbowBodyColor(int frameIndex)
    {
        float hue = frameIndex / (float)(RainbowFrameCount - 1);
        return ToColor32(Color.HSVToRGB(hue, 1f, 1f));
    }

    private static Color32 RainbowShadowColor(Color32 color)
        => new(
            (byte)Mathf.Clamp(color.r - 77, 0, 255),
            (byte)Mathf.Clamp(color.g - 77, 0, 255),
            (byte)Mathf.Clamp(color.b - 77, 0, 255),
            255);

    private static Color32 ToColor32(Color color)
        => new(
            (byte)Mathf.RoundToInt(Mathf.Clamp01(color.r) * 255f),
            (byte)Mathf.RoundToInt(Mathf.Clamp01(color.g) * 255f),
            (byte)Mathf.RoundToInt(Mathf.Clamp01(color.b) * 255f),
            255);

    private static bool EnsureTemplatePixels()
    {
        if (templatePixels != null) return true;
        try
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            if (!tex.LoadImage(System.Convert.FromBase64String(BaseCrewmatePngBase64), false)) return false;
            templateWidth = tex.width;
            templateHeight = tex.height;
            templatePixels = tex.GetPixels32();
            Object.Destroy(tex);
            return templatePixels.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static Color32 RecolorPixel(Color32 pixel, Color32 color, Color32 shadow, Color32 highlight)
    {
        if (pixel.a == 0) return pixel;

        var (hue, saturation) = RgbToHueSaturation(pixel.r, pixel.g, pixel.b);
        if (saturation <= 0.4f
            || (!IsHueNear(hue, 240f, 30f) && !IsHueNear(hue, 0f, 100f) && !IsHueNear(hue, 120f, 40f)))
            return pixel;

        var mixed = MixRgb(new Color32(0, 0, 0, 255), shadow, pixel.b / 255f);
        mixed = MixRgb(mixed, color, pixel.r / 255f);
        mixed = MixRgb(mixed, highlight, pixel.g / 255f);
        return new Color32(mixed.r, mixed.g, mixed.b, pixel.a);
    }

    private static (float Hue, float Saturation) RgbToHueSaturation(byte red, byte green, byte blue)
    {
        float r = red / 255f;
        float g = green / 255f;
        float b = blue / 255f;
        float max = Mathf.Max(r, Mathf.Max(g, b));
        float min = Mathf.Min(r, Mathf.Min(g, b));
        float delta = max - min;

        float hue = 0f;
        if (delta != 0f)
        {
            if (max == r) hue = 60f * PositiveModulo((g - b) / delta, 6f);
            else if (max == g) hue = 60f * (((b - r) / delta) + 2f);
            else hue = 60f * (((r - g) / delta) + 4f);
        }

        float saturation = max == 0f ? 0f : delta / max;
        return (hue, saturation);
    }

    private static bool IsHueNear(float value, float target, float maxDifference)
        => 180f - Mathf.Abs(Mathf.Abs(value - target) - 180f) < maxDifference;

    private static float PositiveModulo(float value, float modulo)
        => ((value % modulo) + modulo) % modulo;

    private static Color32 MixRgb(Color32 first, Color32 second, float amount)
    {
        amount = Mathf.Clamp01(amount);
        return new Color32(
            (byte)Mathf.RoundToInt(first.r * (1f - amount) + second.r * amount),
            (byte)Mathf.RoundToInt(first.g * (1f - amount) + second.g * amount),
            (byte)Mathf.RoundToInt(first.b * (1f - amount) + second.b * amount),
            255);
    }

    private static bool TryAddCachedPose(Transform root, byte playerId, PlayerControl pc)
    {
        try
        {
            if (!IdlePoseCache.TryGetValue(playerId, out var snapshot)) return false;
            if (!snapshot.Matches(pc)) return false;

            foreach (var layer in snapshot.Layers)
            {
                if (layer.Sprite == null) continue;
                var target = AddSprite(root, layer.Name, layer.Sprite, layer.LocalPosition, layer.LocalRotation, layer.LocalScale, layer.Color, layer.SortOrder);
                target.flipX = false;
                target.flipY = false;
                if (layer.SharedMaterial != null)
                    target.sharedMaterial = layer.SharedMaterial;
            }
            return true;
        }
        catch
        {
            ClearPlayer(playerId);
            return false;
        }
    }

    private static void ClearIdleCandidate(PlayerControl? pc)
    {
        if (pc != null) IdlePoseCandidates.Remove(pc.PlayerId);
    }

    private static bool UpdateIdleCandidate(PlayerControl pc, out IdlePoseCandidate candidate)
    {
        var fingerprint = CapturePoseFingerprint(pc);
        if (!IdlePoseCandidates.TryGetValue(pc.PlayerId, out var existing) || !existing.Matches(fingerprint))
        {
            candidate = new IdlePoseCandidate(fingerprint);
            IdlePoseCandidates[pc.PlayerId] = candidate;
        }
        else
        {
            candidate = existing;
            candidate.StableFrames++;
        }

        return true;
    }

    private static PoseFingerprint CapturePoseFingerprint(PlayerControl pc)
    {
        var outfit = GetDisplayOutfit(pc);
        int outfitTypeId = GetDisplayOutfitId(pc);
        int colorId = GetPlayerColorId(pc);
        var cosmetics = pc.cosmetics;
        var parent = cosmetics.transform;
        var hash = new HashCode();
        int layers = 0;

        hash.Add(outfitTypeId);
        hash.Add(colorId);
        hash.Add(outfit.HatId ?? string.Empty);
        hash.Add(outfit.SkinId ?? string.Empty);
        hash.Add(outfit.VisorId ?? string.Empty);
        hash.Add(outfit.PlayerName ?? string.Empty);

        foreach (var source in cosmetics.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (source == null || source.sprite == null || !source.enabled || !source.gameObject.activeInHierarchy) continue;
            if (!ShouldCopyIdlePoseLayer(source)) continue;

            layers++;
            string parentName = source.transform.parent != null ? source.transform.parent.gameObject.name : string.Empty;
            var localPosition = parent.InverseTransformPoint(source.transform.position);
            var localRotation = Quaternion.Inverse(parent.rotation) * source.transform.rotation;
            var localScale = DivideScale(source.transform.lossyScale, parent.lossyScale);

            hash.Add(source.gameObject.name);
            hash.Add(parentName);
            hash.Add(source.sprite.GetInstanceID());
            hash.Add(QuantizePoseValue(localPosition.x));
            hash.Add(QuantizePoseValue(localPosition.y));
            hash.Add(QuantizePoseValue(localRotation.x));
            hash.Add(QuantizePoseValue(localRotation.y));
            hash.Add(QuantizePoseValue(localRotation.z));
            hash.Add(QuantizePoseValue(localRotation.w));
            hash.Add(QuantizePoseValue(localScale.x));
            hash.Add(QuantizePoseValue(localScale.y));
            hash.Add(QuantizePoseValue(localScale.z));
            hash.Add(CosmeticSortOrder(source));
        }

        return new PoseFingerprint(
            outfitTypeId,
            colorId,
            outfit.HatId ?? string.Empty,
            outfit.SkinId ?? string.Empty,
            outfit.VisorId ?? string.Empty,
            outfit.PlayerName ?? string.Empty,
            layers,
            hash.ToHashCode());
    }

    private static AvatarSnapshot CaptureCurrentPose(PlayerControl pc)
    {
        var outfit = GetDisplayOutfit(pc);
        int outfitTypeId = GetDisplayOutfitId(pc);
        int colorId = GetPlayerColorId(pc);
        var layers = new List<SpriteLayerSnapshot>();
        var cosmetics = pc.cosmetics;
        var parent = cosmetics.transform;

        foreach (var source in cosmetics.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (source == null || source.sprite == null || !source.enabled || !source.gameObject.activeInHierarchy) continue;
            if (!ShouldCopyIdlePoseLayer(source)) continue;

            layers.Add(new SpriteLayerSnapshot(
                "VC_Cached_" + source.gameObject.name,
                source.sprite,
                parent.InverseTransformPoint(source.transform.position),
                Quaternion.Inverse(parent.rotation) * source.transform.rotation,
                DivideScale(source.transform.lossyScale, parent.lossyScale),
                source.color,
                CosmeticSortOrder(source),
                source.sharedMaterial));
        }

        return new AvatarSnapshot(outfitTypeId, colorId, outfit.HatId, outfit.SkinId, outfit.VisorId, outfit.PlayerName, layers);
    }

    private static SpriteRenderer AddSprite(Transform parent, string name, Sprite sprite, Vector3 localPosition, Quaternion localRotation, Vector3 localScale, Color color, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(localPosition.x, localPosition.y, 0f);
        go.transform.localRotation = localRotation;
        go.transform.localScale = localScale;

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = color;
        sr.sortingLayerName = VCSorting.Layer;
        sr.sortingOrder = order;
        sr.maskInteraction = SpriteMaskInteraction.None;
        return sr;
    }

    private static bool ShouldCopyIdlePoseLayer(SpriteRenderer source)
    {
        string name = source.gameObject.name.ToLowerInvariant();
        string parent = source.transform.parent != null ? source.transform.parent.gameObject.name.ToLowerInvariant() : string.Empty;
        if (IsBodyLike(name) || IsBodyLike(parent)) return false;
        if (name.Contains("shadow") || parent.Contains("shadow") || name.Contains("pet") || parent.Contains("pet")) return false;
        return HasIdlePoseName(name) || HasIdlePoseName(parent);
    }

    private static bool HasIdlePoseName(string value)
        => value.Contains("hat") || value.Contains("skin") || value.Contains("visor");

    private static bool IsBodyLike(string value)
        => value.Contains("body") || value.Contains("bean") || value.Contains("player") || value.Contains("base");

    private static int CosmeticSortOrder(SpriteRenderer source)
    {
        string name = source.gameObject.name.ToLowerInvariant();
        string parent = source.transform.parent != null ? source.transform.parent.gameObject.name.ToLowerInvariant() : string.Empty;
        if (name.Contains("back") || parent.Contains("back")) return BackCosmeticOrder;
        if (name.Contains("visor") || parent.Contains("visor")) return FrontCosmeticOrder;
        if (name.Contains("hat") || parent.Contains("hat")) return FrontCosmeticOrder;
        return CosmeticOrder;
    }

    private static Vector3 DivideScale(Vector3 value, Vector3 divisor)
        => new(SafeDiv(value.x, divisor.x), SafeDiv(value.y, divisor.y), SafeDiv(value.z, divisor.z));

    private static float SafeDiv(float value, float divisor)
        => Mathf.Abs(divisor) < 0.0001f ? value : value / divisor;

    private static int QuantizePoseValue(float value)
        => Mathf.RoundToInt(value / PoseTransformEpsilon);

    private static int ClampColorId(int colorId)
        => colorId >= 0 && colorId < Palette.PlayerColors.Length ? colorId : 0;

    private static int GetPlayerColorId(PlayerControl pc)
    {
        int bodyColor;
        try { bodyColor = pc.cosmetics.bodyMatProperties.ColorId; }
        catch { try { return GetDisplayOutfit(pc).ColorId; } catch { return 0; } }

        // bodyMatProperties briefly reads 0 (red) before cosmetics initialize during
        // lobby/intro/transition. If the authoritative networked outfit reports a
        // different, valid color, trust it so the fallback body isn't transiently red.
        if (bodyColor == 0)
        {
            try
            {
                int outfitColor = GetDisplayOutfit(pc).ColorId;
                if (outfitColor > 0) return outfitColor;
            }
            catch { /* keep bodyColor */ }
        }
        return bodyColor;
    }

    private static NetworkedPlayerInfo.PlayerOutfit GetDisplayOutfit(PlayerControl pc)
    {
        try
        {
            return pc.CurrentOutfit ?? pc.Data.DefaultOutfit;
        }
        catch
        {
            return pc.Data.DefaultOutfit;
        }
    }

    private static int GetDisplayOutfitId(PlayerControl pc)
    {
        try
        {
            return (int)pc.CurrentOutfitType;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsRightFacingIdle(PlayerControl? pc)
    {
        if (!IsAvatarReady(pc)) return false;
        var player = pc!;
        if (player.cosmetics.FlipX) return false;
        var physics = player.MyPhysics;
        if (physics == null) return false;
        return physics.Velocity.sqrMagnitude <= IdleVelocityEpsilon;
    }

    private static bool IsAvatarReady(PlayerControl? pc)
    {
        if (pc?.Data == null || pc.cosmetics == null) return false;
        if (!pc.gameObject.activeInHierarchy || !pc.cosmetics.gameObject.activeInHierarchy) return false;
        if (!pc.moveable) return false;
        if (IntroCutscene.Instance != null) return false;
        return true;
    }

    private sealed class AvatarSnapshot
    {
        public readonly int OutfitTypeId;
        public readonly int ColorId;
        public readonly string HatId;
        public readonly string SkinId;
        public readonly string VisorId;
        public readonly string PlayerName;
        public readonly List<SpriteLayerSnapshot> Layers;

        public AvatarSnapshot(int outfitTypeId, int colorId, string hatId, string skinId, string visorId, string playerName, List<SpriteLayerSnapshot> layers)
        {
            OutfitTypeId = outfitTypeId;
            ColorId = colorId;
            HatId = hatId ?? string.Empty;
            SkinId = skinId ?? string.Empty;
            VisorId = visorId ?? string.Empty;
            PlayerName = playerName ?? string.Empty;
            Layers = layers;
        }

        public bool Matches(PlayerControl pc)
        {
            if (pc?.Data == null) return false;
            var outfit = GetDisplayOutfit(pc);
            return OutfitTypeId == GetDisplayOutfitId(pc)
                && ColorId == GetPlayerColorId(pc)
                && HatId == (outfit.HatId ?? string.Empty)
                && SkinId == (outfit.SkinId ?? string.Empty)
                && VisorId == (outfit.VisorId ?? string.Empty)
                && PlayerName == (outfit.PlayerName ?? string.Empty);
        }
    }

    private sealed class IdlePoseCandidate
    {
        private readonly PoseFingerprint Fingerprint;
        public int StableFrames;
        public bool Promoted;

        public IdlePoseCandidate(PoseFingerprint fingerprint)
        {
            Fingerprint = fingerprint;
            StableFrames = 1;
        }

        public bool Matches(PoseFingerprint fingerprint)
            => Fingerprint.Matches(fingerprint);
    }

    private readonly struct PoseFingerprint
    {
        private readonly int OutfitTypeId;
        private readonly int ColorId;
        private readonly string HatId;
        private readonly string SkinId;
        private readonly string VisorId;
        private readonly string PlayerName;
        private readonly int Hash;
        public readonly int LayerCount;

        public PoseFingerprint(int outfitTypeId, int colorId, string hatId, string skinId, string visorId, string playerName, int layerCount, int hash)
        {
            OutfitTypeId = outfitTypeId;
            ColorId = colorId;
            HatId = hatId ?? string.Empty;
            SkinId = skinId ?? string.Empty;
            VisorId = visorId ?? string.Empty;
            PlayerName = playerName ?? string.Empty;
            LayerCount = layerCount;
            Hash = hash;
        }

        public bool Matches(PoseFingerprint other)
            => OutfitTypeId == other.OutfitTypeId
               && ColorId == other.ColorId
               && HatId == other.HatId
               && SkinId == other.SkinId
               && VisorId == other.VisorId
               && PlayerName == other.PlayerName
               && LayerCount == other.LayerCount
               && Hash == other.Hash;
    }

    private readonly struct SpriteLayerSnapshot
    {
        public readonly string Name;
        public readonly Sprite Sprite;
        public readonly Vector3 LocalPosition;
        public readonly Quaternion LocalRotation;
        public readonly Vector3 LocalScale;
        public readonly Color Color;
        public readonly int SortOrder;
        public readonly Material? SharedMaterial;

        public SpriteLayerSnapshot(
            string name,
            Sprite sprite,
            Vector3 localPosition,
            Quaternion localRotation,
            Vector3 localScale,
            Color color,
            int sortOrder,
            Material? sharedMaterial)
        {
            Name = name;
            Sprite = sprite;
            LocalPosition = localPosition;
            LocalRotation = localRotation;
            LocalScale = localScale;
            Color = color;
            SortOrder = sortOrder;
            SharedMaterial = sharedMaterial;
        }
    }
}

internal sealed class RainbowBodyAnimator : MonoBehaviour
{
    private SpriteRenderer? _renderer;
    private int _lastFrame = -1;

    static RainbowBodyAnimator()
    {
        ClassInjector.RegisterTypeInIl2Cpp<RainbowBodyAnimator>();
    }

    public void Init(SpriteRenderer renderer)
    {
        _renderer = renderer;
        UpdateFrame(true);
    }

    void Update()
    {
        UpdateFrame(false);
    }

    private void UpdateFrame(bool force)
    {
        if (_renderer == null)
        {
            Object.Destroy(this);
            return;
        }

        int frame = CrewmateAvatarRenderer.GetRainbowFrameIndex(Time.time);
        if (!force && frame == _lastFrame) return;

        var sprite = CrewmateAvatarRenderer.GetRainbowBaseSprite(frame);
        if (sprite != null)
        {
            _renderer.sprite = sprite;
            _lastFrame = frame;
        }
    }
}
