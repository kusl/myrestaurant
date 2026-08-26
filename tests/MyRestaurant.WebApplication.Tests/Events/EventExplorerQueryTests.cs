using MyRestaurant.DataAccess.Events;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.WebApplication.Events;
using MyRestaurant.WebApplication.Time;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

public sealed class EventExplorerQueryTests
{
    [Fact]
    public void NoQueryString_SelectsAllThreeStreamsAndNoOtherBound()
    {
        EventExplorerQuery query = Parse();

        Assert.True(query.IncludeSecurityEvents);
        Assert.True(query.IncludeOrderEvents);
        Assert.True(query.IncludeMenuEvents);
        Assert.Null(query.Subject);
        Assert.Null(query.Actor);
        Assert.Null(query.EventType);
        Assert.False(query.IsNarrowed);
        Assert.Empty(query.Problems);
        Assert.Equal(EventExplorerQuery.BasePath, query.ToPath());
    }

    [Fact]
    public void OneStreamNamed_SelectsOnlyThatStream()
    {
        EventExplorerQuery query = Parse(streams: [EventStream.Order]);

        Assert.False(query.IncludeSecurityEvents);
        Assert.True(query.IncludeOrderEvents);
        Assert.False(query.IncludeMenuEvents);
        Assert.True(query.IsNarrowed);
    }

    [Fact]
    public void NoStreamNamed_MeansAllThreeRatherThanNone()
    {
        EventExplorerQuery query = Parse(streams: []);

        Assert.True(query.IncludeSecurityEvents);
        Assert.True(query.IncludeOrderEvents);
        Assert.True(query.IncludeMenuEvents);
        Assert.False(query.Filter.IncludesNoStream);
    }

    [Fact]
    public void StreamNames_AreMatchedCaseInsensitively()
    {
        EventExplorerQuery query = Parse(streams: ["SECURITY", "Menu"]);

        Assert.True(query.IncludeSecurityEvents);
        Assert.False(query.IncludeOrderEvents);
        Assert.True(query.IncludeMenuEvents);
    }

    [Fact]
    public void UnknownStreamName_IsIgnoredAndReported()
    {
        EventExplorerQuery query = Parse(streams: ["securty", EventStream.Menu]);

        Assert.False(query.IncludeSecurityEvents);
        Assert.True(query.IncludeMenuEvents);
        Assert.Contains(query.Problems, problem => problem.Contains("securty", StringComparison.Ordinal));
    }

    [Fact]
    public void ToPath_OmitsTheStreamParameterWhenAllThreeAreSelected()
    {
        Assert.Equal(EventExplorerQuery.BasePath, Parse(streams: [EventStream.Security, EventStream.Order, EventStream.Menu]).ToPath());
    }

    [Fact]
    public void ToPath_WritesEachSelectedStreamWhenNarrowed()
    {
        Assert.Equal(
            $"{EventExplorerQuery.BasePath}?stream=security&stream=menu",
            Parse(streams: [EventStream.Menu, EventStream.Security]).ToPath());
    }

    [Fact]
    public void BlankTextBounds_AreNoFilterAtAll()
    {
        EventExplorerQuery query = Parse(subject: "   ", actor: string.Empty, eventType: "  ");

        Assert.Null(query.Subject);
        Assert.Null(query.Actor);
        Assert.Null(query.EventType);
        Assert.False(query.IsNarrowed);
        Assert.Equal(EventExplorerQuery.BasePath, query.ToPath());
    }

    [Fact]
    public void TextBounds_AreTrimmed()
    {
        EventExplorerQuery query = Parse(subject: "  Ada  ");

        Assert.Equal("Ada", query.Subject);
        Assert.Equal("Ada", query.Filter.Subject);
    }

    [Fact]
    public void ToPath_EscapesTextBounds()
    {
        EventExplorerQuery query = Parse(subject: "Table 7 & 8");

        Assert.Equal(
            $"{EventExplorerQuery.BasePath}?subject=Table%207%20%26%208",
            query.ToPath());
    }

    [Fact]
    public void Dates_BecomeAHalfOpenUtcRangeInTheRestaurantsZone()
    {
        EventExplorerQuery query = Parse(from: "2026-07-26", to: "2026-07-27");

        Assert.Equal(new DateTimeOffset(2026, 7, 26, 4, 0, 0, TimeSpan.Zero), query.Filter.OccurredFrom);
        Assert.Equal(new DateTimeOffset(2026, 7, 28, 4, 0, 0, TimeSpan.Zero), query.Filter.OccurredBefore);
        Assert.True(query.IsNarrowed);
        Assert.Empty(query.Problems);
    }

    [Fact]
    public void Dates_AreTheRestaurantsDay_NotUtcs()
    {
        EventExplorerQuery query = EventExplorerQuery.Parse(
            null, null, null, null, "2026-07-26", null, Clock("Asia/Tokyo"));

        Assert.Equal(new DateTimeOffset(2026, 7, 25, 15, 0, 0, TimeSpan.Zero), query.Filter.OccurredFrom);
    }

    [Fact]
    public void OneDateAlone_LeavesTheOtherBoundOpen()
    {
        EventExplorerQuery query = Parse(to: "2026-07-26");

        Assert.Null(query.Filter.OccurredFrom);
        Assert.Equal(new DateTimeOffset(2026, 7, 27, 4, 0, 0, TimeSpan.Zero), query.Filter.OccurredBefore);
    }

    [Fact]
    public void UnreadableDate_IsIgnoredAndReported()
    {
        EventExplorerQuery query = Parse(from: "26/07/2026");

        Assert.Null(query.Filter.OccurredFrom);
        Assert.Single(query.Problems);
    }

    [Fact]
    public void ReversedRange_DropsBothBoundsAndReports()
    {
        EventExplorerQuery query = Parse(from: "2026-07-27", to: "2026-07-26");

        Assert.Null(query.Filter.OccurredFrom);
        Assert.Null(query.Filter.OccurredBefore);
        Assert.Single(query.Problems);

        Assert.Equal("2026-07-27", query.From);
        Assert.Equal("2026-07-26", query.To);
    }

    [Fact]
    public void SameDayAtBothEnds_IsAOneDayWindow()
    {
        EventExplorerQuery query = Parse(from: "2026-07-26", to: "2026-07-26");

        Assert.Equal(new DateTimeOffset(2026, 7, 26, 4, 0, 0, TimeSpan.Zero), query.Filter.OccurredFrom);
        Assert.Equal(new DateTimeOffset(2026, 7, 27, 4, 0, 0, TimeSpan.Zero), query.Filter.OccurredBefore);
        Assert.Empty(query.Problems);
    }

    [Fact]
    public void CatalogedType_WithItsStreamOn_RaisesNothing()
    {
        EventExplorerQuery query = Parse(eventType: SecurityEventType.SignInFailed);

        Assert.Equal(SecurityEventType.SignInFailed, query.Filter.EventType);
        Assert.Empty(query.Problems);
    }

    [Fact]
    public void CatalogedType_WithItsStreamOff_IsReportedAsUnmatchable()
    {
        EventExplorerQuery query = Parse(
            streams: [EventStream.Menu], eventType: SecurityEventType.SignInFailed);

        Assert.Equal(SecurityEventType.SignInFailed, query.Filter.EventType);
        Assert.Single(query.Problems);
    }

    [Fact]
    public void UnknownType_IsStillMatchedExactly_AndReported()
    {
        EventExplorerQuery query = Parse(eventType: "not_a_real_event_type");

        Assert.Equal("not_a_real_event_type", query.Filter.EventType);
        Assert.Single(query.Problems);
    }

    [Fact]
    public void ToPath_PreservesEveryBound()
    {
        EventExplorerQuery query = Parse(
            streams: [EventStream.Security, EventStream.Order],
            subject: "ada",
            actor: "mira",
            eventType: SecurityEventType.RoleGranted,
            from: "2026-07-26",
            to: "2026-07-27");

        Assert.Equal(
            $"{EventExplorerQuery.BasePath}?stream=security&stream=order&subject=ada&actor=mira" +
            $"&type={SecurityEventType.RoleGranted}&from=2026-07-26&to=2026-07-27",
            query.ToPath());
    }

    [Fact]
    public void ReparsingItsOwnPath_YieldsTheSameFilter()
    {
        EventExplorerQuery original = Parse(
            streams: [EventStream.Menu],
            subject: "Soup",
            actor: "mira",
            eventType: "price_changed",
            from: "2026-07-26",
            to: "2026-07-27");

        EventExplorerQuery reparsed = Parse(
            streams: [EventStream.Menu],
            subject: original.Subject,
            actor: original.Actor,
            eventType: original.EventType,
            from: original.From,
            to: original.To);

        Assert.Equal(original.Filter, reparsed.Filter);
        Assert.Equal(original.ToPath(), reparsed.ToPath());
    }

    [Fact]
    public void PathWithStreams_ReplacesTheStreamsAndKeepsTheRest()
    {
        EventExplorerQuery query = Parse(subject: "ada", from: "2026-07-26");

        Assert.Equal(
            $"{EventExplorerQuery.BasePath}?stream=order&subject=ada&from=2026-07-26",
            query.PathWithStreams(security: false, order: true, menu: false));
    }

    [Fact]
    public void PathWithSubject_ReplacesTheSubjectAndKeepsTheRest()
    {
        EventExplorerQuery query = Parse(streams: [EventStream.Order], subject: "ada", actor: "mira");

        Assert.Equal(
            $"{EventExplorerQuery.BasePath}?stream=order&subject=Bo&actor=mira",
            query.PathWithSubject("Bo"));
    }

    [Fact]
    public void PathWithActor_ReplacesTheActorAndKeepsTheRest()
    {
        EventExplorerQuery query = Parse(subject: "ada", actor: "mira");

        Assert.Equal(
            $"{EventExplorerQuery.BasePath}?subject=ada&actor=Cass%20Okonkwo",
            query.PathWithActor("Cass Okonkwo"));
    }

    [Fact]
    public void PathWithNull_ClearsOnlyThatBound()
    {
        EventExplorerQuery query = Parse(subject: "ada", actor: "mira", eventType: "price_changed");

        Assert.Equal(
            $"{EventExplorerQuery.BasePath}?subject=ada&actor=mira",
            query.PathWithEventType(null));
    }

    private static EventExplorerQuery Parse(
        IReadOnlyList<string>? streams = null,
        string? subject = null,
        string? actor = null,
        string? eventType = null,
        string? from = null,
        string? to = null)
        => EventExplorerQuery.Parse(streams, subject, actor, eventType, from, to, Clock("America/New_York"));

    private static RestaurantTime Clock(string zoneIdentifier)
        => new(TimeZoneInfo.FindSystemTimeZoneById(zoneIdentifier), zoneIdentifier, usesTwelveHourClock: true);
}
