using System;
using System.Threading.Tasks;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using VoiceChatPlugin.VoiceChat;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin;

internal static class PerfectCommsUpdateNotifier
{
    private const int SortBase = 32740;
    private const float PanelWidth = 3.45f;
    private const float PanelHeight = 0.82f;
    private static readonly Color Accent = new(0f, 0.898f, 1f, 1f);

    private static GameObject? _root;
    private static Task<PerfectCommsUpdateInfo?>? _checkTask;
    private static int _mainMenuLoadId;
    private static int _shownLoadId;

    internal static void OnMainMenuLoaded(MainMenuManager menu)
    {
        _mainMenuLoadId++;
        _shownLoadId = -1;
        Clear();

        var settings = VoiceSettings.Instance;
        if (settings?.UpdateNotificationsEnabled.Value == false) return;

        var url = settings?.UpdateNotificationUrl.Value ?? "";
        _checkTask = PerfectCommsUpdateClient.GetLatestAsync(url);
    }

    internal static void Update(MainMenuManager menu)
    {
        // Do not consume this menu load's notification while first-run setup is pending/open.
        // Once setup releases the gate, the completed task is handled by a later LateUpdate.
        if (VoiceFirstRunSetup.ShouldDeferMainMenuPopups) return;
        if (_root != null) return;
        if (_shownLoadId == _mainMenuLoadId) return;
        if (_checkTask == null || !_checkTask.IsCompleted) return;

        _shownLoadId = _mainMenuLoadId;
        PerfectCommsUpdateInfo? info;
        try
        {
            info = _checkTask.Result;
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.DebugWarning("[VC] Update notification check failed: " + ex.Message);
            info = null;
        }

        if (!ShouldShow(info)) return;
        Show(menu, info!);
    }

    internal static void Clear()
    {
        if (_root != null)
        {
            Object.Destroy(_root);
            _root = null;
        }
    }

    private static bool ShouldShow(PerfectCommsUpdateInfo? info)
    {
        if (info == null || !info.Enabled) return false;

        if (info.ShowEveryMainMenu) return true;
        return PerfectCommsUpdateClient.IsNewerThanCurrent(info.LatestVersion);
    }

    private static void Show(MainMenuManager menu, PerfectCommsUpdateInfo info)
    {
        var parent = menu.PlayOnlineButton?.transform.parent ?? menu.transform;
        _root = new GameObject("PerfectComms_UpdateNotification");
        _root.transform.SetParent(parent, false);
        _root.transform.localPosition = new Vector3(1.02f, 2.32f, -30f);

        CreatePanelArt();
        CreateAccentLine();

        CreateClickArea(info.ReleaseUrl);

        CreateText("Title", new Vector3(0f, 0.17f, -0.2f), "Perfect Comms Update Available", 0.92f, Accent, TextAlignmentOptions.Center);
        CreateText("Message", new Vector3(0f, -0.16f, -0.2f), "(Click here to download)", 0.64f, Color.white, TextAlignmentOptions.Center);

        VoiceDiagnostics.DebugInfo($"[VC] Showing update notification: {info.Title}");
    }

    private static void CreateClickArea(string releaseUrl)
    {
        var go = new GameObject("ClickArea");
        go.transform.SetParent(_root!.transform, false);
        go.transform.localPosition = new Vector3(0f, 0f, -0.3f);
        var collider = go.AddComponent<BoxCollider2D>();
        collider.size = new Vector2(PanelWidth, PanelHeight);
        var button = go.AddComponent<PassiveButton>();
        button.ClickMask = collider;
        button.Colliders = new Collider2D[] { collider };
        button.OnClick = new ButtonClickedEvent();
        button.OnMouseOver = new UnityEvent();
        button.OnMouseOut = new UnityEvent();
        button.OnClick.AddListener((Action)(() =>
        {
            if (!string.IsNullOrWhiteSpace(releaseUrl))
                Application.OpenURL(releaseUrl);
            Clear();
        }));
    }

    private static void CreatePanelArt()
    {
        var panelArt = new GameObject("PanelArt");
        panelArt.transform.SetParent(_root!.transform, false);
        panelArt.transform.localPosition = new Vector3(0f, 0f, -0.05f);
        var sr = panelArt.AddComponent<SpriteRenderer>();
        sr.sprite = VoiceChatHudState.LoadSprite("VoiceChatPlugin.Resources.LobbyBrowserPanel.png")
                    ?? VoiceUiKit.Solid(new Color(0.02f, 0.06f, 0.10f, 0.94f));
        sr.sortingLayerName = "UI";
        sr.sortingOrder = SortBase;

        var size = sr.sprite.bounds.size;
        if (size.x > 0f && size.y > 0f)
            panelArt.transform.localScale = new Vector3(PanelWidth / size.x, PanelHeight / size.y, 1f);
    }

    private static void CreateAccentLine()
    {
        var line = new GameObject("AccentLine");
        line.transform.SetParent(_root!.transform, false);
        line.transform.localPosition = new Vector3(0f, 0.035f, -0.10f);
        line.transform.localScale = new Vector3(1.48f, 0.012f, 1f);
        var sr = line.AddComponent<SpriteRenderer>();
        sr.sprite = VoiceUiKit.Solid(Accent);
        sr.sortingLayerName = "UI";
        sr.sortingOrder = SortBase + 2;
    }

    private static TextMeshPro CreateText(string name, Vector3 pos, string text, float size, Color color, TextAlignmentOptions align)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root!.transform, false);
        go.transform.localPosition = pos;
        var tmp = go.AddComponent<TextMeshPro>();
        tmp.text = text;
        tmp.fontSize = size;
        tmp.color = color;
        tmp.alignment = align;
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Overflow;
        tmp.sortingLayerID = SortingLayer.NameToID("UI");
        tmp.sortingOrder = SortBase + 3;
        tmp.rectTransform.sizeDelta = new Vector2(3.10f, 0.42f);
        return tmp;
    }

}

[HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
internal static class PerfectCommsUpdateMainMenuStartPatch
{
    private static void Postfix(MainMenuManager __instance)
        => PerfectCommsUpdateNotifier.OnMainMenuLoaded(__instance);
}

[HarmonyPatch(typeof(MainMenuManager), "LateUpdate")]
internal static class PerfectCommsUpdateMainMenuUpdatePatch
{
    private static void Postfix(MainMenuManager __instance)
        => PerfectCommsUpdateNotifier.Update(__instance);
}
