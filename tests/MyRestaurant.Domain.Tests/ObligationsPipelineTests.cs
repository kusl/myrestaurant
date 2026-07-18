using MyRestaurant.Domain.Authentication;
using Xunit;

namespace MyRestaurant.Domain.Tests;

/// <summary>
/// Verifies the post-authentication obligations state machine (TECHNICAL_SPECIFICATION §3.5): a forced
/// password change always precedes forced TOTP enrollment, and no other endpoint is reachable until
/// the pipeline clears (except the pipeline's own pages and sign-out).
/// </summary>
public sealed class ObligationsPipelineTests
{
    [Theory]
    [InlineData(false, false, PostAuthenticationObligation.None)]
    [InlineData(true, false, PostAuthenticationObligation.ForcePasswordChange)]
    [InlineData(true, true, PostAuthenticationObligation.ForcePasswordChange)]  // password wins when both set
    [InlineData(false, true, PostAuthenticationObligation.ForceTotpEnrollment)]
    public void NextObligation_ResolvesInPasswordThenTotpOrder(
        bool mustChangePassword,
        bool mustEnrollTotp,
        PostAuthenticationObligation expected)
        => Assert.Equal(expected, ObligationsPipeline.NextObligation(mustChangePassword, mustEnrollTotp));

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void IsCleared_OnlyWhenNoFlagSet(bool mustChangePassword, bool mustEnrollTotp, bool expected)
        => Assert.Equal(expected, ObligationsPipeline.IsCleared(mustChangePassword, mustEnrollTotp));

    [Fact]
    public void MayReachEndpoint_AllowsAnyEndpointWhenCleared()
        => Assert.True(ObligationsPipeline.MayReachEndpoint(false, false, endpointIsPipelineOrSignOut: false));

    [Fact]
    public void MayReachEndpoint_BlocksOrdinaryEndpointsWhileObligationsPending()
        => Assert.False(ObligationsPipeline.MayReachEndpoint(true, false, endpointIsPipelineOrSignOut: false));

    [Fact]
    public void MayReachEndpoint_AllowsPipelineAndSignOutWhileObligationsPending()
        => Assert.True(ObligationsPipeline.MayReachEndpoint(true, true, endpointIsPipelineOrSignOut: true));
}
