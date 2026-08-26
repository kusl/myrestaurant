(function () {
    'use strict';

    var BYTE_BUDGET_ATTRIBUTE = 'data-picture-byte-budget';
    var LONGEST_EDGE_ATTRIBUTE = 'data-picture-longest-edge';
    var DESCRIBED_BY_ATTRIBUTE = 'aria-describedby';

    var LADDER = [
        { edge: 0, quality: 0.82 },
        { edge: 0, quality: 0.70 },
        { edge: 1280, quality: 0.70 },
        { edge: 1024, quality: 0.65 },
        { edge: 800, quality: 0.60 },
        { edge: 640, quality: 0.50 }
    ];

    var OUTPUT_CONTENT_TYPE = 'image/jpeg';
    var OUTPUT_EXTENSION = '.jpg';

    function supported() {
        return typeof createImageBitmap === 'function'
            && typeof DataTransfer === 'function'
            && typeof document.createElement('canvas').toBlob === 'function';
    }

    function describeSize(bytes) {
        if (bytes >= 1048576) {
            return (bytes / 1048576).toFixed(1) + ' MB';
        }
        if (bytes >= 1024) {
            return Math.round(bytes / 1024) + ' KB';
        }
        return bytes + ' bytes';
    }

    function statusElementFor(input) {
        var id = input.getAttribute(DESCRIBED_BY_ATTRIBUTE);
        return id ? document.getElementById(id) : null;
    }

    function say(input, message) {
        var element = statusElementFor(input);
        if (!element) {
            return;
        }
        element.textContent = message;
        if (message) {
            element.removeAttribute('hidden');
        } else {
            element.setAttribute('hidden', 'hidden');
        }
    }

    function setBusy(input, busy) {
        var form = input.form;
        if (!form) {
            return;
        }
        var submit = form.querySelector('button[type="submit"]');
        if (submit) {
            submit.disabled = busy;
        }
        input.disabled = busy;
    }

    function encode(canvas, quality) {
        return new Promise(function (resolve) {
            canvas.toBlob(function (blob) { resolve(blob); }, OUTPUT_CONTENT_TYPE, quality);
        });
    }

    function renameToJpeg(name) {
        if (!name) {
            return 'picture' + OUTPUT_EXTENSION;
        }
        var dot = name.lastIndexOf('.');
        return (dot > 0 ? name.slice(0, dot) : name) + OUTPUT_EXTENSION;
    }

    function drawAt(bitmap, edge) {
        var scale = Math.min(1, edge / Math.max(bitmap.width, bitmap.height));
        var width = Math.max(1, Math.round(bitmap.width * scale));
        var height = Math.max(1, Math.round(bitmap.height * scale));

        var canvas = document.createElement('canvas');
        canvas.width = width;
        canvas.height = height;

        var context = canvas.getContext('2d');
        if (!context) {
            return null;
        }

        context.fillStyle = '#ffffff';
        context.fillRect(0, 0, width, height);
        context.drawImage(bitmap, 0, 0, width, height);

        return canvas;
    }

    async function downscale(file, budget, longestEdge) {
        var bitmap = await createImageBitmap(file);

        try {
            for (var index = 0; index < LADDER.length; index++) {
                var step = LADDER[index];
                var edge = step.edge > 0 ? Math.min(step.edge, longestEdge) : longestEdge;

                var canvas = drawAt(bitmap, edge);
                if (!canvas) {
                    return null;
                }

                var blob = await encode(canvas, step.quality);
                if (blob && blob.size <= budget) {
                    return {
                        file: new File([blob], renameToJpeg(file.name), { type: OUTPUT_CONTENT_TYPE }),
                        width: canvas.width,
                        height: canvas.height
                    };
                }
            }

            return null;
        } finally {

            if (typeof bitmap.close === 'function') {
                bitmap.close();
            }
        }
    }

    async function onFileChosen(input) {
        var budget = parseInt(input.getAttribute(BYTE_BUDGET_ATTRIBUTE), 10);
        var longestEdge = parseInt(input.getAttribute(LONGEST_EDGE_ATTRIBUTE), 10);

        if (!(budget > 0) || !(longestEdge > 0)) {
            return;
        }

        var file = input.files && input.files.length > 0 ? input.files[0] : null;
        if (!file) {
            say(input, '');
            return;
        }

        if (file.size <= budget) {
            say(input, 'Ready to attach — ' + describeSize(file.size) + ', which is inside the limit,'
                + ' so this file will be stored exactly as it is.');
            return;
        }

        setBusy(input, true);
        say(input, 'Resizing ' + describeSize(file.size) + ' to fit the limit…');

        try {
            var result = await downscale(file, budget, longestEdge);

            if (!result) {

                say(input, 'This picture could not be reduced below the ' + describeSize(budget)
                    + ' limit in this browser. Attaching it will be refused — export or crop it smaller'
                    + ' and choose it again.');
                return;
            }

            var transfer = new DataTransfer();
            transfer.items.add(result.file);
            input.files = transfer.files;

            say(input, 'Resized for the menu — ' + describeSize(file.size) + ' became '
                + describeSize(result.file.size) + ' at ' + result.width + ' × ' + result.height
                + ' pixels. Only what is attached changes; the file on this device is untouched.');
        } catch (error) {

            say(input, 'This browser could not resize the picture, so it will be attached as it is —'
                + ' which the server will refuse if it is over the ' + describeSize(budget) + ' limit.');
        } finally {
            setBusy(input, false);
        }
    }

    if (!supported()) {
        return;
    }

    document.addEventListener('change', function (event) {
        var target = event.target;
        if (!target || target.tagName !== 'INPUT' || target.type !== 'file') {
            return;
        }
        if (!target.hasAttribute(BYTE_BUDGET_ATTRIBUTE)) {
            return;
        }

        onFileChosen(target);
    });
})();
