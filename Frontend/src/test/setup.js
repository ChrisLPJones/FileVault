// Runs before every test file (see vite.config.js)
import "@testing-library/jest-dom/vitest";
import { cleanup } from "@testing-library/react";
import { afterEach, vi } from "vitest";
import { clearToken } from "../utils/auth";

// Pages show a Privacy link only on a hosted site, found out from GET /site. Tests default to a
// self-hosted one without touching the network; tests of the link or the privacy page mock this themselves.
vi.mock("../api/siteAPI", () => ({ getSiteInfoAPI: async () => ({ privacy: null }) }));

// jsdom has no matchMedia. Tests can change what matches with setMediaMatches().
let matching = new Set();
const lists = new Set();

window.matchMedia = vi.fn((query) => {
    const listeners = new Set();
    const list = {
        media: query,
        get matches() {
            return matching.has(query);
        },
        addEventListener: (_type, listener) => listeners.add(listener),
        removeEventListener: (_type, listener) => listeners.delete(listener),
        addListener: (listener) => listeners.add(listener),
        removeListener: (listener) => listeners.delete(listener),
        notify: () => listeners.forEach((listener) => listener({ matches: list.matches, media: query })),
    };
    lists.add(list);
    return list;
});

// Make these media queries match (and tell anything listening)
export const setMediaMatches = (...queries) => {
    matching = new Set(queries);
    lists.forEach((list) => list.notify());
};

afterEach(() => {
    cleanup();
    // The access token lives in module state, which would otherwise leak into the next test
    clearToken();
    localStorage.clear();
    matching = new Set();
});
