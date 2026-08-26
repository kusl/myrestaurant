(function () {
    'use strict';

    var ELEMENT_ID = 'restaurant-clock';
    var READING_SELECTOR = '[data-restaurant-clock-reading]';

    var ABSENT_RECHECK_MILLISECONDS = 2000;
    var RESYNC_INTERVAL_MILLISECONDS = 600000;
    var RESYNC_AFTER_HIDDEN_MILLISECONDS = 60000;
    var RESYNC_MINIMUM_SPACING_MILLISECONDS = 60000;
    var DIVERGENCE_TOLERANCE_MILLISECONDS = 2000;

    var DAY_NAMES = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
    var MONTH_NAMES = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

    var anchor = null;
    var lastRenderedText = '';
    var pendingTimeout = null;
    var hiddenSinceMilliseconds = 0;
    var lastResyncMilliseconds = Date.now();
    var lastResyncAttemptMilliseconds = 0;
    var resyncInFlight = false;

    function monotonicNow() {
        return (typeof performance !== 'undefined' && typeof performance.now === 'function')
            ? performance.now()
            : Date.now();
    }

    function initialAnchorMonotonic() {
        try {
            if (typeof performance === 'undefined' || typeof performance.getEntriesByType !== 'function') {
                return monotonicNow();
            }

            var entries = performance.getEntriesByType('navigation');
            if (entries && entries.length > 0 && entries[0].responseStart > 0) {
                return entries[0].responseStart;
            }
        } catch (ignored) {

        }

        return monotonicNow();
    }

    function readNumber(element, attributeName) {
        var raw = element.getAttribute(attributeName);
        if (raw === null || raw === '') {
            return null;
        }

        var parsed = Number(raw);
        return isFinite(parsed) ? parsed : null;
    }

    function twoDigits(value) {
        return value < 10 ? '0' + value : '' + value;
    }

    function offsetForEpoch(epochMilliseconds) {
        if (anchor.nextTransitionEpochMilliseconds !== null
            && anchor.nextUtcOffsetMinutes !== null
            && epochMilliseconds >= anchor.nextTransitionEpochMilliseconds) {
            return anchor.nextUtcOffsetMinutes;
        }

        return anchor.utcOffsetMinutes;
    }

    function shiftedDate(epochMilliseconds, utcOffsetMinutes) {
        return new Date(Math.round(epochMilliseconds) + (utcOffsetMinutes * 60000));
    }

    function formatReading(epochMilliseconds, utcOffsetMinutes, usesTwelveHourClock) {
        var shifted = shiftedDate(epochMilliseconds, utcOffsetMinutes);
        var hours = shifted.getUTCHours();
        var suffix = '';
        var renderedHours;

        if (usesTwelveHourClock) {
            suffix = hours < 12 ? ' AM' : ' PM';
            hours = hours % 12;
            renderedHours = '' + (hours === 0 ? 12 : hours);
        } else {
            renderedHours = twoDigits(hours);
        }

        return DAY_NAMES[shifted.getUTCDay()]
            + ' ' + shifted.getUTCDate()
            + ' ' + MONTH_NAMES[shifted.getUTCMonth()]
            + ' ' + shifted.getUTCFullYear()
            + ', ' + renderedHours
            + ':' + twoDigits(shifted.getUTCMinutes())
            + ':' + twoDigits(shifted.getUTCSeconds())
            + suffix;
    }

    function formatMachineReadable(epochMilliseconds, utcOffsetMinutes) {
        var shifted = shiftedDate(epochMilliseconds, utcOffsetMinutes);
        var absolute = Math.abs(utcOffsetMinutes);

        return shifted.getUTCFullYear()
            + '-' + twoDigits(shifted.getUTCMonth() + 1)
            + '-' + twoDigits(shifted.getUTCDate())
            + 'T' + twoDigits(shifted.getUTCHours())
            + ':' + twoDigits(shifted.getUTCMinutes())
            + ':' + twoDigits(shifted.getUTCSeconds())
            + (utcOffsetMinutes < 0 ? '-' : '+')
            + twoDigits(Math.floor(absolute / 60))
            + ':' + twoDigits(absolute % 60);
    }

    function adoptAnchorFrom(element) {
        var epochMilliseconds = readNumber(element, 'data-epoch-milliseconds');
        var utcOffsetMinutes = readNumber(element, 'data-utc-offset-minutes');
        if (epochMilliseconds === null || utcOffsetMinutes === null) {
            anchor = null;
            return;
        }

        var key = 'markup:' + epochMilliseconds;
        if (anchor !== null && anchor.key === key) {
            return;
        }

        var firstAnchor = anchor === null;
        anchor = {
            key: key,
            epochMilliseconds: epochMilliseconds,
            monotonicMilliseconds: firstAnchor ? initialAnchorMonotonic() : monotonicNow(),
            wallClockMilliseconds: Date.now(),
            utcOffsetMinutes: utcOffsetMinutes,
            nextTransitionEpochMilliseconds: readNumber(element, 'data-next-transition-epoch-milliseconds'),
            nextUtcOffsetMinutes: readNumber(element, 'data-next-utc-offset-minutes'),
            usesTwelveHourClock: element.getAttribute('data-twelve-hour-clock') !== 'false',
            snapshotUrl: element.getAttribute('data-snapshot-url')
        };

        lastRenderedText = '';
    }

    function currentEpochMilliseconds() {
        var byMonotonic = anchor.epochMilliseconds + (monotonicNow() - anchor.monotonicMilliseconds);
        var byWallClock = anchor.epochMilliseconds + (Date.now() - anchor.wallClockMilliseconds);
        var divergence = byWallClock - byMonotonic;

        if (divergence > DIVERGENCE_TOLERANCE_MILLISECONDS) {

            requestResync();
            return byWallClock;
        }

        if (divergence < -DIVERGENCE_TOLERANCE_MILLISECONDS) {

            requestResync();
        }

        return byMonotonic;
    }

    function render(element, epochMilliseconds) {
        var reading = element.querySelector(READING_SELECTOR);
        if (!reading) {
            return;
        }

        var utcOffsetMinutes = offsetForEpoch(epochMilliseconds);
        var text = formatReading(epochMilliseconds, utcOffsetMinutes, anchor.usesTwelveHourClock);
        if (text === lastRenderedText) {
            return;
        }

        lastRenderedText = text;
        reading.textContent = text;
        reading.setAttribute('datetime', formatMachineReadable(epochMilliseconds, utcOffsetMinutes));
    }

    function applySnapshot(snapshot, sentAtMonotonic, receivedAtMonotonic) {

        if (anchor === null || !snapshot || typeof snapshot.epochMilliseconds !== 'number') {
            return;
        }

        var serverInstant = snapshot.epochMilliseconds + ((receivedAtMonotonic - sentAtMonotonic) / 2);

        anchor = {
            key: 'snapshot:' + snapshot.epochMilliseconds,
            epochMilliseconds: serverInstant,
            monotonicMilliseconds: receivedAtMonotonic,
            wallClockMilliseconds: Date.now(),
            utcOffsetMinutes: typeof snapshot.utcOffsetMinutes === 'number'
                ? snapshot.utcOffsetMinutes
                : anchor.utcOffsetMinutes,
            nextTransitionEpochMilliseconds: typeof snapshot.nextTransitionEpochMilliseconds === 'number'
                ? snapshot.nextTransitionEpochMilliseconds
                : null,
            nextUtcOffsetMinutes: typeof snapshot.nextUtcOffsetMinutes === 'number'
                ? snapshot.nextUtcOffsetMinutes
                : null,
            usesTwelveHourClock: snapshot.usesTwelveHourClock !== false,
            snapshotUrl: anchor.snapshotUrl
        };

        lastResyncMilliseconds = Date.now();
    }

    function requestResync() {
        if (resyncInFlight
            || anchor === null
            || !anchor.snapshotUrl
            || document.hidden
            || typeof window.fetch !== 'function') {
            return;
        }

        if ((Date.now() - lastResyncAttemptMilliseconds) < RESYNC_MINIMUM_SPACING_MILLISECONDS) {
            return;
        }

        resyncInFlight = true;
        lastResyncAttemptMilliseconds = Date.now();

        var sentAtMonotonic = monotonicNow();

        window.fetch(anchor.snapshotUrl, { cache: 'no-store', credentials: 'omit' })
            .then(function (response) {
                return response.ok ? response.json() : null;
            })
            .then(function (snapshot) {
                resyncInFlight = false;
                applySnapshot(snapshot, sentAtMonotonic, monotonicNow());
                tick();
            })
            .catch(function () {

                resyncInFlight = false;
            });
    }

    function clearPending() {
        if (pendingTimeout !== null) {
            window.clearTimeout(pendingTimeout);
            pendingTimeout = null;
        }
    }

    function scheduleAfter(delayMilliseconds) {
        clearPending();
        if (document.hidden) {
            return;
        }

        pendingTimeout = window.setTimeout(tick, delayMilliseconds);
    }

    function scheduleNextSecond(epochMilliseconds) {
        var untilBoundary = 1000 - (Math.round(epochMilliseconds) % 1000) + 8;
        if (untilBoundary < 60) {
            untilBoundary = 60;
        }

        if (untilBoundary > 1000) {
            untilBoundary = 1000;
        }

        scheduleAfter(untilBoundary);
    }

    function tick() {
        clearPending();
        if (document.hidden) {
            return;
        }

        var element = document.getElementById(ELEMENT_ID);
        if (!element) {

            anchor = null;
            lastRenderedText = '';
            scheduleAfter(ABSENT_RECHECK_MILLISECONDS);
            return;
        }

        adoptAnchorFrom(element);
        if (anchor === null) {
            scheduleAfter(ABSENT_RECHECK_MILLISECONDS);
            return;
        }

        var epochMilliseconds = currentEpochMilliseconds();
        render(element, epochMilliseconds);

        if ((Date.now() - lastResyncMilliseconds) >= RESYNC_INTERVAL_MILLISECONDS) {
            requestResync();
        }

        scheduleNextSecond(epochMilliseconds);
    }

    document.addEventListener('visibilitychange', function () {
        if (document.hidden) {
            hiddenSinceMilliseconds = Date.now();
            clearPending();
            return;
        }

        var hiddenFor = hiddenSinceMilliseconds === 0 ? 0 : (Date.now() - hiddenSinceMilliseconds);
        hiddenSinceMilliseconds = 0;

        if (hiddenFor >= RESYNC_AFTER_HIDDEN_MILLISECONDS) {

            lastResyncAttemptMilliseconds = 0;
            requestResync();
        }

        tick();
    });

    window.addEventListener('pageshow', function (event) {
        if (event && event.persisted) {
            lastResyncAttemptMilliseconds = 0;
            requestResync();
        }

        tick();
    });

    window.addEventListener('pagehide', clearPending);

    tick();
})();
