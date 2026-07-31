using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch(typeof(PassiveButton), "Start")]
internal static class JailVoiceButtonHitboxPatch
{
    private const string ButtonObjectName = "JailVoiceButton";
    private const string HitboxObjectName = "JailVoiceButtonHitbox";
    private const float MinimumWidth = 0.55f;
    private const float MinimumHeight = 0.45f;
    private const float HorizontalPadding = 0.08f;
    private const float VerticalPadding = 0.06f;

    [HarmonyPostfix]
    private static void Postfix(PassiveButton __instance)
    {
        if (__instance == null)
            return;

        var buttonObject = __instance.gameObject;
        if (buttonObject == null || buttonObject.name != ButtonObjectName)
            return;

        var buttonTransform = buttonObject.transform;
        var hitboxTransform = buttonTransform.Find(HitboxObjectName);
        GameObject hitboxObject;
        BoxCollider2D hitbox;

        if (hitboxTransform == null)
        {
            hitboxObject = new GameObject(HitboxObjectName);
            hitboxTransform = hitboxObject.transform;
            hitboxTransform.SetParent(buttonTransform, false);
            hitbox = hitboxObject.AddComponent<BoxCollider2D>();
        }
        else
        {
            hitboxObject = hitboxTransform.gameObject;
            hitbox = hitboxObject.GetComponent<BoxCollider2D>();
            if (hitbox == null)
                hitbox = hitboxObject.AddComponent<BoxCollider2D>();
        }

        hitboxObject.layer = buttonObject.layer;
        hitboxObject.SetActive(true);
        hitboxTransform.localPosition = Vector3.zero;
        hitboxTransform.localRotation = Quaternion.identity;
        hitboxTransform.localScale = Vector3.one;

        var size = new Vector2(MinimumWidth, MinimumHeight);
        var spriteRenderer = buttonObject.GetComponent<SpriteRenderer>();
        if (spriteRenderer == null)
            spriteRenderer = buttonObject.GetComponentInChildren<SpriteRenderer>(true);

        if (spriteRenderer != null && spriteRenderer.sprite != null)
        {
            var spriteSize = spriteRenderer.sprite.bounds.size;
            size.x = Mathf.Max(MinimumWidth, spriteSize.x + HorizontalPadding * 2f);
            size.y = Mathf.Max(MinimumHeight, spriteSize.y + VerticalPadding * 2f);
        }

        hitbox.offset = Vector2.zero;
        hitbox.size = size;
        hitbox.isTrigger = true;

        var colliders = buttonObject.GetComponentsInChildren<Collider2D>(true);
        for (var i = 0; i < colliders.Length; i++)
        {
            if (colliders[i] != hitbox)
                colliders[i].enabled = false;
        }

        hitbox.enabled = true;
        __instance.ClickMask = hitbox;
        var authoritativeColliders = new Il2CppReferenceArray<Collider2D>(1);
        authoritativeColliders[0] = hitbox;
        __instance.Colliders = authoritativeColliders;
        __instance.CachedZ = buttonTransform.position.z;
    }
}
