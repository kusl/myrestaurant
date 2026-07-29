using MyRestaurant.DataAccess.Events;
using MyRestaurant.Domain.Authentication;
using MyRestaurant.WebApplication.Events;
using MyRestaurant.WebApplication.Time;
using Xunit;

namespace MyRestaurant.WebApplication.Tests;

/// <summary>
/// The event explorer's URL handling (TECHNICAL_SPECIFICATION §11.4, §8.1). No server, no container: this
/// is a pure function from six query-string values to a filter and back to a URL, which is exactly why it
/// was lifted out of the page.
///
/// <para>Two properties matter more than any individual case. First, <b>a round trip loses nothing</b>:
/// every affordance on the screen rebuilds the URL, so a bound that survives parsing but not
/// <see cref="EventExplorerQuery.ToPath"/> would silently drop the administrator's question the moment
/// they clicked anything, and the page would look perfectly fine while it happened. Second, <b>dates are
/// the restaurant's</b> (§8.1) — the same rule every rendered instant obeys, applied in the other
/// direction, because somebody typing 26 July means the day the restaurant had, not the day UTC had.</para>
/// </summary>
public sealed class EventExplorerQueryTests
{
    // ---- streams -----------------------------------------------------------------------------------

    /// <summary>
    /// Nothing in the URL is §11.4's default: everything. The screen opens complete and narrows only when
    /// asked.
    /// </summary>
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

    /// <summary>One stream named selects that stream and no other.</summary>
    [Fact]
    public void OneStreamNamed_SelectsOnlyThatStream()
    {
        EventExplorerQuery query = Parse(streams: [EventStream.Order]);

        Assert.False(query.IncludeSecurityEvents);
        Assert.True(query.IncludeOrderEvents);
        Assert.False(query.IncludeMenuEvents);
        Assert.True(query.IsNarrowed);
    }

    /// <summary>
    /// An empty checkbox set and a fresh URL arrive as the same request — no <c>stream</c> values at all —
    /// so they must mean the same thing, and the only defensible meaning is the default.
    /// </summary>
    [Fact]
    public void NoStreamNamed_MeansAllThreeRatherThanNone()
    {
        EventExplorerQuery query = Parse(streams: []);

        Assert.True(query.IncludeSecurityEvents);
        Assert.True(query.IncludeOrderEvents);
        Assert.True(query.IncludeMenuEvents);
        Assert.False(query.Filter.IncludesNoStream);
    }

    /// <summary>Stream names are matched without regard to case, as a hand-typed URL will have them.</summary>
    [Fact]
    public void StreamNames_AreMatchedCaseInsensitively()
    {
        EventExplorerQuery query = Parse(streams: ["SECURITY", "Menu"]);

        Assert.True(query.IncludeSecurityEvents);
        Assert.False(query.IncludeOrderEvents);
        Assert.True(query.IncludeMenuEvents);
    }

    /// <summary>
    /// An unrecognised stream word is ignored and said out loud. Silently widening the answer is the
    /// failure worth avoiding: a typo would otherwise show everything, and the administrator would read
    /// the result as though it were the narrow question they asked.
    /// </summary>
    [Fact]
    public void UnknownStreamName_IsIgnoredAndReported()
    {
        EventExplorerQuery query = Parse(streams: ["securty", EventStream.Menu]);

        Assert.False(query.IncludeSecurityEvents);
        Assert.True(query.IncludeMenuEvents);
        Assert.Contains(query.Problems, problem => problem.Contains("securty", StringComparison.Ordinal));
    }

    /// <summary>
    /// All three selected writes no <c>stream</c> parameter at all — it is the default, and a URL that
    /// spells out its defaults is a URL nobody can read.
    /// </summary>
    [Fact]
    public void ToPath_OmitsTheStreamParameterWhenAllThreeAreSelected()
    {
        Assert.Equal(EventExplorerQuery.BasePath, Parse(streams: [EventStream.Security, EventStream.Order, EventStream.Menu]).ToPath());
    }

    /// <summary>Fewer than three writes each selected stream, in a fixed order.</summary>
    [Fact]
    public void ToPath_WritesEachSelectedStreamWhenNarrowed()
    {
        Assert.Equal(
            $"{EventExplorerQuery.BasePath}?stream=security&stream=menu",
            Parse(streams: [EventStream.Menu, EventStream.Security]).ToPath());
    }

    // ---- text bounds -------------------------------------------------------------------------------

    /// <summary>A blank box is not a filter, and does not appear in the URL either.</summary>
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

    /// <summary>Surrounding whitespace is trimmed on the way in, so the URL is canonical.</summary>
    [Fact]
    public void TextBounds_AreTrimmed()
    {
        EventExplorerQuery query = Parse(subject: "  Ada  ");

        Assert.Equal("Ada", query.Subject);
        Assert.Equal("Ada", query.Filter.Subject);
    }

    /// <summary>Anything a URL cannot carry raw is escaped, and the whole bound survives the trip.</summary>
    [Fact]
    public void ToPath_EscapesTextBounds()
    {
        EventExplorerQuery query = Parse(subject: "Table 7 & 8");

        Assert.Equal(
            $"{EventExplorerQuery.BasePath}?subject=Table%207%20%26%208",
            query.ToPath());
    }

    // ---- dates -------------------------------------------------------------------------------------

    /// <summary>
    /// The range is built in the restaurant's zone and handed over as UTC instants, half-open: the start
    /// of the earlier day, to the start of the day <em>after</em> the later one. New York on 26 July is
    /// four hours behind UTC, so the lower bound is 04:00Z that morning.
    /// </summary>
    [Fact]
    public void Dates_BecomeAHalfOpenUtcRangeInTheRestaurantsZone()
    {
        EventExplorerQuery query = Parse(from: "2026-07-26", to: "2026-07-27");

        Assert.Equal(new DateTimeOffset(2026, 7, 26, 4, 0, 0, TimeSpan.Zero), query.Filter.OccurredFrom);
        Assert.Equal(new DateTimeOffset(2026, 7, 28, 4, 0, 0, TimeSpan.Zero), query.Filter.OccurredBefore);
        Assert.True(query.IsNarrowed);
        Assert.Empty(query.Problems);
    }

    /// <summary>
    /// A different restaurant, a different instant for the same typed date — the mirror image of the
    /// rendering rule, and the reason the conversion lives in <see cref="RestaurantTime"/> rather than in
    /// an <c>AT TIME ZONE</c> in the query (§8.1: one type performs that conversion).
    /// </summary>
    [Fact]
    public void Dates_AreTheRestaurantsDay_NotUtcs()
    {
        EventExplorerQuery query = EventExplorerQuery.Parse(
            null, null, null, null, "2026-07-26", null, Clock("Asia/Tokyo"));

        Assert.Equal(new DateTimeOffset(2026, 7, 25, 15, 0, 0, TimeSpan.Zero), query.Filter.OccurredFrom);
    }

    /// <summary>One date alone is one bound alone; the other stays open.</summary>
    [Fact]
    public void OneDateAlone_LeavesTheOtherBoundOpen()
    {
        EventExplorerQuery query = Parse(to: "2026-07-26");

        Assert.Null(query.Filter.OccurredFrom);
        Assert.Equal(new DateTimeOffset(2026, 7, 27, 4, 0, 0, TimeSpan.Zero), query.Filter.OccurredBefore);
    }

    /// <summary>
    /// An unreadable date is ignored and reported, never thrown on. Only a hand-edited URL can produce
    /// one — the date input submits <c>yyyy-MM-dd</c> or nothing — and a filter that returns a wider
    /// answer is still a filter, while one that throws is a blank page in front of somebody trying to find
    /// out what happened.
    /// </summary>
    [Fact]
    public void UnreadableDate_IsIgnoredAndReported()
    {
        EventExplorerQuery query = Parse(from: "26/07/2026");

        Assert.Null(query.Filter.OccurredFrom);
        Assert.Single(query.Problems);
    }

    /// <summary>A reversed range drops both bounds and says so, rather than answering with nothing.</summary>
    [Fact]
    public void ReversedRange_DropsBothBoundsAndReports()
    {
        EventExplorerQuery query = Parse(from: "2026-07-27", to: "2026-07-26");

        Assert.Null(query.Filter.OccurredFrom);
        Assert.Null(query.Filter.OccurredBefore);
        Assert.Single(query.Problems);

        // The typed values stay in their boxes so the mistake is visible and fixable.
        Assert.Equal("2026-07-27", query.From);
        Assert.Equal("2026-07-26", query.To);
    }

    /// <summary>The same day at both ends is a legal one-day window, not a reversed one.</summary>
    [Fact]
    public void SameDayAtBothEnds_IsAOneDayWindow()
    {
        EventExplorerQuery query = Parse(from: "2026-07-26", to: "2026-07-26");

        Assert.Equal(new DateTimeOffset(2026, 7, 26, 4, 0, 0, TimeSpan.Zero), query.Filter.OccurredFrom);
        Assert.Equal(new DateTimeOffset(2026, 7, 27, 4, 0, 0, TimeSpan.Zero), query.Filter.OccurredBefore);
        Assert.Empty(query.Problems);
    }

    // ---- event types -------------------------------------------------------------------------------

    /// <summary>A catalogued type whose stream is on is unremarkable, and is passed straight through.</summary>
    [Fact]
    public void CatalogedType_WithItsStreamOn_RaisesNothing()
    {
        EventExplorerQuery query = Parse(eventType: SecurityEventType.SignInFailed);

        Assert.Equal(SecurityEventType.SignInFailed, query.Filter.EventType);
        Assert.Empty(query.Problems);
    }

    /// <summary>
    /// Picking a type whose stream is switched off can never match, and the page says why rather than
    /// leaving somebody staring at an empty list wondering whether it means nothing happened.
    /// </summary>
    [Fact]
    public void CatalogedType_WithItsStreamOff_IsReportedAsUnmatchable()
    {
        EventExplorerQuery query = Parse(
            streams: [EventStream.Menu], eventType: SecurityEventType.SignInFailed);

        Assert.Equal(SecurityEventType.SignInFailed, query.Filter.EventType);
        Assert.Single(query.Problems);
    }

    /// <summary>
    /// A word the catalogue does not know is still sent, exactly as typed. A schema this build has not
    /// caught up with is precisely the case where somebody needs to look, so the filter reports the
    /// oddity and gets out of the way.
    /// </summary>
    [Fact]
    public void UnknownType_IsStillMatchedExactly_AndReported()
    {
        EventExplorerQuery query = Parse(eventType: "not_a_real_event_type");

        Assert.Equal("not_a_real_event_type", query.Filter.EventType);
        Assert.Single(query.Problems);
    }

    // ---- round trips -------------------------------------------------------------------------------

    /// <summary>
    /// Every bound at once survives a rebuild. This is the test that protects every narrowing link on the
    /// page: each one rebuilds the URL from the current selection, and a bound dropped here is a question
    /// silently changed under the person who asked it.
    /// </summary>
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

    /// <summary>Reparsing a rebuilt URL yields the same filter — the round trip is closed.</summary>
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

    /// <summary>Narrowing to one stream keeps the other bounds — the badge link on a row.</summary>
    [Fact]
    public void PathWithStreams_ReplacesTheStreamsAndKeepsTheRest()
    {
        EventExplorerQuery query = Parse(subject: "ada", from: "2026-07-26");

        Assert.Equal(
            $"{EventExplorerQuery.BasePath}?stream=order&subject=ada&from=2026-07-26",
            query.PathWithStreams(security: false, order: true, menu: false));
    }

    /// <summary>Narrowing to one subject keeps the other bounds — the "only this subject" link.</summary>
    [Fact]
    public void PathWithSubject_ReplacesTheSubjectAndKeepsTheRest()
    {
        EventExplorerQuery query = Parse(streams: [EventStream.Order], subject: "ada", actor: "mira");

        Assert.Equal(
            $"{EventExplorerQuery.BasePath}?stream=order&subject=Bo&actor=mira",
            query.PathWithSubject("Bo"));
    }

    /// <summary>Narrowing to one actor keeps the other bounds — the actor link on a row.</summary>
    [Fact]
    public void PathWithActor_ReplacesTheActorAndKeepsTheRest()
    {
        EventExplorerQuery query = Parse(subject: "ada", actor: "mira");

        Assert.Equal(
            $"{EventExplorerQuery.BasePath}?subject=ada&actor=Cass%20Okonkwo",
            query.PathWithActor("Cass Okonkwo"));
    }

    /// <summary>Passing null widens that one dimension and leaves everything else alone.</summary>
    [Fact]
    public void PathWithNull_ClearsOnlyThatBound()
    {
        EventExplorerQuery query = Parse(subject: "ada", actor: "mira", eventType: "price_changed");

        Assert.Equal(
            $"{EventExplorerQuery.BasePath}?subject=ada&actor=mira",
            query.PathWithEventType(null));
    }

    // ---- arrangement -------------------------------------------------------------------------------

    /// <summary>
    /// The restaurant is in New York unless a test says otherwise: it is four hours behind UTC in July,
    /// so a date bound that was silently being treated as UTC would be visibly four hours wrong rather
    /// than accidentally right.
    /// </summary>
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
