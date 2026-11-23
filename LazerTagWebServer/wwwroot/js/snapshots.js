'use strict';

// State and telemetry travel independently over SignalR and HTTP. Revisions
// order a single server process; a restart begins a new pair of streams.
class ArenaSnapshotOrder {
    constructor() {
        this.instance = null;
        this.retired = new Set();
        this.revisions = new Map();
        this.received = new Map();
        this.generation = 0;
        this.pending = null;
        this.reconnecting = false;
    }

    reconnect() {
        this.generation++;
        this.received.clear();
        this.reconnecting = true;
    }

    refresh(load) {
        if (this.pending) return this.pending;
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 5000);
        this.pending = load(this.generation, controller.signal).finally(() => {
            clearTimeout(timeout);
            this.pending = null;
        });
        return this.pending;
    }

    accept(kind, value, requestGeneration) {
        if (!value || typeof value !== 'object') return false;
        const instance = value.serverInstance;
        if (instance && Number.isSafeInteger(value.revision)) {
            if (instance !== this.instance) {
                if (this.retired.has(instance) ||
                    (requestGeneration !== undefined && requestGeneration !== this.generation)) return false;
                if (this.instance !== null) this.retired.add(this.instance);
                this.instance = instance;
                this.generation++;
                this.revisions.clear();
                this.received.clear();
            }
            if (value.revision <= (this.revisions.get(kind) ?? -1)) return false;
            this.revisions.set(kind, value.revision);
        } else if (this.instance !== null) {
            if ((!this.reconnecting && !this.needsRefresh()) ||
                (requestGeneration !== undefined && requestGeneration !== this.generation)) return false;
            this.retired.add(this.instance);
            this.instance = null;
            this.generation++;
            this.revisions.clear();
            this.received.clear();
        } else if (requestGeneration !== undefined && requestGeneration !== this.generation) {
            return false;
        }
        this.reconnecting = false;
        this.received.set(kind, performance.now());
        return true;
    }

    needsRefresh() {
        const now = performance.now();
        return ['state', 'telemetry'].some(kind => now - (this.received.get(kind) ?? -Infinity) >= 4500);
    }
}
