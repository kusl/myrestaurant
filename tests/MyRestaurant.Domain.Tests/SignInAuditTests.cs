using MyRestaurant.Domain.Authentication;
using Xunit;

namespace MyRestaurant.Domain.Tests;

public sealed class SignInAuditTests
{
    [Theory]
    [InlineData(SignInAttemptResult.Succeeded, SecurityEventType.SignInSucceeded)]
    [InlineData(SignInAttemptResult.Failed, SecurityEventType.SignInFailed)]
    [InlineData(SignInAttemptResult.NotAllowed, SecurityEventType.SignInFailed)]
    [InlineData(SignInAttemptResult.LockedOut, SecurityEventType.AccountLockedOut)]
    public void SecurityEventFor_TerminalOutcomes_MapToTheExpectedEvent(
        SignInAttemptResult result,
        string expectedEventType)
        => Assert.Equal(expectedEventType, SignInAudit.SecurityEventFor(result));

    [Fact]
    public void SecurityEventFor_RequiresTwoFactor_RecordsNothing()
        => Assert.Null(SignInAudit.SecurityEventFor(SignInAttemptResult.RequiresTwoFactor));

    [Fact]
    public void MetricResultFor_Success_IsSucceeded()
        => Assert.Equal(SignInAudit.MetricSucceeded, SignInAudit.MetricResultFor(SignInAttemptResult.Succeeded));

    [Theory]
    [InlineData(SignInAttemptResult.Failed)]
    [InlineData(SignInAttemptResult.NotAllowed)]
    [InlineData(SignInAttemptResult.LockedOut)]
    public void MetricResultFor_NonSuccessTerminalOutcomes_AreFailed(SignInAttemptResult result)
        => Assert.Equal(SignInAudit.MetricFailed, SignInAudit.MetricResultFor(result));

    [Fact]
    public void MetricResultFor_RequiresTwoFactor_IsNotMetered()
        => Assert.Null(SignInAudit.MetricResultFor(SignInAttemptResult.RequiresTwoFactor));

    [Fact]
    public void EverySecurityEventProduced_IsAKnownType()
    {
        foreach (SignInAttemptResult result in Enum.GetValues<SignInAttemptResult>())
        {
            string? eventType = SignInAudit.SecurityEventFor(result);
            if (eventType is not null)
            {
                Assert.True(SecurityEventType.IsKnown(eventType), $"{result} produced an unknown event type '{eventType}'.");
            }
        }
    }

    [Fact]
    public void AuditAndMetric_AgreeOnWhenToRecord()
    {
        foreach (SignInAttemptResult result in Enum.GetValues<SignInAttemptResult>())
        {
            bool hasEvent = SignInAudit.SecurityEventFor(result) is not null;
            bool hasMetric = SignInAudit.MetricResultFor(result) is not null;
            Assert.Equal(hasEvent, hasMetric);
        }
    }
}
