using MyRestaurant.Domain.LiveUpdates;

namespace MyRestaurant.WebApplication.Orders;

/// <summary>
/// The kitchen board's alert state (TECHNICAL_SPECIFICATION §10.3): whether sound has been armed for
/// this session, how many alerts have arrived that nobody has acknowledged, and a token the component
/// uses to decide when to ask the browser to make a noise.
///
/// <para>§10.3 in full: "Browsers block autoplay: the kitchen surface shows a one-tap 'enable sound' arm
/// control per session; until armed (and whenever playback fails) a persistent, high-contrast visual
/// badge with unseen-alert count is the fallback." Three separate facts are load-bearing there — armed,
/// playback-failed, and the count — and <see cref="ShowsVisualFallback"/> is the sentence that combines
/// them, stated once here rather than re-derived in markup.</para>
///
/// <para>Circuit state, deliberately. Arming is a browser-audio permission that lives exactly as long as
/// the page does, so "per session" means per circuit and persisting it would be a lie: a fresh tab has
/// not been armed no matter what a database says. Pure and outside the component for the same reason
/// <see cref="KitchenQueue"/> is (§16.1 — no bUnit): the failure this guards against is a board that
/// silently stops alerting, and that is worth a test.</para>
/// </summary>
public sealed class KitchenAlertState
{
    private bool _armed;
    private bool _playbackFailed;
    private int _unseenCount;
    private int _unseenReminderCount;
    private int _alertToken;
    private bool _lastAlertWasReminder;

    /// <summary>True once the browser has confirmed it can play a sound (§10.3's one-tap arm).</summary>
    public bool IsArmed => _armed;

    /// <summary>
    /// True when arming was refused, or when a later attempt to play failed. Kept distinct from
    /// <see cref="IsArmed"/> so the board can say <em>why</em> it is falling back to the badge — "sound
    /// is off" and "sound is on but the browser would not play" need different sentences.
    /// </summary>
    public bool PlaybackFailed => _playbackFailed;

    /// <summary>Alerts that have arrived since the last acknowledgement (§10.3's "unseen-alert count").</summary>
    public int UnseenCount => _unseenCount;

    /// <summary>How many of the unseen alerts were §10.2 reminders rather than fresh sends.</summary>
    public int UnseenReminderCount => _unseenReminderCount;

    public bool HasUnseen => _unseenCount > 0;

    /// <summary>
    /// Increments on every recorded alert and never otherwise. The component compares it against the
    /// token it last announced, so exactly one sound is played per alert even though a single alert
    /// triggers several re-renders (the notification arrives, the queue re-reads, the board repaints).
    /// </summary>
    public int AlertToken => _alertToken;

    /// <summary>Whether the most recent alert was a reminder — the board plays a more insistent pattern for those.</summary>
    public bool LastAlertWasReminder => _lastAlertWasReminder;

    /// <summary>
    /// §10.3's fallback condition: the high-contrast badge stands in whenever sound is not actually
    /// working, whether because nobody armed it or because the browser refused to play.
    /// </summary>
    public bool ShowsVisualFallback => !_armed || _playbackFailed;

    /// <summary>
    /// Records an arriving <see cref="KitchenAlert"/>. Always reports a change: an alert that arrives
    /// while the count is already non-zero still has to bump the token, or the second send of a rush
    /// would be silent.
    /// </summary>
    public bool Record(KitchenAlertKind kind)
    {
        _unseenCount++;

        if (kind == KitchenAlertKind.Reminder)
        {
            _unseenReminderCount++;
        }

        _lastAlertWasReminder = kind == KitchenAlertKind.Reminder;
        _alertToken++;
        return true;
    }

    /// <summary>
    /// Clears the unseen count — the cook has looked at the board, or acted on it. Returns false when
    /// there was nothing to clear, so a caller can skip a re-render.
    ///
    /// <para>The token is deliberately <em>not</em> reset: it is a monotonic sequence, not a count, and
    /// resetting it would make the next alert's token collide with one already announced, which is
    /// exactly how an alert goes silent.</para>
    /// </summary>
    public bool Acknowledge()
    {
        if (_unseenCount == 0)
        {
            return false;
        }

        _unseenCount = 0;
        _unseenReminderCount = 0;
        return true;
    }

    /// <summary>
    /// Records the outcome of the one-tap arm control. A refusal is not just "still unarmed" — it sets
    /// <see cref="PlaybackFailed"/> too, because the person pressed the button and deserves to be told
    /// it did not work rather than being left to wonder why the kitchen is quiet.
    /// </summary>
    public bool Arm(bool succeeded)
    {
        bool changed = _armed != succeeded || _playbackFailed == succeeded;

        _armed = succeeded;
        _playbackFailed = !succeeded;
        return changed;
    }

    /// <summary>
    /// Records that a play attempt failed after arming (a tab-focus policy change, a device that lost
    /// its audio output). Idempotent — returns false when the flag was already set, so a run of failures
    /// costs one re-render rather than one each.
    /// </summary>
    public bool ReportPlaybackFailed()
    {
        if (_playbackFailed)
        {
            return false;
        }

        _playbackFailed = true;
        return true;
    }

    /// <summary>Records that a play attempt succeeded, clearing a previous failure.</summary>
    public bool ReportPlaybackSucceeded()
    {
        if (!_playbackFailed)
        {
            return false;
        }

        _playbackFailed = false;
        return true;
    }
}
