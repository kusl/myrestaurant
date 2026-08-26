namespace MyRestaurant.Domain.Authentication;

public enum PostAuthenticationObligation
{
    None,
    ForcePasswordChange,
    ForceTotpEnrollment,
}

public static class ObligationsPipeline
{
    public static PostAuthenticationObligation NextObligation(bool mustChangePassword, bool mustEnrollTotp)
    {
        if (mustChangePassword)
        {
            return PostAuthenticationObligation.ForcePasswordChange;
        }

        if (mustEnrollTotp)
        {
            return PostAuthenticationObligation.ForceTotpEnrollment;
        }

        return PostAuthenticationObligation.None;
    }

    public static bool IsCleared(bool mustChangePassword, bool mustEnrollTotp)
        => NextObligation(mustChangePassword, mustEnrollTotp) == PostAuthenticationObligation.None;

    public static bool MayReachEndpoint(bool mustChangePassword, bool mustEnrollTotp, bool endpointIsPipelineOrSignOut)
        => IsCleared(mustChangePassword, mustEnrollTotp) || endpointIsPipelineOrSignOut;
}
