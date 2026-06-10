using UnityEngine;

namespace VoiceChatPlugin;

internal static class VoiceLevelVisual
{
    public const float LevelSmoothSpeed = 12f;
    public const float FadeInSpeed = 7f;
    public const float FadeOutSpeed = 5f;

    public static float NormalizeVoiceLevel(float level)
    {
        if (level <= 0.003f) return 0f;
        float normalized = Mathf.InverseLerp(0.003f, 0.55f, level);
        return Mathf.Pow(Mathf.Clamp01(normalized), 0.65f);
    }

    public static float SmoothLevel(float smoothed, float targetLevel, float deltaTime)
        => Mathf.Lerp(smoothed, NormalizeVoiceLevel(targetLevel), Mathf.Clamp01(deltaTime * LevelSmoothSpeed));

    public static float StepVisibility(float visibility, bool speaking, float deltaTime)
        => Mathf.MoveTowards(visibility, speaking ? 1f : 0f, deltaTime * (speaking ? FadeInSpeed : FadeOutSpeed));
}
