using System.Globalization;
using MyRestaurant.DataAccess.Events;
using MyRestaurant.WebApplication.Time;

namespace MyRestaurant.WebApplication.Events;

/// <summary>
/// The event explorer's filter as it travels in the URL, and the two translations either side of it:
/// query string → <see cref="EventExplorerFilter"/>, and selection → canonical URL
/// (TECHNICAL_SPECIFICATION §11.4).
///
/// <para><b>Why this is a class and not thirty lines in the page.</b> <c>HiddenRecords.razor</c> holds
/// the same logic inline — parse the dates, notice a reversed range, rebuild the path with the filter
/// preserved — and none of it is reachable by a test, because reaching it means rendering a static-SSR
/// component with an <see cref="Microsoft.AspNetCore.Http.HttpContext"/>. It is also the part most
/// likely to be quietly wrong: a filter that drops a bound when rebuilding its own URL loses the
/// administrator's question the moment they click anything, and nothing about the page looks broken
/// while it happens. Lifting it out costs one file and buys the whole surface a container-free test.</para>
///
/// <para><b>A filter is a bookmarkable question, so it lives in the URL</b> rather than in component
/// state, and the form that sets it is a plain GET — no antiforgery token, no handler, no POST. Every
/// affordance on the page (narrow to this actor, only this stream, clear the type) is therefore an
/// ordinary link, which is also what makes them work with no JavaScript and open in a new tab.</para>
///
/// <para>Instances are immutable; <see cref="Parse"/> is the only way to make one, and every
/// <c>PathWith…</c> method returns a string rather than a mutated copy.</para>
/// </summary>
public sealed class EventExplorerQuery
{
    /// <summary>The explorer's route. One place, so a link and a redirect cannot disagree.</summary>
    public const string BasePath = "/administration/events";

    /// <summary>The repeated checkbox field naming which streams to include.</summary>
    public const string StreamField = "stream";

    /// <summary>The subject substring field.</summary>
    public const string SubjectField = "subject";

    /// <summary>The actor substring field.</summary>
    public const string ActorField = "actor";

    /// <summary>The exact-event-type field.</summary>
    public const string TypeField = "type";

    /// <summary>The inclusive lower date bound field.</summary>
    public const string FromField = "from";

    /// <summary>The inclusive upper date bound field (the range it builds is half-open).</summary>
    public const string ToField = "to";

    /// <summary>What an <c>&lt;input type="date"&gt;</c> submits, and the only format read back.</summary>
    private const string DatePattern = "yyyy-MM-dd";

    private EventExplorerQuery(
        bool includeSecurityEvents,
        bool includeOrderEvents,
        bool includeMenuEvents,
        string? subject,
        string? actor,
        string? eventType,
        string? from,
        string? to,
        EventExplorerFilter filter,
        IReadOnlyList<string> problems)
    {
        IncludeSecurityEvents = includeSecurityEvents;
        IncludeOrderEvents = includeOrderEvents;
        IncludeMenuEvents = includeMenuEvents;
        Subject = subject;
        Actor = actor;
        EventType = eventType;
        From = from;
        To = to;
        Filter = filter;
        Problems = problems;
    }

    /// <summary>Whether the security stream is selected — the state of its checkbox.</summary>
    public bool IncludeSecurityEvents { get; }

    /// <summary>Whether the order stream is selected.</summary>
    public bool IncludeOrderEvents { get; }

    /// <summary>Whether the menu stream is selected.</summary>
    public bool IncludeMenuEvents { get; }

    /// <summary>The subject search text as it should appear back in its input, or <c>null</c>.</summary>
    public string? Subject { get; }

    /// <summary>The actor search text as it should appear back in its input, or <c>null</c>.</summary>
    public string? Actor { get; }

    /// <summary>The chosen event type, or <c>null</c> for "any type".</summary>
    public string? EventType { get; }

    /// <summary>The lower date bound as typed, <c>yyyy-MM-dd</c>, or <c>null</c>.</summary>
    public string? From { get; }

    /// <summary>The upper date bound as typed, <c>yyyy-MM-dd</c>, or <c>null</c>.</summary>
    public string? To { get; }

    /// <summary>The filter to hand the reader — dates already converted to UTC instants (§8.1).</summary>
    public EventExplorerFilter Filter { get; }

    /// <summary>
    /// Everything about the incoming URL that could not be honoured, in plain sentences for the page to
    /// print. Empty in the ordinary case. Each one is a thing that was <em>ignored</em>, never a refusal:
    /// a filter that returns a slightly wider answer is a filter, and a filter that throws is a blank
    /// page in front of somebody trying to find out what happened.
    /// </summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>True when at least one bound is set — what the page says out loud above the list.</summary>
    public bool IsNarrowed => Filter.IsNarrowed;

    /// <summary>
    /// Reads the six query-string fields into a selection and a filter.
    ///
    /// <para><b>No stream selected means all three.</b> An unchecked checkbox submits nothing, so
    /// "the administrator cleared every box and pressed the button" and "somebody opened
    /// <c>/administration/events</c> fresh" arrive as exactly the same request. They cannot be told
    /// apart, so they must mean the same thing, and the only defensible meaning is §11.4's default:
    /// everything. The page then re-checks all three boxes, which is how it says so.</para>
    ///
    /// <para><b>Dates are the restaurant's, not UTC's and not the reader's</b> (§8.1). An administrator
    /// typing 26 July means the restaurant's 26 July; the range built from it runs from the start of that
    /// day to the start of the day after the upper bound, so no instant on either edge can fall through
    /// it.</para>
    /// </summary>
    /// <param name="streams">The repeated <c>stream</c> values. Unrecognised words are ignored and noted.</param>
    /// <param name="subject">The <c>subject</c> value; blank is no filter.</param>
    /// <param name="actor">The <c>actor</c> value; blank is no filter.</param>
    /// <param name="eventType">The <c>type</c> value; blank is no filter.</param>
    /// <param name="from">The <c>from</c> value, <c>yyyy-MM-dd</c>; anything else is ignored and noted.</param>
    /// <param name="to">The <c>to</c> value, on the same terms.</param>
    /// <param name="clock">Converts a restaurant-zone calendar day into the UTC instants the reader takes.</param>
    public static EventExplorerQuery Parse(
        IReadOnlyList<string>? streams,
        string? subject,
        string? actor,
        string? eventType,
        string? from,
        string? to,
        RestaurantTime clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        List<string> problems = [];

        bool security = false;
        bool order = false;
        bool menu = false;

        // string? rather than string: the sequence's element type is non-nullable, but its source is a
        // query string bound by the framework, and a defensive read here costs nothing.
        foreach (string? requested in streams ?? [])
        {
            string candidate = requested?.Trim() ?? string.Empty;
            if (candidate.Length == 0)
            {
                continue;
            }

            if (string.Equals(candidate, EventStream.Security, StringComparison.OrdinalIgnoreCase))
            {
                security = true;
            }
            else if (string.Equals(candidate, EventStream.Order, StringComparison.OrdinalIgnoreCase))
            {
                order = true;
            }
            else if (string.Equals(candidate, EventStream.Menu, StringComparison.OrdinalIgnoreCase))
            {
                menu = true;
            }
            else
            {
                // Only reachable from a hand-edited URL — the checkboxes submit one of three words. Said
                // out loud because the alternative is a typo that silently widens the answer.
                problems.Add(
                    $"“{candidate}” is not one of the three event streams, so it was ignored. " +
                    "They are security, order, and menu.");
            }
        }

        if (!security && !order && !menu)
        {
            security = true;
            order = true;
            menu = true;
        }

        string? subjectText = Clean(subject);
        string? actorText = Clean(actor);
        string? typeText = Clean(eventType);
        string? fromText = Clean(from);
        string? toText = Clean(to);

        DateOnly? lower = ParseDate(fromText, "the earlier date", problems);
        DateOnly? upper = ParseDate(toText, "the later date", problems);

        if (lower is { } start && upper is { } end && end < start)
        {
            problems.Add(
                "The later date is before the earlier one, so the range was ignored. Swap them, or " +
                "clear one of the two.");
            lower = null;
            upper = null;
        }

        if (typeText is not null)
        {
            NoteTypeProblems(typeText, security, order, menu, problems);
        }

        EventExplorerFilter filter = new(
            security,
            order,
            menu,
            subjectText,
            actorText,
            typeText,
            lower is { } lowerBound ? clock.StartOfDay(lowerBound) : null,
            upper is { } upperBound ? clock.StartOfNextDay(upperBound) : null);

        return new EventExplorerQuery(
            security, order, menu, subjectText, actorText, typeText, fromText, toText, filter, problems);
    }

    /// <summary>The canonical URL of this exact view — every bound preserved, nothing else.</summary>
    public string ToPath()
        => Build(IncludeSecurityEvents, IncludeOrderEvents, IncludeMenuEvents, Subject, Actor, EventType, From, To);

    /// <summary>This view with a different set of streams — the "only this stream" affordance.</summary>
    public string PathWithStreams(bool security, bool order, bool menu)
        => Build(security, order, menu, Subject, Actor, EventType, From, To);

    /// <summary>This view narrowed to one subject, or widened to every subject with <c>null</c>.</summary>
    public string PathWithSubject(string? subject)
        => Build(IncludeSecurityEvents, IncludeOrderEvents, IncludeMenuEvents, subject, Actor, EventType, From, To);

    /// <summary>This view narrowed to one actor, or widened to every actor with <c>null</c>.</summary>
    public string PathWithActor(string? actor)
        => Build(IncludeSecurityEvents, IncludeOrderEvents, IncludeMenuEvents, Subject, actor, EventType, From, To);

    /// <summary>This view narrowed to one event type, or widened to every type with <c>null</c>.</summary>
    public string PathWithEventType(string? eventType)
        => Build(IncludeSecurityEvents, IncludeOrderEvents, IncludeMenuEvents, Subject, Actor, eventType, From, To);

    /// <summary>
    /// Builds the URL from an explicit selection.
    ///
    /// <para>All three streams selected emits no <c>stream=</c> at all, because that is the default and a
    /// URL that spells out its defaults is a URL nobody can read. Everything else is emitted in a fixed
    /// order, so two ways of reaching the same view produce the same string — which is what makes the
    /// page's "you are already looking at everything" test a simple comparison.</para>
    /// </summary>
    private static string Build(
        bool security,
        bool order,
        bool menu,
        string? subject,
        string? actor,
        string? eventType,
        string? from,
        string? to)
    {
        List<string> parts = [];

        // All-three and none-at-all both mean "everything" (see Parse), and "everything" is the default,
        // so neither is written out.
        if (!(security && order && menu))
        {
            if (security)
            {
                parts.Add($"{StreamField}={EventStream.Security}");
            }

            if (order)
            {
                parts.Add($"{StreamField}={EventStream.Order}");
            }

            if (menu)
            {
                parts.Add($"{StreamField}={EventStream.Menu}");
            }
        }

        Append(parts, SubjectField, subject);
        Append(parts, ActorField, actor);
        Append(parts, TypeField, eventType);
        Append(parts, FromField, from);
        Append(parts, ToField, to);

        return parts.Count == 0 ? BasePath : $"{BasePath}?{string.Join("&", parts)}";
    }

    private static void Append(List<string> parts, string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{field}={Uri.EscapeDataString(value.Trim())}");
        }
    }

    /// <summary>
    /// Two things worth saying about a chosen type, both of which turn "no results" from a mystery into a
    /// sentence. Neither changes the filter: an unknown word is still sent as an exact match, because a
    /// schema this build has not caught up with is precisely when somebody needs to look.
    /// </summary>
    private static void NoteTypeProblems(
        string eventType,
        bool security,
        bool order,
        bool menu,
        List<string> problems)
    {
        string? stream = EventTypeCatalogue.StreamFor(eventType);

        if (stream is null)
        {
            problems.Add(
                $"“{eventType}” is not an event type this build knows about. It is still being matched " +
                "exactly, so if the database has it you will see it.");
            return;
        }

        bool streamIsOn = stream switch
        {
            EventStream.Security => security,
            EventStream.Order => order,
            _ => menu,
        };

        if (!streamIsOn)
        {
            problems.Add(
                $"“{eventType}” is a {stream} event, and the {stream} stream is switched off — so " +
                "nothing can match. Switch it back on, or clear the type.");
        }
    }

    private static DateOnly? ParseDate(string? value, string which, List<string> problems)
    {
        if (value is null)
        {
            return null;
        }

        if (DateOnly.TryParseExact(
                value, DatePattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
        {
            return parsed;
        }

        // Only reachable from a hand-edited URL: the date input submits yyyy-MM-dd or nothing at all.
        problems.Add($"Could not read {which}, so it was ignored. Dates are year-month-day.");
        return null;
    }

    /// <summary>Trims, and turns blank into absent — a blank box is not a filter.</summary>
    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
