/*
    The two things the order page needs from the browser that C# cannot ask for.

    WHY THIS FILE EXISTS AT ALL. The order page polls the API while it watches an order move.
    A background tab must not keep polling: it wakes a serverless database and bills for it, to
    refresh a timeline nobody is looking at. Browsers already throttle timers in hidden tabs,
    but throttling is not stopping, and "roughly once a minute, forever" is still a cost with
    no reader.

    Blazor cannot observe that on its own. `document.hidden` is a PROPERTY, and JS interop can
    only call functions, so there is nothing to invoke; `visibilitychange` is an EVENT, which
    needs a listener holding a .NET object reference. Both halves are a few lines, so they live
    here rather than being approximated with `document.hasFocus`, which reports focus rather
    than visibility and would pause the timeline for anyone watching it on a second monitor.

    It is an ES module, imported at runtime by the one page that needs it:

        await JS.InvokeAsync<IJSObjectReference>("import", "./js/order-page.js")

    which is why index.html does not reference it — index.html loads css/app.css and the
    framework and nothing else, on purpose. A visitor who never opens an order page never
    fetches this, and a failed import costs a page that polls a little too eagerly and selects
    its own link a little less conveniently, rather than a page that does not work.
*/

const handlers = new Map();
let nextId = 1;

/**
 * Starts reporting visibility changes to a .NET object.
 *
 * @param {object} target A DotNetObjectReference exposing [JSInvokable] OnVisibilityChanged(bool).
 * @returns {number} A handle for `unwatch`.
 */
export function watch(target) {
    const id = nextId++;

    const handler = () => {
        // The component may have been disposed between the event firing and this callback
        // running, which rejects with "there is no tracked object with id". That is an
        // ordinary race on a page being navigated away from, not an error worth surfacing —
        // and an unhandled rejection here would put the Blazor error bar over a page that is
        // already gone.
        target.invokeMethodAsync('OnVisibilityChanged', !document.hidden).catch(() => { });
    };

    handlers.set(id, handler);
    document.addEventListener('visibilitychange', handler);

    return id;
}

/**
 * Stops reporting. Safe to call twice, and safe to call with a handle that was never issued —
 * a component's disposal path must not be able to throw.
 *
 * @param {number} id The handle returned by `watch`.
 */
export function unwatch(id) {
    const handler = handlers.get(id);

    if (handler) {
        document.removeEventListener('visibilitychange', handler);
        handlers.delete(id);
    }
}

/**
 * Whether the page is on screen right now.
 *
 * Needed as well as the event: a tab can be opened in the background, so the first render may
 * already be hidden and no `visibilitychange` will fire to say so.
 *
 * @returns {boolean} True when the document is visible.
 */
export function isVisible() {
    return !document.hidden;
}

/**
 * Selects the whole value of a text field.
 *
 * The order page's retrieval link is sixty-odd characters of ciphertext in a read-only input.
 * Selecting it on focus turns copying it into one gesture instead of a careful drag. There is
 * no clipboard write here on purpose: writing to the clipboard without an explicit press is
 * the kind of thing a page should not do on its own.
 *
 * @param {HTMLInputElement} element The field to select.
 */
export function selectAll(element) {
    if (element && typeof element.select === 'function') {
        element.select();
    }
}
