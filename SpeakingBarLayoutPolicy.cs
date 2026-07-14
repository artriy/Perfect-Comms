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

internal readonly record struct SpeakingBarSlotCoordinates(float X, float Y);

/// <summary>
/// Result of evaluating every balanced line count in both viewport dimensions.
/// ContentWidth/ContentHeight are the unscaled footprint of the chosen layout,
/// including any outer padding supplied by the caller.
/// </summary>
internal readonly record struct SpeakingBarLayoutSolution(
    int LineCount,
    int MaxItemsInLine,
    float ContentWidth,
    float ContentHeight,
    float EffectiveScale,
    bool FitsRequestedScale);

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

    internal static SpeakingBarNamePosition ResolveNamePosition(
        SpeakingBarNamePosition configured,
        bool manualLayout,
        VoiceControlsLayout manualOrientation,
        float manualX,
        float manualY,
        SpeakingBarPosition preset)
    {
        if (configured != SpeakingBarNamePosition.Auto)
            return configured;
        if (!manualLayout)
            return RecommendedNamePositionFor(preset);

        float leftRightDistance = Math.Min(manualX, 1f - manualX);
        float topBottomDistance = Math.Min(manualY, 1f - manualY);
        if (leftRightDistance > 0.33f && topBottomDistance > 0.33f)
            return manualOrientation == VoiceControlsLayout.Vertical
                ? SpeakingBarNamePosition.Right
                : SpeakingBarNamePosition.Bottom;
        if (leftRightDistance <= topBottomDistance)
            return manualX <= 0.5f ? SpeakingBarNamePosition.Right : SpeakingBarNamePosition.Left;
        return manualY >= 0.5f ? SpeakingBarNamePosition.Bottom : SpeakingBarNamePosition.Top;
    }

    internal static SpeakingBarLayoutDirection ResolvePrimaryDirection(
        bool manualLayout,
        SpeakingBarPosition preset,
        bool vertical,
        float manualY)
    {
        if (!manualLayout)
            return PrimaryDirectionFor(preset);
        if (!vertical)
            return SpeakingBarLayoutDirection.Right;
        return manualY < 0.5f ? SpeakingBarLayoutDirection.Up : SpeakingBarLayoutDirection.Down;
    }

    internal static SpeakingBarLayoutDirection ResolveOverflowDirection(
        bool manualLayout,
        SpeakingBarPosition preset,
        bool vertical,
        float manualX,
        float manualY)
    {
        if (!manualLayout)
            return OverflowDirectionFor(preset);
        if (vertical)
            return manualX <= 0.5f ? SpeakingBarLayoutDirection.Right : SpeakingBarLayoutDirection.Left;
        return manualY < 0.5f ? SpeakingBarLayoutDirection.Up : SpeakingBarLayoutDirection.Down;
    }

    internal static bool CentersVerticalPrimaryLine(
        bool manualLayout,
        SpeakingBarPosition preset,
        float manualY)
        => manualLayout
            ? manualY > 0.33f && manualY < 0.67f
            : preset is SpeakingBarPosition.MiddleLeft or SpeakingBarPosition.MiddleRight;

    internal static bool CentersOverflowLines(
        bool manualLayout,
        bool vertical,
        float manualX,
        float manualY)
        => manualLayout && (vertical
            ? manualX > 0.33f && manualX < 0.67f
            : manualY > 0.33f && manualY < 0.67f);

    internal static SpeakingBarSlotCoordinates CoordinatesFor(
        SpeakingBarLinePlacement placement,
        int lineCount,
        bool vertical,
        SpeakingBarLayoutDirection primaryDirection,
        SpeakingBarLayoutDirection overflowDirection,
        bool centerVerticalPrimaryLine,
        bool centerOverflowLines,
        float crossPitch,
        float primaryPitch)
    {
        if (lineCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(lineCount), lineCount, "Line count must be positive.");
        ValidatePositiveFinite(crossPitch, nameof(crossPitch));
        ValidatePositiveFinite(primaryPitch, nameof(primaryPitch));

        if (vertical)
        {
            float overflowSign = overflowDirection == SpeakingBarLayoutDirection.Left ? -1f : 1f;
            float primarySign = primaryDirection == SpeakingBarLayoutDirection.Up ? 1f : -1f;
            float x = centerOverflowLines
                ? (placement.LineIndex - (lineCount - 1) * 0.5f) * crossPitch
                : placement.LineIndex * crossPitch * overflowSign;
            float indexOffset = placement.IndexInLine;
            if (centerVerticalPrimaryLine)
                indexOffset -= (placement.LineSize - 1) * 0.5f;
            return new SpeakingBarSlotCoordinates(x, indexOffset * primaryPitch * primarySign);
        }

        float horizontalOverflowSign = overflowDirection == SpeakingBarLayoutDirection.Up ? 1f : -1f;
        float horizontalX = (placement.IndexInLine - (placement.LineSize - 1) * 0.5f) * crossPitch;
        float horizontalY = centerOverflowLines
            ? ((lineCount - 1) * 0.5f - placement.LineIndex) * primaryPitch
            : placement.LineIndex * primaryPitch * horizontalOverflowSign;
        return new SpeakingBarSlotCoordinates(horizontalX, horizontalY);
    }

    internal static bool IsSidePreset(SpeakingBarPosition position) => position switch
    {
        SpeakingBarPosition.TopLeft or
        SpeakingBarPosition.TopRight or
        SpeakingBarPosition.MiddleLeft or
        SpeakingBarPosition.MiddleRight or
        SpeakingBarPosition.BottomLeft or
        SpeakingBarPosition.BottomRight => true,
        SpeakingBarPosition.TopMiddle or
        SpeakingBarPosition.BottomMiddle => false,
        _ => throw new ArgumentOutOfRangeException(nameof(position), position, "Unknown speaking-bar preset."),
    };

    internal static bool UsesSingleVerticalLaneFor(
        SpeakingBarPosition position,
        SpeakingBarSideLayout sideLayout)
    {
        bool singleLane = sideLayout switch
        {
            SpeakingBarSideLayout.SingleLane => true,
            SpeakingBarSideLayout.Wrapped => false,
            _ => throw new ArgumentOutOfRangeException(nameof(sideLayout), sideLayout,
                "Unknown speaking-bar side-layout value."),
        };
        return IsSidePreset(position) && singleLane;
    }

    internal static bool AvatarFacesLeftFor(SpeakingBarPosition position) => position switch
    {
        SpeakingBarPosition.TopRight or
        SpeakingBarPosition.MiddleRight or
        SpeakingBarPosition.BottomRight => true,
        SpeakingBarPosition.TopLeft or
        SpeakingBarPosition.TopMiddle or
        SpeakingBarPosition.MiddleLeft or
        SpeakingBarPosition.BottomLeft or
        SpeakingBarPosition.BottomMiddle => false,
        _ => throw new ArgumentOutOfRangeException(nameof(position), position, "Unknown speaking-bar preset."),
    };

    internal static bool ResolveAvatarFacesLeft(
        bool manualLayout,
        SpeakingBarPosition position,
        SpeakingBarAvatarFacing manualFacing)
    {
        if (!manualLayout)
            return AvatarFacesLeftFor(position);

        return manualFacing switch
        {
            SpeakingBarAvatarFacing.Right => false,
            SpeakingBarAvatarFacing.Left => true,
            _ => throw new ArgumentOutOfRangeException(nameof(manualFacing), manualFacing,
                "Unknown manual avatar-facing value."),
        };
    }

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

    /// <summary>
    /// Chooses a balanced two-dimensional layout. Every candidate keeps at most the requested
    /// <paramref name="maxItemsPerLine"/> items in a line. Candidates are considered
    /// from fewest to most lines, so the first layout that fits at the requested scale wins.
    /// If none fits, the layout with the largest possible uniform scale wins; equal-scale
    /// ties retain the candidate with fewer lines.
    /// </summary>
    /// <param name="vertical">
    /// True when items advance vertically within a line and overflow into columns;
    /// false when items advance horizontally and overflow into rows.
    /// </param>
    /// <param name="itemWidth">Unscaled width of one conservative item footprint.</param>
    /// <param name="itemHeight">Unscaled height of one conservative item footprint.</param>
    /// <param name="horizontalPitch">Unscaled distance between adjacent item/column centres.</param>
    /// <param name="verticalPitch">Unscaled distance between adjacent item/row centres.</param>
    /// <param name="requiredLineCount">
    /// Optional hard line-count constraint. Side presets use one line to scale a BCL-style
    /// vertical lane instead of allowing the solver to add inward columns.
    /// </param>
    internal static SpeakingBarLayoutSolution SolveTwoDimensional(
        int itemCount,
        bool vertical,
        float itemWidth,
        float itemHeight,
        float horizontalPitch,
        float verticalPitch,
        float availableWidth,
        float availableHeight,
        float requestedScale,
        int maxItemsPerLine = PreferredMaxItemsPerLine,
        int? requiredLineCount = null)
    {
        if (itemCount < 0)
            throw new ArgumentOutOfRangeException(nameof(itemCount), itemCount, "Item count cannot be negative.");
        ValidatePositiveFinite(itemWidth, nameof(itemWidth));
        ValidatePositiveFinite(itemHeight, nameof(itemHeight));
        ValidatePositiveFinite(horizontalPitch, nameof(horizontalPitch));
        ValidatePositiveFinite(verticalPitch, nameof(verticalPitch));
        ValidatePositiveOrInfinity(availableWidth, nameof(availableWidth));
        ValidatePositiveOrInfinity(availableHeight, nameof(availableHeight));
        ValidatePositiveFinite(requestedScale, nameof(requestedScale));
        if (maxItemsPerLine <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxItemsPerLine), maxItemsPerLine,
                "Maximum items per line must be positive.");

        if (itemCount == 0)
        {
            if (requiredLineCount.HasValue)
                throw new ArgumentOutOfRangeException(nameof(requiredLineCount), requiredLineCount,
                    "An empty layout cannot require a line count.");
            return new SpeakingBarLayoutSolution(0, 0, 0f, 0f, requestedScale, true);
        }

        int minimumLineCount = 1 + (itemCount - 1) / maxItemsPerLine;
        int maximumLineCount = itemCount;
        if (requiredLineCount.HasValue)
        {
            int required = requiredLineCount.Value;
            if (required < minimumLineCount || required > itemCount)
                throw new ArgumentOutOfRangeException(nameof(requiredLineCount), required,
                    "Required line count must fit the item count and maximum items per line.");
            minimumLineCount = required;
            maximumLineCount = required;
        }

        SpeakingBarLayoutSolution best = default;
        float bestScale = -1f;

        for (int lineCount = minimumLineCount; lineCount <= maximumLineCount; lineCount++)
        {
            int maxItemsInLine = GetLineSize(itemCount, lineCount, 0);
            float contentWidth = vertical
                ? itemWidth + (lineCount - 1) * horizontalPitch
                : itemWidth + (maxItemsInLine - 1) * horizontalPitch;
            float contentHeight = vertical
                ? itemHeight + (maxItemsInLine - 1) * verticalPitch
                : itemHeight + (lineCount - 1) * verticalPitch;

            float widthScale = availableWidth / contentWidth;
            float heightScale = availableHeight / contentHeight;
            float availableScale = Math.Min(widthScale, heightScale);
            bool fitsRequestedScale = availableScale + 0.00001f >= requestedScale;
            float effectiveScale = fitsRequestedScale
                ? requestedScale
                : Math.Min(requestedScale, availableScale);

            var candidate = new SpeakingBarLayoutSolution(
                lineCount,
                maxItemsInLine,
                contentWidth,
                contentHeight,
                effectiveScale,
                fitsRequestedScale);

            // Line counts increase monotonically, so this is the requested-scale fit with
            // the least wrapping and is therefore the stable, deterministic winner.
            if (fitsRequestedScale)
                return candidate;

            if (effectiveScale > bestScale + 0.00001f)
            {
                best = candidate;
                bestScale = effectiveScale;
            }
        }

        return best;
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

    private static void ValidatePositiveFinite(float value, string paramName)
    {
        if (value <= 0f || float.IsNaN(value) || float.IsInfinity(value))
            throw new ArgumentOutOfRangeException(paramName, value, "Value must be positive and finite.");
    }

    private static void ValidatePositiveOrInfinity(float value, string paramName)
    {
        if (value <= 0f || float.IsNaN(value) || float.IsNegativeInfinity(value))
            throw new ArgumentOutOfRangeException(paramName, value, "Value must be positive.");
    }
}
