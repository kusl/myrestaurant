/*
 * The restaurant wall clock (TECHNICAL_SPECIFICATION §11.7, §8.1).
 *
 * The footer carries one server-rendered anchor — an instant, the RESTAURANT_TIME_ZONE offset that
 * applies to it, and when that offset next changes — and this script advances it once a second. All of
 * it is deliberately local: a per-second round trip to the server, on every open tab, would be the most
 * expensive thing on a page whose whole point is that a phone can hold it through a meal.
 *
 * WHAT IT MUST NOT DO, because most readers are on a handheld:
 *
 *   1. Tick while nobody is looking. Everything stops on visibilitychange; the timer is cleared, not
 *      merely ignored. A backgrounded tab costs nothing at all.
 *   2. Wake more than once a second. Each tick schedules the next one AT the coming second boundary
 *      (setTimeout, not setInterval, so drift cannot accumulate into a double-fire) — never
 *      requestAnimationFrame, which would run this sixty times for one visible change.
 *   3. Touch the DOM when nothing changed. One string comparison guards the write.
 *
 * WHICH CLOCK IT TRUSTS. Elapsed time comes from performance.now(), a monotonic counter that an NTP
 * step cannot move — Date.now() can jump in either direction while a page is open. But performance.now()
 * has its own failure: on several platforms it stops advancing while the device is suspended, so a phone
 * that spends an hour in a pocket wakes up an hour behind. Both are therefore read every tick, and a
 * divergence between them is treated as the signal it is: prefer the wall clock (only it saw the
 * suspend), and ask the server for a fresh anchor.
 *
 * WHEN IT ASKS THE SERVER. On page load, implicitly, because the anchor is in the markup. After that,
 * only: every ten minutes while visible; on returning from a minute or more hidden; and when the two
 * local clocks disagree. Never while hidden, never more than once a minute, and a failed request is
 * ignored rather than allowed to break the clock — a wall clock that is a second off beats a blank one.
 *
 * The formatting below reproduces RestaurantTime's invariant-culture patterns character for character.
 * Intl is deliberately unused: it would format in the READER's locale and zone, which is exactly the
 * thing §8.1 rules out.
 *
 * A classic script, loaded once for the whole app like passkey.js and display.js. It is a no-op on any
 * document without the footer, and re-anchors by itself when Blazor replaces the DOM — whether that is
 * enhanced navigation on a static page or a circuit reconciling its prerender.
 */
(function () {
    'use strict';

    var ELEMENT_ID = 'restaurant-clock';
    var READING_SELECTOR = '[data-restaurant-clock-reading]';

    var ABSENT_RECHECK_MILLISECONDS = 2000;
    var RESYNC_INTERVAL_MILLISECONDS = 600000;
    var RESYNC_AFTER_HIDDEN_MILLISECONDS = 60000;
    var RESYNC_MINIMUM_SPACING_MILLISECONDS = 60000;
    var DIVERGENCE_TOLERANCE_MILLISECONDS = 2000;

    // CultureInfo.InvariantCulture's abbreviated names, hardcoded so the ticking text and the
    // server-rendered text it replaces are byte-identical.
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

    /*
     * The best available estimate of when the server stamped the anchor that is already in the markup.
     * responseStart is when the first byte of that document arrived, which is far closer to the truth
     * than "whenever this script got around to reading the DOM" — the difference is the parse and the
     * deferred-script wait, and getting it wrong shows up as a clock permanently a second slow.
     * Only meaningful for the FIRST anchor; a later one came from a live circuit or a navigation.
     */
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
            // Navigation Timing is unavailable or blocked; the script-run instant is a fine fallback.
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

    /*
     * Shift the instant by the restaurant's offset and then read it back with the UTC getters. The
     * browser's own zone never enters the calculation — which is the entire point.
     */
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

        // The server just repainted the text; anything remembered about it is stale.
        lastRenderedText = '';
    }

    function currentEpochMilliseconds() {
        var byMonotonic = anchor.epochMilliseconds + (monotonicNow() - anchor.monotonicMilliseconds);
        var byWallClock = anchor.epochMilliseconds + (Date.now() - anchor.wallClockMilliseconds);
        var divergence = byWallClock - byMonotonic;

        if (divergence > DIVERGENCE_TOLERANCE_MILLISECONDS) {
            // The monotonic counter stalled — almost always a device suspend. Only the wall clock saw
            // that time pass, so believe it, and confirm with the server at the next opportunity.
            requestResync();
            return byWallClock;
        }

        if (divergence < -DIVERGENCE_TOLERANCE_MILLISECONDS) {
            // The wall clock was stepped backwards under us (an NTP correction, or someone editing the
            // device's date). The monotonic reading is the trustworthy one; still worth re-anchoring.
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
        // The reply can outlive the page that asked: a navigation away from the footer drops the
        // anchor, and there is then nothing to correct.
        if (anchor === null || !snapshot || typeof snapshot.epochMilliseconds !== 'number') {
            return;
        }

        // Half the round trip is the usual symmetric-latency assumption; over a tunnel it is worth a
        // few tens of milliseconds, and it is strictly better than ignoring the trip entirely.
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
        // credentials omitted on purpose: the answer is the same for everyone, so there is no reason to
        // send a session cookie — or to let this request be caught by anything that inspects one.
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
                // Offline, tunnel down, or the page is going away. Keep ticking on the local anchor.
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

    /* Aim at the next second boundary, so exactly one wake produces exactly one visible change. */
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
            // No footer on this document (or it has not been rendered yet). Forget the anchor so a
            // later arrival re-anchors from the server rather than inheriting a stale reading.
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
            // Long enough that the device may well have slept. Let the resync jump the spacing guard.
            lastResyncAttemptMilliseconds = 0;
            requestResync();
        }

        tick();
    });

    // Restored from the back/forward cache: the document is intact but arbitrarily old.
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
