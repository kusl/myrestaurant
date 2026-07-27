using MyRestaurant.Domain.LiveUpdates;
using MyRestaurant.WebApplication.Orders;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

/// <summary>
/// Unit tests for <see cref="KitchenAlertState"/> (TECHNICAL_SPECIFICATION §10.3): "Browsers block
/// autoplay: the kitchen surface shows a one-tap 'enable sound' arm control per session; until armed
/// (and whenever playback fails) a persistent, high-contrast visual badge with unseen-alert count is
/// the fallback."
///
/// <para>The failure mode this type guards against is the worst one the kitchen board has, and it is
/// invisible: a board that stops alerting looks exactly like a board with nothing to do. Two facts
/// carry most of that weight — <see cref="TheTokenAdvancesOnEveryAlert"/>, because the token is what the
/// component uses to decide whether to make a noise, and
/// <see cref="AcknowledgingDoesNotRewindTheToken"/>, because rewinding it would make the next alert
/// collide with one already announced and go silent.</para>
/// </summary>
public sealed class KitchenAlertStateTests
{
    [Fact]
    public void AFreshBoardIsUnarmedWithNothingUnseen()
    {
        KitchenAlertState state = new();

        Assert.False(state.IsArmed);
        Assert.False(state.PlaybackFailed);
        Assert.False(state.HasUnseen);
        Assert.Equal(0, state.UnseenCount);
        Assert.Equal(0, state.AlertToken);

        // §10.3: the badge is the fallback "until armed".
        Assert.True(state.ShowsVisualFallback);
    }

    [Fact]
    public void RecordingAnAlertCountsItAndAdvancesTheToken()
    {
        KitchenAlertState state = new();

        Assert.True(state.Record(KitchenAlertKind.Initial));

        Assert.Equal(1, state.UnseenCount);
        Assert.True(state.HasUnseen);
        Assert.Equal(1, state.AlertToken);
        Assert.False(state.LastAlertWasReminder);
    }

    /// <summary>
    /// A second alert arriving while the first is still unacknowledged must still bump the token —
    /// otherwise the second send of a rush is silent, which is precisely when silence costs most.
    /// </summary>
    [Fact]
    public void TheTokenAdvancesOnEveryAlert()
    {
        KitchenAlertState state = new();

        state.Record(KitchenAlertKind.Initial);
        state.Record(KitchenAlertKind.Initial);
        state.Record(KitchenAlertKind.Reminder);

        Assert.Equal(3, state.AlertToken);
        Assert.Equal(3, state.UnseenCount);
    }

    [Fact]
    public void RemindersAreCountedSeparatelyAndRemembered()
    {
        KitchenAlertState state = new();

        state.Record(KitchenAlertKind.Initial);
        state.Record(KitchenAlertKind.Reminder);

        Assert.Equal(2, state.UnseenCount);
        Assert.Equal(1, state.UnseenReminderCount);
        Assert.True(state.LastAlertWasReminder);

        state.Record(KitchenAlertKind.Initial);
        Assert.False(state.LastAlertWasReminder);
    }

    [Fact]
    public void AcknowledgingClearsTheCounts()
    {
        KitchenAlertState state = new();
        state.Record(KitchenAlertKind.Reminder);

        Assert.True(state.Acknowledge());

        Assert.Equal(0, state.UnseenCount);
        Assert.Equal(0, state.UnseenReminderCount);
        Assert.False(state.HasUnseen);
    }

    [Fact]
    public void AcknowledgingNothingReportsNoChange()
    {
        KitchenAlertState state = new();

        Assert.False(state.Acknowledge());
    }

    /// <summary>
    /// The token is a monotonic sequence, not a count. Resetting it on acknowledgement would make the
    /// next alert's token equal to one the component has already announced, and the board would go
    /// quiet with no error anywhere.
    /// </summary>
    [Fact]
    public void AcknowledgingDoesNotRewindTheToken()
    {
        KitchenAlertState state = new();
        state.Record(KitchenAlertKind.Initial);
        state.Record(KitchenAlertKind.Initial);

        int before = state.AlertToken;
        state.Acknowledge();

        Assert.Equal(before, state.AlertToken);

        state.Record(KitchenAlertKind.Initial);
        Assert.Equal(before + 1, state.AlertToken);
    }

    [Fact]
    public void ArmingSuccessfullyPutsTheBadgeAway()
    {
        KitchenAlertState state = new();

        Assert.True(state.Arm(succeeded: true));

        Assert.True(state.IsArmed);
        Assert.False(state.PlaybackFailed);
        Assert.False(state.ShowsVisualFallback);
    }

    [Fact]
    public void ArmingAgainWhenAlreadyArmedReportsNoChange()
    {
        KitchenAlertState state = new();
        state.Arm(succeeded: true);

        Assert.False(state.Arm(succeeded: true));
    }

    /// <summary>
    /// A refused arm is not merely "still unarmed": the person pressed the button, so the board owes
    /// them an explanation rather than continued silence.
    /// </summary>
    [Fact]
    public void ArmingThatFailsIsRecordedAsAFailure()
    {
        KitchenAlertState state = new();

        Assert.True(state.Arm(succeeded: false));

        Assert.False(state.IsArmed);
        Assert.True(state.PlaybackFailed);
        Assert.True(state.ShowsVisualFallback);
    }

    /// <summary>§10.3: the fallback returns "whenever playback fails", armed or not.</summary>
    [Fact]
    public void PlaybackFailingAfterArmingRaisesTheFallbackAgain()
    {
        KitchenAlertState state = new();
        state.Arm(succeeded: true);

        Assert.True(state.ReportPlaybackFailed());

        Assert.True(state.IsArmed);
        Assert.True(state.PlaybackFailed);
        Assert.True(state.ShowsVisualFallback);
    }

    [Fact]
    public void ARunOfPlaybackFailuresCostsOneStateChange()
    {
        KitchenAlertState state = new();
        state.Arm(succeeded: true);

        Assert.True(state.ReportPlaybackFailed());
        Assert.False(state.ReportPlaybackFailed());
        Assert.False(state.ReportPlaybackFailed());
    }

    [Fact]
    public void PlaybackRecoveringClearsTheFailure()
    {
        KitchenAlertState state = new();
        state.Arm(succeeded: true);
        state.ReportPlaybackFailed();

        Assert.True(state.ReportPlaybackSucceeded());

        Assert.False(state.PlaybackFailed);
        Assert.False(state.ShowsVisualFallback);
        Assert.False(state.ReportPlaybackSucceeded());
    }
}
