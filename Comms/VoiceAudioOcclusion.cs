using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceAudioOcclusion
{
    private const float CacheSeconds = 0.15f;
    private const float PositionQuantize = 4f;
    private const int MaxCacheEntries = 128;
    private static readonly int ShadowMask = LayerMask.GetMask("Shadow");
    private static readonly Dictionary<OcclusionCacheKey, OcclusionCacheEntry> Cache = new();

    public static VoiceOcclusionDiagnostics Inspect(Vector2 listenerPos, Vector2 targetPos)
    {
        var occlusion = ResolveOcclusion(listenerPos, targetPos);
        return new VoiceOcclusionDiagnostics(occlusion.HasWall, occlusion.HasClosedDoor, !occlusion.IsOccluded);
    }

    public static float ApplyFalloff(float distance, float maxDistance, VoiceFalloffMode mode)
    {
        if (maxDistance <= 0f) return 0f;
        float t = Math.Clamp(distance / maxDistance, 0f, 1f);
        return mode switch
        {
            VoiceFalloffMode.Smooth => 1f - SmoothStep(t),
            VoiceFalloffMode.VoiceFocused => t < 0.35f ? 1f : MathF.Pow(1f - t, 1.35f),
            _ => 1f - t,
        };
    }

    public static VoiceOcclusionResult Evaluate(Vector2 listenerPos, Vector2 targetPos, VoiceOcclusionMode mode)
    {
        if (mode == VoiceOcclusionMode.Off)
            return new(false, false, 1f, VoiceAudioFilterMode.None);

        var occlusion = ResolveOcclusion(listenerPos, targetPos);
        if (!occlusion.IsOccluded)
            return new(false, false, 1f, VoiceAudioFilterMode.None);

        return mode switch
        {
            VoiceOcclusionMode.HardBlock => new(occlusion.HasWall, occlusion.HasClosedDoor, 0f, VoiceAudioFilterMode.None),
            VoiceOcclusionMode.VisionOnly => new(occlusion.HasWall, occlusion.HasClosedDoor, 0f, VoiceAudioFilterMode.None),
            VoiceOcclusionMode.SoftMuffle => new(occlusion.HasWall, occlusion.HasClosedDoor, 0.70f, VoiceAudioFilterMode.WallMuffle),
            _ => new(occlusion.HasWall, occlusion.HasClosedDoor, 0.35f, VoiceAudioFilterMode.WallMuffle),
        };
    }

    private static VoiceOcclusionResult ResolveOcclusion(Vector2 listenerPos, Vector2 targetPos)
    {
        var key = OcclusionCacheKey.Create(listenerPos, targetPos);
        float now = Time.time;
        if (Cache.TryGetValue(key, out var cached) && now - cached.Time <= CacheSeconds)
            return cached.Result;

        if (Cache.Count > MaxCacheEntries)
            Cache.Clear();

        bool hasWall = Physics2D.Linecast(listenerPos, targetPos, ShadowMask);
        bool hasClosedDoor = IsClosedDoorBetween(listenerPos, targetPos);
        var result = new VoiceOcclusionResult(hasWall, hasClosedDoor, hasWall || hasClosedDoor ? 0f : 1f, VoiceAudioFilterMode.None);
        Cache[key] = new OcclusionCacheEntry(result, now);
        return result;
    }

    public static bool TryGetCameraListenerPosition(
        int mapId,
        bool cameraViewActive,
        int activeCameraIndex,
        Vector2? activeCameraPosition,
        Vector2 targetPos,
        out Vector2 position)
    {
        position = default;
        if (!cameraViewActive) return false;

        if (IsAllCameraView(mapId, activeCameraIndex, out var allCameras))
            return TryGetNearestCameraPosition(allCameras, targetPos, out position);

        if (activeCameraPosition.HasValue)
        {
            position = activeCameraPosition.Value;
            return true;
        }

        return TryGetFixedCameraPosition(mapId, activeCameraIndex, out position);
    }

    public static bool TryGetFixedCameraPosition(int mapId, int cameraIndex, out Vector2 position)
    {
        position = default;
        var cameras = mapId switch
        {
            2 => PolusCameras,
            4 => AirshipCameras,
            _ => null,
        };

        if (cameras == null || cameraIndex < 0 || cameraIndex >= cameras.Length) return false;
        position = cameras[cameraIndex];
        return true;
    }

    private static readonly Vector2[] SkeldCameras =
    [
        CreateVector(13.2417f, -4.348f),
        CreateVector(0.6216f, -6.5642f),
        CreateVector(-7.1503f, 1.6709f),
        CreateVector(-17.8098f, -4.8983f),
    ];

    private static readonly Vector2[] PolusCameras =
    [
        CreateVector(29f, -15.7f),
        CreateVector(15.4f, -15.4f),
        CreateVector(24.4f, -8.5f),
        CreateVector(17f, -20.6f),
        CreateVector(4.7f, -22.73f),
        CreateVector(11.6f, -8.2f),
    ];

    private static readonly Vector2[] AirshipCameras =
    [
        CreateVector(-8.2872f, 0.0527f),
        CreateVector(-4.0477f, 9.1447f),
        CreateVector(23.5616f, 9.8882f),
        CreateVector(4.881f, -11.1688f),
        CreateVector(30.3702f, -0.874f),
        CreateVector(3.3018f, 16.2631f),
    ];

    private static Vector2 CreateVector(float x, float y)
    {
        var vector = default(Vector2);
        vector.x = x;
        vector.y = y;
        return vector;
    }

    private static bool TryGetNearestCameraPosition(Vector2[] cameras, Vector2 targetPos, out Vector2 position)
    {
        position = default;
        if (cameras.Length == 0) return false;

        float bestDistance = float.MaxValue;
        foreach (var camera in cameras)
        {
            float dx = targetPos.x - camera.x;
            float dy = targetPos.y - camera.y;
            float distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            position = camera;
        }

        return bestDistance < float.MaxValue;
    }

    private static bool IsAllCameraView(int mapId, int activeCameraIndex, out Vector2[] cameras)
    {
        cameras = mapId switch
        {
            0 or 3 when activeCameraIndex == 6 => SkeldCameras,
            4 when activeCameraIndex == 6 => AirshipCameras,
            _ => Array.Empty<Vector2>(),
        };
        return cameras.Length > 0;
    }

    private static float SmoothStep(float t)
        => t * t * (3f - 2f * t);

    private static bool IsClosedDoorBetween(Vector2 listenerPos, Vector2 targetPos)
    {
        var doors = ShipStatus.Instance?.AllDoors;
        if (doors == null) return false;

        foreach (var door in doors)
        {
            if (door == null || door.IsOpen) continue;

            Vector2 doorPos = door.transform.position;
            if (DistancePointToSegment(doorPos, listenerPos, targetPos) <= 0.65f)
                return true;
        }

        return false;
    }

    private static float DistancePointToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float denom = Vector2.Dot(ab, ab);
        if (denom <= 0.0001f) return Vector2.Distance(point, a);

        float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / denom);
        return Vector2.Distance(point, a + ab * t);
    }

    private readonly record struct OcclusionCacheKey(int Ax, int Ay, int Bx, int By)
    {
        public static OcclusionCacheKey Create(Vector2 a, Vector2 b)
        {
            int ax = Mathf.RoundToInt(a.x * PositionQuantize);
            int ay = Mathf.RoundToInt(a.y * PositionQuantize);
            int bx = Mathf.RoundToInt(b.x * PositionQuantize);
            int by = Mathf.RoundToInt(b.y * PositionQuantize);

            if (ax > bx || (ax == bx && ay > by))
                return new OcclusionCacheKey(bx, by, ax, ay);
            return new OcclusionCacheKey(ax, ay, bx, by);
        }
    }

    private readonly record struct OcclusionCacheEntry(VoiceOcclusionResult Result, float Time);
}

internal readonly record struct VoiceOcclusionDiagnostics(
    bool HasWall,
    bool HasClosedDoor,
    bool InSight);
