// YANMT "Keep screen on" for the map page, using the Screen Wake Lock API.
// Hand-written and dependency-free (no libraries, no build step). The browser drops the lock when
// the tab is hidden, so it is requested again when the tab becomes visible while the toggle is on.
// The choice is remembered per browser in localStorage; storage failures are ignored.

const STORAGE_KEY = "yanmt.keepScreenOn";

export function create(dotnet) {
    return new WakeLockController(dotnet);
}

function readSaved() {
    try {
        return window.localStorage.getItem(STORAGE_KEY) === "1";
    } catch {
        return false;
    }
}

function save(on) {
    try {
        if (on) {
            window.localStorage.setItem(STORAGE_KEY, "1");
        } else {
            window.localStorage.removeItem(STORAGE_KEY);
        }
    } catch {
        // Storage blocked (private window, policy): the toggle still works for this visit.
    }
}

function unsupportedReason() {
    if (!window.isSecureContext) {
        return "Needs HTTPS: the browser only allows this on a secure connection.";
    }
    if (!("wakeLock" in navigator)) {
        return "This browser doesn't support keeping the screen on.";
    }
    return null;
}

class WakeLockController {
    constructor(dotnet) {
        this.dotnet = dotnet;
        this.wanted = false;
        this.lock = null;
        this.error = null;
        this.pending = null;
        this.disposed = false;
        this.reason = unsupportedReason();
        this.onVisibility = () => {
            if (document.visibilityState === "visible" && this.wanted) {
                this.acquire();
            }
        };
        document.addEventListener("visibilitychange", this.onVisibility);
    }

    // Returns the initial state and re-applies the saved choice.
    async start() {
        if (!this.reason && readSaved()) {
            this.wanted = true;
            await this.acquire();
        }
        return this.state();
    }

    async setWanted(on) {
        if (this.reason) {
            return this.state();
        }
        this.wanted = !!on;
        save(this.wanted);
        if (this.wanted) {
            await this.acquire();
        } else {
            await this.release();
        }
        return this.state();
    }

    state() {
        return {
            supported: !this.reason,
            reason: this.reason ?? this.error,
            wanted: this.wanted,
            held: !!this.lock && !this.lock.released,
        };
    }

    acquire() {
        if (this.reason || this.disposed || this.lock || document.visibilityState !== "visible") {
            return this.pending ?? Promise.resolve();
        }
        if (this.pending) {
            return this.pending;
        }
        this.pending = (async () => {
            try {
                const lock = await navigator.wakeLock.request("screen");
                if (!this.wanted || this.disposed) {
                    // Turned off or left the page while the request was in flight.
                    await lock.release().catch(() => {});
                    return;
                }
                this.lock = lock;
                this.error = null;
                lock.addEventListener("release", () => {
                    if (this.lock === lock) {
                        this.lock = null;
                        this.report();
                    }
                });
            } catch (e) {
                this.error = "Couldn't keep the screen on: " + (e && e.message ? e.message : "the browser refused")
                    + " (for example, battery saver). It will try again when the tab is shown again.";
            } finally {
                this.pending = null;
            }
            this.report();
        })();
        return this.pending;
    }

    async release() {
        const lock = this.lock;
        this.lock = null;
        this.error = null;
        if (lock) {
            await lock.release().catch(() => {});
        }
    }

    report() {
        if (this.disposed) {
            return;
        }
        this.dotnet.invokeMethodAsync("OnWakeLockChanged", this.state()).catch(() => {});
    }

    async dispose() {
        this.disposed = true;
        this.wanted = false;
        document.removeEventListener("visibilitychange", this.onVisibility);
        await this.release();
    }
}
