using MyRestaurant.Domain.LiveUpdates;
using MyRestaurant.WebApplication.Orders;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

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

    [Fact]
    public void ArmingThatFailsIsRecordedAsAFailure()
    {
        KitchenAlertState state = new();

        Assert.True(state.Arm(succeeded: false));

        Assert.False(state.IsArmed);
        Assert.True(state.PlaybackFailed);
        Assert.True(state.ShowsVisualFallback);
    }

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
