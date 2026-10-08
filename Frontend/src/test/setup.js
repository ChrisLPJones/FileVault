// Runs before every test file (see vite.config.js)
import "@testing-library/jest-dom/vitest";
import { cleanup } from "@testing-library/react";
import { afterEach, vi } from "vitest";

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
    localStorage.clear();
    matching = new Set();
});
