/*
 * Kitchen board helper (TECHNICAL_SPECIFICATION §10.3, §11.2).
 *
 * Two jobs, both of them things a Blazor circuit cannot do for itself:
 *
 *   1. The alert sound. Browsers block autoplay, so §10.3 requires "a one-tap 'enable sound' arm
 *      control per session"; until it is tapped, no amount of server-side wishing produces a noise.
 *      arm() is called from that tap — inside the user gesture, which is the only moment an
 *      AudioContext will start — and reports honestly whether it worked, so the surface can keep
 *      showing the visual fallback when it did not.
 *
 *   2. Screen wake lock. A kitchen board is a screen on a wall that must not sleep mid-service. The
 *      browser drops the lock whenever the page is hidden, so it is re-acquired on visibilitychange
 *      and re-checked on a slow tick — the same dance js/display.js does for the table display.
 *
 * WHY A SYNTHESISED TONE AND NOT AN AUDIO FILE. An .mp3/.ogg would be a binary asset to ship, cache,
 * license, and get wrong at 3 kHz on a cheap tablet speaker. Two square-wave beeps from the Web Audio
 * API need no file, cannot 404, start with zero network latency, and can be told apart: a rising
 * two-note chime for a new send (§10.1), a flatter insistent triple for a reminder (§10.2) — because
 * "somebody just ordered" and "you have not touched this in a minute" are different news.
 *
 * A classic script, loaded once for the whole app like passkey.js, display.js, and clock.js. It is
 * inert on every page that is not the kitchen board.
 */
(function () {
    'use strict';

    var SURFACE_ELEMENT_ID = 'kitchen-board-surface';

    /*
     * Two seconds, not one. The only thing the tick does is notice that the board has appeared or gone
     * (Blazor navigates without a page load, so there is no event to hook) and re-take a wake lock the
     * browser released. Neither is urgent, and this runs alongside display.js's and clock.js's timers on
     * hardware that is often a phone — so it is deliberately half as busy as display.js, which has an
     * actual per-second job (staleness).
     */
    var TICK_MILLISECONDS = 2000;

    var audioContext = null;
    var wakeLockSentinel = null;
    var wakeLockPending = false;

    function surfacePresent() {
        return document.getElementById(SURFACE_ELEMENT_ID) !== null;
    }

    /* ---- audio ---------------------------------------------------------------------------------- */

    function audioContextConstructor() {
        return window.AudioContext || window.webkitAudioContext || null;
    }

    function ensureContext() {
        if (audioContext !== null) {
            return audioContext;
        }

        var Constructor = audioContextConstructor();
        if (!Constructor) {
            return null;
        }

        try {
            audioContext = new Constructor();
        } catch (error) {
            audioContext = null;
        }

        return audioContext;
    }

    /*
     * One beep. The gain ramp is not decoration: an oscillator started and stopped at full amplitude
     * clicks, and on a small speaker a click is most of what you hear. exponentialRampToValueAtTime
     * cannot touch zero, hence the 0.0001 floor at both ends.
     */
    function scheduleTone(frequency, when, seconds, peakGain) {
        var oscillator = audioContext.createOscillator();
        var gain = audioContext.createGain();

        oscillator.type = 'square';
        oscillator.frequency.setValueAtTime(frequency, when);

        gain.gain.setValueAtTime(0.0001, when);
        gain.gain.exponentialRampToValueAtTime(peakGain, when + 0.012);
        gain.gain.exponentialRampToValueAtTime(0.0001, when + seconds);

        oscillator.connect(gain);
        gain.connect(audioContext.destination);

        oscillator.start(when);
        oscillator.stop(when + seconds + 0.02);

        oscillator.onended = function () {
            try {
                oscillator.disconnect();
                gain.disconnect();
            } catch (error) {
                /* already torn down */
            }
        };
    }

    /*
     * steps is an array of [frequencyHz, seconds]; a frequency of 0 is a rest. Everything is scheduled
     * up front against the context's own clock rather than driven by setTimeout, so the rhythm is exact
     * even while the main thread is busy re-rendering twenty tickets.
     */
    function playPattern(steps, peakGain) {
        if (audioContext === null || audioContext.state !== 'running') {
            return false;
        }

        var when = audioContext.currentTime + 0.01;

        for (var index = 0; index < steps.length; index++) {
            var frequency = steps[index][0];
            var seconds = steps[index][1];

            if (frequency > 0) {
                try {
                    scheduleTone(frequency, when, seconds, peakGain);
                } catch (error) {
                    return false;
                }
            }

            when += seconds;
        }

        return true;
    }

    /*
     * §10.3's arm control. Must be called from inside a user gesture. Resuming is not enough to know it
     * worked — a context can report 'running' on a device with no output — so it proves itself with a
     * short quiet tone. If that throws or the context refuses to run, this says false and the board
     * keeps the badge up rather than pretending the kitchen is covered.
     */
    function arm() {
        var context = ensureContext();
        if (context === null) {
            return Promise.resolve(false);
        }

        var resumed;
        try {
            resumed = context.resume();
        } catch (error) {
            return Promise.resolve(false);
        }

        return Promise.resolve(resumed).then(function () {
            if (context.state !== 'running') {
                return false;
            }

            /* Quiet on purpose: this is a confirmation, not an alert. */
            return playPattern([[660, 0.07], [0, 0.03], [880, 0.09]], 0.12);
        }).catch(function () {
            return false;
        });
    }

    /*
     * The alert itself. Returns false rather than throwing when it cannot play, because the caller
     * treats that as §10.3's "whenever playback fails" and raises the visual fallback.
     */
    function alert(isReminder) {
        if (audioContext === null || audioContext.state !== 'running') {
            return Promise.resolve(false);
        }

        var pattern = isReminder
            /* Reminder (§10.2): three flat, urgent notes — something has been sitting. */
            ? [[988, 0.13], [0, 0.06], [988, 0.13], [0, 0.06], [988, 0.24]]
            /* Initial (§10.1): a rising two-note chime — something new arrived. */
            : [[784, 0.14], [0, 0.05], [1047, 0.24]];

        try {
            return Promise.resolve(playPattern(pattern, 0.38));
        } catch (error) {
            return Promise.resolve(false);
        }
    }

    /* ---- wake lock ------------------------------------------------------------------------------ */

    function wakeLockSupported() {
        return typeof navigator !== 'undefined'
            && navigator.wakeLock
            && typeof navigator.wakeLock.request === 'function';
    }

    function requestWakeLock() {
        if (!wakeLockSupported() || wakeLockPending || wakeLockSentinel !== null || document.hidden) {
            return;
        }

        wakeLockPending = true;
        navigator.wakeLock.request('screen').then(function (sentinel) {
            wakeLockPending = false;
            wakeLockSentinel = sentinel;
            sentinel.addEventListener('release', function () {
                wakeLockSentinel = null;
            });
        }).catch(function () {
            /*
             * Denied, unsupported in this context, or backgrounded mid-request. Not worth surfacing:
             * OPERATIONS already says to disable OS sleep on a board that lives on a wall.
             */
            wakeLockPending = false;
        });
    }

    function releaseWakeLock() {
        if (wakeLockSentinel === null) {
            return;
        }

        var sentinel = wakeLockSentinel;
        wakeLockSentinel = null;

        if (typeof sentinel.release === 'function') {
            sentinel.release().catch(function () { /* already gone */ });
        }
    }

    function tick() {
        if (document.hidden) {
            return;
        }

        if (surfacePresent()) {
            requestWakeLock();
        } else {
            releaseWakeLock();
        }
    }

    document.addEventListener('visibilitychange', function () {
        if (document.hidden) {
            return;
        }

        /* The browser drops the lock whenever the page is hidden; take it back the moment it is not. */
        tick();
    });

    window.myRestaurantKitchen = {
        arm: arm,
        alert: alert
    };

    setInterval(tick, TICK_MILLISECONDS);
    tick();
})();
