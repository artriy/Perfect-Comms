using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using PoolList = Il2CppSystem.Collections.Generic.List<PoolableBehavior>;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Repairs Town of Us chat-pool aliases without taking a compile-time dependency on Town of Us.
/// </summary>
[HarmonyPatch]
internal static class TouMiraChatPoolIsolationPatch
{
    private const string TargetTypeName = "TownOfUs.Patches.Options.TeamChatPatches";
    private const string TargetMethodName = "AlignAllChatBubbles";
    private const long DiagnosticIntervalMilliseconds = 60_000;

    private static readonly object MetadataLock = new();
    private enum ActivePoolKind
    {
        None,
        Public,
        Private,
        Merged
    }


    private static bool _metadataInitialized;
    private static bool _metadataValid;
    private static MethodBase? _targetMethod;
    private static FieldInfo? _publicBubblesField;
    private static FieldInfo? _privateBubblesField;
    private static FieldInfo? _mergedBubblesField;
    private static FieldInfo? _publicPoolField;
    private static FieldInfo? _privatePoolField;
    private static FieldInfo? _mergedPoolField;
    private static FieldInfo? _publicChatItemsField;
    private static FieldInfo? _privateChatItemsField;
    private static FieldInfo? _mergedChatItemsField;
    private static MemberInfo? _mergedBubbleMember;
    private static long _nextDiagnosticAtMilliseconds;

    private static bool Prepare() => EnsureMetadata();

    private static MethodBase? TargetMethod()
    {
        EnsureMetadata();
        return _targetMethod;
    }

    [HarmonyPostfix]
    private static void Postfix(ChatController __0)
    {
        if (!_metadataValid || __0 is null)
            return;

        try
        {
            var originalPublicPool = _publicPoolField!.GetValue(null);
            var originalPrivatePool = _privatePoolField!.GetValue(null);
            var originalMergedPool = _mergedPoolField!.GetValue(null);
            var poolOwner = __0.chatBubblePool;
            var originalActivePool = poolOwner?.activeChildren;

            if (!TryBuildPool(_publicBubblesField!.GetValue(null), out var publicPool, out var publicFailure))
            {
                WarnRateLimited("Could not recover public chat bubbles: " + publicFailure);
                return;
            }

            if (!TryBuildPool(_privateBubblesField!.GetValue(null), out var privatePool, out var privateFailure))
            {
                WarnRateLimited("Could not recover private chat bubbles: " + privateFailure);
                return;
            }

            PoolList mergedPool = null!;
            string mergedFailure = "merged bubble source unavailable";
            var recoveredMergedBubbles = _mergedBubblesField != null &&
                                         _mergedBubbleMember != null &&
                                         TryBuildMergedPool(
                                             _mergedBubblesField.GetValue(null),
                                             out mergedPool,
                                             out mergedFailure);

            if (!recoveredMergedBubbles &&
                !TryCopySafelyDistinctMergedPool(
                    originalMergedPool,
                    originalPublicPool,
                    originalPrivatePool,
                    out mergedPool,
                    out var fallbackFailure))
            {
                WarnRateLimited(
                    "Could not recover merged chat bubbles: " +
                    (_mergedBubblesField == null
                        ? "MergedChatBubbles is unavailable; "
                        : _mergedBubbleMember == null
                            ? "MergedChatBubbles.Bubble is unavailable; "
                            : mergedFailure + "; ") +
                    fallbackFailure);
                return;
            }

            if (!TryResolveActivePool(
                    __0,
                    originalActivePool,
                    originalPublicPool,
                    originalPrivatePool,
                    originalMergedPool,
                    publicPool,
                    privatePool,
                    mergedPool,
                    out var activePoolKind,
                    out var activePoolFailure))
            {
                WarnRateLimited("Could not preserve the active chat-pool binding: " + activePoolFailure);
                return;
            }

            // Populate all three replacements before assigning any of them. Adding a bubble to a
            // list and rebinding activeChildren do not change bubble ownership, hierarchy, index,
            // or active state.
            try
            {
                _publicPoolField.SetValue(null, publicPool);
                _privatePoolField.SetValue(null, privatePool);
                _mergedPoolField.SetValue(null, mergedPool);

                if (poolOwner != null)
                {
                    poolOwner.activeChildren = activePoolKind switch
                    {
                        ActivePoolKind.Public => publicPool,
                        ActivePoolKind.Private => privatePool,
                        ActivePoolKind.Merged => mergedPool,
                        _ => originalActivePool
                    };
                }
            }
            catch (Exception assignmentException)
            {
                RestoreOriginalPools(originalPublicPool, originalPrivatePool, originalMergedPool);
                if (poolOwner != null)
                {
                    try { poolOwner.activeChildren = originalActivePool; }
                    catch { }
                }
                WarnRateLimited(
                    "Could not assign isolated chat pools: " +
                    assignmentException.GetType().Name + ": " + assignmentException.Message);
            }
        }
        catch (Exception exception)
        {
            WarnRateLimited(
                "Chat-pool isolation failed closed: " +
                exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static bool EnsureMetadata()
    {
        if (_metadataInitialized)
            return _metadataValid;

        lock (MetadataLock)
        {
            if (_metadataInitialized)
                return _metadataValid;

            var targetType = AccessTools.TypeByName(TargetTypeName);
            if (targetType == null)
            {
                // Town of Us is optional. Its absence is expected and must stay silent.
                _metadataInitialized = true;
                return false;
            }

            try
            {
                var targetMethod = AccessTools.Method(targetType, TargetMethodName);
                if (targetMethod == null || !targetMethod.IsStatic)
                    throw new MissingMethodException(TargetTypeName, TargetMethodName);

                var publicBubblesField = RequireEnumerableField(targetType, "PublicChatBubbles");
                var privateBubblesField = RequireEnumerableField(targetType, "PrivateChatBubbles");
                var publicPoolField = RequireWritablePoolField(targetType, "PublicChatPool");
                var privatePoolField = RequireWritablePoolField(targetType, "PrivateChatPool");
                var mergedPoolField = RequireWritablePoolField(targetType, "MergedChatPool");
                var publicChatItemsField = AccessTools.Field(targetType, "PublicChatItems");
                var privateChatItemsField = AccessTools.Field(targetType, "PrivateChatItems");
                var mergedChatItemsField = AccessTools.Field(targetType, "MergedChatItems");


                // MergedChatBubbles is the authoritative source when the installed TOU version
                // exposes its wrapper shape. A safely distinct final pool remains a supported
                // fallback for versions that hide or rename that wrapper member.
                var mergedBubblesField = AccessTools.Field(targetType, "MergedChatBubbles");
                MemberInfo? mergedBubbleMember = null;
                if (mergedBubblesField is { IsStatic: true } &&
                    typeof(IEnumerable).IsAssignableFrom(mergedBubblesField.FieldType))
                {
                    var elementType = GetEnumerableElementType(mergedBubblesField.FieldType);
                    if (elementType != null)
                    {
                        var bubbleProperty = AccessTools.Property(elementType, "Bubble");
                        if (bubbleProperty is { CanRead: true } &&
                            typeof(PoolableBehavior).IsAssignableFrom(bubbleProperty.PropertyType))
                        {
                            mergedBubbleMember = bubbleProperty;
                        }
                        else
                        {
                            var bubbleField = AccessTools.Field(elementType, "Bubble");
                            if (bubbleField != null &&
                                typeof(PoolableBehavior).IsAssignableFrom(bubbleField.FieldType))
                            {
                                mergedBubbleMember = bubbleField;
                            }
                        }
                    }
                }
                else
                {
                    mergedBubblesField = null;
                }

                _targetMethod = targetMethod;
                _publicBubblesField = publicBubblesField;
                _privateBubblesField = privateBubblesField;
                _mergedBubblesField = mergedBubblesField;
                _publicPoolField = publicPoolField;
                _privatePoolField = privatePoolField;
                _mergedPoolField = mergedPoolField;
                _mergedBubbleMember = mergedBubbleMember;
                _publicChatItemsField = publicChatItemsField is { IsStatic: true } ? publicChatItemsField : null;
                _privateChatItemsField = privateChatItemsField is { IsStatic: true } ? privateChatItemsField : null;
                _mergedChatItemsField = mergedChatItemsField is { IsStatic: true } ? mergedChatItemsField : null;
                _metadataValid = true;
            }
            catch (Exception exception)
            {
                WarnRateLimited(
                    "Town of Us chat integration is incompatible; patch disabled: " +
                    exception.GetType().Name + ": " + exception.Message);
            }
            finally
            {
                _metadataInitialized = true;
            }

            return _metadataValid;
        }
    }

    private static FieldInfo RequireEnumerableField(Type declaringType, string name)
    {
        var field = AccessTools.Field(declaringType, name);
        if (field == null || !field.IsStatic || !typeof(IEnumerable).IsAssignableFrom(field.FieldType))
            throw new MissingFieldException(declaringType.FullName, name);
        return field;
    }

    private static FieldInfo RequireWritablePoolField(Type declaringType, string name)
    {
        var field = AccessTools.Field(declaringType, name);
        if (field == null || !field.IsStatic || field.IsInitOnly ||
            !field.FieldType.IsAssignableFrom(typeof(PoolList)))
        {
            throw new MissingFieldException(declaringType.FullName, name);
        }

        return field;
    }

    private static Type? GetEnumerableElementType(Type collectionType)
    {
        if (collectionType.IsArray)
            return collectionType.GetElementType();

        if (collectionType.IsGenericType)
        {
            var arguments = collectionType.GetGenericArguments();
            if (arguments.Length == 1)
                return arguments[0];
        }

        foreach (var interfaceType in collectionType.GetInterfaces())
        {
            if (!interfaceType.IsGenericType ||
                interfaceType.GetGenericTypeDefinition() != typeof(System.Collections.Generic.IEnumerable<>))
            {
                continue;
            }

            return interfaceType.GetGenericArguments()[0];
        }

        return null;
    }

    private static bool TryBuildPool(object? collection, out PoolList pool, out string failure)
    {
        pool = new PoolList();
        if (collection is not IEnumerable bubbles)
        {
            failure = "collection is null or no longer enumerable";
            return false;
        }

        try
        {
            foreach (var item in bubbles)
            {
                if (item is not PoolableBehavior bubble)
                {
                    failure = item == null
                        ? "collection contains a null bubble"
                        : "collection contains " + item.GetType().FullName;
                    return false;
                }

                pool.Add(bubble);
            }
        }
        catch (Exception exception)
        {
            failure = exception.GetType().Name + ": " + exception.Message;
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static bool TryBuildMergedPool(object? collection, out PoolList pool, out string failure)
    {
        pool = new PoolList();
        if (collection is not IEnumerable mergedBubbles)
        {
            failure = "MergedChatBubbles is null or no longer enumerable";
            return false;
        }

        try
        {
            foreach (var mergedBubble in mergedBubbles)
            {
                if (mergedBubble == null)
                {
                    failure = "MergedChatBubbles contains a null item";
                    return false;
                }

                var bubble = ReadMergedBubble(mergedBubble);
                if (bubble is not PoolableBehavior poolableBubble)
                {
                    failure = bubble == null
                        ? "a merged item has no Bubble"
                        : "a merged Bubble has type " + bubble.GetType().FullName;
                    return false;
                }

                pool.Add(poolableBubble);
            }
        }
        catch (Exception exception)
        {
            failure = exception.GetType().Name + ": " + exception.Message;
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static object? ReadMergedBubble(object mergedBubble)
    {
        return _mergedBubbleMember switch
        {
            PropertyInfo property => property.GetValue(mergedBubble),
            FieldInfo field => field.GetValue(mergedBubble),
            _ => null
        };
    }

    private static bool TryCopySafelyDistinctMergedPool(
        object? mergedPoolValue,
        object? publicPoolValue,
        object? privatePoolValue,
        out PoolList mergedPool,
        out string failure)
    {
        mergedPool = new PoolList();
        if (mergedPoolValue is not PoolList existingMergedPool)
        {
            failure = "the final MergedChatPool has an unexpected type";
            return false;
        }

        if (ReferenceEquals(mergedPoolValue, publicPoolValue) ||
            ReferenceEquals(mergedPoolValue, privatePoolValue))
        {
            failure = "the final MergedChatPool is aliased to another pool";
            return false;
        }

        try
        {
            foreach (var bubble in existingMergedPool)
            {
                if (bubble == null)
                {
                    failure = "the final MergedChatPool contains a null bubble";
                    return false;
                }

                mergedPool.Add(bubble);
            }
        }
        catch (Exception exception)
        {
            failure = exception.GetType().Name + ": " + exception.Message;
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static bool TryResolveActivePool(
        ChatController instance,
        PoolList? activePool,
        object? originalPublicPool,
        object? originalPrivatePool,
        object? originalMergedPool,
        PoolList publicPool,
        PoolList privatePool,
        PoolList mergedPool,
        out ActivePoolKind activePoolKind,
        out string failure)
    {
        activePoolKind = ActivePoolKind.None;
        if (activePool == null)
        {
            failure = string.Empty;
            return true;
        }

        try
        {
            var activeItems = instance?.scroller?.Inner;
            if (MatchesChatItems(activeItems, _publicChatItemsField))
                activePoolKind = ActivePoolKind.Public;
            else if (MatchesChatItems(activeItems, _privateChatItemsField))
                activePoolKind = ActivePoolKind.Private;
            else if (MatchesChatItems(activeItems, _mergedChatItemsField))
                activePoolKind = ActivePoolKind.Merged;

            if (activePoolKind != ActivePoolKind.None)
            {
                failure = string.Empty;
                return true;
            }
        }
        catch
        {
            // Older versions may not expose the active chat transform. Pool identity and contents
            // below remain safe fallbacks.
        }

        var isPublic = ReferenceEquals(activePool, originalPublicPool);
        var isPrivate = ReferenceEquals(activePool, originalPrivatePool);
        var isMerged = ReferenceEquals(activePool, originalMergedPool);
        var identityMatches = (isPublic ? 1 : 0) + (isPrivate ? 1 : 0) + (isMerged ? 1 : 0);
        if (identityMatches == 1)
        {
            activePoolKind = isPublic
                ? ActivePoolKind.Public
                : isPrivate
                    ? ActivePoolKind.Private
                    : ActivePoolKind.Merged;
            failure = string.Empty;
            return true;
        }

        isPublic = PoolsContainSameBubbles(activePool, publicPool);
        isPrivate = PoolsContainSameBubbles(activePool, privatePool);
        isMerged = PoolsContainSameBubbles(activePool, mergedPool);
        var contentMatches = (isPublic ? 1 : 0) + (isPrivate ? 1 : 0) + (isMerged ? 1 : 0);
        if (contentMatches == 1)
        {
            activePoolKind = isPublic
                ? ActivePoolKind.Public
                : isPrivate
                    ? ActivePoolKind.Private
                    : ActivePoolKind.Merged;
            failure = string.Empty;
            return true;
        }

        failure = "activeChildren is aliased or no longer identifies exactly one chat view";
        return false;
    }

    private static bool MatchesChatItems(Transform? activeItems, FieldInfo? expectedItemsField)
    {
        return activeItems != null &&
               expectedItemsField?.GetValue(null) is Transform expectedItems &&
               activeItems == expectedItems;
    }

    private static bool PoolsContainSameBubbles(PoolList first, PoolList second)
    {
        if (first.Count != second.Count)
            return false;

        for (var index = 0; index < first.Count; index++)
        {
            if (first[index] != second[index])
                return false;
        }

        return true;
    }

    private static void RestoreOriginalPools(
        object? originalPublicPool,
        object? originalPrivatePool,
        object? originalMergedPool)
    {
        try { _publicPoolField!.SetValue(null, originalPublicPool); }
        catch { }
        try { _privatePoolField!.SetValue(null, originalPrivatePool); }
        catch { }
        try { _mergedPoolField!.SetValue(null, originalMergedPool); }
        catch { }
    }

    private static void WarnRateLimited(string message)
    {
        var now = Environment.TickCount64;
        var next = Interlocked.Read(ref _nextDiagnosticAtMilliseconds);
        if (now < next ||
            Interlocked.CompareExchange(
                ref _nextDiagnosticAtMilliseconds,
                now + DiagnosticIntervalMilliseconds,
                next) != next)
        {
            return;
        }

        try
        {
            global::VoiceChatPlugin.VoiceChatPluginMain.Logger?.LogWarning(
                "[PerfectComms][TOU chat pools] " + message);
        }
        catch
        {
            // Compatibility diagnostics must never make an optional patch startup-fatal.
        }
    }
}
