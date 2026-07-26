/*
 * Table display helper (TECHNICAL_SPECIFICATION §11.5, §10.3).
 *
 * Two jobs, both of them things a Blazor circuit cannot do for itself:
 *
 *   1. Screen wake lock. A table display is a tablet on a stand that must never sleep. The lock is
 *      released by the browser whenever the page is hidden, so it is re-acquired on visibilitychange
 *      and re-checked on every tick — §11.5 asks for exactly that dance.
 *
 *   2. Staleness. §11.5: "the QR must not silently freeze stale". If the circuit dies, the server stops
 *      re-rendering and the code on screen quietly ages out — the single worst failure this surface has,
 *      because a frozen QR looks exactly like a live one. Rather than reach into Blazor's reconnection
 *      internals, the surface publishes two attributes and this script watches them:
 *
 *          data-refresh-token   changes on every server-side refresh
 *          data-fresh-for-ms    how long a refresh stays trustworthy
 *
 *      The deadline is measured from when THIS script observed the token change, never from a server
 *      timestamp — a kiosk's clock is frequently wrong by minutes, and a skewed clock must not be able
 *      to declare a healthy display offline or a dead one live. When the deadline passes, `is-stale` is
 *      set on the surface and the page's own CSS raises the offline curtain.
 *
 * A classic script, loaded once for the whole app like passkey.js. It is a no-op on every page that
 * does not contain the display surface, so it costs an element lookup per second and nothing else.
 */
(function () {
    'use strict';

    var SURFACE_ELEMENT_ID = 'table-display-surface';
    var STALE_CLASS = 'is-stale';
    var TICK_MILLISECONDS = 1000;
    var FALLBACK_FRESH_FOR_MILLISECONDS = 90000;

    var observedToken = null;
    var observedAtMilliseconds = 0;
    var wakeLockSentinel = null;
    var wakeLockPending = false;

    function surfaceElement() {
        return document.getElementById(SURFACE_ELEMENT_ID);
    }

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
            // Denied, unsupported in this context, or the page was backgrounded mid-request. Not an
            // error worth surfacing: the operations runbook already says to disable OS sleep as well.
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

    function freshForMilliseconds(element) {
        var declared = parseInt(element.getAttribute('data-fresh-for-ms') || '', 10);
        return isNaN(declared) || declared <= 0 ? FALLBACK_FRESH_FOR_MILLISECONDS : declared;
    }

    function tick() {
        var element = surfaceElement();
        if (!element) {
            // Navigated away from the display surface: drop the lock and forget the last observation so
            // a later return starts its clock fresh instead of inheriting a stale one.
            observedToken = null;
            observedAtMilliseconds = 0;
            releaseWakeLock();
            return;
        }

        requestWakeLock();

        var token = element.getAttribute('data-refresh-token');
        if (token !== observedToken) {
            observedToken = token;
            observedAtMilliseconds = Date.now();
        }

        var stale = (Date.now() - observedAtMilliseconds) > freshForMilliseconds(element);
        if (stale) {
            element.classList.add(STALE_CLASS);
        } else {
            element.classList.remove(STALE_CLASS);
        }
    }

    document.addEventListener('visibilitychange', function () {
        if (document.hidden) {
            return;
        }

        // The browser drops the lock whenever the page is hidden; take it back the moment it is not.
        requestWakeLock();
    });

    setInterval(tick, TICK_MILLISECONDS);
    tick();
})();
