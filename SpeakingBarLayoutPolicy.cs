using System;

namespace VoiceChatPlugin.VoiceChat;

internal enum SpeakingBarLayoutDirection
{
    Left,
    Right,
    Up,
    Down,
}

internal readonly record struct SpeakingBarPresetLayout(
    VoiceControlsLayout Orientation,
    SpeakingBarNamePosition RecommendedNamePosition,
    SpeakingBarLayoutDirection PrimaryDirection,
    SpeakingBarLayoutDirection OverflowDirection);

internal readonly record struct SpeakingBarLinePlacement(
    int LineIndex,
    int IndexInLine,
    int LineSize);

/// <summary>
/// Pure policy for speaking-bar preset flow and balanced wrapping. Unity-facing code is
/// responsible for measuring the safe-area capacity and applying the returned placement.
/// </summary>
internal static class SpeakingBarLayoutPolicy
{
    // Nine is the largest comfortable single line. The tenth item therefore starts a
    // second balanced line: 10 -> 5+5, 11 -> 6+5, and 15 -> 8+7.
    internal const int PreferredMaxItemsPerLine = 9;

    internal static SpeakingBarPresetLayout ForPreset(SpeakingBarPosition position) => position switch
    {
        SpeakingBarPosition.TopLeft => new(
            VoiceControlsLayout.Vertical,
            SpeakingBarNamePosition.Right,
            SpeakingBarLayoutDirection.Down,
            SpeakingBarLayoutDirection.Right),
        SpeakingBarPosition.TopMiddle => new(
            VoiceControlsLayout.Horizontal,
            SpeakingBarNamePosition.Bottom,
            SpeakingBarLayoutDirection.Right,
            SpeakingBarLayoutDirection.Down),
        SpeakingBarPosition.TopRight => new(
            VoiceControlsLayout.Vertical,
            SpeakingBarNamePosition.Left,
            SpeakingBarLayoutDirection.Down,
            SpeakingBarLayoutDirection.Left),
        SpeakingBarPosition.MiddleLeft => new(
            VoiceControlsLayout.Vertical,
            SpeakingBarNamePosition.Right,
            SpeakingBarLayoutDirection.Down,
            SpeakingBarLayoutDirection.Right),
        SpeakingBarPosition.MiddleRight => new(
            VoiceControlsLayout.Vertical,
            SpeakingBarNamePosition.Left,
            SpeakingBarLayoutDirection.Down,
            SpeakingBarLayoutDirection.Left),
        SpeakingBarPosition.BottomLeft => new(
            VoiceControlsLayout.Vertical,
            SpeakingBarNamePosition.Right,
            SpeakingBarLayoutDirection.Up,
            SpeakingBarLayoutDirection.Right),
        SpeakingBarPosition.BottomMiddle => new(
            VoiceControlsLayout.Horizontal,
            SpeakingBarNamePosition.Top,
            SpeakingBarLayoutDirection.Right,
            SpeakingBarLayoutDirection.Up),
        SpeakingBarPosition.BottomRight => new(
            VoiceControlsLayout.Vertical,
            SpeakingBarNamePosition.Left,
            SpeakingBarLayoutDirection.Up,
            SpeakingBarLayoutDirection.Left),
        _ => throw new ArgumentOutOfRangeException(nameof(position), position, "Unknown speaking-bar preset."),
    };

    internal static VoiceControlsLayout OrientationFor(SpeakingBarPosition position)
        => ForPreset(position).Orientation;

    internal static SpeakingBarNamePosition RecommendedNamePositionFor(SpeakingBarPosition position)
        => ForPreset(position).RecommendedNamePosition;

    internal static SpeakingBarLayoutDirection PrimaryDirectionFor(SpeakingBarPosition position)
        => ForPreset(position).PrimaryDirection;

    internal static SpeakingBarLayoutDirection OverflowDirectionFor(SpeakingBarPosition position)
        => ForPreset(position).OverflowDirection;

    /// <summary>
    /// Returns the number of balanced lines needed for <paramref name="itemCount"/>.
    /// <paramref name="safeCapacity"/> is the number of items measured to fit on one line
    /// at the requested scale. Values below one degrade to one item per line.
    /// </summary>
    internal static int GetLineCount(int itemCount, int safeCapacity = int.MaxValue)
    {
        if (itemCount < 0)
            throw new ArgumentOutOfRangeException(nameof(itemCount), itemCount, "Item count cannot be negative.");
        if (itemCount == 0)
            return 0;

        int capacity = Math.Min(PreferredMaxItemsPerLine, Math.Max(1, safeCapacity));
        return 1 + (itemCount - 1) / capacity;
    }

    internal static int GetLineSize(int itemCount, int lineCount, int lineIndex)
    {
        ValidateLine(itemCount, lineCount, lineIndex);
        int baseSize = itemCount / lineCount;
        int largerLineCount = itemCount % lineCount;
        return baseSize + (lineIndex < largerLineCount ? 1 : 0);
    }

    internal static int GetLineStartIndex(int itemCount, int lineCount, int lineIndex)
    {
        ValidateLine(itemCount, lineCount, lineIndex);
        int baseSize = itemCount / lineCount;
        int largerLineCount = itemCount % lineCount;
        return lineIndex * baseSize + Math.Min(lineIndex, largerLineCount);
    }

    internal static int GetLineIndex(int itemIndex, int itemCount, int lineCount)
    {
        ValidateItem(itemIndex, itemCount, lineCount);

        int baseSize = itemCount / lineCount;
        int largerLineCount = itemCount % lineCount;
        int largerLineSize = baseSize + 1;
        int itemsInLargerLines = largerLineCount * largerLineSize;

        if (itemIndex < itemsInLargerLines)
            return itemIndex / largerLineSize;

        return largerLineCount + (itemIndex - itemsInLargerLines) / baseSize;
    }

    internal static int GetIndexInLine(int itemIndex, int itemCount, int lineCount)
    {
        int lineIndex = GetLineIndex(itemIndex, itemCount, lineCount);
        return itemIndex - GetLineStartIndex(itemCount, lineCount, lineIndex);
    }

    internal static SpeakingBarLinePlacement GetPlacement(int itemIndex, int itemCount, int lineCount)
    {
        int lineIndex = GetLineIndex(itemIndex, itemCount, lineCount);
        return new SpeakingBarLinePlacement(
            lineIndex,
            itemIndex - GetLineStartIndex(itemCount, lineCount, lineIndex),
            GetLineSize(itemCount, lineCount, lineIndex));
    }

    private static void ValidateLine(int itemCount, int lineCount, int lineIndex)
    {
        ValidateDistribution(itemCount, lineCount);
        if ((uint)lineIndex >= (uint)lineCount)
            throw new ArgumentOutOfRangeException(nameof(lineIndex), lineIndex, "Line index is outside the distribution.");
    }

    private static void ValidateItem(int itemIndex, int itemCount, int lineCount)
    {
        ValidateDistribution(itemCount, lineCount);
        if ((uint)itemIndex >= (uint)itemCount)
            throw new ArgumentOutOfRangeException(nameof(itemIndex), itemIndex, "Item index is outside the distribution.");
    }

    private static void ValidateDistribution(int itemCount, int lineCount)
    {
        if (itemCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(itemCount), itemCount, "A line distribution requires at least one item.");
        if (lineCount <= 0 || lineCount > itemCount)
            throw new ArgumentOutOfRangeException(nameof(lineCount), lineCount, "Line count must be between one and the item count.");
    }
}
