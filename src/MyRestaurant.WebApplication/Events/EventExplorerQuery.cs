using System.Globalization;
using MyRestaurant.DataAccess.Events;
using MyRestaurant.WebApplication.Time;

namespace MyRestaurant.WebApplication.Events;

public sealed class EventExplorerQuery
{
    public const string BasePath = "/administration/events";

    public const string StreamField = "stream";

    public const string SubjectField = "subject";

    public const string ActorField = "actor";

    public const string TypeField = "type";

    public const string FromField = "from";

    public const string ToField = "to";

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

    public bool IncludeSecurityEvents { get; }

    public bool IncludeOrderEvents { get; }

    public bool IncludeMenuEvents { get; }

    public string? Subject { get; }

    public string? Actor { get; }

    public string? EventType { get; }

    public string? From { get; }

    public string? To { get; }

    public EventExplorerFilter Filter { get; }

    public IReadOnlyList<string> Problems { get; }

    public bool IsNarrowed => Filter.IsNarrowed;

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

    public string ToPath()
        => Build(IncludeSecurityEvents, IncludeOrderEvents, IncludeMenuEvents, Subject, Actor, EventType, From, To);

    public string PathWithStreams(bool security, bool order, bool menu)
        => Build(security, order, menu, Subject, Actor, EventType, From, To);

    public string PathWithSubject(string? subject)
        => Build(IncludeSecurityEvents, IncludeOrderEvents, IncludeMenuEvents, subject, Actor, EventType, From, To);

    public string PathWithActor(string? actor)
        => Build(IncludeSecurityEvents, IncludeOrderEvents, IncludeMenuEvents, Subject, actor, EventType, From, To);

    public string PathWithEventType(string? eventType)
        => Build(IncludeSecurityEvents, IncludeOrderEvents, IncludeMenuEvents, Subject, Actor, eventType, From, To);

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

        problems.Add($"Could not read {which}, so it was ignored. Dates are year-month-day.");
        return null;
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
