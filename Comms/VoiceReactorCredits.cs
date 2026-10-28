using System;
using System.Globalization;
using System.Reflection;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceReactorCredits
{
    private const string FontName = "Perfect Comms Space Grotesk SDF";
    private const string GradientName = "PerfectCommsCredits";
    private static readonly string PlainEntry =
        $"<noparse>Perfect Comms {VoiceChatPluginMain.Version}</noparse>";
    private static readonly string MenuEntry = StyledEntry("70%", "50%");
    private static readonly string PingEntry = StyledEntry("50%", "35%");
    private static string _styledPingEntry = PingEntry;
    private static float _pingCharacterSpacing;
    private static bool _registered;
    private static bool _styleDisabled;
    private static TMP_FontAsset? _creditFont;
    private static string? _lastMenuText;
    private static string? _lastStyledMenuText;
    private static string? _lastPingText;
    private static string? _lastStyledPingText;

    internal static void Register(Harmony harmony)
    {
        if (_registered || !IL2CPPChainloader.Instance.Plugins.TryGetValue("gg.reactor.api", out var reactor))
            return;

        try
        {
            var credits = reactor.Instance.GetType().Assembly.GetType("Reactor.Utilities.ReactorCredits")
                ?? throw new TypeLoadException("Reactor.Utilities.ReactorCredits is unavailable.");
            var location = credits.GetNestedType("Location")
                ?? throw new MissingMemberException(credits.FullName, "Location");
            if (Enum.GetUnderlyingType(location) != typeof(int))
                throw new NotSupportedException("Reactor credits use an unsupported location type.");
            var register = credits.GetMethod("Register", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(string), typeof(bool), typeof(Func<,>).MakeGenericType(location, typeof(bool)) }, null)
                ?? throw new MissingMethodException(credits.FullName, "Register");
            var getText = credits.GetMethod("GetText", BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { location }, null)
                ?? throw new MissingMethodException(credits.FullName, "GetText");

            harmony.Patch(getText, postfix: new HarmonyMethod(typeof(VoiceReactorCredits), nameof(StyleCredits)));
            harmony.Patch(AccessTools.Method(typeof(PingTracker), nameof(PingTracker.Update)),
                prefix: new HarmonyMethod(typeof(VoiceReactorCredits), nameof(CaptureGameplaySpacing)));
            register.Invoke(null, new object?[] { "Perfect Comms", VoiceChatPluginMain.Version, false, null });
            _registered = true;
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogWarning("[VC] Optional Reactor credits registration failed: " +
                (ex.InnerException ?? ex).Message);
        }
    }

    private static string StyledEntry(string nameSize, string versionSize) =>
        $"<font=\"{FontName}\"><size={nameSize}><color=#A6ACC3>Perfect Comms</color></size> " +
        $"<size={versionSize}><color=#818AA7>v{VoiceChatPluginMain.Version}</color></size></font>";

    private static void CaptureGameplaySpacing(PingTracker __instance)
    {
        if (_styleDisabled) return;
        var spacing = __instance.text.characterSpacing;
        if (spacing == _pingCharacterSpacing) return;

        // Ping credits use 50% size, but TMP's tracking uses the base font size.
        var em = (-spacing / 50f).ToString("0.########", CultureInfo.InvariantCulture);
        // Closing cspace removes one increment; space restores it before the next credit.
        _styledPingEntry = spacing == 0 ? PingEntry :
            $"<size=50%><cspace={em}em>{PingEntry}</cspace><space={em}em></size>";
        _pingCharacterSpacing = spacing;
        _lastPingText = null;
        _lastStyledPingText = null;
    }

    private static void StyleCredits(int __0, ref string? __result)
    {
        if (_styleDisabled || __result == null) return;
        ref string? previousText = ref (__0 == 1 ? ref _lastPingText : ref _lastMenuText);
        ref string? previousStyledText = ref (__0 == 1 ? ref _lastStyledPingText : ref _lastStyledMenuText);
        if (string.Equals(__result, previousText, StringComparison.Ordinal))
        {
            __result = previousStyledText;
            return;
        }
        if (!__result.Contains(PlainEntry, StringComparison.Ordinal)) return;

        try
        {
            if (_creditFont == null)
            {
                var font = VoiceUiKit.GameFont();
                if (font == null) return;
                // TMP 3.x font and gradient tags hash asset names case-sensitively.
                font.hashCode = TMP_TextUtilities.GetSimpleHashCode(FontName);
                font.materialHashCode = TMP_TextUtilities.GetSimpleHashCode(font.material.name);
                MaterialReferenceManager.AddFontAsset(font);

                var gradient = ScriptableObject.CreateInstance<TMP_ColorGradient>();
                gradient.name = GradientName;
                gradient.hideFlags = HideFlags.HideAndDontSave;
                gradient.colorMode = ColorMode.VerticalGradient;
                gradient.topLeft = gradient.topRight = new Color32(106, 203, 236, 205);
                gradient.bottomLeft = gradient.bottomRight = new Color32(149, 106, 236, 205);
                MaterialReferenceManager.AddColorGradientPreset(TMP_TextUtilities.GetSimpleHashCode(GradientName), gradient);
                _creditFont = font;
            }

            previousText = __result;
            __result = __result.Replace(PlainEntry, __0 == 1 ? _styledPingEntry : MenuEntry, StringComparison.Ordinal);
            previousStyledText = __result;
        }
        catch (Exception ex)
        {
            _styleDisabled = true;
            VoiceChatPluginMain.Logger.LogWarning("[VC] Reactor credits styling failed; keeping Reactor's default text: " + ex.Message);
        }
    }
}
