// DOM helpers of the Studio: h() hyperscript, mount/remount, toasts, modal dialogs, confirm, menus.
// No libraries, no build step; text always goes in as text nodes (never innerHTML), so game texts cannot inject markup.

import { t } from "./i18n.mjs";

const TEXT_ATTRIBUTES = new Set(["title", "placeholder", "alt", "aria-label"]);

/**
 * h("div", {class: "card", onClick: fn}, "text", h("b", {}, "bold")).
 * Props: class, style (object), dataset, on* listeners, value, checked, any attribute. Strings are translated with t();
 * use plain() for game names and user text (never translated).
 */
export function h(tag, props = {}, ...children) {
    const element = document.createElement(tag);
    for (const [key, value] of Object.entries(props ?? {})) {
        if (value === undefined || value === null || value === false) continue;
        if (key === "class") element.className = value;
        else if (key === "style" && typeof value === "object") Object.assign(element.style, value);
        else if (key === "dataset") Object.assign(element.dataset, value);
        else if (key.startsWith("on") && typeof value === "function") element.addEventListener(key.slice(2).toLowerCase(), value);
        else if (key === "value") element.value = value;
        else if (key === "checked") element.checked = Boolean(value);
        else if (value === true) element.setAttribute(key, "");
        else element.setAttribute(key, TEXT_ATTRIBUTES.has(key) ? t(String(value)) : String(value));
    }
    append(element, children);
    return element;
}

/** An element holding foreign text (a game name, what the user typed): never translated. */
export function plain(tag, props = {}, value = "") {
    const element = h(tag, props);
    element.textContent = value === undefined || value === null ? "" : String(value);
    return element;
}

function append(parent, children) {
    for (const child of children.flat(8)) {
        if (child === undefined || child === null || child === false || child === "") continue;
        parent.append(child instanceof Node ? child : document.createTextNode(typeof child === "string" ? t(child) : String(child)));
    }
}

export function clear(element) {
    while (element.firstChild) element.removeChild(element.firstChild);
    return element;
}

export function mount(element, ...children) {
    clear(element);
    append(element, children);
    return element;
}

// ---- typing: what the user is typing is never rewritten under their fingers (studio SPEC §9.1). An input being typed in carries
// data-editing="1" (forms.mjs bindTyping) until it is committed (blur, change, Enter); a remount keeps its raw text, focus and caret.

let composing = false;
if (typeof document !== "undefined") {
    document.addEventListener("compositionstart", () => { composing = true; }, true);
    document.addEventListener("compositionend", () => { composing = false; }, true);
}

/** Whether an IME composition is running (nothing may re-render the focused field then). */
export const isComposing = () => composing;

/** Whether the user is typing in a field (focused, not committed yet). */
export function isTyping() {
    const a = document.activeElement;
    return composing || a?.dataset?.editing === "1";
}

/** Forget the typed text of the focused field (undo/redo: the draft's value wins). */
export function discardTyping() {
    const a = document.activeElement;
    if (a?.dataset?.editing) delete a.dataset.editing;
}

/**
 * mount() that keeps the scroll of [data-scroll-key] nodes and the focus (and caret) of a [data-focus-key] field — and, when that
 * field is being typed in, the text as typed (not the value normalised from the draft).
 */
export function remount(element, ...children) {
    const scrolled = new Map();
    for (const node of element.querySelectorAll("[data-scroll-key]")) scrolled.set(node.dataset.scrollKey, [node.scrollTop, node.scrollLeft]);
    const ownScroll = [element.scrollTop, element.scrollLeft];
    const active = document.activeElement;
    const focusKey = active && element.contains(active) ? active.dataset?.focusKey : "";
    let caret = null;
    try { caret = focusKey && typeof active.selectionStart === "number" ? [active.selectionStart, active.selectionEnd, active.selectionDirection] : null; } catch { caret = null; }
    const typed = focusKey && active.dataset.editing === "1" ? active.value : null;
    mount(element, ...children);
    for (const node of element.querySelectorAll("[data-scroll-key]")) {
        const saved = scrolled.get(node.dataset.scrollKey);
        if (saved) [node.scrollTop, node.scrollLeft] = saved;
    }
    [element.scrollTop, element.scrollLeft] = ownScroll;
    if (focusKey) {
        const node = [...element.querySelectorAll("[data-focus-key]")].find((n) => n.dataset.focusKey === focusKey);
        if (node) {
            if (typed !== null && "value" in node) {
                if (node.value !== typed) node.value = typed;
                node.dataset.editing = "1";
            }
            node.focus({ preventScroll: true });
            try { if (caret) node.setSelectionRange(caret[0], caret[1], caret[2] ?? "none"); } catch { /* no caret on this field */ }
        }
    }
    return element;
}

/** A toast at the bottom right; kind: "", "ok", "warn", "bad". */
export function toast(message, kind = "") {
    const root = document.getElementById("toasts");
    if (!root) return;
    const node = h("div", { class: `toast ${kind}`, role: "status" }, message);
    root.append(node);
    setTimeout(() => {
        node.classList.add("fade");
        setTimeout(() => node.remove(), 320);
    }, kind === "bad" ? 8000 : 3500);
}

let modalSequence = 0;

/**
 * A modal dialog; resolves with what close(result) got, undefined on Escape / the backdrop / Close.
 * body(close) and footer(close) return the content. Dialogs stack (a picker over an editor dialog); Escape closes the top one.
 */
export function modal({ title, body, footer, width, className = "" }) {
    return new Promise((resolve) => {
        const root = document.getElementById("modal-root");
        const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
        const titleId = `modal-title-${++modalSequence}`;
        let backdrop;
        const close = (result) => {
            backdrop?.remove();
            document.removeEventListener("keydown", onKey, true);
            if (previousFocus?.isConnected) previousFocus.focus({ preventScroll: true });
            resolve(result);
        };
        const onKey = (event) => {
            if (root.lastElementChild !== backdrop) return;
            if (event.key === "Escape") {
                event.stopPropagation();
                return close(undefined);
            }
            if (event.key !== "Tab") return;
            const focusable = [...dialog.querySelectorAll('button:not(:disabled), a[href], input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex]:not([tabindex="-1"])')]
                .filter((e) => e.getClientRects().length > 0);
            if (!focusable.length) return;
            const first = focusable[0];
            const last = focusable.at(-1);
            if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
            else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
        };
        document.addEventListener("keydown", onKey, true);
        const dialog = h("div", { class: `modal ${className}`, role: "dialog", "aria-modal": "true", "aria-labelledby": titleId, tabindex: "-1", style: width ? { width } : undefined },
            h("header", {}, h("div", { class: "grow", id: titleId }, title), h("button", { class: "btn small ghost", onClick: () => close(undefined), title: "Close (Esc)" }, "✕")),
            h("div", { class: "body" }, body(close)),
            footer ? h("footer", {}, footer(close)) : null);
        backdrop = h("div", { class: "modal-backdrop", onMousedown: (e) => { if (e.target === backdrop) close(undefined); } }, dialog);
        root.append(backdrop);
        const autofocus = dialog.querySelector("[autofocus]") ?? dialog.querySelector("input, select, textarea") ?? dialog;
        autofocus.focus({ preventScroll: true });
    });
}

/** A dialog with several outcomes: the key of the button pressed, or "cancel". */
export async function choiceDialog(title, text, buttons) {
    const result = await modal({
        title,
        body: () => h("div", { class: "dialog-text" }, text),
        footer: (close) => [
            h("button", { class: "btn", onClick: () => close("cancel") }, "Cancel"),
            ...buttons.map((b) => h("button", { class: `btn ${b.kind ?? ""}`, onClick: () => close(b.key) }, b.label)),
        ],
    });
    return typeof result === "string" ? result : "cancel";
}

export async function confirmDialog(title, text, okLabel = "OK", kind = "primary") {
    return (await choiceDialog(title, text, [{ key: "ok", label: okLabel, kind }])) === "ok";
}

/** A one-field prompt; resolves with the text, or undefined. validate(text) returns an error text or "". */
export function promptDialog(title, { label = "", value = "", placeholder = "", hint = "", validate, transform } = {}) {
    return modal({
        title,
        width: "480px",
        body: (close) => {
            const error = h("div", { class: "field-error" });
            const input = h("input", { type: "text", value, placeholder, autofocus: true, spellcheck: "false" });
            const preview = h("div", { class: "hint mono" });
            // The text stays as typed; the normalised form (e.g. upper case) is shown below and applied on leaving the field / OK.
            const normal = () => (transform ? transform(input.value) : input.value).trim();
            const check = () => {
                const text = normal();
                preview.textContent = transform && text !== input.value.trim() && text ? `${t("will be saved as")} ${text}` : "";
                error.textContent = input.value.trim() ? t(validate?.(text) ?? "") : "";
            };
            const checkSoon = debounce(check, 400);
            const submit = () => {
                checkSoon.cancel();
                const text = normal();
                const problem = validate?.(text) ?? "";
                if (problem) { error.textContent = t(problem); return; }
                close(text);
            };
            input.addEventListener("input", () => { preview.textContent = ""; error.textContent = ""; checkSoon(); });
            input.addEventListener("change", () => { if (transform && !composing) input.value = transform(input.value); check(); });
            input.addEventListener("keydown", (e) => { if (e.key === "Enter" && !e.isComposing) { e.preventDefault(); submit(); } });
            setTimeout(() => input.select(), 0);
            return h("div", { class: "stack" }, label ? h("label", { class: "label" }, label) : null, input, preview, hint ? h("div", { class: "hint" }, hint) : null, error,
                h("div", { class: "row end" }, h("button", { class: "btn", onClick: () => close(undefined) }, "Cancel"), h("button", { class: "btn primary", onClick: submit }, "OK")));
        },
    });
}

/** A button with a drop-down menu: items [{label, onClick, danger, separator, disabled}]. */
export function menuButton(label, items, { title = "", className = "btn" } = {}) {
    const anchor = h("div", { class: "menu-anchor" });
    const button = h("button", { class: className, title, "aria-haspopup": "menu" }, label);
    let menu = null;
    const close = () => {
        menu?.remove();
        menu = null;
        document.removeEventListener("mousedown", onOutside, true);
    };
    const onOutside = (e) => { if (!anchor.contains(e.target)) close(); };
    button.addEventListener("click", (e) => {
        e.stopPropagation();
        if (menu) return close();
        menu = h("div", { class: "menu", role: "menu" }, ...items.filter(Boolean).map((item) => item.separator ? h("div", { class: "sep" })
            : h("button", { class: item.danger ? "danger" : "", role: "menuitem", disabled: item.disabled, onClick: () => { close(); item.onClick(); } }, item.label)));
        anchor.append(menu);
        document.addEventListener("mousedown", onOutside, true);
    });
    anchor.append(button);
    return anchor;
}

export function debounce(callback, delay = 250) {
    let timer;
    const run = (...args) => {
        clearTimeout(timer);
        timer = setTimeout(() => callback(...args), delay);
    };
    run.cancel = () => clearTimeout(timer);
    return run;
}

/** An <img> of an icon URL that hides itself when it fails, with a placeholder letter. */
export function icon(url, size = 32, letter = "") {
    const box = h("span", { class: "icon-box", style: { width: `${size}px`, height: `${size}px` } });
    if (url) {
        const img = h("img", { src: url, width: size, height: size, alt: "", loading: "lazy", draggable: "false" });
        img.addEventListener("error", () => { img.remove(); box.classList.add("empty"); box.textContent = letter; }, { once: true });
        box.append(img);
    } else {
        box.classList.add("empty");
        box.textContent = letter;
    }
    return box;
}

/** Deep copy of a JSON value. */
export const clone = (v) => (v === undefined ? undefined : JSON.parse(JSON.stringify(v)));

/** JSON pointer helpers ("/recipe/resources/0/item"). */
export function pointerParts(path) {
    return String(path ?? "").split("/").slice(1).map((s) => s.replace(/~1/g, "/").replace(/~0/g, "~"));
}

export function pointer(...parts) {
    return parts.length ? "/" + parts.map((p) => String(p).replace(/~/g, "~0").replace(/\//g, "~1")).join("/") : "";
}

export function getAt(obj, path) {
    let at = obj;
    for (const part of pointerParts(path)) {
        if (at === null || at === undefined) return undefined;
        at = at[part];
    }
    return at;
}

/** Sets (or with value undefined deletes) the value at a JSON pointer, making objects on the way. */
export function setAt(obj, path, value) {
    const parts = pointerParts(path);
    let at = obj;
    for (let i = 0; i < parts.length - 1; i++) {
        if (at[parts[i]] === null || typeof at[parts[i]] !== "object") at[parts[i]] = /^\d+$/.test(parts[i + 1]) ? [] : {};
        at = at[parts[i]];
    }
    const last = parts.at(-1);
    if (value === undefined) {
        if (Array.isArray(at)) at.splice(Number(last), 1);
        else delete at[last];
    } else at[last] = value;
}

/** Renders Albion's text markup ([dmg]…[/dmg], [cc], [other], <color=…>) as spans of classes, safely (text nodes only). */
export function markup(text) {
    const root = h("span", { class: "markup" });
    if (!text) return root;
    const stack = [root];
    const re = /\[(\/?)([a-z]+)\]|<\/?color[^>]*>|<\/?b>|<br\s*\/?>/gi;
    let last = 0;
    let m;
    while ((m = re.exec(text)) !== null) {
        stack.at(-1).append(document.createTextNode(text.slice(last, m.index)));
        last = m.index + m[0].length;
        if (m[2]) {
            if (m[1]) { if (stack.length > 1) stack.pop(); }
            else {
                const span = h("span", { class: `mk-${m[2].toLowerCase()}` });
                stack.at(-1).append(span);
                stack.push(span);
            }
        } else if (/^<br/i.test(m[0])) stack.at(-1).append(document.createElement("br"));
    }
    stack.at(-1).append(document.createTextNode(text.slice(last)));
    return root;
}
