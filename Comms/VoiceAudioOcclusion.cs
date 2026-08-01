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
    private static readonly int ObjectsLayer = LayerMask.NameToLayer("Objects");
    private static readonly Dictionary<OcclusionCacheKey, OcclusionCacheEntry> Cache = new();

    // Door geometry is authored as a finite EdgeCollider2D polyline. Resolve and transform those points only
    // when the loaded ShipStatus changes, then rebuild the active closed-segment list at most once per frame.
    // Per listener/target checks remain pure float math without treating the area around a door center as a
    // blocker. Door state is refreshed before the pair cache so opening/closing invalidates stale results.
    private static readonly List<DoorBarrier> _doorBarriers = new();
    private static readonly List<DoorBarrierSegment> _closedDoorSegments = new();
    private static readonly List<bool> _doorClosedStates = new();
    private static int _doorGeometryShipId = -1;
    private static int _lastDoorScanFrame = -1;

    public static VoiceOcclusionDiagnostics Inspect(Vector2 listenerPos, Vector2 targetPos)
    {
        var occlusion = ResolveOcclusion(listenerPos, targetPos);
        return new VoiceOcclusionDiagnostics(occlusion.HasWall, occlusion.HasClosedDoor, !occlusion.IsOccluded);
    }

    // Local, per-player listener preference (0 = host falloff unchanged, 1 = flattest).
    // Set from VoiceChatLocalSettings; read here so it layers on top of any host curve.
    private static float _proximitySoftness01;
    internal static float ProximitySoftness01
    {
        get => _proximitySoftness01;
        set => _proximitySoftness01 = Math.Clamp(value, 0f, 1f);
    }

    public static float ApplyFalloff(float distance, float maxDistance, VoiceFalloffMode mode)
    {
        if (maxDistance <= 0f) return 0f;
        float t = SoftenDistance(Math.Clamp(distance / maxDistance, 0f, 1f));
        return mode switch
        {
            VoiceFalloffMode.Smooth => 1f - SmoothStep(t),
            VoiceFalloffMode.VoiceFocused => t < 0.35f ? 1f : MathF.Pow((1f - t) / 0.65f, 1.35f),
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
            VoiceOcclusionMode.SoftFade => new(occlusion.HasWall, occlusion.HasClosedDoor, 0.35f, VoiceAudioFilterMode.None),
            _ => new(occlusion.HasWall, occlusion.HasClosedDoor, 0.35f, VoiceAudioFilterMode.WallMuffle),
        };
    }

    private static VoiceOcclusionResult ResolveOcclusion(Vector2 listenerPos, Vector2 targetPos)
    {
        RefreshClosedDoorCacheIfNeeded();
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
        {
            // Prefer the map's hardcoded camera coordinates (deterministic, unit-testable); only
            // consult the live ShipStatus.AllCameras list when the map exposes no hardcoded cameras
            // (e.g. dynamic/Sentry-only camera maps).
            if (allCameras.Length > 0)
                return TryGetNearestCameraPosition(allCameras, targetPos, out position);
            return TryGetNearestShipCameraPosition(targetPos, out position);
        }

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
        if (cameraIndex < 0) return false;

        var cameras = mapId switch
        {
            2 => PolusCameras,
            4 => AirshipCameras,
            _ => null,
        };

        // Hardcoded coordinate for known vanilla indices; live ShipStatus camera for indices beyond
        // the hardcoded set (Sentry/dynamic cameras) or maps with no hardcoded list.
        if (cameras != null && cameraIndex < cameras.Length)
        {
            position = cameras[cameraIndex];
            return true;
        }

        return TryGetShipCameraPosition(cameraIndex, out position);
    }

    // True when ShipStatus exposes at least one camera (vanilla or Town of Us Sentry-placed). Guarded
    // and main-thread only, mirroring the existing door/linecast occlusion reads.
    public static bool HasShipCameras()
    {
        try
        {
            var cameras = ShipStatus.Instance?.AllCameras;
            return cameras != null && cameras.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    // Live world position of a specific camera index from ShipStatus.AllCameras, so Sentry/dynamic
    // cameras appended at runtime resolve on every map. Called from the game-thread snapshot path and
    // the index fallback above; never invoked on the static-camera unit-test paths.
    public static bool TryGetShipCameraPosition(int cameraIndex, out Vector2 position)
    {
        position = default;
        if (cameraIndex < 0) return false;
        try
        {
            var cameras = ShipStatus.Instance?.AllCameras;
            if (cameras == null || cameraIndex >= cameras.Length) return false;
            var camera = cameras[cameraIndex];
            if (camera == null) return false;
            position = camera.transform.position;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetNearestShipCameraPosition(Vector2 targetPos, out Vector2 position)
    {
        position = default;
        try
        {
            var cameras = ShipStatus.Instance?.AllCameras;
            if (cameras == null || cameras.Length == 0) return false;

            float bestDistance = float.MaxValue;
            bool found = false;
            foreach (var camera in cameras)
            {
                if (camera == null) continue;
                Vector2 cameraPos = camera.transform.position;
                float dx = targetPos.x - cameraPos.x;
                float dy = targetPos.y - cameraPos.y;
                float distance = dx * dx + dy * dy;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                position = cameraPos;
                found = true;
            }
            return found;
        }
        catch
        {
            return false;
        }
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
        // Recognize the all-camera view even on maps with no hardcoded camera list when dynamic
        // (Sentry) cameras are registered on ShipStatus.
        return activeCameraIndex == 6 && (cameras.Length > 0 || HasShipCameras());
    }

    private static float SmoothStep(float t)
        => t * t * (3f - 2f * t);

    // Local listener softening: pull the normalized distance toward 0 so the host curve stays
    // near its loud end across most of the range and the fade compresses toward the edge.
    // Endpoints are preserved (0 -> 0, 1 -> 1), so hearing range is never extended or shrunk.
    // gamma = 1 + s*3 stays in [1..4]; Pow(t in [0,1], gamma) is always finite in [0,1].
    internal static float SoftenDistance(float t)
    {
        float s = _proximitySoftness01;
        if (s <= 0f) return t;
        return MathF.Pow(t, 1f + s * 3f);
    }

    private static int _warmedShipId = -1;

    // Warm the occlusion path once per map. The first Physics2D query against a freshly-loaded map builds
    // Unity's 2D physics broadphase (and JITs this whole path), which measured ~70-100ms on the FIRST
    // task-phase frame that ran occlusion. Calling this when the map first appears pays that cost on the
    // (already-hitchy) round-start frame instead of surprising the first in-range speaker mid-round. Cheap
    // after the first call for a given ShipStatus (an instance-id compare). Main thread only.
    public static void WarmUp(Vector2 around)
    {
        var ship = ShipStatus.Instance;
        if (ship == null) return;
        int id = ship.GetInstanceID();
        if (id == _warmedShipId) return;
        _warmedShipId = id;

        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            _lastDoorScanFrame = -1; // force door geometry/state to build now
            RefreshClosedDoorCacheIfNeeded();
            Physics2D.Linecast(around, around + new Vector2(0.1f, 0f), ShadowMask); // build the physics broadphase
        }
        catch
        {
            // Physics/ship not ready yet; will warm on a later frame.
            _warmedShipId = -1;
            return;
        }
        if (VoiceDiagnostics.IsEnabled)
            VoiceDiagnostics.Log("voice.occlusion.warm",
                $"ms={(System.Diagnostics.Stopwatch.GetTimestamp() - t) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0} doors={_doorBarriers.Count} closedSegments={_closedDoorSegments.Count} shipId={id}");
    }

    private static void RefreshClosedDoorCacheIfNeeded()
    {
        int frame = Time.frameCount;
        if (frame == _lastDoorScanFrame) return;
        _lastDoorScanFrame = frame;

        var ship = ShipStatus.Instance;
        if (ship == null)
        {
            ResetDoorGeometry();
            return;
        }

        int shipId = ship.GetInstanceID();
        if (shipId != _doorGeometryShipId)
            RebuildDoorGeometry(ship, shipId);

        _closedDoorSegments.Clear();
        bool stateChanged = false;
        for (int i = 0; i < _doorBarriers.Count; i++)
        {
            var barrier = _doorBarriers[i];
            bool closed = barrier.Door != null && !barrier.Door.IsOpen;
            if (_doorClosedStates[i] != closed)
            {
                _doorClosedStates[i] = closed;
                stateChanged = true;
            }

            if (!closed) continue;
            var segments = barrier.Segments;
            for (int j = 0; j < segments.Length; j++)
                _closedDoorSegments.Add(segments[j]);
        }

        if (stateChanged)
            Cache.Clear();
    }

    private static void RebuildDoorGeometry(ShipStatus ship, int shipId)
    {
        _doorBarriers.Clear();
        _closedDoorSegments.Clear();
        _doorClosedStates.Clear();
        Cache.Clear();

        int unresolved = 0;
        var doors = ship.AllDoors;
        if (doors == null) return;
        _doorGeometryShipId = shipId;
        foreach (var door in doors)
        {
            if (door == null) continue;
            try
            {
                var collider = ResolveDoorBarrierCollider(door);
                if (collider == null)
                {
                    unresolved++;
                    continue;
                }

                var segments = BuildDoorSegments(collider);
                if (segments.Length == 0)
                {
                    unresolved++;
                    continue;
                }

                _doorBarriers.Add(new DoorBarrier(door, segments));
                _doorClosedStates.Add(false);
            }
            catch
            {
                unresolved++;
            }
        }

        if (VoiceDiagnostics.IsEnabled && unresolved > 0)
            VoiceDiagnostics.Log("voice.occlusion.doors",
                $"shipId={shipId} resolved={_doorBarriers.Count} unresolved={unresolved}");
    }

    private static void ResetDoorGeometry()
    {
        if (_doorGeometryShipId == -1 && _doorBarriers.Count == 0 && _closedDoorSegments.Count == 0)
            return;

        _doorGeometryShipId = -1;
        _doorBarriers.Clear();
        _closedDoorSegments.Clear();
        _doorClosedStates.Clear();
        Cache.Clear();
    }

    private static EdgeCollider2D? ResolveDoorBarrierCollider(OpenableDoor door)
    {
        var plainDoor = door.TryCast<PlainDoor>();
        if (plainDoor != null && plainDoor.shadowCollider != null)
        {
            var direct = plainDoor.shadowCollider.TryCast<EdgeCollider2D>();
            if (direct != null)
                return direct;
        }

        // MushroomWallDoor keeps its shadow edge private. Resolve that and any future equivalent once at
        // map load by selecting the non-trigger EdgeCollider2D authored on Among Us' Objects layer.
        EdgeCollider2D? namedShadow = null;
        var edges = door.GetComponentsInChildren<EdgeCollider2D>(true);
        foreach (var edge in edges)
        {
            if (edge == null || edge.isTrigger) continue;
            var edgeObject = edge.gameObject;
            if (edgeObject != null && edgeObject.layer == ObjectsLayer)
                return edge;
            if (namedShadow == null &&
                edgeObject != null &&
                edgeObject.name.IndexOf("Shadow", StringComparison.OrdinalIgnoreCase) >= 0)
                namedShadow = edge;
        }

        return namedShadow;
    }

    private static DoorBarrierSegment[] BuildDoorSegments(EdgeCollider2D collider)
    {
        var points = collider.points;
        if (points == null || points.Length < 2)
            return Array.Empty<DoorBarrierSegment>();

        var segments = new DoorBarrierSegment[points.Length - 1];
        var edgeTransform = collider.transform;
        for (int i = 0; i < segments.Length; i++)
        {
            Vector3 a = edgeTransform.TransformPoint(new Vector3(points[i].x, points[i].y, 0f));
            Vector3 b = edgeTransform.TransformPoint(new Vector3(points[i + 1].x, points[i + 1].y, 0f));
            segments[i] = new DoorBarrierSegment(new Vector2(a.x, a.y), new Vector2(b.x, b.y));
        }

        return segments;
    }

    private static bool IsClosedDoorBetween(Vector2 listenerPos, Vector2 targetPos)
    {
        var segments = _closedDoorSegments;
        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (SegmentsIntersect(listenerPos, targetPos, segment.A, segment.B))
                return true;
        }

        return false;
    }

    internal static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        const float epsilon = 0.00001f;
        const float epsilonSquared = epsilon * epsilon;
        float rx = b.x - a.x;
        float ry = b.y - a.y;
        float sx = d.x - c.x;
        float sy = d.y - c.y;
        float rLengthSquared = rx * rx + ry * ry;
        float sLengthSquared = sx * sx + sy * sy;
        if (rLengthSquared <= epsilonSquared || sLengthSquared <= epsilonSquared)
            return false;

        float qx = c.x - a.x;
        float qy = c.y - a.y;
        float denominator = rx * sy - ry * sx;
        float collinearity = qx * ry - qy * rx;
        if (MathF.Abs(denominator) <= epsilon)
        {
            if (MathF.Abs(collinearity) > epsilon)
                return false;

            float t0 = (qx * rx + qy * ry) / rLengthSquared;
            float t1 = t0 + (sx * rx + sy * ry) / rLengthSquared;
            float start = MathF.Min(t0, t1);
            float end = MathF.Max(t0, t1);
            return end >= -epsilon && start <= 1f + epsilon;
        }

        float t = (qx * sy - qy * sx) / denominator;
        float u = collinearity / denominator;
        return t >= -epsilon && t <= 1f + epsilon &&
               u >= -epsilon && u <= 1f + epsilon;
    }

    private readonly record struct DoorBarrier(OpenableDoor Door, DoorBarrierSegment[] Segments);
    private readonly record struct DoorBarrierSegment(Vector2 A, Vector2 B);


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
