using System;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Explains why the first-run setup should be opened. Keeping the reason explicit lets the
/// UI distinguish a required one-time revision from a user-requested rerun without coupling
/// the decision to Unity or BepInEx.
/// </summary>
internal enum FirstRunSetupTriggerReason
{
    None = 0,
    AutomaticRevisionRequired = 1,
    Manual = 2,
}

/// <summary>
/// Immutable result of evaluating persisted onboarding state for the current setup revision.
/// </summary>
internal readonly struct FirstRunSetupState
{
    internal FirstRunSetupState(
        int completedRevision,
        int targetRevision,
        FirstRunSetupTriggerReason triggerReason)
    {
        CompletedRevision = completedRevision;
        TargetRevision = targetRevision;
        TriggerReason = triggerReason;
    }

    internal int CompletedRevision { get; }
    internal int TargetRevision { get; }
    internal FirstRunSetupTriggerReason TriggerReason { get; }
    internal bool ShouldShow => TriggerReason != FirstRunSetupTriggerReason.None;
    internal bool IsAutomatic => TriggerReason == FirstRunSetupTriggerReason.AutomaticRevisionRequired;
    internal bool IsManual => TriggerReason == FirstRunSetupTriggerReason.Manual;

    /// <summary>
    /// The marker to persist after an explicit Finish or Use Existing Settings choice. A marker written by a newer
    /// build is never lowered if the user later runs an older build manually.
    /// </summary>
    internal int RevisionToStoreOnCompletion => Math.Max(CompletedRevision, TargetRevision);
}

/// <summary>
/// Pure first-run setup policy. Revision zero is the missing/default config value, so revision
/// one opens once for both a fresh install and an existing installation upgrading to v4.
/// Future releases should increment this only when another automatic setup pass is intentional.
/// </summary>
internal static class FirstRunSetupPolicy
{
    internal const int CurrentRevision = 1;

    internal static bool NeedsAutomaticSetup(int completedRevision)
        => completedRevision < CurrentRevision;

    internal static FirstRunSetupTriggerReason TriggerReasonFor(
        int completedRevision,
        bool manualRequested)
    {
        if (manualRequested)
            return FirstRunSetupTriggerReason.Manual;

        return NeedsAutomaticSetup(completedRevision)
            ? FirstRunSetupTriggerReason.AutomaticRevisionRequired
            : FirstRunSetupTriggerReason.None;
    }

    internal static FirstRunSetupState Evaluate(
        int completedRevision,
        bool manualRequested = false)
        => new(
            completedRevision,
            CurrentRevision,
            TriggerReasonFor(completedRevision, manualRequested));

    internal static int RevisionToStoreOnCompletion(int completedRevision)
        => Math.Max(completedRevision, CurrentRevision);
}
