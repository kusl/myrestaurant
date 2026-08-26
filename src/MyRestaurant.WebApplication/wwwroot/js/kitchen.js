(function () {
    'use strict';

    var SURFACE_ELEMENT_ID = 'kitchen-board-surface';

    var TICK_MILLISECONDS = 2000;

    var audioContext = null;
    var wakeLockSentinel = null;
    var wakeLockPending = false;

    function surfacePresent() {
        return document.getElementById(SURFACE_ELEMENT_ID) !== null;
    }

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

            }
        };
    }

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

            return playPattern([[660, 0.07], [0, 0.03], [880, 0.09]], 0.12);
        }).catch(function () {
            return false;
        });
    }

    function alert(isReminder) {
        if (audioContext === null || audioContext.state !== 'running') {
            return Promise.resolve(false);
        }

        var pattern = isReminder

            ? [[988, 0.13], [0, 0.06], [988, 0.13], [0, 0.06], [988, 0.24]]

            : [[784, 0.14], [0, 0.05], [1047, 0.24]];

        try {
            return Promise.resolve(playPattern(pattern, 0.38));
        } catch (error) {
            return Promise.resolve(false);
        }
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
            sentinel.release().catch(function () {  });
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

        tick();
    });

    window.myRestaurantKitchen = {
        arm: arm,
        alert: alert
    };

    setInterval(tick, TICK_MILLISECONDS);
    tick();
})();
