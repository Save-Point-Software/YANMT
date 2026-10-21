// YANMT map interaction: pan, zoom, click, and (in edit mode) dragging icons.
// Hand-written and dependency-free on purpose (no libraries, no build step). Blazor renders the
// SVG; this module only moves things locally while the pointer is down, then reports the final
// position or the click to .NET, which saves it and re-renders.

const DRAG_THRESHOLD = 4; // px of pointer travel before a press counts as a drag, not a click
const MIN_ZOOM = 0.1;
const MAX_ZOOM = 8;

export function create(svg, dotnet, options) {
    return new MapController(svg, dotnet, options);
}

class MapController {
    constructor(svg, dotnet, options) {
        this.svg = svg;
        this.dotnet = dotnet;
        this.editable = !!options.editable;
        this.width = options.width;
        this.height = options.height;
        this.k = 1;
        this.tx = 0;
        this.ty = 0;
        this.press = null;

        this.onDown = e => this.pointerDown(e);
        this.onMove = e => this.pointerMove(e);
        this.onUp = e => this.pointerUp(e);
        this.onWheel = e => this.wheel(e);
        svg.addEventListener("pointerdown", this.onDown);
        svg.addEventListener("pointermove", this.onMove);
        svg.addEventListener("pointerup", this.onUp);
        svg.addEventListener("pointercancel", this.onUp);
        svg.addEventListener("wheel", this.onWheel, { passive: false });
        this.fit();
    }

    viewport() {
        return this.svg.querySelector("[data-viewport]");
    }

    setEditable(editable) {
        this.editable = !!editable;
    }

    // Called when Blazor shows a different map (or the canvas size changed).
    setCanvas(width, height, refit) {
        this.width = width;
        this.height = height;
        if (refit) {
            this.fit();
        }
    }

    fit() {
        const r = this.svg.getBoundingClientRect();
        if (r.width === 0 || r.height === 0) {
            return;
        }
        this.k = clamp(Math.min(r.width / this.width, r.height / this.height) * 0.95, MIN_ZOOM, MAX_ZOOM);
        this.tx = (r.width - this.width * this.k) / 2;
        this.ty = (r.height - this.height * this.k) / 2;
        this.apply();
    }

    zoomBy(factor) {
        const r = this.svg.getBoundingClientRect();
        this.zoomAt(r.width / 2, r.height / 2, factor);
    }

    // Map coordinates at the middle of what's on screen, where new icons are placed.
    center() {
        const r = this.svg.getBoundingClientRect();
        return { x: (r.width / 2 - this.tx) / this.k, y: (r.height / 2 - this.ty) / this.k };
    }

    apply() {
        const vp = this.viewport();
        if (vp) {
            vp.setAttribute("transform", `translate(${this.tx} ${this.ty}) scale(${this.k})`);
        }
    }

    zoomAt(sx, sy, factor) {
        const k = clamp(this.k * factor, MIN_ZOOM, MAX_ZOOM);
        this.tx = sx - (sx - this.tx) * (k / this.k);
        this.ty = sy - (sy - this.ty) * (k / this.k);
        this.k = k;
        this.apply();
    }

    wheel(e) {
        e.preventDefault();
        const r = this.svg.getBoundingClientRect();
        this.zoomAt(e.clientX - r.left, e.clientY - r.top, Math.exp(-e.deltaY * 0.0015));
    }

    pointerDown(e) {
        if (e.button !== 0) {
            return;
        }
        const el = e.target.closest("[data-el]");
        const link = el ? null : e.target.closest("[data-link]");
        this.press = {
            kind: el ? "element" : link ? "link" : "background",
            id: el ? +el.dataset.el : link ? +link.dataset.link : 0,
            node: el,
            startX: e.clientX,
            startY: e.clientY,
            originX: el ? +el.dataset.x : 0,
            originY: el ? +el.dataset.y : 0,
            panFromX: this.tx,
            panFromY: this.ty,
            moved: false,
        };
        this.svg.setPointerCapture(e.pointerId);
    }

    pointerMove(e) {
        const p = this.press;
        if (!p) {
            return;
        }
        const dx = e.clientX - p.startX;
        const dy = e.clientY - p.startY;
        if (!p.moved && Math.hypot(dx, dy) < DRAG_THRESHOLD) {
            return;
        }
        p.moved = true;

        if (p.kind === "element" && this.editable) {
            const x = clamp(p.originX + dx / this.k, 0, this.width);
            const y = clamp(p.originY + dy / this.k, 0, this.height);
            this.moveElement(p.node, x, y);
        } else {
            // Dragging the background, or anything when not editing, pans the view.
            this.tx = p.panFromX + dx;
            this.ty = p.panFromY + dy;
            this.apply();
        }
    }

    moveElement(node, x, y) {
        node.dataset.x = x;
        node.dataset.y = y;
        node.setAttribute("transform", `translate(${x} ${y})`);
        const id = node.dataset.el;
        for (const link of this.svg.querySelectorAll(`[data-link][data-from="${id}"], [data-link][data-to="${id}"]`)) {
            const a = this.svg.querySelector(`[data-el="${link.dataset.from}"]`);
            const b = this.svg.querySelector(`[data-el="${link.dataset.to}"]`);
            if (!a || !b) {
                continue;
            }
            for (const line of link.querySelectorAll("line")) {
                line.setAttribute("x1", a.dataset.x);
                line.setAttribute("y1", a.dataset.y);
                line.setAttribute("x2", b.dataset.x);
                line.setAttribute("y2", b.dataset.y);
            }
            const label = link.querySelector("text");
            if (label) {
                label.setAttribute("x", (+a.dataset.x + +b.dataset.x) / 2);
                label.setAttribute("y", (+a.dataset.y + +b.dataset.y) / 2 - 6);
            }
        }
    }

    pointerUp(e) {
        const p = this.press;
        this.press = null;
        if (!p) {
            return;
        }
        if (this.svg.hasPointerCapture(e.pointerId)) {
            this.svg.releasePointerCapture(e.pointerId);
        }
        if (e.type === "pointercancel") {
            return;
        }
        if (!p.moved) {
            const r = this.svg.getBoundingClientRect();
            const x = (e.clientX - r.left - this.tx) / this.k;
            const y = (e.clientY - r.top - this.ty) / this.k;
            this.dotnet.invokeMethodAsync("OnMapClick", p.kind, p.id, x, y);
        } else if (p.kind === "element" && this.editable) {
            this.dotnet.invokeMethodAsync("OnElementMoved", p.id, +p.node.dataset.x, +p.node.dataset.y);
        }
    }

    dispose() {
        const svg = this.svg;
        svg.removeEventListener("pointerdown", this.onDown);
        svg.removeEventListener("pointermove", this.onMove);
        svg.removeEventListener("pointerup", this.onUp);
        svg.removeEventListener("pointercancel", this.onUp);
        svg.removeEventListener("wheel", this.onWheel);
        this.dotnet = null;
    }
}

function clamp(v, min, max) {
    return Math.min(Math.max(v, min), max);
}
