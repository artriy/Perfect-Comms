using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using Object = UnityEngine.Object;
using VoiceChatPlugin.VoiceChat;

namespace VoiceChatPlugin;

internal static class CrewmateAvatarRenderer
{
    private const float RootScale = 0.16f;
    private const float BodyScale = 0.68f;
    // The custom living body is 100 px at 32 PPU and uses BodyScale; the vanilla ghost and
    // cosmetics are roughly 200 px at 100 PPU and match that visible HUD footprint at scale 1.
    // Their supported player-prefab roots are half-scale, so normalize the copied hierarchy to 1.
    private const float VanillaBodyFormScale = 0.5f;
    private const float GhostHudTargetScale = 1f;
    internal const float GhostHudNormalizationScale = GhostHudTargetScale / VanillaBodyFormScale;
    internal const float VanillaGhostFallbackScale = GhostHudTargetScale;
    private const float BasePixelsPerUnit = 32f;
    internal const int BodyOrder = VCSorting.Base - 3;
    internal const int BackCosmeticOrder = BodyOrder - 1;
    private const int CosmeticOrder = VCSorting.Base - 1;
    private const int FrontCosmeticOrder = VCSorting.Base;
    private const int RainbowFrameCount = 48;
    private const float RainbowHueSpeed = 0.3f;
    private const string AliveBodyName = "VC_Body_Base";
    private const string GhostBodyName = "VC_Body_Ghost";
    private const string LivingCosmeticPrefix = "VC_LivingCosmetic_";
    private const string GhostCosmeticPrefix = "VC_GhostCosmetic_";
    private const string BaseCrewmatePngBase64 = "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";

    private static readonly Dictionary<int, Sprite> BaseSpriteCache = new();
    private static readonly Dictionary<int, Sprite> RainbowSpriteCache = new();
    // Memoized "is this color id the animated Rainbow color?" so the per-frame ring/glow/highlight color path
    // never repeats IsRainbowColorId's assembly-scanning reflection. Cleared per game by ClearCache().
    private static readonly Dictionary<int, bool> RainbowColorIdCache = new();
    private static readonly Dictionary<int, Sprite?> StaticLeftSpriteCache = new();
    private static MethodInfo? _townOfUsRainbowMethod;
    private static Type? _townOfUsRainbowMethodOwner;
    private static readonly object?[] TownOfUsRainbowArguments = new object?[1];
    private static Color32[]? templatePixels;
    private static int templateWidth;
    private static int templateHeight;
    private static Sprite? concealedBaseSprite;
    // Game-owned canonical ghost artwork. This is only a borrowed reference and must never be destroyed.
    private static Sprite? vanillaGhostSprite;
    private static int vanillaGhostLookupFrame = -1;
    // Neutral grey for concealed players: grey body, no cosmetics, no name.
    private static readonly Color32 ConcealedColor = new(0x7f, 0x7f, 0x7f, 0xff);
    private static readonly Color32 ConcealedShadowColor = new(0x4a, 0x4a, 0x4a, 0xff);

    // End-game avatar cache: the results screen has no live PlayerControls, so we snapshot each speaker's
    // resolved living outfit and, once published dead, the exact post-ghost renderer geometry. Transform
    // paths are stored from the PlayerControl root rather than flattened to one guessed anchor; that keeps
    // nested, animated, and modded cosmetic pivots/scales aligned with the game-owned ghost body.
    internal readonly struct CachedTransformNode
    {
        public readonly Vector3 LocalPosition;
        public readonly Quaternion LocalRotation;
        public readonly Vector3 LocalScale;

        public CachedTransformNode(Vector3 localPosition, Quaternion localRotation, Vector3 localScale)
        {
            LocalPosition = localPosition;
            LocalRotation = localRotation;
            LocalScale = localScale;
        }
    }

    internal readonly struct CachedCosmeticLayer
    {
        public readonly string Name;
        public readonly Sprite Sprite;
        public readonly Sprite? LeftSprite;
        public readonly Color Color;
        public readonly bool FlipX;
        public readonly bool FlipY;
        public readonly Material? Material;
        public readonly CachedTransformNode[] TransformPath;
        public readonly int Order;

        public CachedCosmeticLayer(
            string name,
            Sprite sprite,
            Color color,
            bool flipX,
            bool flipY,
            Material? material,
            CachedTransformNode[] transformPath,
            int order,
            Sprite? leftSprite = null)
        {
            Name = name;
            Sprite = sprite;
            LeftSprite = leftSprite;
            Color = color;
            FlipX = flipX;
            FlipY = flipY;
            Material = material;
            TransformPath = transformPath;
            Order = order;
        }
    }

    private readonly struct CachedGhostBodyLayer
    {
        public readonly Sprite Sprite;
        public readonly Color Color;
        public readonly bool FlipX;
        public readonly bool FlipY;
        public readonly Material? Material;
        public readonly CachedTransformNode[] TransformPath;
        public readonly int Order;

        public CachedGhostBodyLayer(
            Sprite sprite,
            Color color,
            bool flipX,
            bool flipY,
            Material? material,
            CachedTransformNode[] transformPath,
            int order)
        {
            Sprite = sprite;
            Color = color;
            FlipX = flipX;
            FlipY = flipY;
            Material = material;
            TransformPath = transformPath;
            Order = order;
        }
    }

    private readonly record struct GhostAppearanceIdentity(
        int ColorId,
        string HatId,
        string SkinId,
        string VisorId,
        bool Concealed);

    private sealed class CachedOutfit
    {
        public readonly GhostAppearanceIdentity Identity;
        public readonly int ColorId;
        public readonly bool IsRainbow;
        public readonly bool Concealed;
        public readonly bool CosmeticsResolved;
        public readonly Sprite? GhostSprite;
        public readonly List<CachedCosmeticLayer> Layers;
        public readonly List<CachedGhostBodyLayer> GhostBodyLayers;
        public readonly List<CachedCosmeticLayer> GhostLayers;
        public readonly bool GhostAppearanceCaptured;

        public CachedOutfit(
            GhostAppearanceIdentity identity,
            int colorId,
            bool isRainbow,
            bool concealed,
            bool cosmeticsResolved,
            Sprite? ghostSprite,
            List<CachedCosmeticLayer> layers,
            List<CachedGhostBodyLayer>? ghostBodyLayers = null,
            List<CachedCosmeticLayer>? ghostLayers = null,
            bool ghostAppearanceCaptured = false)
        {
            Identity = identity;
            ColorId = colorId;
            IsRainbow = isRainbow;
            Concealed = concealed;
            CosmeticsResolved = cosmeticsResolved;
            GhostSprite = ghostSprite;
            Layers = layers;
            GhostBodyLayers = ghostBodyLayers ?? new List<CachedGhostBodyLayer>();
            GhostLayers = ghostLayers ?? new List<CachedCosmeticLayer>();
            GhostAppearanceCaptured = ghostAppearanceCaptured;
        }
    }
    private static readonly Dictionary<byte, CachedOutfit> OutfitCache = new();

    internal static bool HasCachedIdentity(byte playerId)
        => OutfitCache.ContainsKey(playerId);

    // Set per-frame by the speaking-bar overlay. True when the bar is a roster (fixed all-players mode), where
    // the avatar should show each player's real identity, never their live in-world disguise -- same rule as a
    // meeting. A meeting is always treated as real-identity regardless of this flag.
    internal static bool PreferRealIdentity;

    private static bool ShowRealIdentity => PreferRealIdentity || MeetingHud.Instance != null;

    // Fixed-roster and meeting surfaces deliberately render the stable default identity. Concealment
    // still participates in the separate speaking-attribution privacy policy, but must never mutate a
    // fixed avatar into a grey/live-disguise icon.
    private static bool RenderAsConcealed(PlayerControl pc)
        => !ShowRealIdentity && IsConcealed(pc);

    // True when the player's live outfit is a disguise (morph/shapeshift/mimic) that differs from their real
    // default outfit. Used to suppress the live (disguised) cosmetics when the bar should show real identity.
    private static bool IsDisguised(PlayerControl pc)
    {
        try
        {
            var cur = pc.CurrentOutfit;
            if (cur == null) return false;
            var def = pc.Data.DefaultOutfit;
            return cur.HatId != def.HatId || cur.SkinId != def.SkinId || cur.VisorId != def.VisorId || cur.ColorId != def.ColorId;
        }
        catch { return false; }
    }

    public static bool TryCreate(byte playerId, PlayerControl pc, Transform parent, out GameObject? iconGO)
    {
        iconGO = null;
        if (pc?.Data == null || parent == null) return false;

        // Concealed players render as a neutral grey body with no rainbow/cosmetics.
        bool concealed = RenderAsConcealed(pc);
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

        var bodyRenderer = AddSprite(root.transform, AliveBodyName, baseSprite, Vector3.zero, Quaternion.identity, Vector3.one * BodyScale, Color.white, BodyOrder);
        if (isRainbow) AddRainbowBodyAnimator(bodyRenderer);
        var ghostSprite = TryGetVanillaGhostSprite(pc);
        TryAddVanillaGhostBody(root.transform, ghostSprite, colorId, isRainbow, concealed, pc);
        // Cosmetics are built straight from the player's live outfit (hat/skin/visor), so they render
        // immediately and reliably — no idle-pose capture, GameObject-name matching, or fingerprint gate.
        var capturedLayers = new List<CachedCosmeticLayer>();
        if (!concealed)
            TryAddOutfitCosmetics(root.transform, pc, capturedLayers);
        CacheOutfit(playerId, pc, ghostSprite, capturedLayers);
        ApplySorting(root);
        VCOverlayCamera.EnsureOnTop(root);
        iconGO = root;
        return true;
    }

    /// <summary>
    /// Creates an avatar for the in-game 15-player fake speaking-bar roster. The living body remains
    /// deterministic; fake ghost entries may also borrow the already-loaded canonical game ghost
    /// sprite. Settings/setup previews use the isolated overload below and never enter this path.
    /// </summary>
    internal static bool TryCreateSpeakingBarPreview(
        int previewIndex,
        Transform parent,
        out GameObject? iconGO,
        out bool ghostArtworkReady)
    {
        ghostArtworkReady = !SpeakingBarPreviewRoster.IsGhost(previewIndex);
        if (!TryCreatePreview(
                previewIndex,
                parent,
                VCOverlayCamera.OverlayLayer,
                attachToOverlayCamera: true,
                out iconGO))
            return false;

        if (SpeakingBarPreviewRoster.IsGhost(previewIndex) && iconGO != null)
            ghostArtworkReady = TryEnsureSpeakingBarPreviewGhost(previewIndex, iconGO);
        return true;
    }

    /// <summary>
    /// Creates the same deterministic preview avatar on a caller-owned rendering layer.
    /// Settings previews use this overload so constructing an avatar does not create, sync,
    /// or share the real speaking-overlay camera.
    /// </summary>
    internal static bool TryCreatePreview(
        int previewIndex,
        Transform parent,
        int targetLayer,
        bool attachToOverlayCamera,
        out GameObject? iconGO)
    {
        iconGO = null;
        if (parent == null || (uint)targetLayer > 31u ||
            !TryGetPreviewColorId(previewIndex, out int colorId)) return false;

        GameObject? root = null;
        try
        {
            var baseSprite = GetBaseSprite(colorId);
            if (baseSprite == null) return false;

            root = new GameObject($"VC_SpriteIcon_Preview_{previewIndex}");
            root.transform.SetParent(parent, false);
            root.transform.localScale = Vector3.one * RootScale;
            root.transform.localPosition = Vector3.zero;

            AddSprite(
                root.transform,
                AliveBodyName,
                baseSprite,
                Vector3.zero,
                Quaternion.identity,
                Vector3.one * BodyScale,
                Color.white,
                BodyOrder);
            ApplySorting(root);
            if (attachToOverlayCamera)
                VCOverlayCamera.EnsureOnTop(root);
            else
                SetLayerRecursive(root.transform, targetLayer);
            iconGO = root;
            return true;
        }
        catch
        {
            // A preview is optional UI. A palette/scene transition must not break the live overlay.
            if (root != null) Object.Destroy(root);
            return false;
        }
    }

    /// <summary>
    /// Adds the real vanilla ghost body to an existing in-game fake-roster icon. This is deliberately
    /// separate from the caller-owned settings/setup preview overload so opening those surfaces never
    /// touches PlayerControl, game body assets, overlay materials, or the overlay camera.
    /// </summary>
    internal static bool TryEnsureSpeakingBarPreviewGhost(int previewIndex, GameObject? iconRoot)
    {
        if (iconRoot == null || !SpeakingBarPreviewRoster.IsGhost(previewIndex)) return false;
        for (int i = 0; i < iconRoot.transform.childCount; i++)
            if (iconRoot.transform.GetChild(i).name == GhostBodyName)
                return true;

        if (!TryGetPreviewColorId(previewIndex, out int colorId)) return false;
        var ghostSprite = TryGetAnyVanillaGhostSprite();
        if (ghostSprite == null) return false;

        bool added = TryAddVanillaGhostBody(
            iconRoot.transform,
            ghostSprite,
            colorId,
            // The synthetic alive-body path is intentionally static, so keep its paired ghost
            // static too even when a modded palette exposes a Rainbow color id.
            isRainbow: false,
            concealed: false);
        if (!added) return false;

        ApplySorting(iconRoot);
        VCOverlayCamera.EnsureOnTop(iconRoot);
        return true;
    }

    /// <summary>
    /// Builds a small, crewmate-shaped preview chip from already-cached UI sprites. Unlike the
    /// full preview avatar path, this never performs a per-colour 100x100 texture recolour/upload,
    /// so opening a HUD editor cannot stall the UI while fifteen synthetic colours are prepared.
    /// </summary>
    internal static bool TryCreateLightweightPreview(
        int previewIndex,
        Transform parent,
        int targetLayer,
        out GameObject? iconGO)
    {
        iconGO = null;
        if (parent == null || (uint)targetLayer > 31u ||
            !TryGetPreviewColorId(previewIndex, out int colorId)) return false;

        GameObject? root = null;
        try
        {
            var chip = VoiceUiKit.Rounded(soft: true);
            Color main = Palette.PlayerColors[colorId];
            Color shadow = Palette.ShadowColors[colorId];
            root = new GameObject($"VC_SpriteIcon_PreviewChip_{previewIndex}");
            root.transform.SetParent(parent, false);
            root.transform.localScale = Vector3.one;
            root.transform.localPosition = Vector3.zero;

            AddSprite(root.transform, "VC_PreviewBackpack", chip,
                new Vector3(-0.15f, -0.01f, 0f), Quaternion.identity,
                new Vector3(0.17f, 0.27f, 1f), shadow, BodyOrder - 1);
            AddSprite(root.transform, "VC_PreviewBody", chip,
                Vector3.zero, Quaternion.identity,
                new Vector3(0.30f, 0.38f, 1f), main, BodyOrder);
            AddSprite(root.transform, "VC_PreviewVisor", chip,
                new Vector3(0.075f, 0.055f, 0f), Quaternion.identity,
                new Vector3(0.16f, 0.09f, 1f),
                new Color32(174, 231, 241, 255), BodyOrder + 1);

            ApplySorting(root);
            SetLayerRecursive(root.transform, targetLayer);
            iconGO = root;
            return true;
        }
        catch
        {
            if (root != null) Object.Destroy(root);
            return false;
        }
    }

    private static void SetLayerRecursive(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        for (int i = 0; i < root.childCount; i++)
            SetLayerRecursive(root.GetChild(i), layer);
    }

    private static bool TryGetPreviewColorId(int previewIndex, out int colorId)
    {
        colorId = 0;
        try
        {
            int count = Math.Min(Palette.PlayerColors.Length, Palette.ShadowColors.Length);
            if (count <= 0) return false;
            colorId = previewIndex % count;
            if (colorId < 0) colorId += count;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Rebuilds a speaker's avatar from the outfit captured while in-game. Used on the end-game results screen,
    // where there is no live PlayerControl to read cosmetics from. Returns false if the player was never cached
    // (e.g. never spoke during the game) — the caller then falls back to a ring + name slot.
    internal static bool TryCreateFromCache(byte playerId, Transform parent, out GameObject? iconGO)
    {
        iconGO = null;
        if (parent == null || !OutfitCache.TryGetValue(playerId, out var outfit)) return false;

        var baseSprite = outfit.Concealed
            ? GetConcealedBaseSprite()
            : outfit.IsRainbow ? GetRainbowBaseSprite(0) : GetBaseSprite(outfit.ColorId);
        if (baseSprite == null) return false;

        var root = new GameObject($"VC_SpriteIcon_{playerId}");
        root.transform.SetParent(parent, false);
        root.transform.localScale = Vector3.one * RootScale;
        root.transform.localPosition = Vector3.zero;

        bool ghostBodyReady = outfit.GhostBodyLayers.Count > 0
            ? TryAddGhostBody(
                root.transform,
                outfit.GhostBodyLayers,
                outfit.ColorId,
                outfit.IsRainbow,
                outfit.Concealed)
            : TryAddVanillaGhostBody(
                root.transform,
                outfit.GhostSprite,
                outfit.ColorId,
                outfit.IsRainbow,
                outfit.Concealed);
        if (!outfit.Concealed)
        {
            foreach (var layer in outfit.Layers)
                AddCachedCosmeticLayer(root.transform, layer, ghost: false);
            if (ghostBodyReady)
            {
                if (outfit.GhostLayers.Count > 0)
                {
                    foreach (var layer in outfit.GhostLayers)
                        AddCachedCosmeticLayer(root.transform, layer, ghost: true);
                }
                else
                {
                    foreach (var layer in outfit.Layers)
                    {
                        if (IsSkinLayer(layer.Name)) continue;
                        AddCachedCosmeticLayer(
                            root.transform,
                            ConvertLivingCosmeticToGhost(layer),
                            ghost: true);
                    }
                }
            }
        }
        ApplySorting(root);
        VCOverlayCamera.EnsureOnTop(root);
        iconGO = root;
        return true;
    }

    // ── Outfit-driven cosmetics ────────────────────────────────────────────────
    // Empty/sentinel cosmetic ids (Among Us): a slot carrying one of these has no cosmetic to render.
    private const string EmptyHatId   = "hat_NoHat";
    private const string EmptySkinId  = "skin_None";
    private const string EmptyVisorId = "visor_EmptyVisor";

    private static bool IsEmptyCosmeticId(string? id)
        => string.IsNullOrEmpty(id) || id == EmptyHatId || id == EmptySkinId || id == EmptyVisorId;

    // Typed cosmetic SpriteRenderers, read straight off the player's live CosmeticsLayer — no GameObject-name
    // matching and no idle-pose gating. These are the very renderers the player's in-world body uses, so for any
    // cosmetic the player actually wears they already hold the correct (incl. disguised) sprite + material.
    private static SpriteRenderer? HatFrontRenderer(CosmeticsLayer c) { try { return c.hat   != null ? c.hat.FrontLayer : null; } catch { return null; } }
    private static SpriteRenderer? HatBackRenderer(CosmeticsLayer c)  { try { return c.hat   != null ? c.hat.BackLayer  : null; } catch { return null; } }
    private static SpriteRenderer? SkinRenderer(CosmeticsLayer c)     { try { return c.skin  != null ? c.skin.layer     : null; } catch { return null; } }
    private static SpriteRenderer? VisorRenderer(CosmeticsLayer c)    { try { return c.visor != null ? c.visor.Image    : null; } catch { return null; } }

    // The skin's IDLE frame, from its loaded view data. Many skins (e.g. "pompousPerson") are WALK-ANIMATED, so
    // the live skin renderer's sprite cycles Walk####/Spawn#### frames while the player moves. Baking those into
    // the icon is what made the icon "morph as you move". Using the fixed IdleFrame keeps the icon a calm standing
    // crewmate regardless of the player's walk animation. Falls back to the live sprite if the idle frame is null.
    private static Sprite? SkinIdleSprite(CosmeticsLayer c)
    {
        try { return c.skin != null && c.skin.skin != null ? c.skin.skin.IdleFrame : null; } catch { return null; }
    }
    private static Sprite? SkinLeftIdleSprite(CosmeticsLayer c)
    {
        try
        {
            var clip = c.skin != null && c.skin.skin != null ? c.skin.skin.IdleLeftAnim : null;
            return SampleStaticSprite(clip);
        }
        catch
        {
            return null;
        }
    }

    private static Sprite? SampleStaticSprite(AnimationClip? clip)
    {
        if (clip == null) return null;
        int key = clip.GetInstanceID();
        if (StaticLeftSpriteCache.TryGetValue(key, out var cached)) return cached;

        GameObject? probe = null;
        Sprite? sprite = null;
        try
        {
            probe = new GameObject("VC_StaticCosmeticProbe")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            var renderer = probe.AddComponent<SpriteRenderer>();
            clip.SampleAnimation(probe, 0f);
            sprite = renderer.sprite;
        }
        catch
        {
            sprite = null;
        }
        finally
        {
            if (probe != null) Object.Destroy(probe);
        }
        StaticLeftSpriteCache[key] = sprite;
        return sprite;
    }

    // The hat/visor IDLE frames, from their loaded view data — the same morph-proofing the skin gets. Some hats and
    // visors are SpriteAnimNodeSync-animated, so the live FrontLayer/BackLayer/Image sprite cycles while the player
    // walks; baking those frames is what makes an animated hat/visor "morph as you move" on the icon. HatViewData
    // exposes MainImage (front) + BackImage (back); VisorViewData exposes IdleFrame. We read them through the
    // cosmetic layer's loaded AddressableAsset and fall back to the live sprite (null override) for static cosmetics.
    private static Sprite? HatFrontIdleSprite(CosmeticsLayer c)
    {
        try { var v = c.hat != null ? c.hat.viewAsset.GetAsset() : null; return v != null ? v.MainImage : null; } catch { return null; }
    }
    private static Sprite? HatFrontLeftIdleSprite(CosmeticsLayer c)
    {
        try { var v = c.hat != null ? c.hat.viewAsset.GetAsset() : null; return v != null ? v.LeftMainImage : null; } catch { return null; }
    }

    private static Sprite? HatBackIdleSprite(CosmeticsLayer c)
    {
        try { var v = c.hat != null ? c.hat.viewAsset.GetAsset() : null; return v != null ? v.BackImage : null; } catch { return null; }
    }
    private static Sprite? HatBackLeftIdleSprite(CosmeticsLayer c)
    {
        try { var v = c.hat != null ? c.hat.viewAsset.GetAsset() : null; return v != null ? v.LeftBackImage : null; } catch { return null; }
    }

    private static Sprite? VisorIdleSprite(CosmeticsLayer c)
    {
        try { var v = c.visor != null ? c.visor.viewAsset.GetAsset() : null; return v != null ? v.IdleFrame : null; } catch { return null; }
    }
    private static Sprite? VisorLeftIdleSprite(CosmeticsLayer c)
    {
        try { var v = c.visor != null ? c.visor.viewAsset.GetAsset() : null; return v != null ? v.LeftIdleFrame : null; } catch { return null; }
    }

    // The game's own "current cosmetic finished loading" flags. Gating readiness on these (not merely sprite!=null)
    // (a) covers back-only hats whose FrontLayer sprite is permanently null, and (b) stays false while a freshly
    // assigned outfit (morph/shapeshift/spawn) is still async-loading, so we never latch a stale previous sprite.
    private static bool HatLoaded(CosmeticsLayer c)   { try { return c.hat   != null && c.hat.IsLoaded;   } catch { return false; } }
    private static bool SkinLoaded(CosmeticsLayer c)  { try { return c.skin  != null && c.skin.IsLoaded;  } catch { return false; } }
    private static bool VisorLoaded(CosmeticsLayer c) { try { return c.visor != null && c.visor.IsLoaded; } catch { return false; } }

    // True once every cosmetic the outfit DECLARES has finished loading, i.e. nothing is still loading on the
    // player's own CosmeticsLayer. Concealed players are trivially resolved (they intentionally show no cosmetics).
    internal static bool OutfitCosmeticsResolved(PlayerControl? pc)
    {
        try
        {
            if (pc?.Data == null) return false;
            if (RenderAsConcealed(pc)) return true;
            // A fixed/meeting avatar must not mistake the fully-loaded *disguise* cosmetics for
            // the real outfit being ready. Use only a complete cache captured while undisguised;
            // otherwise keep retrying so the real cosmetics attach as soon as the disguise ends.
            if (ShowRealIdentity && IsDisguised(pc))
            {
                return OutfitCache.TryGetValue(pc.PlayerId, out var cached)
                       && !cached.Concealed
                       && cached.CosmeticsResolved;
            }
            var c = pc.cosmetics;
            if (c == null) return false;
            var outfit = GetDisplayOutfit(pc);
            return (IsEmptyCosmeticId(outfit.HatId)   || HatLoaded(c))
                && (IsEmptyCosmeticId(outfit.SkinId)  || SkinLoaded(c))
                && (IsEmptyCosmeticId(outfit.VisorId) || VisorLoaded(c));
        }
        catch { return false; }
    }

    // ── Cosmetic placement ─────────────────────────────────────────────────────
    // Living icons remain upright and use stable idle artwork, but retain every renderer transform below
    // HatParent/SkinLayer/VisorLayer. Public ghosts take a full transform path from PlayerControl to each
    // post-ghost renderer, so the body and every cosmetic share the exact coordinate system used by Among Us.
    private static readonly Vector3 HatVisorAnchor = new(-0.04f, 0.575f, 0f);
    private static readonly Vector3 SkinAnchor = Vector3.zero;

    internal static void TryAddOutfitCosmetics(
        Transform root,
        PlayerControl pc,
        List<CachedCosmeticLayer>? capture = null)
    {
        try
        {
            // Showing real identity (meeting / fixed roster) but the player is disguised: the live cosmetics layer
            // wears the morph, so rebuild the real hat/skin/visor from the outfit cached while they were undisguised.
            if (ShowRealIdentity && IsDisguised(pc)) { AddCachedRealCosmetics(root, pc.PlayerId, capture); return; }
            var c = pc.cosmetics;
            if (c == null) return;
            var outfit = GetDisplayOutfit(pc);
            Vector3 hatVisorAnchor = HatVisorAnchor + NormalBodyCosmeticOffset(c);

            // Only copy a cosmetic once its CURRENT sprite has finished loading, so a stale one is never baked in.
            bool hatReady = !IsEmptyCosmeticId(outfit.HatId) && HatLoaded(c);
            if (hatReady)
                AddFixedCosmeticLayer(
                    root,
                    "VC_HatBack",
                    HatBackRenderer(c),
                    c.hat != null ? c.hat.transform : null,
                    hatVisorAnchor,
                    BackCosmeticOrder,
                    HatBackIdleSprite(c),
                    HatBackLeftIdleSprite(c),
                    capture);
            if (!IsEmptyCosmeticId(outfit.SkinId) && SkinLoaded(c))
                AddFixedCosmeticLayer(
                    root,
                    "VC_Skin",
                    SkinRenderer(c),
                    c.skin != null ? c.skin.transform : null,
                    SkinAnchor,
                    CosmeticOrder,
                    SkinIdleSprite(c),
                    SkinLeftIdleSprite(c),
                    capture);
            if (hatReady)
                AddFixedCosmeticLayer(
                    root,
                    "VC_HatFront",
                    HatFrontRenderer(c),
                    c.hat != null ? c.hat.transform : null,
                    hatVisorAnchor,
                    FrontCosmeticOrder,
                    HatFrontIdleSprite(c),
                    HatFrontLeftIdleSprite(c),
                    capture);
            if (!IsEmptyCosmeticId(outfit.VisorId) && VisorLoaded(c))
                AddFixedCosmeticLayer(
                    root,
                    "VC_Visor",
                    VisorRenderer(c),
                    c.visor != null ? c.visor.transform : null,
                    hatVisorAnchor,
                    VisorOrder(outfit.VisorId),
                    VisorIdleSprite(c),
                    VisorLeftIdleSprite(c),
                    capture);
        }
        catch
        {
            // Degrade to body-only; the per-frame retry re-attaches as the player's cosmetics finish loading.
        }
    }

    private static Vector3 NormalBodyCosmeticOffset(CosmeticsLayer cosmetics)
    {
        try
        {
            var normal = cosmetics.normalBodySprite;
            return normal != null ? normal.normalCosmeticOffset : cosmetics.NormalCosmeticOffset;
        }
        catch
        {
            return Vector3.zero;
        }
    }

    // A visor flagged BehindHats sits under the front hat (drawn at the cosmetic order); otherwise in front.
    private static int VisorOrder(string? visorId)
    {
        try
        {
            var v = IsEmptyCosmeticId(visorId) ? null : HatManager.Instance?.GetVisorById(visorId);
            if (v != null && v.BehindHats) return CosmeticOrder;
        }
        catch { }
        return FrontCosmeticOrder;
    }

    private static void AddFixedCosmeticLayer(
        Transform root,
        string name,
        SpriteRenderer? source,
        Transform? sourceRoot,
        Vector3 canonicalRootPosition,
        int order,
        Sprite? spriteOverride,
        Sprite? leftSpriteOverride,
        List<CachedCosmeticLayer>? capture)
    {
        try
        {
            if (source == null || !source.enabled) return;
            // Prefer a fixed idle sprite over the live renderer's possibly walking frame. The transform path below
            // still retains arbitrary child pivots/scales used by layered, animated, and modded cosmetics.
            var sprite = spriteOverride != null ? spriteOverride : source.sprite;
            if (sprite == null) return;
            var transformPath = CaptureCanonicalTransformPath(sourceRoot, source.transform, canonicalRootPosition);
            var layer = new CachedCosmeticLayer(
                name,
                sprite,
                source.color,
                flipX: false,
                source.flipY,
                source.sharedMaterial,
                transformPath,
                order,
                leftSpriteOverride);
            AddCachedCosmeticLayer(root, layer, ghost: false);
            capture?.Add(layer);
        }
        catch
        {
            // Skip just this layer; the other cosmetics still attach.
        }
    }

    // The live cosmetic root already contains the world player's facing sign. The speaking-avatar root
    // owns its own left/right choice, so retaining that sign mirrors a left-facing source twice.
    internal static float CanonicalCosmeticScaleX(float sourceScaleX)
        => Math.Abs(sourceScaleX);

    private static CachedTransformNode[] CaptureCanonicalTransformPath(
        Transform? sourceRoot,
        Transform sourceLeaf,
        Vector3 canonicalRootPosition)
    {
        var relative = sourceRoot != null
            ? CaptureTransformPath(sourceRoot, sourceLeaf)
            : null;
        var result = new CachedTransformNode[(relative?.Length ?? 0) + 1];
        Vector3 rootScale = sourceRoot != null ? sourceRoot.localScale : Vector3.one;
        rootScale.x = CanonicalCosmeticScaleX(rootScale.x);
        result[0] = new CachedTransformNode(canonicalRootPosition, Quaternion.identity, rootScale);
        if (relative != null && relative.Length > 0)
            Array.Copy(relative, 0, result, 1, relative.Length);
        return result;
    }

    // Adds the player's real hat/skin/visor from the outfit cached while they were undisguised. Used when a
    // disguised player must be shown as themselves (meeting / fixed roster) but the live body wears the morph.
    private static void AddCachedRealCosmetics(
        Transform root,
        byte playerId,
        List<CachedCosmeticLayer>? capture)
    {
        if (!OutfitCache.TryGetValue(playerId, out var outfit) || outfit.Concealed) return;
        foreach (var layer in outfit.Layers)
        {
            AddCachedCosmeticLayer(root, layer, ghost: false);
            capture?.Add(layer);
        }
    }

    private static void AddCachedCosmeticLayer(Transform root, CachedCosmeticLayer layer, bool ghost)
    {
        if (layer.Sprite == null) return;
        var renderer = AddSpriteFromPath(
            root,
            (ghost ? GhostCosmeticPrefix : LivingCosmeticPrefix) + layer.Name,
            layer.Sprite,
            layer.TransformPath,
            layer.Color,
            layer.Order,
            out var layerRoot);
        renderer.flipX = layer.FlipX;
        renderer.flipY = layer.FlipY;
        if (layer.Material != null) renderer.sharedMaterial = layer.Material;
        var facing = renderer.gameObject.AddComponent<StaticCosmeticFacing>();
        facing.Init(renderer, layer.Sprite, layer.LeftSprite);
        if (ghost) layerRoot.SetActive(false);
    }

    private static CachedCosmeticLayer ConvertLivingCosmeticToGhost(CachedCosmeticLayer layer)
        => new(
            layer.Name,
            layer.Sprite,
            layer.Color,
            flipX: false,
            layer.FlipY,
            layer.Material,
            NormalizeLivingCosmeticTransformPath(layer.TransformPath),
            layer.Order,
            layer.LeftSprite);

    private static CachedTransformNode[] NormalizeGhostTransformPath(
        CachedTransformNode[] transformPath)
    {
        var result = new CachedTransformNode[transformPath.Length + 1];
        result[0] = new CachedTransformNode(
            Vector3.zero,
            Quaternion.identity,
            new Vector3(
                GhostHudNormalizationScale,
                GhostHudNormalizationScale,
                1f));
        Array.Copy(transformPath, 0, result, 1, transformPath.Length);
        return result;
    }

    private static CachedTransformNode[] NormalizeLivingCosmeticTransformPath(
        CachedTransformNode[] transformPath)
    {
        var result = new CachedTransformNode[transformPath.Length + 2];
        result[0] = new CachedTransformNode(
            Vector3.zero,
            Quaternion.identity,
            new Vector3(
                GhostHudNormalizationScale,
                GhostHudNormalizationScale,
                1f));
        result[1] = new CachedTransformNode(
            Vector3.zero,
            Quaternion.identity,
            new Vector3(VanillaBodyFormScale, VanillaBodyFormScale, 1f));
        Array.Copy(transformPath, 0, result, 2, transformPath.Length);
        return result;
    }

    // Rebuilds the living set and removes any stale post-ghost geometry (resurrection/mod role changes).
    internal static void TryRefreshOutfitCosmetics(GameObject? iconRoot, PlayerControl? pc, byte playerId)
    {
        if (iconRoot == null || pc?.Data == null) return;
        RemoveCosmeticLayers(iconRoot, ghost: false);
        RemoveCosmeticLayers(iconRoot, ghost: true);
        var ghostSprite = TryGetVanillaGhostSprite(pc);
        bool concealed = RenderAsConcealed(pc);
        int colorId = GetPlayerColorId(pc);
        ReplaceGhostBody(
            iconRoot.transform,
            DefaultGhostBodyLayers(pc, ghostSprite),
            colorId,
            !concealed && IsRainbowColorId(colorId),
            concealed);
        var capturedLayers = new List<CachedCosmeticLayer>();
        if (!concealed)
            TryAddOutfitCosmetics(iconRoot.transform, pc, capturedLayers);
        CacheOutfit(playerId, pc, ghostSprite, capturedLayers);
        ApplySorting(iconRoot);
        VCOverlayCamera.EnsureOnTop(iconRoot);
    }

    /// <summary>
    /// Rebuilds a publicly dead avatar from the live post-ghost body and cosmetic renderers. Each renderer keeps
    /// its complete transform path and sorting delta relative to the same PlayerControl root, so front/back hats,
    /// visors, animated assets, modded pivots, and alternate multi-part ghost bodies stay mutually aligned.
    /// </summary>
    internal static void TryRefreshPublicGhostCosmetics(GameObject? iconRoot, PlayerControl? pc)
    {
        if (iconRoot == null || pc?.Data == null) return;
        if (TryRestoreCachedGhostAppearance(iconRoot, pc)) return;

        RemoveCosmeticLayers(iconRoot, ghost: true);
        bool concealed = RenderAsConcealed(pc);
        int colorId = GetPlayerColorId(pc);
        bool liveGhostBodyApplied = IsLiveGhostBodyApplied(pc);
        var bodyLayers = new List<CachedGhostBodyLayer>();
        int sourceBodyOrder = CurrentBodySortingOrder(pc);
        bool capturedLiveBody = false;
        if (liveGhostBodyApplied
            && TryCaptureLiveGhostBody(pc, bodyLayers, out int capturedBodyOrder))
        {
            sourceBodyOrder = capturedBodyOrder;
            capturedLiveBody = true;
        }
        if (!capturedLiveBody)
            bodyLayers.AddRange(DefaultGhostBodyLayers(pc, TryGetVanillaGhostSprite(pc)));

        var ghostLayers = new List<CachedCosmeticLayer>();
        if (!concealed)
            TryAddLiveGhostCosmetics(iconRoot.transform, pc, sourceBodyOrder, ghostLayers);
        CacheGhostAppearance(pc, bodyLayers, ghostLayers);
        ApplySorting(iconRoot);
        VCOverlayCamera.EnsureOnTop(iconRoot);
    }

    private static bool TryRestoreCachedGhostAppearance(GameObject iconRoot, PlayerControl pc)
    {
        if (!OutfitCache.TryGetValue(pc.PlayerId, out var cached)
            || !cached.GhostAppearanceCaptured
            || cached.GhostBodyLayers.Count == 0
            || cached.Identity != GetGhostAppearanceIdentity(pc))
            return false;

        RemoveCosmeticLayers(iconRoot, ghost: true);
        ReplaceGhostBody(
            iconRoot.transform,
            cached.GhostBodyLayers,
            cached.ColorId,
            cached.IsRainbow,
            cached.Concealed);
        foreach (var layer in cached.GhostLayers)
            AddCachedCosmeticLayer(iconRoot.transform, layer, ghost: true);
        ApplySorting(iconRoot);
        VCOverlayCamera.EnsureOnTop(iconRoot);
        return true;
    }

    /// <summary>
    /// Completes a pending ghost-body transition without rebuilding the already aligned cosmetic snapshot.
    /// Among Us may finish currentBodySprite later than its cosmetic hierarchy; replacing only the body avoids
    /// a second hat/visor position sample and the visible one-frame vertical hop that sample could introduce.
    /// </summary>
    internal static void TryRefreshPublicGhostBody(GameObject? iconRoot, PlayerControl? pc)
    {
        if (iconRoot == null || pc?.Data == null || !IsLiveGhostBodyApplied(pc)) return;

        var bodyLayers = new List<CachedGhostBodyLayer>();
        if (!TryCaptureLiveGhostBody(pc, bodyLayers, out _)) return;

        bool concealed = RenderAsConcealed(pc);
        int colorId = GetPlayerColorId(pc);
        ReplaceGhostBody(
            iconRoot.transform,
            bodyLayers,
            colorId,
            !concealed && IsRainbowColorId(colorId),
            concealed);
        CacheGhostBodyAppearance(pc, bodyLayers);
        ApplySorting(iconRoot);
        VCOverlayCamera.EnsureOnTop(iconRoot);
    }

    private static void TryAddLiveGhostCosmetics(
        Transform iconRoot,
        PlayerControl pc,
        int sourceBodyOrder,
        List<CachedCosmeticLayer> capture)
    {
        // Never expose a live disguise on stable-identity surfaces. The last undisguised living snapshot uses
        // the same canonical ghost anchor; omit its skin to match CosmeticsLayer.SetGhost().
        if (ShowRealIdentity && IsDisguised(pc))
        {
            if (!OutfitCache.TryGetValue(pc.PlayerId, out var cached) || cached.Concealed) return;
            foreach (var layer in cached.Layers)
            {
                if (IsSkinLayer(layer.Name)) continue;
                var ghostLayer = ConvertLivingCosmeticToGhost(layer);
                AddCachedCosmeticLayer(iconRoot, ghostLayer, ghost: true);
                capture.Add(ghostLayer);
            }
            return;
        }

        var cosmetics = pc.cosmetics;
        if (cosmetics == null) return;
        var outfit = GetDisplayOutfit(pc);
        Transform sourceRoot = pc.transform;
        bool hatReady = !IsEmptyCosmeticId(outfit.HatId) && HatLoaded(cosmetics);
        if (hatReady)
            AddLiveGhostCosmeticLayer(iconRoot, sourceRoot, "VC_HatBack", HatBackRenderer(cosmetics), sourceBodyOrder, capture);
        if (hatReady)
            AddLiveGhostCosmeticLayer(iconRoot, sourceRoot, "VC_HatFront", HatFrontRenderer(cosmetics), sourceBodyOrder, capture);
        if (!IsEmptyCosmeticId(outfit.VisorId) && VisorLoaded(cosmetics))
            AddLiveGhostCosmeticLayer(iconRoot, sourceRoot, "VC_Visor", VisorRenderer(cosmetics), sourceBodyOrder, capture);
    }

    private static void AddLiveGhostCosmeticLayer(
        Transform iconRoot,
        Transform sourceRoot,
        string name,
        SpriteRenderer? source,
        int sourceBodyOrder,
        List<CachedCosmeticLayer> capture)
    {
        try
        {
            if (source == null || !source.enabled || source.sprite == null) return;
            var transformPath = CaptureTransformPath(sourceRoot, source.transform);
            if (transformPath == null) return;
            var layer = new CachedCosmeticLayer(
                name,
                source.sprite,
                source.color,
                flipX: false,
                source.flipY,
                source.sharedMaterial,
                NormalizeGhostTransformPath(transformPath),
                MapGhostSortingOrder(source.sortingOrder, sourceBodyOrder));
            AddCachedCosmeticLayer(iconRoot, layer, ghost: true);
            capture.Add(layer);
        }
        catch
        {
            // One malformed mod renderer must not suppress the remaining ghost cosmetics.
        }
    }

    private static int CurrentBodySortingOrder(PlayerControl pc)
    {
        try
        {
            var cosmetics = pc.cosmetics;
            var current = cosmetics?.currentBodySprite?.BodySprite;
            if (current != null) return current.sortingOrder;
            var normal = cosmetics?.normalBodySprite?.BodySprite;
            return normal != null ? normal.sortingOrder : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryCaptureLiveGhostBody(
        PlayerControl pc,
        List<CachedGhostBodyLayer> capture,
        out int sourceBodyOrder)
    {
        sourceBodyOrder = 0;
        try
        {
            var body = pc.cosmetics?.currentBodySprite;
            var primary = body?.BodySprite;
            if (body == null || primary == null || primary.sprite == null) return false;
            sourceBodyOrder = primary.sortingOrder;
            AddGhostBodyLayer(pc.transform, primary, sourceBodyOrder, capture);

            var extraParts = body.LongModeParts;
            if (extraParts != null)
                foreach (var renderer in extraParts)
                    if (renderer != null && renderer != primary)
                        AddGhostBodyLayer(pc.transform, renderer, sourceBodyOrder, capture);
            return capture.Count > 0;
        }
        catch
        {
            capture.Clear();
            return false;
        }
    }

    private static void AddGhostBodyLayer(
        Transform sourceRoot,
        SpriteRenderer source,
        int sourceBodyOrder,
        List<CachedGhostBodyLayer> capture)
    {
        if (!source.enabled || source.sprite == null) return;
        var transformPath = CaptureTransformPath(sourceRoot, source.transform);
        if (transformPath == null) return;
        Color color = source.color;
        color.a = 1f;
        capture.Add(new CachedGhostBodyLayer(
            source.sprite,
            color,
            flipX: false,
            source.flipY,
            source.sharedMaterial,
            NormalizeGhostTransformPath(transformPath),
            MapGhostSortingOrder(source.sortingOrder, sourceBodyOrder)));
    }

    internal static int MapGhostSortingOrder(int sourceOrder, int sourceBodyOrder)
        => BodyOrder + (sourceOrder - sourceBodyOrder);

    private static bool IsSkinLayer(string name)
        => name.StartsWith("VC_Skin", StringComparison.Ordinal);

    private static CachedTransformNode[]? CaptureTransformPath(Transform sourceRoot, Transform sourceLeaf)
    {
        var reversePath = new List<CachedTransformNode>();
        Transform? current = sourceLeaf;
        while (current != null && current != sourceRoot)
        {
            reversePath.Add(new CachedTransformNode(
                current.localPosition,
                current.localRotation,
                current.localScale));
            current = current.parent;
        }
        if (current != sourceRoot) return null;
        reversePath.Reverse();
        return reversePath.ToArray();
    }

    internal static bool IsLiveGhostBodyApplied(PlayerControl? pc)
    {
        try
        {
            var cosmetics = pc?.cosmetics;
            var body = cosmetics?.currentBodySprite;
            var currentSprite = body?.BodySprite?.sprite;
            if (body == null || currentSprite == null) return false;
            if (body.GhostSprite != null && currentSprite == body.GhostSprite)
                return true;

            var extraGhostSprites = body.ExtraGhostSprites;
            if (extraGhostSprites != null)
            {
                foreach (var sprite in extraGhostSprites)
                    if (sprite != null && currentSprite == sprite)
                        return true;
            }

            // The standard body asset is the authoritative fallback for mods that swap
            // currentBodySprite while retaining the normal ghost renderer.
            var normalGhost = cosmetics?.normalBodySprite?.GhostSprite;
            return normalGhost != null && currentSprite == normalGhost;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The canonical normal ghost is already rendered from normalBodySprite's stable transform before
    /// the game's delayed sprite swap completes. Only alternate or multi-part ghost bodies need replacing;
    /// replacing the canonical body sampled a different animation offset and looked like cosmetic jitter.
    /// </summary>
    internal static bool RequiresLiveGhostBodyRefresh(PlayerControl? pc)
    {
        try
        {
            if (!IsLiveGhostBodyApplied(pc)) return false;
            var cosmetics = pc?.cosmetics;
            var body = cosmetics?.currentBodySprite;
            var primary = body?.BodySprite;
            var currentSprite = primary?.sprite;
            if (body == null || primary == null || currentSprite == null) return false;

            var canonicalGhost = cosmetics?.normalBodySprite?.GhostSprite;
            if (canonicalGhost == null || currentSprite != canonicalGhost)
                return true;

            var extraParts = body.LongModeParts;
            if (extraParts != null)
                foreach (var renderer in extraParts)
                    if (renderer != null
                        && renderer != primary
                        && renderer.enabled
                        && renderer.sprite != null)
                        return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    // Stores the just-built outfit (body color + resolved cosmetic layers) so the end-game screen can rebuild
    // this avatar without a live PlayerControl. Concealed speakers cache as a grey, cosmetic-less body so the
    // end-game screen never leaks a hidden identity. Called only from the avatar (re)build path, so it runs at
    // most once per speaker per outfit change -- never per frame.
    private static void CacheOutfit(
        byte playerId,
        PlayerControl pc,
        Sprite? ghostSprite,
        List<CachedCosmeticLayer> layers)
    {
        // Never overwrite the cache with a disguise; keep the last real outfit so a meeting/roster can rebuild it.
        if (IsDisguised(pc)) return;
        // A stable-identity surface may be rendering DefaultOutfit while the live player is otherwise
        // invisible/anonymous. Do not poison the end-game/stable cache with that live concealment state.
        if (ShowRealIdentity && IsConcealed(pc)) return;
        bool concealed = IsConcealed(pc);
        int colorId = GetPlayerColorId(pc);
        var identity = GetGhostAppearanceIdentity(pc);
        bool preserveGhostAppearance =
            OutfitCache.TryGetValue(playerId, out var previous)
            && previous.Identity == identity
            && previous.GhostAppearanceCaptured;
        // Preserve the game-owned artwork and the first complete post-ghost pose while the same
        // outfit is repeatedly removed/recreated by dynamic speaking slots. Re-sampling an animated
        // hat or visor on every VAD pulse makes it jump relative to the otherwise static ghost body.
        if (ghostSprite == null && previous != null)
            ghostSprite = previous.GhostSprite;
        OutfitCache[playerId] = new CachedOutfit(
            identity,
            colorId,
            !concealed && IsRainbowColorId(colorId),
            concealed,
            concealed || OutfitCosmeticsResolved(pc),
            ghostSprite,
            concealed ? new List<CachedCosmeticLayer>() : layers,
            preserveGhostAppearance ? previous!.GhostBodyLayers : null,
            preserveGhostAppearance ? previous!.GhostLayers : null,
            preserveGhostAppearance);
    }

    private static void CacheGhostAppearance(
        PlayerControl pc,
        List<CachedGhostBodyLayer> bodyLayers,
        List<CachedCosmeticLayer> ghostLayers)
    {
        // A live disguise must never replace the stable real-identity cache used by meetings/results.
        if (IsDisguised(pc)) return;
        if (ShowRealIdentity && IsConcealed(pc)) return;
        if (!OutfitCache.TryGetValue(pc.PlayerId, out var existing)) return;
        OutfitCache[pc.PlayerId] = new CachedOutfit(
            existing.Identity,
            existing.ColorId,
            existing.IsRainbow,
            existing.Concealed,
            existing.CosmeticsResolved,
            existing.GhostSprite,
            existing.Layers,
            bodyLayers,
            ghostLayers,
            ghostAppearanceCaptured: bodyLayers.Count > 0);
    }

    private static void CacheGhostBodyAppearance(
        PlayerControl pc,
        List<CachedGhostBodyLayer> bodyLayers)
    {
        if (IsDisguised(pc)) return;
        if (ShowRealIdentity && IsConcealed(pc)) return;
        if (!OutfitCache.TryGetValue(pc.PlayerId, out var existing)) return;
        OutfitCache[pc.PlayerId] = new CachedOutfit(
            existing.Identity,
            existing.ColorId,
            existing.IsRainbow,
            existing.Concealed,
            existing.CosmeticsResolved,
            existing.GhostSprite,
            existing.Layers,
            bodyLayers,
            existing.GhostLayers,
            ghostAppearanceCaptured: bodyLayers.Count > 0);
    }

    /// <summary>
    /// Switches an already-created speaking avatar between living and post-ghost renderer sets. The caller supplies
    /// only publicly-known death state; this method never reads live IsDead and cannot reveal a task-phase death.
    /// </summary>
    /// <returns>True only when genuine ghost body artwork is active.</returns>
    internal static bool SetPublicGhostAppearance(GameObject? iconRoot, bool publiclyDead)
    {
        if (iconRoot == null) return false;

        GameObject? aliveBody = null;
        GameObject? ghostBody = null;
        var livingCosmetics = new List<GameObject>();
        var ghostCosmetics = new List<GameObject>();
        var root = iconRoot.transform;
        for (int i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);
            if (child.name == AliveBodyName)
                aliveBody = child.gameObject;
            else if (child.name == GhostBodyName)
                ghostBody = child.gameObject;
            else if (child.name.StartsWith(LivingCosmeticPrefix, StringComparison.Ordinal))
                livingCosmetics.Add(child.gameObject);
            else if (child.name.StartsWith(GhostCosmeticPrefix, StringComparison.Ordinal))
                ghostCosmetics.Add(child.gameObject);
        }

        SpriteRenderer[]? ghostRenderers = null;
        bool ghostBodyReady = false;
        if (ghostBody != null)
        {
            ghostRenderers = ghostBody.GetComponentsInChildren<SpriteRenderer>(true);
            foreach (var renderer in ghostRenderers)
                if (renderer != null && renderer.sprite != null)
                {
                    ghostBodyReady = true;
                    break;
                }
        }

        bool useGhost = publiclyDead && ghostBodyReady;
        if (aliveBody != null) aliveBody.SetActive(!useGhost);
        if (ghostBody != null && ghostRenderers != null)
        {
            var owners = ghostBody.GetComponentsInChildren<OwnedGhostBodyMaterial>(true);
            foreach (var owner in owners)
            {
                if (owner == null) continue;
                if (useGhost) owner.RefreshNow();
                owner.enabled = useGhost;
            }
            foreach (var renderer in ghostRenderers)
                if (renderer != null) renderer.enabled = useGhost;
        }

        bool hasExactGhostCosmetics = ghostCosmetics.Count > 0;
        foreach (var layer in ghostCosmetics)
            layer.SetActive(SpeakingBarGhostAppearancePolicy.ShouldShowCosmetic(
                useGhost,
                hasExactGhostCosmetics,
                ghostLayer: true,
                skinLayer: false));
        foreach (var layer in livingCosmetics)
        {
            // Before an exact post-ghost snapshot exists, reuse living hats/visors but clear the skin exactly as
            // CosmeticsLayer.SetGhost() does. Once a snapshot exists, switch the entire set atomically.
            bool isSkin = layer.name.StartsWith(
                LivingCosmeticPrefix + "VC_Skin",
                StringComparison.Ordinal);
            layer.SetActive(SpeakingBarGhostAppearancePolicy.ShouldShowCosmetic(
                useGhost,
                hasExactGhostCosmetics,
                ghostLayer: false,
                skinLayer: isSkin));
        }

        return useGhost;
    }

    private static Sprite? TryGetVanillaGhostSprite(PlayerControl? pc)
    {
        if (vanillaGhostSprite != null) return vanillaGhostSprite;
        try
        {
            // This HUD intentionally renders the canonical upright/normal body. Special Long/Horse
            // bodies require ExtraGhostSprites plus game-mode callbacks, so copying only their main
            // sprite would be incomplete. normalBodySprite.GhostSprite is the complete standard art.
            var bodySet = pc?.cosmetics?.normalBodySprite;
            var sprite = bodySet?.GhostSprite;
            if (sprite != null) vanillaGhostSprite = sprite;
            return sprite;
        }
        catch
        {
            return null;
        }
    }

    private static Sprite? TryGetAnyVanillaGhostSprite()
    {
        if (vanillaGhostSprite != null) return vanillaGhostSprite;

        // Five fake ghost slots can ask during the same update. Scan the live roster at most once
        // per frame until a normal body asset becomes available, then reuse the borrowed reference.
        int frame = Time.frameCount;
        if (vanillaGhostLookupFrame == frame) return null;
        vanillaGhostLookupFrame = frame;

        Sprite? sprite = null;
        try
        {
            // PlayerPrefab is serialized before the menu PingTracker exists, so fake ghosts work
            // on the main menu where there is intentionally no joined LocalPlayer yet.
            sprite = TryGetVanillaGhostSprite(AmongUsClient.Instance?.PlayerPrefab);
        }
        catch
        {
            // Fall through to live players for modded clients that replace the prefab late.
        }
        if (sprite != null) return sprite;

        sprite = TryGetVanillaGhostSprite(PlayerControl.LocalPlayer);
        if (sprite != null) return sprite;
        try
        {
            var players = PlayerControl.AllPlayerControls;
            if (players == null) return null;
            foreach (var player in players)
            {
                sprite = TryGetVanillaGhostSprite(player);
                if (sprite != null) return sprite;
            }
        }
        catch
        {
            // Scene transitions can temporarily invalidate the IL2CPP player collection.
        }
        return null;
    }

    private static List<CachedGhostBodyLayer> DefaultGhostBodyLayers(
        PlayerControl? placementSource,
        Sprite? ghostSprite)
    {
        var layers = new List<CachedGhostBodyLayer>();
        if (ghostSprite == null) return layers;

        // normalBodySprite owns the same GhostSprite borrowed above. Pair the artwork with that
        // renderer's complete placement path even while CosmeticsLayer is still transitioning from
        // the living frame; otherwise the temporary ghost is rendered at 2x scale for up to 120 frames.
        try
        {
            var source = placementSource?.cosmetics?.normalBodySprite?.BodySprite;
            if (placementSource != null && source != null)
            {
                var transformPath = CaptureTransformPath(placementSource.transform, source.transform);
                if (transformPath != null)
                {
                    Color color = source.color;
                    color.a = 1f;
                    layers.Add(new CachedGhostBodyLayer(
                        ghostSprite,
                        color,
                        flipX: false,
                        source.flipY,
                        source.sharedMaterial,
                        NormalizeGhostTransformPath(transformPath),
                        BodyOrder));
                    return layers;
                }
            }
        }
        catch
        {
            // Scene teardown and incomplete modded body hierarchies use the canonical prefab fallback.
        }

        layers.Add(new CachedGhostBodyLayer(
            ghostSprite,
            Color.white,
            flipX: false,
            flipY: false,
            material: null,
            new[]
            {
                new CachedTransformNode(
                    Vector3.zero,
                    Quaternion.identity,
                    new Vector3(VanillaGhostFallbackScale, VanillaGhostFallbackScale, 1f))
            },
            BodyOrder));
        return layers;
    }

    private static bool TryAddVanillaGhostBody(
        Transform root,
        Sprite? ghostSprite,
        int colorId,
        bool isRainbow,
        bool concealed,
        PlayerControl? placementSource = null)
    {
        if (root == null || ghostSprite == null) return false;
        for (int i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);
            if (child.name != GhostBodyName) continue;
            foreach (var renderer in child.GetComponentsInChildren<SpriteRenderer>(true))
                if (renderer != null && renderer.sprite != null)
                    return true;
        }
        return TryAddGhostBody(
            root,
            DefaultGhostBodyLayers(placementSource, ghostSprite),
            colorId,
            isRainbow,
            concealed);
    }

    private static bool ReplaceGhostBody(
        Transform root,
        List<CachedGhostBodyLayer> layers,
        int colorId,
        bool isRainbow,
        bool concealed)
    {
        for (int i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);
            if (child.name != GhostBodyName) continue;
            child.name = "VC_DisposedGhostBody";
            child.gameObject.SetActive(false);
            Object.Destroy(child.gameObject);
        }
        return TryAddGhostBody(root, layers, colorId, isRainbow, concealed);
    }

    private static bool TryAddGhostBody(
        Transform root,
        List<CachedGhostBodyLayer> layers,
        int colorId,
        bool isRainbow,
        bool concealed)
    {
        if (root == null || layers.Count == 0) return false;
        GameObject? ghostObject = null;
        Material? pendingMaterial = null;
        try
        {
            var defaultTemplate = CosmeticsLayer.GetBodyMaterial(PlayerMaterial.MaskType.None);
            ghostObject = new GameObject(GhostBodyName);
            ghostObject.transform.SetParent(root, false);
            ghostObject.transform.localPosition = Vector3.zero;
            ghostObject.transform.localRotation = Quaternion.identity;
            ghostObject.transform.localScale = Vector3.one;

            int rendered = 0;
            for (int i = 0; i < layers.Count; i++)
            {
                var layer = layers[i];
                if (layer.Sprite == null) continue;
                var template = layer.Material != null ? layer.Material : defaultTemplate;
                if (template == null) continue;
                pendingMaterial = new Material(template)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (isRainbow)
                    PlayerMaterial.SetColors(GetRainbowMaterialColor(GetRainbowFrameIndex(Time.time)), pendingMaterial);
                else if (concealed)
                    PlayerMaterial.SetColors((Color)ConcealedColor, pendingMaterial);
                else
                    PlayerMaterial.SetColors(ClampColorId(colorId), pendingMaterial);

                var renderer = AddSpriteFromPath(
                    ghostObject.transform,
                    $"VC_GhostBodyPart_{i}",
                    layer.Sprite,
                    layer.TransformPath,
                    layer.Color,
                    layer.Order,
                    out _);
                renderer.flipX = layer.FlipX;
                renderer.flipY = layer.FlipY;
                renderer.sharedMaterial = pendingMaterial;
                var owner = renderer.gameObject.AddComponent<OwnedGhostBodyMaterial>();
                owner.Init(renderer, pendingMaterial, isRainbow);
                pendingMaterial = null; // ownership transferred to the component
                renderer.enabled = false;
                owner.enabled = false;
                rendered++;
            }

            if (rendered > 0) return true;
            Object.Destroy(ghostObject);
            return false;
        }
        catch
        {
            if (pendingMaterial != null) Object.Destroy(pendingMaterial);
            if (ghostObject != null) Object.Destroy(ghostObject);
            return false;
        }
    }

    private static readonly List<GameObject> _cosmeticRemovalScratch = new();
    private static void RemoveCosmeticLayers(GameObject iconRoot, bool ghost)
    {
        _cosmeticRemovalScratch.Clear();
        string prefix = ghost ? GhostCosmeticPrefix : LivingCosmeticPrefix;
        var root = iconRoot.transform;
        for (int i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);
            if (child.name.StartsWith(prefix, StringComparison.Ordinal))
                _cosmeticRemovalScratch.Add(child.gameObject);
        }
        foreach (var layer in _cosmeticRemovalScratch)
        {
            layer.name = "VC_DisposedCosmetic";
            layer.SetActive(false);
            Object.Destroy(layer);
        }
        _cosmeticRemovalScratch.Clear();
    }

    // Lifecycle reset hook (called at HudManager.Start). Cosmetics are placed at fixed offsets from the live outfit,
    // so there is nothing there to clear; we only drop the rainbow color-id memo so a result that happened to resolve
    // before Town of Us finished registering its colors can never stay stale into the next game.
    internal static void ClearCache()
    {
        RainbowColorIdCache.Clear();
        OutfitCache.Clear();
        StaticLeftSpriteCache.Clear();
        vanillaGhostSprite = null;
        vanillaGhostLookupFrame = -1;
        DestroySpriteCache(BaseSpriteCache);
        DestroySpriteCache(RainbowSpriteCache);
        if (concealedBaseSprite != null) { DestroySprite(concealedBaseSprite); concealedBaseSprite = null; }
    }

    private static void DestroySpriteCache(Dictionary<int, Sprite> cache)
    {
        foreach (var kv in cache) DestroySprite(kv.Value);
        cache.Clear();
    }

    private static void DestroySprite(Sprite? sprite)
    {
        if (sprite == null) return;
        var tex = sprite.texture;
        Object.Destroy(sprite);
        if (tex != null) Object.Destroy(tex);
    }

    public static bool IsCustomIcon(GameObject go)
        => go != null && go.name.StartsWith("VC_SpriteIcon_");

    // Shared palette color for bar + meeting overlays, kept in parity with the body.
    internal static Color GetPaletteColor(PlayerControl? pc)
    {
        if (pc?.Data == null) return new Color(0.18f, 0.80f, 0.44f, 1f); // voice fallback green
        if (RenderAsConcealed(pc)) return (Color)ConcealedColor;
        // A Rainbow-colored player's palette swatch is solid black (the body only LOOKS rainbow because Town of Us
        // rewrites its material every frame). Return the SAME animated color the body icon shows this frame so the
        // ring/glow + meeting highlight cycle rainbow in lockstep instead of rendering a dead black blob.
        if (IsRainbowPlayer(pc)) return (Color)RainbowBodyColor(GetRainbowFrameIndex(Time.time));
        // Clamp via the same index the body uses so ring/glow never disagrees with the body.
        return (Color)Palette.PlayerColors[ClampColorId(GetPlayerColorId(pc))];
    }

    /// <summary>
    /// Settings rosters pair this swatch with the player's real name, so they must always use the
    /// stable default identity. Reading the live Morph/Mimic/Swoop appearance here would itself be
    /// an identity leak even when every speaking meter is correctly hidden.
    /// </summary>
    internal static Color GetStableIdentityPaletteColor(PlayerControl? pc)
    {
        if (pc?.Data == null) return new Color(0.18f, 0.80f, 0.44f, 1f);
        int colorId;
        try { colorId = ClampColorId(pc.Data.DefaultOutfit.ColorId); }
        catch { colorId = 0; }
        if (IsRainbowColorIdCached(colorId))
            return (Color)RainbowBodyColor(GetRainbowFrameIndex(Time.time));
        return (Color)Palette.PlayerColors[colorId];
    }

    internal static Sprite? GetBodySpriteFor(PlayerControl? pc)
    {
        if (pc?.Data == null) return null;
        if (RenderAsConcealed(pc)) return GetConcealedBaseSprite();
        int colorId = ClampColorId(GetPlayerColorId(pc));
        return IsRainbowColorIdCached(colorId) ? GetRainbowBaseSprite(0) : GetBaseSprite(colorId);
    }

    internal static Sprite? GetStableIdentityBodySpriteFor(PlayerControl? pc)
    {
        if (pc?.Data == null) return null;
        int colorId;
        try { colorId = ClampColorId(pc.Data.DefaultOutfit.ColorId); }
        catch { colorId = 0; }
        return IsRainbowColorIdCached(colorId) ? GetRainbowBaseSprite(0) : GetBaseSprite(colorId);
    }

    // True when this speaker picked the animated "Rainbow" color and is NOT concealed — i.e. GetPaletteColor returns
    // a time-varying color for them. The speaking bar caches a slot's color and only refreshes it on a fingerprint
    // change (which never fires for a fixed Rainbow color id), so it consults this to recompute rainbow rings live.
    internal static bool IsRainbowPlayer(PlayerControl? pc)
    {
        if (pc?.Data == null || RenderAsConcealed(pc)) return false;
        try { return IsRainbowColorIdCached(ClampColorId(GetPlayerColorId(pc))); }
        catch { return false; }
    }

    // Per-frame-safe wrapper over IsRainbowColorId: that scan walks every loaded assembly via reflection, far too
    // costly for GetPaletteColor's per-frame-per-speaker callers, so memoize by color id (reset each game).
    private static bool IsRainbowColorIdCached(int colorId)
    {
        if (RainbowColorIdCache.TryGetValue(colorId, out bool cached)) return cached;
        bool result = IsRainbowColorId(colorId);
        RainbowColorIdCache[colorId] = result;
        return result;
    }

    // Whether the speaker is hidden by the game and must render as a grey, nameless, cosmetic-less blob (never
    // leaking their real color/cosmetics/name). CurrentOutfitType (vanilla + Town of Us share the space):
    //   3=MushroomMixUp, 4=Swooper(invisible), 6=Camouflage, 8=PlayerNameOnly, 9=PlayerOnly(ghost fade).
    // Disguises (1=Shapeshift, 5=Mimic, 7=Morph) intentionally show the TARGET's look, so they are NOT concealed.
    // Some TOU concealments keep CurrentOutfitType==0 (Default) and hide identity only via the outfit fields, so
    // type 0 is additionally treated as concealed when its outfit looks camouflaged (HNS global camo stamps the
    // name "???"; Venerer camo empties all cosmetics and blanks the name). A normal type-0 player keeps a real
    // (non-empty) name, so is never falsely concealed.
    // Comms sabotage participates in speaking-attribution privacy. Dynamic speaker icons may still use this
    // neutral fallback, while fixed-roster and meeting surfaces retain their stable real identities with rings off.
    private static bool CommsConcealmentActive()
    {
        try
        {
            var snap = VoiceChatRoom.Current?.CurrentSnapshot;
            return snap != null && snap.CommsSabotageActive && VoiceRoomSettingsState.Current.CommsSabDisables;
        }
        catch { return false; }
    }

    internal static bool IsConcealed(PlayerControl? pc)
    {
        if (pc?.Data == null) return false;
        if (CommsConcealmentActive()) return true;
        try
        {
            int outfitType = (int)pc.CurrentOutfitType;
            if (outfitType == 3 || outfitType == 4 || outfitType == 6 || outfitType == 8 || outfitType == 9)
                return true;
            if (outfitType == 0)
            {
                var o = GetDisplayOutfit(pc);
                if (o.PlayerName == "???") return true;            // HNS global camouflage (only writer of "???")
                if (IsEmptyCosmeticId(o.HatId) && IsEmptyCosmeticId(o.SkinId) && IsEmptyCosmeticId(o.VisorId)
                    && string.IsNullOrEmpty(o.PlayerName)) return true; // Venerer camo: type 0, no cosmetics, no name
            }
            return false;
        }
        catch
        {
            // Fail closed: if the outfit type can't be read, treat the speaker as concealed so an
            // indeterminate state defaults to the anonymized grey body instead of leaking identity.
            return true;
        }
    }

    private static Sprite? GetConcealedBaseSprite()
    {
        if (concealedBaseSprite != null) return concealedBaseSprite;
        concealedBaseSprite = CreateBaseSprite(ConcealedColor, ConcealedShadowColor);
        return concealedBaseSprite;
    }

    internal static void SetAvatarFacing(GameObject icon, bool facesLeft)
    {
        foreach (var cosmetic in icon.GetComponentsInChildren<StaticCosmeticFacing>(true))
            cosmetic.SetFacing(facesLeft);
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

    // Proactively build + cache the base sprite for ONE not-yet-cached player colour currently present in
    // the lobby/game. Called at a low cadence from the speaking-bar overlay so the ~60-75ms avatar texture
    // build (per-pixel recolor + Texture2D upload + Sprite.Create) happens during idle lobby/task time
    // instead of the first frame that colour's player speaks — which is exactly when it was most noticeable.
    // Returns true if it built one. Cheap after every present colour is cached.
    internal static bool PrewarmNextColor()
    {
        try
        {
            var players = PlayerControl.AllPlayerControls;
            if (players == null) return false;
            foreach (var pc in players)
            {
                if (pc == null || pc.Data == null) continue;
                int colorId = ClampColorId(GetPlayerColorId(pc));
                if (!BaseSpriteCache.ContainsKey(colorId))
                {
                    GetBaseSprite(colorId); // builds + caches it now, off the speaking path
                    return true;
                }
            }
        }
        catch
        {
            // AllPlayerControls / cosmetics can be null or mid-init during scene transitions.
        }
        return false;
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

    internal static Color GetRainbowMaterialColor(int frameIndex)
        => (Color)RainbowBodyColor(frameIndex);

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
            var type = SoftDependencyTypeResolver.ResolveExact("TownOfUs.Modules.RainbowMod.RainbowUtils");
            if (type == null) return false;

            if (_townOfUsRainbowMethod == null || _townOfUsRainbowMethodOwner != type)
            {
                _townOfUsRainbowMethod = type.GetMethod(
                    "IsRainbow",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(int) },
                    null);
                _townOfUsRainbowMethodOwner = type;
            }

            if (_townOfUsRainbowMethod == null) return false;
            TownOfUsRainbowArguments[0] = colorId;
            if (_townOfUsRainbowMethod.Invoke(null, TownOfUsRainbowArguments) is not bool value)
                return false;

            isRainbow = value;
            return true;
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

    private static SpriteRenderer AddSpriteFromPath(
        Transform parent,
        string name,
        Sprite sprite,
        CachedTransformNode[] transformPath,
        Color color,
        int order,
        out GameObject layerRoot)
    {
        layerRoot = new GameObject(name);
        layerRoot.transform.SetParent(parent, false);
        Transform target = layerRoot.transform;
        if (transformPath.Length > 0)
            ApplyTransformNode(target, transformPath[0]);
        else
            ApplyTransformNode(target, new CachedTransformNode(Vector3.zero, Quaternion.identity, Vector3.one));

        for (int i = 1; i < transformPath.Length; i++)
        {
            var node = new GameObject($"{name}_Transform_{i}");
            node.transform.SetParent(target, false);
            ApplyTransformNode(node.transform, transformPath[i]);
            target = node.transform;
        }

        var renderer = target.gameObject.AddComponent<SpriteRenderer>();
        renderer.sprite = sprite;
        renderer.color = color;
        renderer.sortingLayerName = VCSorting.Layer;
        renderer.sortingOrder = order;
        renderer.maskInteraction = SpriteMaskInteraction.None;
        return renderer;
    }

    private static void ApplyTransformNode(Transform target, CachedTransformNode node)
    {
        target.localPosition = node.LocalPosition;
        target.localRotation = node.LocalRotation;
        target.localScale = node.LocalScale;
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

    private static int ClampColorId(int colorId)
        => colorId >= 0 && colorId < Palette.PlayerColors.Length ? colorId : 0;

    private static int GetPlayerColorId(PlayerControl pc)
    {
        if (ShowRealIdentity)
        {
            try { return GetDisplayOutfit(pc).ColorId; } catch { }
        }
        int bodyColor;
        try { bodyColor = pc.cosmetics.bodyMatProperties.ColorId; }
        catch { try { return GetDisplayOutfit(pc).ColorId; } catch { return 0; } }

        // bodyMatProperties briefly reads 0 (red) before cosmetics init; trust the
        // networked outfit color when it reports a valid non-zero id instead.
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

    private static GhostAppearanceIdentity GetGhostAppearanceIdentity(PlayerControl pc)
    {
        var outfit = GetDisplayOutfit(pc);
        return new GhostAppearanceIdentity(
            GetPlayerColorId(pc),
            outfit.HatId ?? string.Empty,
            outfit.SkinId ?? string.Empty,
            outfit.VisorId ?? string.Empty,
            IsConcealed(pc));
    }

    private static NetworkedPlayerInfo.PlayerOutfit GetDisplayOutfit(PlayerControl pc)
    {
        try
        {
            // Meeting or fixed-roster bar shows the stable real identity. Speaking-attribution privacy is
            // enforced separately, so keeping this avatar stable cannot light a concealed player's ring.
            if (ShowRealIdentity) return pc.Data.DefaultOutfit;
            return pc.CurrentOutfit ?? pc.Data.DefaultOutfit;
        }
        catch
        {
            return pc.Data.DefaultOutfit;
        }
    }
}

internal sealed class StaticCosmeticFacing : MonoBehaviour
{
    private SpriteRenderer? _renderer;
    private Sprite? _rightSprite;
    private Sprite? _leftSprite;

    static StaticCosmeticFacing()
    {
        ClassInjector.RegisterTypeInIl2Cpp<StaticCosmeticFacing>();
    }

    public void Init(SpriteRenderer renderer, Sprite rightSprite, Sprite? leftSprite)
    {
        _renderer = renderer;
        _rightSprite = rightSprite;
        _leftSprite = leftSprite;
    }

    public void SetFacing(bool facesLeft)
    {
        if (_renderer == null || _rightSprite == null) return;
        _renderer.sprite = facesLeft && _leftSprite != null
            ? _leftSprite
            : _rightSprite;
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

/// <summary>
/// Owns the cloned material used by a vanilla ghost body and optionally updates its Rainbow
/// palette. PlayerMaterial's Renderer overload instantiates materials internally, so this owner
/// deliberately uses the Material overload and destroys exactly the clone it created.
/// </summary>
internal sealed class OwnedGhostBodyMaterial : MonoBehaviour
{
    private SpriteRenderer? _renderer;
    private Material? _material;
    private bool _animateRainbow;
    private int _lastFrame = -1;

    static OwnedGhostBodyMaterial()
    {
        ClassInjector.RegisterTypeInIl2Cpp<OwnedGhostBodyMaterial>();
    }

    public void Init(SpriteRenderer renderer, Material material, bool animateRainbow)
    {
        _renderer = renderer;
        _material = material;
        _animateRainbow = animateRainbow;
        if (_animateRainbow)
            UpdateRainbow(true);
    }

    public void RefreshNow()
    {
        if (_animateRainbow)
            UpdateRainbow(true);
    }

    void Update()
    {
        if (_animateRainbow)
            UpdateRainbow(false);
    }

    void OnDestroy()
    {
        if (_material != null)
        {
            Object.Destroy(_material);
            _material = null;
        }
        _renderer = null;
    }

    private void UpdateRainbow(bool force)
    {
        if (_renderer == null || _material == null)
        {
            Object.Destroy(this);
            return;
        }

        int frame = CrewmateAvatarRenderer.GetRainbowFrameIndex(Time.time);
        if (!force && frame == _lastFrame) return;

        PlayerMaterial.SetColors(CrewmateAvatarRenderer.GetRainbowMaterialColor(frame), _material);
        _lastFrame = frame;
    }
}
