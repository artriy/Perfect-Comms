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
    private const float BasePixelsPerUnit = 32f;
    private const int BodyOrder = VCSorting.Base - 3;
    private const int BackCosmeticOrder = VCSorting.Base - 2;
    private const int CosmeticOrder = VCSorting.Base - 1;
    private const int FrontCosmeticOrder = VCSorting.Base;
    private const int RainbowFrameCount = 48;
    private const float RainbowHueSpeed = 0.3f;
    private const string BaseCrewmatePngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAGQAAABkCAYAAABw4pVUAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsEAAA7BAbiRa+0AABUTSURBVHhe7Z0LlB9Vfcd/M/99b14mEB4KJJCYZHfNCe+YYM9RKVIKVA5VS4s1lJQe6oGWh3porS0iYIUeC5xCUaQptAEVqIJteUhFsRFIiGiSjYZAIJKEkGwe+/rv/l/T73ce2fnfvTM7r///v+nhs+f3n5k7M3fu3N/93ffclXd5l3d5l8MHw90eJvQsws+ZIlYPtidCZuMVjuaZaiy+1zBkJ/bfEDF/I1LpxXabyMaf25dMUg4DhXQtQ6RegQj9lEhTmxNkyzllo3sF//lxlEUKW+DXauw/KrJ5s+M8OZjECun+Xfx8DUHsco5rARVXgPXIP4o03yfS2287N5BJqJBFRyFYSL3mhxFhCF8tg+i3JKsCK/wKFHSryOsjrmPdmWQKef/ZSKnfQ7A6cBAhbGMR6r9Yn2FFeVXeaeGncI3I1jsct/oyiRSy4Hzk648hSE2IFIQrOGg5RNzxZlnmGkXpyRVlfq4ksw0UDS6M1hJkc7lZNkG2Vppkc6UFhcdEr+tXZRFlS9NyZGP7XYe6MEkU8v6zYBk/xE6rPkhORC0wC3JF67Bc1JyXuSZymBgU4MV3i+2yqtApPyq1RlAOKUGvxY+JvPY/rkPNmSQK6R7AzxRnf3yQTjFH5eb2g3Juy6jjYOsnftB5G+VAxZDrRmbIo4V2GRDTPheMfcflIjvud45rS87dNpCFdyEYyBoMxHB1JL9PivLElD1yS/uAzMvRInjek/h4d7bj5+PNI3J164DsgQuztWCLse/4PfxAc0PP2U41JCgUdeIkFN5tzKNb1KBc2jIk93Xsl1YDKTShRUyEZzHvVEw5f+gIebmMYATCK3f/mcjebzjHtWEie60xbd9BRPuU4UTRVDlD7m0fopZwyHO1STf0lRFwFCoEazp3y6VNg7a7Hl599L0iHSjvakeDFSLnulsfc+TGtqnSZhTc4/qQQ3yv6twvj3fusWtxwRyDykfbLPcgc2qT9CLRdQPSw83YqQpDTp6S7dM+Kseab7suCbDj00AJZMk+1Mb2mZYMI+tzqwQTsgvp9PcHjoA/SK8suuwtgwmxLXb3QyL7/pDXZk2DFNKF56IhobQ3DJkHhTwq+emzpcnXrojD20ZFNubKcnvHiDzdUh5L60nzAp2xDKPoax7+LPa+KW9A7xnSKIXgjUxk2GoD8E45v3lUnuj8A+xPHDQvrgx4sx4NxKumDsua1njtk1TA8CCXQ74vr0nedU1Fo8qQv3K3CqfKsqYX3P1o9CMrunB6v5w2a1DWtNRRGcSQDsTgasgbMO6TXddUNMhCejQZwRzI47J56hmyILc1NGD2zfjZglxv2cwBlBE4jvIm/qdm/+YWypsVslUecI8T0QAL6Wp1dxSusn/bI1r+ICzjghmDjjImgs3fNshUyHQI60ieoOyuOvYLrw9rmqiYssqYZ1zqHiWiARbSMxc/rzv7fthdNEu2TVssJ5g7QgNWQko/a0a/vMgsyn+hZwFU+SmQZZBPQt4DCYM5HaTVapWLcxfLEmNJlTU9ZDwk21/ZLvm1ecm/mBdrPU4O4QSvGZ8gLBTz3bJNEg18NUAhXffjLS5zD3y8CGmT3dPmyZHm/tCA3dKel7+e4lZiVYUcA/l7OC81cOiL1TBw2ZTyFNlj7ZGWXItUKhUx2JPjwzvegL/ri9fLc795TkqXldDMt539MOtiQXgWsq/YhVoDsixzhbvjw8ALM09hgEIiEafKkFs7oAzGjxdnvIXyUch5kKU8DPFHw1eNr0qz2SyWZY1TBqE7pcfqkSebnpT8nLzMf26+yHKcrI5FA8cfxO9H3ONY1FkhPQHlx5UQJyhtRnjz7c62ERnUhZrZ02xIt30Um9UcpIxJr9Urs+/DQ1neqBjyt+5eLOptIc3uVuESe5zOMMKDw1bLY+1oh6kJ+CjICc6uHTnxjMNmLf6KVtHOrrwsS2cpHt75D1gfEPkt17Ga5XKiaGbEhFNHhfScjp+znX2VJnc7MVvsbniF490tmeluY1LEX4fRIV80vijPGs/KFvxFYZexS+Qa90DFCFBVCDVSyEJkIN0PQVDqdZchjEWW2o/Zp8fhDMvQSjqN/DgD8GCz+J2cJvkzq/JIMuDKB0Is05LbcrfJebnzZJGxSHJGzi5XWswWWW2tlhFr5FBZQm7BX6/R61Sd/WHwMOM3FoNtMjZdJ8K7VTDjZQivp+iI/nPummMl1ozgOuomWEfPLM1MnU9BvCexUq2pNqQGSQqZFELZZG+ZXdGqKoZrsaxMvObsHsKSp+RVXY92MBlYyMIFUMZuBBc2bnwIyog5CsnWV9RbNNbRCfGrfRvkl85uptCC0BgtGkUpGAUZReXjkDKI/hU4uzIWKRWyENrPbYA3nNLpDxKjKKJ1oFDMmv+AZD0f0Xsj3Zvtg+g7GGLHb0qF5NgE85XITMFhokI3v0U7rQfdlYSjuZFgwv02hI1/9uL7pRbwOWq9JGpYFVIopOskRNFi7FSlFzbsfrspL7e1HZBHOvbKlqm75IH2PjndDGpfnAZxvDg59wt7G8SCiia4Qd4yQn4MuR3CbKxGGO8g7CjX7axTRbWkCKRQCCvg1dzUekAGpu+Up6f0yfVtg3Jxy4jMz5Xl0ta8PDNlr3uVCuupTnJqRiEZhomGSCdTvz/1cUZc2MRPzoF/EELFPE8HDfQvyIJ4zv88P8/g1D/jJO/TXWPJm+5eZFIoxKjqGlicK8gNbUP2FBsnaVRLUfsonhsrevx36KD7THsoVYF5+ERQMT+CfBnyD5DvQl6An4Wgp1Vj7MV1nAR0N+TrkJsgP4V4SmTlb7xXb7jbyEQLjZYuFJvmQvdA7mg/IFe1OrM2VE+ZePqQ3RzZf6zjUAWrvFSKIWfm1soLU8+xXXWw+r/0Pf3yUrNdBx2DVd0znd1EsP+A/rEcUANPo6UEWQnhucch/oKdbpaslK3yLcchGgktpGsKbkV1d4xPNA/b76K+j4evgqgQIwjwfE4FylMfwoyBFpAURjgnudAPdqv7he5hyiDrIbpalmkX97FImmXBMtiz5HAUQjzTrLiJQmRL6Xi5c3SlXDl8uy23j35W+mSafe14glQ4Hvp9fgHJWY0gapv9ABNFXC3YCHnV2a1iFLaxJX51InpsVNF1AW6FkTq3n2yW5Z4pbfKFoTtkQ6VL9llq9ydjbA9E7criKNIqZxd+TZRl0Rd2n8w84qAUdUmJo8BLnd2aw7KDlYSg2UoH5I/lHbs6EYuEFmJ4fas2vZUzZWn/Wvlx+SwoQ9e7R8Ul1L0P+tCJ33MKyOypHdUimB6fgFD3uvNZwBrdGgh75XbRQYHP7JedSZRBkipkvrtjM2qPkdKroIgPU0jYuWq8K28bbJcmXWTzJPN95tyU7ZAsYBm1FvJfkO+5x7QQXbBZlvTJnzgH8UmoEKtKIdX93zoYe7p5s97gRbykvAAF+70D7e6RBnrHZs/PIKzePgn5X8gmyFuQAwHCnmJG9ksQVnH/G/IwhP6w4/AgJCiojElWCnbJalQSnrLdEpBQIWqnmVdvDGOru/UTu+/tECsKrXLTkDPsGwpTMiObn3ZugLDtQAXphNHIyOcUDJYNVIAfz0R1r7rnSCi74xU8789dl0QkzbKqyhCHeKncofoewyrbY+beeEMQXpzcMNQqj+7vkHavvEgShKSwh4Ay2Inm3zxkUxwUmcP+AFWNsUigkC4OS7rfi2fBWJJbV1kSKU69OygXohq8qW+qXD3Uoi9XMoZjIeSkEWSZO5Fz70TVrkBLZVQmzXDGSOKDZpz4OHebjlLQkHsAjBq+wAllU+4Y6pC3+qbJqv52OXsEjUe/xXAbpdblXee3OFdOLJmyMt8ijx3okH17pst1+1H+jXDOhqMgF6VsjU+Vb9HoPhe3sbjzQUv9mLOrhW/FbzqvtY/GuALizFh0qMjQ9GOl3RhNErAx8LiDhiW/yJXkLZjNy01l+RWkD277TYYlGPaVHYk271w0dBaVc7IQijgRlYhj4WYiVF4KXlNqluWDHLf1h7R4l8ivr3YPEpHgvXv4XcS/O/se2Slk//Q5Mt0YSK2QQxt6hDLJ3tjH0XxWx168fgnv7t0VU47u56w8z4U3jD6CyssnnONkJMmy3utua8KIFTB1Kw6MIwjj3t61d5xxcPfUhKI6eLses+3Psv0uhCOn6UiikPe5WwUlSSVk0NKN9Ew+WMGaruojAxIoxHK/J68N2yqaGvUkpN/qkOGqbiJbO/FqJRqSWAi78BSyi8THi5xPM/l5vHAhGuRcksWDOUSJHzukIomF+GeXuOhGdpLxcPEid29y80iRq0ep0ZfjnKZUJFCIbgW3KFSNZwVgyF7rCDlQSZ3QagbtgLKhzFndLNj9GDPcncTEVghqKkmUCAnpDDwErzNluxVQb5gkvFU5Rt6y2Bj2K8TOIRLETTWpPUgHe/28NDfG3aP8nme8++TAki/nP4dfNers8Gqy83g0WCG6HmCRbxRWyFAki6o/bNI8WeSEG1qEbRU+7E6tVCRQSD1SrSHri4snpX1sKZ0kO8SbPaOGUC1T4pORhaSJOl1KM+Tq/Nekkj4HyJwVw3fjbb15o1zmy0N9h2TEVohlWbvdXR8c/UmKnfc6uz5eqSyWl8qnBJxtAAjEsNUmv7RrVx7eLDmSTSiTWIhmNm34FNBgfu1uVRyr+aPh++xImBRYpnw+fyPKNnZUeNagTgazUi9hlEQhmtiPMrVPZ9KckRDMtspxcnP+WlglP3HOKg0mg1NhHyj4O3J/5W79lHTzUGKRQCGmZi5HlDnFQeWBYw16LLm1cK3cOfqnKE+CrqkPfzFyE0oM/2Q/XTadPowJFGKx8aAQbTkMPWFp33nBa0ZulQcLn2yIpfBZDxcuknsKKx2HQ+gUUowy7TuU2ApBPVwzV49za8JSB88FnQ+rKjJ4tCxLLsv/k3wp/wUZsvwderXn24WPoyy7F3usWfmtnB+FqORSf4kSWyGWpZt+xoSRNN1Guc9R5s2Fz0nPwBpZVzpZyihkOTmFdyd9sornF6WEyL9p5Dq5ZPhbSDK67FZnIZX6KwTQHBQ4wyyraNHhWYopb1ZOkDMGfwh5BlXQLhmx4izXMzFFtH1+UDxHjjjwqnxphMt68dm6aNLk3JJLU/+3SaIQluCIfb8CNE2TKsKUlUyR68tLZMngT+W4gc3yd8jKtpbnyCCzM9tsHLG/Kce1wTL2u68yQ54vLZU5gxvkwqGH5KC9jlMYun+k0MEJp6kIy/hD6GY5wv9i4BzafB8SNBORr8yppFyQRIXvMG46TUzov7M9AZXAi5r/U04zX5HTcuulLeDbxlFYFts4PyudLvcXPi3rKqe6Z6KEgwO46poADIOBONk4fn2gGCRVCGP/wrHbGRjOKAmaY8zzbHN80D6qJluFjEfnr/86b9/LLKKEgwU6VyvwQ7PclCTHqSKpBz9xty58CX7yGgTPB71o+g65Mf/5Oqp45/ziP8+yieKdi4LuI/jRsFWYI8MQJcBCslZnLnFquTe+oaJzO1zhu+gWVClGaR1PSEKF9MJCyv6uTsC8+kZIUOT/f1KK7sOTMj98SE1ChRDzLieS/RHN2Yk0Z9WdBM2QySLLSosXXn+Yde9A6KbrFG3lVyWpSaEQ+VcEThNiLoL8HQg7PtnpSGGtJKhHOE7eXUuYMLhwGbcMb9jnt7pPfttecXdSkTImur+Jn5UTe+O9mO46rheZdLaiP8L+DcIvcvg1F4XjFvyAPQxG/DoIc19+A8eKCSOb4bwE8nmImmapMH68yg8ZCcNA6c0kVaX0pGcW8k603HMpBi1Yg2bEJQmKp5BbIPz2LCv45ahmPqANFcKvh73BKYYhj9rM6xMtRhuJNFkW2NgHZSxxDxKSurcBZPXPOzk19OfItoKUQWhR/pFCMrzT3UlNSoWQjSjhKkgd5S1j5lsvaFWUpP8P0gsv5XIIF0Np0paMY/AzXJUKv1DMhAwUQnphssYi5MVdIoMxU0vQ+kr1YB7kGcg6WMVfYhslOnTL1XUGrCUZn4wUQnqRuW5HnffN9yLF0F+U1Pa3BdOx5RTL2diiQakmv6yXfiP2M7ykr4HrcHCBEqZ2fnTTNoFV+NE1CtuiLWEagQwV4qcXr7cR1ZVNkI3ITzYdxBbVEuNZ94J6sBwJI2BZWk7CS/LlAPvj1OyxjPrxZq/KlZoaKSQQjUJYa6kFu+bj9Zi/aCwlfHKFHhbkXINJZTSr9SJs6q0Q5BNqH5huKDQrNnI9B03qTdrt9AN366f0tLuTCXVWiKVpruvn96aDNa99bn2a5ZbKy+42Ooa96OoO56CKkfCFImNSbwth01ghaC3GtPDfcROD3dAKXF8jCboe9uZMy8V6KySgwEhajnjFg3bRKm+9Q009lfPZNEVLKKyV+avovHcUAd+prmedijorhP8qT0fagl0bsW5BUdG0i+JbpWWxe0Z9TiGzFrpHnRWyQZNlkQA9TYjXUlfxF1WWZvIas55/gUxsIc4n7ux45Dp+6rNGYq+pOBH1zrKApcnAA/QUCc2sJKn6rzABtYY7IVwzNvzfKVjWbZDrsKezYivzf1SsS141pvvreCz7KXw8D0nyvSRTOIsI9R+jjSJPevVI9wB0/QRp70PugQLTJL9pVKf9UKfM9YK6dtih+Hrmq1o0wEJ0syHS9NZqZxAqyd681d3RwJTPth3HUvzC3pCwfraDF7g7mdKILIu9eQpcXIipfeI8vRoaOGud3n2eH6bSt2R32TC/j/sADfRi11dE+ljtypxGWIimwKBCwlJjEPdAdM0Ai4P7PjYUYAkXwT1lJLKy8CYsvO9vnOPsaUAZ4v9P0cQLAmc1fgY1mqUoRNUC1IQ72w57cI5dLWx8s7WtFsiHLGW+SK+mMO/mjDwO+nPYeflYGPx4Tn5jYgfiQbQ3+lC6F7hIbKpl/MLQBKgedPP/qLv/oNgfBC8SuH6hP0I44UCZdaQNOu+xnoQyfsc5DqMHhb41DTITfs2CKAV0BSZrcPBth8jmzNsbQTRIIV3IKi3EsNmRTRCoCHZaWg8iW7kSBbJuWshhQSPKEMDBrNxcRKCuEZGAytvw69Mimz5zOCuDNEghhLPEe49DRK6AsLC185tDmyo8N1VG0YCpfFikBQ2JTcqyg4cnDcqydHTNQ3CWQk5FRWwhCtLjEenucrSMfFLuhwK2QQGPiDQ/DYXWqqu4QYj8HwweNyYvNBQWAAAAAElFTkSuQmCC";

    private static readonly Dictionary<int, Sprite> BaseSpriteCache = new();
    private static readonly Dictionary<int, Sprite> RainbowSpriteCache = new();
    // Memoized "is this color id the animated Rainbow color?" so the per-frame ring/glow/highlight color path
    // never repeats IsRainbowColorId's assembly-scanning reflection. Cleared per game by ClearCache().
    private static readonly Dictionary<int, bool> RainbowColorIdCache = new();
    private static Color32[]? templatePixels;
    private static int templateWidth;
    private static int templateHeight;
    private static Sprite? concealedBaseSprite;
    // Neutral grey for concealed players: grey body, no cosmetics, no name.
    private static readonly Color32 ConcealedColor = new(0x7f, 0x7f, 0x7f, 0xff);
    private static readonly Color32 ConcealedShadowColor = new(0x4a, 0x4a, 0x4a, 0xff);

    // End-game avatar cache: the results screen has no live PlayerControls, so we snapshot each speaker's
    // resolved outfit (body color + the actual cosmetic sprites/materials, which are addressable assets that
    // survive the scene change) while in-game and rebuild the avatar from it on the end-game screen.
    internal readonly struct CachedCosmeticLayer
    {
        public readonly string Name;
        public readonly Sprite Sprite;
        public readonly Color Color;
        public readonly bool FlipY;
        public readonly Material? Material;
        public readonly Vector3 LocalPos;
        public readonly int Order;
        public CachedCosmeticLayer(string name, Sprite sprite, Color color, bool flipY, Material? material, Vector3 localPos, int order)
        {
            Name = name; Sprite = sprite; Color = color; FlipY = flipY; Material = material; LocalPos = localPos; Order = order;
        }
    }
    private sealed class CachedOutfit
    {
        public readonly int ColorId;
        public readonly bool IsRainbow;
        public readonly bool Concealed;
        public readonly List<CachedCosmeticLayer> Layers;
        public CachedOutfit(int colorId, bool isRainbow, bool concealed, List<CachedCosmeticLayer> layers)
        {
            ColorId = colorId; IsRainbow = isRainbow; Concealed = concealed; Layers = layers;
        }
    }
    private static readonly Dictionary<byte, CachedOutfit> OutfitCache = new();

    // Set per-frame by the speaking-bar overlay. True when the bar is a roster (fixed all-players mode), where
    // the avatar should show each player's real identity, never their live in-world disguise -- same rule as a
    // meeting. A meeting is always treated as real-identity regardless of this flag.
    internal static bool PreferRealIdentity;

    private static bool ShowRealIdentity => PreferRealIdentity || MeetingHud.Instance != null;

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
        // Cosmetics are built straight from the player's live outfit (hat/skin/visor), so they render
        // immediately and reliably — no idle-pose capture, GameObject-name matching, or fingerprint gate.
        var capturedLayers = new List<CachedCosmeticLayer>();
        if (!concealed)
            TryAddOutfitCosmetics(root.transform, pc, capturedLayers);
        CacheOutfit(playerId, pc, capturedLayers);
        ApplySorting(root);
        VCOverlayCamera.EnsureOnTop(root);
        iconGO = root;
        return true;
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

        var bodyRenderer = AddSprite(root.transform, "VC_Body_Base", baseSprite, Vector3.zero, Quaternion.identity, Vector3.one * BodyScale, Color.white, BodyOrder);
        if (outfit.IsRainbow) AddRainbowBodyAnimator(bodyRenderer);
        if (!outfit.Concealed)
        {
            foreach (var layer in outfit.Layers)
            {
                if (layer.Sprite == null) continue; // cached asset was unloaded; skip just this layer
                var sr = AddSprite(root.transform, layer.Name, layer.Sprite, layer.LocalPos, Quaternion.identity, CosmeticScale, layer.Color, layer.Order);
                sr.flipX = false;
                sr.flipY = layer.FlipY;
                if (layer.Material != null) sr.sharedMaterial = layer.Material;
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

    // The hat/visor IDLE frames, from their loaded view data — the same morph-proofing the skin gets. Some hats and
    // visors are SpriteAnimNodeSync-animated, so the live FrontLayer/BackLayer/Image sprite cycles while the player
    // walks; baking those frames is what makes an animated hat/visor "morph as you move" on the icon. HatViewData
    // exposes MainImage (front) + BackImage (back); VisorViewData exposes IdleFrame. We read them through the
    // cosmetic layer's loaded AddressableAsset and fall back to the live sprite (null override) for static cosmetics.
    private static Sprite? HatFrontIdleSprite(CosmeticsLayer c)
    {
        try { var v = c.hat != null ? c.hat.viewAsset.GetAsset() : null; return v != null ? v.MainImage : null; } catch { return null; }
    }

    private static Sprite? HatBackIdleSprite(CosmeticsLayer c)
    {
        try { var v = c.hat != null ? c.hat.viewAsset.GetAsset() : null; return v != null ? v.BackImage : null; } catch { return null; }
    }

    private static Sprite? VisorIdleSprite(CosmeticsLayer c)
    {
        try { var v = c.visor != null ? c.visor.viewAsset.GetAsset() : null; return v != null ? v.IdleFrame : null; } catch { return null; }
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
            if (IsConcealed(pc)) return true;
            var c = pc.cosmetics;
            if (c == null) return false;
            var outfit = GetDisplayOutfit(pc);
            return (IsEmptyCosmeticId(outfit.HatId)   || HatLoaded(c))
                && (IsEmptyCosmeticId(outfit.SkinId)  || SkinLoaded(c))
                && (IsEmptyCosmeticId(outfit.VisorId) || VisorLoaded(c));
        }
        catch { return false; }
    }

    // ── Fixed cosmetic placement ────────────────────────────────────────────────
    // Cosmetics are dropped onto the upright icon body at FIXED offsets — we never copy the player's live
    // cosmetic transform, which Among Us animates every frame while walking (bounce/lean/flip). That live
    // transform is what made the icon morph with movement; fixed offsets make the icon identical whether the
    // player stands still or runs. We still read the LIVE sprite/material/color so modded/Adaptive cosmetics show.
    // TUNABLE: raise/lower HatVisorAnchor.y if hats sit too high/low; change CosmeticScale if cosmetics look too
    // big/small; nudge SkinAnchor if the skin sits off the body.
    private static readonly Vector3 HatVisorAnchor = new(-0.045f, 0.575f, 0f); // head anchor for hat (front+back) & visor
    private static readonly Vector3 SkinAnchor     = Vector3.zero;             // skin overlays the body
    private static readonly Vector3 CosmeticScale  = Vector3.one;              // cosmetic size on the icon body

    // Copies hat (back + front) / skin / visor from the player's live cosmetics onto the icon at FIXED offsets,
    // preserving each cosmetic's real sprite + material + color so Adaptive (color-matched) cosmetics stay correct.
    internal static void TryAddOutfitCosmetics(Transform root, PlayerControl pc, List<CachedCosmeticLayer>? capture = null)
    {
        try
        {
            // Showing real identity (meeting / fixed roster) but the player is disguised: the live cosmetics layer
            // wears the morph, so rebuild the real hat/skin/visor from the outfit cached while they were undisguised.
            if (ShowRealIdentity && IsDisguised(pc)) { AddCachedRealCosmetics(root, pc.PlayerId, capture); return; }
            var c = pc.cosmetics;
            if (c == null) return;
            var outfit = GetDisplayOutfit(pc);

            // Only copy a cosmetic once its CURRENT sprite has finished loading, so a stale one is never baked in.
            bool hatReady = !IsEmptyCosmeticId(outfit.HatId) && HatLoaded(c);
            if (hatReady)
                AddCosmeticLayer(root, "VC_HatBack", HatBackRenderer(c), HatVisorAnchor, BackCosmeticOrder, HatBackIdleSprite(c), capture);
            if (!IsEmptyCosmeticId(outfit.SkinId) && SkinLoaded(c))
                AddCosmeticLayer(root, "VC_Skin", SkinRenderer(c), SkinAnchor, CosmeticOrder, SkinIdleSprite(c), capture);
            if (hatReady)
                AddCosmeticLayer(root, "VC_HatFront", HatFrontRenderer(c), HatVisorAnchor, FrontCosmeticOrder, HatFrontIdleSprite(c), capture);
            if (!IsEmptyCosmeticId(outfit.VisorId) && VisorLoaded(c))
                AddCosmeticLayer(root, "VC_Visor", VisorRenderer(c), HatVisorAnchor, VisorOrder(outfit.VisorId), VisorIdleSprite(c), capture);
        }
        catch
        {
            // Degrade to body-only; the per-frame retry re-attaches as the player's cosmetics finish loading.
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

    // Drops one live cosmetic sprite onto the icon at a FIXED local offset (never the live walk-animated transform,
    // so the icon can't morph as the player moves). Reads the live sprite/material/color so modded/Adaptive
    // cosmetics show. Forces upright + canonical right-facing. Skips disabled renderers (stale hidden BackLayer).
    private static void AddCosmeticLayer(Transform root, string name, SpriteRenderer? src, Vector3 localPos, int order, Sprite? spriteOverride = null, List<CachedCosmeticLayer>? capture = null)
    {
        try
        {
            if (src == null || !src.enabled) return;
            // Prefer a fixed idle sprite (e.g. the skin's IdleFrame) over the live renderer's current — possibly
            // walk-animated — sprite, so the icon doesn't change pose as the player moves.
            var sprite = spriteOverride != null ? spriteOverride : src.sprite;
            if (sprite == null) return;
            var layer = AddSprite(root, name, sprite, localPos, Quaternion.identity, CosmeticScale, src.color, order);
            layer.flipX = false;                   // canonical right-facing icon body
            layer.flipY = src.flipY;               // preserve the cosmetic's intrinsic vertical orientation
            if (src.sharedMaterial != null) layer.sharedMaterial = src.sharedMaterial;
            // Record the resolved layer so the end-game screen (no live PlayerControl) can rebuild this avatar
            // from cached sprites/materials, which survive the scene change.
            capture?.Add(new CachedCosmeticLayer(name, sprite, src.color, src.flipY, src.sharedMaterial, localPos, order));
        }
        catch
        {
            // Skip just this layer; the other cosmetics still attach.
        }
    }

    // Adds the player's real hat/skin/visor from the outfit cached while they were undisguised. Used when a
    // disguised player must be shown as themselves (meeting / fixed roster) but the live body wears the morph.
    // Falls back to no cosmetics (real-colour body only) if we never captured them undisguised this game.
    private static void AddCachedRealCosmetics(Transform root, byte playerId, List<CachedCosmeticLayer>? capture)
    {
        if (!OutfitCache.TryGetValue(playerId, out var outfit) || outfit.Concealed) return;
        foreach (var layer in outfit.Layers)
        {
            if (layer.Sprite == null) continue;
            var sr = AddSprite(root, layer.Name, layer.Sprite, layer.LocalPos, Quaternion.identity, CosmeticScale, layer.Color, layer.Order);
            sr.flipX = false;
            sr.flipY = layer.FlipY;
            if (layer.Material != null) sr.sharedMaterial = layer.Material;
            capture?.Add(layer);
        }
    }

    // Rebuilds the cosmetic layers in place on an existing icon (no body destroy/recreate). Concealed -> none.
    internal static void TryRefreshOutfitCosmetics(GameObject? iconRoot, PlayerControl? pc, byte playerId)
    {
        if (iconRoot == null || pc?.Data == null) return;
        RemoveCosmeticLayers(iconRoot);
        var capturedLayers = new List<CachedCosmeticLayer>();
        if (!IsConcealed(pc))
            TryAddOutfitCosmetics(iconRoot.transform, pc, capturedLayers);
        CacheOutfit(playerId, pc, capturedLayers);
        ApplySorting(iconRoot);
        VCOverlayCamera.EnsureOnTop(iconRoot);
    }

    // Stores the just-built outfit (body color + resolved cosmetic layers) so the end-game screen can rebuild
    // this avatar without a live PlayerControl. Concealed speakers cache as a grey, cosmetic-less body so the
    // end-game screen never leaks a hidden identity. Called only from the avatar (re)build path, so it runs at
    // most once per speaker per outfit change -- never per frame.
    private static void CacheOutfit(byte playerId, PlayerControl pc, List<CachedCosmeticLayer> layers)
    {
        // Never overwrite the cache with a disguise; keep the last real outfit so a meeting/roster can rebuild it.
        if (IsDisguised(pc)) return;
        bool concealed = IsConcealed(pc);
        int colorId = GetPlayerColorId(pc);
        OutfitCache[playerId] = new CachedOutfit(
            colorId,
            !concealed && IsRainbowColorId(colorId),
            concealed,
            concealed ? new List<CachedCosmeticLayer>() : layers);
    }

    private static readonly List<GameObject> _cosmeticRemovalScratch = new();
    private static void RemoveCosmeticLayers(GameObject iconRoot)
    {
        _cosmeticRemovalScratch.Clear();
        var t = iconRoot.transform;
        int count = t.childCount;
        for (int i = 0; i < count; i++)
        {
            var child = t.GetChild(i);
            var n = child.name;
            if (n.StartsWith("VC_Hat") || n.StartsWith("VC_Skin") || n.StartsWith("VC_Visor"))
                _cosmeticRemovalScratch.Add(child.gameObject);
        }
        foreach (var go in _cosmeticRemovalScratch) Object.Destroy(go);
        _cosmeticRemovalScratch.Clear();
    }

    // Lifecycle reset hook (called at HudManager.Start). Cosmetics are placed at fixed offsets from the live outfit,
    // so there is nothing there to clear; we only drop the rainbow color-id memo so a result that happened to resolve
    // before Town of Us finished registering its colors can never stay stale into the next game.
    internal static void ClearCache() { RainbowColorIdCache.Clear(); OutfitCache.Clear(); }

    public static bool IsCustomIcon(GameObject go)
        => go != null && go.name.StartsWith("VC_SpriteIcon_");

    // Shared palette color for bar + meeting overlays, kept in parity with the body.
    internal static Color GetPaletteColor(PlayerControl? pc)
    {
        if (pc?.Data == null) return new Color(0.18f, 0.80f, 0.44f, 1f); // voice fallback green
        if (IsConcealed(pc)) return (Color)ConcealedColor;
        // A Rainbow-colored player's palette swatch is solid black (the body only LOOKS rainbow because Town of Us
        // rewrites its material every frame). Return the SAME animated color the body icon shows this frame so the
        // ring/glow + meeting highlight cycle rainbow in lockstep instead of rendering a dead black blob.
        if (IsRainbowPlayer(pc)) return (Color)RainbowBodyColor(GetRainbowFrameIndex(Time.time));
        // Clamp via the same index the body uses so ring/glow never disagrees with the body.
        return (Color)Palette.PlayerColors[ClampColorId(GetPlayerColorId(pc))];
    }

    internal static Sprite? GetBodySpriteFor(PlayerControl? pc)
    {
        if (pc?.Data == null) return null;
        if (IsConcealed(pc)) return GetConcealedBaseSprite();
        int colorId = ClampColorId(GetPlayerColorId(pc));
        return IsRainbowColorIdCached(colorId) ? GetRainbowBaseSprite(0) : GetBaseSprite(colorId);
    }

    // True when this speaker picked the animated "Rainbow" color and is NOT concealed — i.e. GetPaletteColor returns
    // a time-varying color for them. The speaking bar caches a slot's color and only refreshes it on a fingerprint
    // change (which never fires for a fixed Rainbow color id), so it consults this to recompute rainbow rings live.
    internal static bool IsRainbowPlayer(PlayerControl? pc)
    {
        if (pc?.Data == null || IsConcealed(pc)) return false;
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
    // Comms sabotage hides everyone in the bar (you can't ID who is talking while comms is down). This outranks
    // the meeting real-identity reveal, so a meeting called while comms is still active stays grey.
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

    private static NetworkedPlayerInfo.PlayerOutfit GetDisplayOutfit(PlayerControl pc)
    {
        try
        {
            // Meeting or fixed-roster bar shows real identity (concealed players still gray via IsConcealed).
            if (ShowRealIdentity) return pc.Data.DefaultOutfit;
            return pc.CurrentOutfit ?? pc.Data.DefaultOutfit;
        }
        catch
        {
            return pc.Data.DefaultOutfit;
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
