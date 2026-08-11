namespace MyRestaurant.WebApplication.Components.Pages.Administration;

/// <summary>
/// Which of §11.4's administration surfaces a page is, so <c>AdministrationAreaLinks</c> can mark it as
/// the current one (TECHNICAL_SPECIFICATION §11.4, §11.12).
///
/// <para><b>Why this is an enum and not a string.</b> The six links were copy-pasted into six pages, and
/// each copy omitted a different one — its own — so the row of links was a different row on every page
/// and no page could be reached from every other. An enum means the set is declared once and a page
/// declares only which member it is; a page that forgets loses its highlight and keeps its links, which
/// is the failure worth having.</para>
///
/// <para><b>Why a page links to itself.</b> The self-link is rendered and marked
/// <c>aria-current="page"</c> rather than omitted, so the row reads identically wherever an operator
/// lands and its geography is learnable — on a handset it is a horizontally scrolled strip, where a row
/// whose contents shift between pages cannot be navigated from memory at all.</para>
///
/// <para>Members are ordered as the row renders, which is the order §11.4 lists the sections in.</para>
/// </summary>
public enum AdministrationArea
{
    /// <summary><c>/administration</c> — accounts, roles, credential resets (§3.7).</summary>
    People,

    /// <summary><c>/administration/tables</c> — tables, join secrets, display devices (§4).</summary>
    Tables,

    /// <summary><c>/administration/menu</c> — menu sections and items, and their event history (§7).</summary>
    Menu,

    /// <summary><c>/administration/sittings</c> — open sittings, end-of-day close, corrections (§5.4, §6.7).</summary>
    Sittings,

    /// <summary><c>/administration/hidden-records</c> — every hidden order system-wide (§6.8).</summary>
    HiddenRecords,

    /// <summary><c>/administration/events</c> — the security, order and menu event explorer (§11.4).</summary>
    Events,
}
