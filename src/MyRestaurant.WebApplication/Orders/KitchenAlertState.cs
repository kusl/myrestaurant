using MyRestaurant.Domain.LiveUpdates;

namespace MyRestaurant.WebApplication.Orders;

public sealed class KitchenAlertState
{
    private bool _armed;
    private bool _playbackFailed;
    private int _unseenCount;
    private int _unseenReminderCount;
    private int _alertToken;
    private bool _lastAlertWasReminder;

    public bool IsArmed => _armed;

    public bool PlaybackFailed => _playbackFailed;

    public int UnseenCount => _unseenCount;

    public int UnseenReminderCount => _unseenReminderCount;

    public bool HasUnseen => _unseenCount > 0;

    public int AlertToken => _alertToken;

    public bool LastAlertWasReminder => _lastAlertWasReminder;

    public bool ShowsVisualFallback => !_armed || _playbackFailed;

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

    public bool Arm(bool succeeded)
    {
        bool changed = _armed != succeeded || _playbackFailed == succeeded;

        _armed = succeeded;
        _playbackFailed = !succeeded;
        return changed;
    }

    public bool ReportPlaybackFailed()
    {
        if (_playbackFailed)
        {
            return false;
        }

        _playbackFailed = true;
        return true;
    }

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
