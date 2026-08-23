// Browser-side picture downscaling for the menu (TECHNICAL_SPECIFICATION §7, §11.4;
// docs/MENU_AND_HANDHELD_PLAN.md Stage 4e).
//
// WHY THIS FILE EXISTS. §8.2 caps a stored picture at half a megabyte and a phone camera produces four,
// so before this file the honest answer to almost every real upload was "too large" — a feature that
// worked and that nobody could use. Nothing on the server can help: there is no free-libre .NET image
// library available to this stack for this use (ImageSharp's licence does not admit it, SkiaSharp is a
// native dependency inside a rootless container), which is why MyRestaurant.Domain.Menu.ImageFormat reads
// signatures and never decodes. The one decoder every guest and every member of staff already has is the
// browser's own, so that is where the resizing happens.
//
// WHAT IT DOES, IN ONE SENTENCE. When somebody chooses a picture larger than the budget the server handed
// down, it decodes the file, redraws it into a <canvas> no larger than the longest edge the server named,
// re-encodes it as JPEG, and puts the result back into the file input in place of what was chosen — so
// the ordinary multipart form posts the smaller file and the transport, the antiforgery token and the
// post/redirect/get are all exactly what they were.
//
// THE THREE THINGS WORTH READING TWICE.
//
//   1. IT IS AN OPTIMISATION AND NOT A CHECK, AND THE DISTINCTION IS LOAD-BEARING. Every refusal this
//      feature makes is still made by the write service and by §8.2's constraints: the empty file, the
//      media type this application does not serve, the bytes that contradict their own declaration, and
//      the cap itself. Nothing here validates anything. That is why every failure path below FAILS OPEN —
//      it leaves the operator's chosen file exactly where it was and lets the server answer. A downscaler
//      that refused an upload would be a second authority on what may be stored, in the one place an
//      attacker controls entirely, which is F-64/F-69's mechanism written in JavaScript.
//
//   2. THE BUDGET IS NOT WRITTEN HERE. There is no number in this file except the ladder's own
//      dimensions and qualities. The cap arrives in a data attribute, having been read out of
//      pg_constraint by IMenuItemImageDirectory.ReadDeclaredByteCapAsync at render time, so §8.2 remains
//      the only place in this repository that states how large a picture may be. An input with no budget
//      attribute is left completely alone, which is also what happens when that read comes back null.
//
//   3. NO OBJECT URL, NO INLINE SCRIPT, NO CSP CHANGE. createImageBitmap takes the File directly and
//      canvas.toBlob hands back a Blob that becomes a File — so nothing here ever produces a blob: or
//      data: URL, and §11.11's `img-src 'self' data:` and `script-src 'self'` are as true after this file
//      as before it. That is deliberate: F-49's lesson is that a policy nobody owns is a policy that
//      quietly stops matching the application, and the cheapest way to keep this one matching was to need
//      nothing from it.
//
// WHY A DELEGATED LISTENER ON `document`. Enhanced navigation replaces the body of the page without a
// reload, so a handler bound to an element found at load time is a handler that stops existing the first
// time somebody follows a link. A capture-phase listener on the document survives every swap and needs no
// re-initialisation hook — the same shape clock.js, display.js and kitchen.js use for the same reason.
(function () {
    'use strict';

    var BYTE_BUDGET_ATTRIBUTE = 'data-picture-byte-budget';
    var LONGEST_EDGE_ATTRIBUTE = 'data-picture-longest-edge';
    var DESCRIBED_BY_ATTRIBUTE = 'aria-describedby';

    // Dimension-and-quality pairs, tried in order until one lands under the budget. Both axes move,
    // because they fail differently: quality alone plateaus on a large raster (a 4000px photograph at
    // q=0.4 is still mostly pixels and looks like a fax), and dimension alone throws away detail a
    // gentler quality would have paid for. The first entry is the one almost every real photograph stops
    // at; the rest exist so that the answer to an unusually incompressible picture is a smaller picture
    // rather than a refusal.
    //
    // The first pair's edge is a placeholder: it is replaced at run time by the longest edge the server
    // named, so the server's number is used rather than one written here.
    var LADDER = [
        { edge: 0, quality: 0.82 },
        { edge: 0, quality: 0.70 },
        { edge: 1280, quality: 0.70 },
        { edge: 1024, quality: 0.65 },
        { edge: 800, quality: 0.60 },
        { edge: 640, quality: 0.50 }
    ];

    // JPEG rather than WebP, although §8.2 admits both. WebP is smaller at equal quality on every current
    // browser, and that is not the property being optimised for: the stored bytes are served back to
    // whatever a guest is holding, and a picture that cannot be decoded on an older handset in the dining
    // room is worse than one that is forty kilobytes larger. A JPEG produced by canvas.toBlob also always
    // carries the FF D8 FF signature ImageFormat identifies, so the re-encoded file passes the same
    // signature check the original would have.
    var OUTPUT_CONTENT_TYPE = 'image/jpeg';
    var OUTPUT_EXTENSION = '.jpg';

    function supported() {
        return typeof createImageBitmap === 'function'
            && typeof DataTransfer === 'function'
            && typeof document.createElement('canvas').toBlob === 'function';
    }

    // Kilobytes and megabytes in the units an operator recognises, because "this is 4194304" is a fact
    // about a computer and "this is 4.0 MB" is a fact about their photograph.
    function describeSize(bytes) {
        if (bytes >= 1048576) {
            return (bytes / 1048576).toFixed(1) + ' MB';
        }
        if (bytes >= 1024) {
            return Math.round(bytes / 1024) + ' KB';
        }
        return bytes + ' bytes';
    }

    // Resolved from the input's own description, so the element the operator reads and the element a
    // screen reader announces are the same one and there is no second association to keep in step.
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

    // The submit control is disabled for the duration, and this is not decoration. Decoding and
    // re-encoding a four-megabyte photograph takes a noticeable moment, and a form submitted during it
    // would post the ORIGINAL file — which the server would refuse, correctly, with a message about size
    // that the operator has just watched the page promise to handle. Disabling is the only way to make
    // "wait" mean something without a spinner nobody reads.
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

    // The chosen name with its extension replaced, so a replaced file is still recognisably the operator's
    // photograph in a file picker's recent list. The name is never sent anywhere that reads it — §8.2
    // stores no filename — so this is purely for the person looking at the control.
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

        // A white ground before the draw, because the output is JPEG and JPEG has no alpha. Without this
        // a transparent PNG re-encodes onto an undefined background, which in practice renders black —
        // so a logo somebody uploaded with a transparent surround would come back as a black rectangle
        // with a dish in the middle of it, and nothing would report an error.
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
            // Released explicitly rather than left to the collector: a decoded four-megapixel bitmap is
            // tens of megabytes of graphics memory, and an operator working through a menu attaches one
            // of these per dish.
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

        // Under the budget already: left completely alone, bytes and declared media type both. §7 stores
        // what it was given, and re-encoding a picture that already fits would throw away quality to
        // solve a problem nobody has — and would silently convert a PNG somebody chose deliberately.
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
                // Every rung tried and none fit. Left alone deliberately: the server's refusal names the
                // size and says what to do, and posting some smallest-attempt file would attach a
                // picture nobody chose.
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
            // Fail open, and say so rather than saying nothing. The reachable causes are a browser
            // without the APIs, a file the decoder will not open, and a raster larger than this device's
            // canvas limit — in all three the operator's file is still selected and the server is still
            // the authority on whether it may be stored.
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

        // Deliberately not awaited and deliberately not preventing anything: a change event has no
        // default to prevent and nothing downstream is listening. The submit control is what serialises
        // this against the form, in setBusy above.
        onFileChosen(target);
    });
})();
