using System;
using System.Collections.Generic;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

/// <summary>
/// Ordering and capping rules for the taskbar's multi-provider tile list (issue #88): visible, available
/// pins lead most-recently-active first, the active provider fills any remaining slot, and the list never
/// exceeds the tile cap.
/// </summary>
public class WidgetDisplayProvidersTests
{
    private static IReadOnlyList<ProviderId> Compute(
        ProviderId? active,
        IReadOnlyList<ProviderId> pinned,
        IReadOnlyList<ProviderId>? recent = null,
        bool present = true,
        Func<ProviderId, bool>? isVisible = null,
        Func<ProviderId, bool>? isAvailable = null,
        bool activityWidgetEnabled = false)
        => UsageCoordinator.ComputeWidgetDisplayProviders(
            active,
            present,
            recent ?? Array.Empty<ProviderId>(),
            Enum.GetValues<ProviderId>(),
            p => pinned.Contains(p),
            isVisible ?? (_ => true),
            isAvailable ?? (_ => true),
            activityWidgetEnabled);

    [Fact]
    public void PinnedProvidersLeadActiveInRecencyOrder()
    {
        // The scenario from the issue thread: Claude used just before Codex, Z.AI never focused.
        var result = Compute(
            active: ProviderId.Codex,
            pinned: new[] { ProviderId.Claude, ProviderId.Zai },
            recent: new[] { ProviderId.Codex, ProviderId.Claude });

        Assert.Equal(new[] { ProviderId.Claude, ProviderId.Zai, ProviderId.Codex }, result);
    }

    [Fact]
    public void ActiveProviderIsNotDuplicatedWhenItIsPinned()
    {
        var result = Compute(
            active: ProviderId.Zai,
            pinned: new[] { ProviderId.Claude, ProviderId.Zai },
            recent: new[] { ProviderId.Zai, ProviderId.Claude });

        Assert.Equal(new[] { ProviderId.Zai, ProviderId.Claude }, result);
    }

    [Fact]
    public void PinnedProvidersStayWhenNoToolIsActive()
    {
        var result = Compute(
            active: null,
            pinned: new[] { ProviderId.Claude },
            present: false);

        Assert.Equal(new[] { ProviderId.Claude }, result);
    }

    [Fact]
    public void WithoutActiveProviderOrPins_IsEmptyEvenWhenAToolIsPresent()
    {
        var result = Compute(
            active: null,
            pinned: Array.Empty<ProviderId>(),
            present: true);

        Assert.Empty(result);
    }

    [Fact]
    public void HiddenOrUnavailablePinnedProvidersAreDropped()
    {
        var hidden = Compute(
            active: ProviderId.Codex,
            pinned: new[] { ProviderId.Claude },
            isVisible: p => p != ProviderId.Claude);
        Assert.Equal(new[] { ProviderId.Codex }, hidden);

        var unavailable = Compute(
            active: ProviderId.Codex,
            pinned: new[] { ProviderId.Claude },
            isAvailable: p => p != ProviderId.Claude);
        Assert.Equal(new[] { ProviderId.Codex }, unavailable);
    }

    [Fact]
    public void ActiveProviderHiddenFromTheWidgetDoesNotTakeATile()
    {
        var result = Compute(
            active: ProviderId.Cursor,
            pinned: new[] { ProviderId.Claude },
            isVisible: p => p != ProviderId.Cursor);

        Assert.Equal(new[] { ProviderId.Claude }, result);
    }

    [Fact]
    public void PinnedProvidersFillTheTileCapBeforeActiveProvider()
    {
        var result = Compute(
            active: ProviderId.Codex,
            pinned: new[] { ProviderId.Claude, ProviderId.Zai, ProviderId.Cursor, ProviderId.Grok });

        Assert.Equal(UsageCoordinator.MaxWidgetTiles, result.Count);
        Assert.DoesNotContain(ProviderId.Codex, result);
    }

    [Fact]
    public void ActivityWidgetShowsTwoPinsWithoutActiveTile()
    {
        var result = Compute(
            active: ProviderId.Codex,
            pinned: new[] { ProviderId.Claude, ProviderId.Zai },
            activityWidgetEnabled: true);

        Assert.Equal(new[] { ProviderId.Claude, ProviderId.Zai }, result);
    }

    [Fact]
    public void OnePinLeavesOneSlotForActiveProvider()
    {
        var result = Compute(
            active: ProviderId.Codex,
            pinned: new[] { ProviderId.Claude });

        Assert.Equal(new[] { ProviderId.Claude, ProviderId.Codex }, result);
    }

    [Fact]
    public void NoPinsShowsOnlyTheActiveProvider()
    {
        var result = Compute(
            active: ProviderId.Codex,
            pinned: Array.Empty<ProviderId>());

        Assert.Equal(new[] { ProviderId.Codex }, result);
    }

    [Fact]
    public void EmptyWhenNothingIsPinnedActiveOrAvailable()
    {
        var result = Compute(
            active: null,
            pinned: Array.Empty<ProviderId>(),
            present: false);

        Assert.Empty(result);
    }
}
