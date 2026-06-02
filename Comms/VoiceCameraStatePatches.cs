using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using VoiceChatPlugin;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch(typeof(SurveillanceMinigame))]
internal static class SurveillanceCameraStatePatches
{
    private const float DeepScanIntervalSeconds = 1f;
    private const float CullingFailureLogThrottleSeconds = 5f;
    private static float _lastDeepScanTime = float.NegativeInfinity;
    private static float _lastCullingFailureLogTime = float.NegativeInfinity;
    private static int _lastCameraCount = -1;
    private static readonly Dictionary<int, CameraMaskSnapshot> _changedCameraMasks = new();

    [HarmonyPostfix, HarmonyPatch(nameof(SurveillanceMinigame.Begin))]
    private static void Begin_Postfix(SurveillanceMinigame __instance)
    {
        VoiceCameraState.Open(__instance);
        ExcludeVoiceOverlayFromSurveillanceCameras(__instance, forceDeepScan: true);
    }

    [HarmonyPostfix, HarmonyPatch(nameof(SurveillanceMinigame.Update))]
    private static void Update_Postfix(SurveillanceMinigame __instance)
    {
        VoiceCameraState.NoteUpdate(__instance);
        ExcludeVoiceOverlayFromSurveillanceCameras(__instance);
    }

    [HarmonyPrefix, HarmonyPatch(nameof(SurveillanceMinigame.Close))]
    private static void Close_Prefix(SurveillanceMinigame __instance)
    {
        VoiceCameraState.Close(__instance);
        RestoreVoiceOverlayCameraMasks();
    }

    [HarmonyPrefix, HarmonyPatch(nameof(SurveillanceMinigame.OnDestroy))]
    private static void OnDestroy_Prefix(SurveillanceMinigame __instance)
    {
        VoiceCameraState.Close(__instance);
        RestoreVoiceOverlayCameraMasks();
    }

    internal static void ExcludeVoiceOverlayFromSurveillanceCameras(object? minigame, bool forceDeepScan = false)
    {
        // Re-apply the overlay-exclusion mask only when the set of cameras actually changed.
        // Camera.allCameras allocates a fresh Camera[] on every access; walking it every frame while a
        // surveillance view was open was a steady GC source. Masks persist once applied, so we only need
        // to re-walk when a camera appears/disappears (count change) or on the forced scan from Begin.
        // Camera.allCamerasCount is a cheap no-alloc property.
        int cameraCount = Camera.allCamerasCount;
        if (forceDeepScan || cameraCount != _lastCameraCount)
        {
            _lastCameraCount = cameraCount;
            try
            {
                foreach (var camera in Camera.allCameras)
                {
                    if (camera == null || camera.targetTexture == null) continue;
                    ExcludeVoiceOverlay(camera);
                }
            }
            catch (Exception ex) { ReportCullingFailure(ex); }
        }

        // Reflection walk for cameras not yet in Camera.allCameras; throttled to ~1/s (forced on Begin).
        float now = Time.unscaledTime;
        if (!forceDeepScan && now - _lastDeepScanTime < DeepScanIntervalSeconds) return;
        _lastDeepScanTime = now;

        try { ApplyToObject(minigame, 0); } catch (Exception ex) { ReportCullingFailure(ex); }
        try { ApplyToObject(ShipStatus.Instance, 0); } catch (Exception ex) { ReportCullingFailure(ex); }
    }

    private static void ReportCullingFailure(Exception ex)
    {
        // Time-throttled, not latched, so a persistent failure stays visible.
        float now = Time.unscaledTime;
        if (now - _lastCullingFailureLogTime < CullingFailureLogThrottleSeconds) return;
        _lastCullingFailureLogTime = now;
        VoiceDiagnostics.DebugError("[VC] Surveillance camera overlay culling failed: " + ex.Message);
    }

    private static void ApplyToObject(object? value, int depth)
    {
        if (value == null || depth > 2) return;

        if (value is Camera camera)
        {
            ExcludeVoiceOverlay(camera);
            return;
        }

        if (value is Component component)
        {
            foreach (var childCamera in component.GetComponentsInChildren<Camera>(true))
                ExcludeVoiceOverlay(childCamera);
        }

        if (value is string) return;
        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
                ApplyToObject(item, depth + 1);
            return;
        }

        if (depth > 0) return;

        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = value.GetType();
        while (type != null && type != typeof(object))
        {
            foreach (var field in type.GetFields(flags))
            {
                if (!MightContainCamera(field.Name, field.FieldType)) continue;
                object? memberValue;
                try { memberValue = field.GetValue(value); }
                catch { continue; }
                ApplyToObject(memberValue, depth + 1);
            }

            foreach (var property in type.GetProperties(flags))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
                if (!MightContainCamera(property.Name, property.PropertyType)) continue;
                object? memberValue;
                try { memberValue = property.GetValue(value); }
                catch { continue; }
                ApplyToObject(memberValue, depth + 1);
            }

            type = type.BaseType;
        }
    }

    private static bool MightContainCamera(string name, Type type)
    {
        if (typeof(Camera).IsAssignableFrom(type)) return true;
        if (typeof(Component).IsAssignableFrom(type)) return true;
        if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string)) return true;
        return name.IndexOf("cam", StringComparison.OrdinalIgnoreCase) >= 0
               || name.IndexOf("camera", StringComparison.OrdinalIgnoreCase) >= 0
               || name.IndexOf("texture", StringComparison.OrdinalIgnoreCase) >= 0
               || name.IndexOf("surveillance", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void ExcludeVoiceOverlay(Camera? camera)
    {
        if (camera == null) return;
        if ((camera.cullingMask & VCOverlayCamera.OverlayLayerMask) == 0) return;

        int id = camera.GetInstanceID();
        if (!_changedCameraMasks.ContainsKey(id))
            _changedCameraMasks[id] = new CameraMaskSnapshot(camera, camera.cullingMask);

        camera.cullingMask &= ~VCOverlayCamera.OverlayLayerMask;
    }

    internal static void RestoreVoiceOverlayCameraMasks()
    {
        if (_changedCameraMasks.Count == 0) return;

        var keys = new List<int>(_changedCameraMasks.Keys);
        foreach (int key in keys)
        {
            if (!_changedCameraMasks.TryGetValue(key, out var snapshot)) continue;
            var camera = snapshot.Camera;
            if (camera != null)
                camera.cullingMask = snapshot.CullingMask;
            _changedCameraMasks.Remove(key);
        }
    }

    private readonly struct CameraMaskSnapshot
    {
        public readonly Camera Camera;
        public readonly int CullingMask;

        public CameraMaskSnapshot(Camera camera, int cullingMask)
        {
            Camera = camera;
            CullingMask = cullingMask;
        }
    }
}

[HarmonyPatch(typeof(PlanetSurveillanceMinigame))]
internal static class PlanetCameraStatePatches
{
    [HarmonyPostfix, HarmonyPatch(nameof(PlanetSurveillanceMinigame.Begin))]
    private static void Begin_Postfix(PlanetSurveillanceMinigame __instance)
    {
        VoiceCameraState.Open(__instance);
        SurveillanceCameraStatePatches.ExcludeVoiceOverlayFromSurveillanceCameras(__instance, forceDeepScan: true);
    }

    [HarmonyPostfix, HarmonyPatch(nameof(PlanetSurveillanceMinigame.Update))]
    private static void Update_Postfix(PlanetSurveillanceMinigame __instance)
    {
        VoiceCameraState.NoteUpdate(__instance);
        SurveillanceCameraStatePatches.ExcludeVoiceOverlayFromSurveillanceCameras(__instance);
    }

    [HarmonyPrefix, HarmonyPatch(nameof(PlanetSurveillanceMinigame.Close))]
    private static void Close_Prefix(PlanetSurveillanceMinigame __instance)
    {
        VoiceCameraState.Close(__instance);
        SurveillanceCameraStatePatches.RestoreVoiceOverlayCameraMasks();
    }

    [HarmonyPrefix, HarmonyPatch(nameof(PlanetSurveillanceMinigame.OnDestroy))]
    private static void OnDestroy_Prefix(PlanetSurveillanceMinigame __instance)
    {
        VoiceCameraState.Close(__instance);
        SurveillanceCameraStatePatches.RestoreVoiceOverlayCameraMasks();
    }
}

[HarmonyPatch(typeof(FungleSurveillanceMinigame))]
internal static class FungleCameraStatePatches
{
    [HarmonyPostfix, HarmonyPatch(nameof(FungleSurveillanceMinigame.Begin))]
    private static void Begin_Postfix(FungleSurveillanceMinigame __instance)
    {
        VoiceCameraState.Open(__instance);
        SurveillanceCameraStatePatches.ExcludeVoiceOverlayFromSurveillanceCameras(__instance, forceDeepScan: true);
    }

    [HarmonyPostfix, HarmonyPatch(nameof(FungleSurveillanceMinigame.Update))]
    private static void Update_Postfix(FungleSurveillanceMinigame __instance)
    {
        VoiceCameraState.NoteUpdate(__instance);
        SurveillanceCameraStatePatches.ExcludeVoiceOverlayFromSurveillanceCameras(__instance);
    }

    [HarmonyPrefix, HarmonyPatch(nameof(FungleSurveillanceMinigame.Close))]
    private static void Close_Prefix(FungleSurveillanceMinigame __instance)
    {
        VoiceCameraState.Close(__instance);
        SurveillanceCameraStatePatches.RestoreVoiceOverlayCameraMasks();
    }
}
