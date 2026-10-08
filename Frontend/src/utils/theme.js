import { useSyncExternalStore } from "react";

// Theme preference: "system" follows the OS setting; stored per device.
// index.html applies the stored theme before the app loads, so there's no flash.
const STORAGE_KEY = "fv-theme";
const CHANGE_EVENT = "fv-theme-change";
export const THEME_OPTIONS = ["system", "light", "dark"];

const darkQuery = window.matchMedia("(prefers-color-scheme: dark)");

// The choice made in this page load. It wins over storage, so a choice still applies when
// storage is unavailable (e.g. private mode) and the write fails.
let chosenTheme = null;

export const getThemePreference = () => {
    if (chosenTheme) return chosenTheme;
    try {
        const stored = localStorage.getItem(STORAGE_KEY);
        return THEME_OPTIONS.includes(stored) ? stored : "system";
    } catch {
        return "system";
    }
};

const resolveTheme = (preference) =>
    preference === "system" ? (darkQuery.matches ? "dark" : "light") : preference;

const applyTheme = () => {
    document.documentElement.dataset.theme = resolveTheme(getThemePreference());
};

export const setThemePreference = (preference) => {
    chosenTheme = THEME_OPTIONS.includes(preference) ? preference : "system";
    try {
        localStorage.setItem(STORAGE_KEY, preference);
    } catch {
        // Storage unavailable (e.g. private mode): still switch for this page load
    }
    applyTheme();
    window.dispatchEvent(new Event(CHANGE_EVENT));
};

// Keep the page in sync with OS changes (in "system" mode) and with other tabs
darkQuery.addEventListener("change", () => {
    applyTheme();
    window.dispatchEvent(new Event(CHANGE_EVENT));
});
window.addEventListener("storage", (event) => {
    if (event.key !== STORAGE_KEY) return;
    chosenTheme = null; // another tab changed it: follow storage again
    applyTheme();
    window.dispatchEvent(new Event(CHANGE_EVENT));
});

const subscribe = (callback) => {
    window.addEventListener(CHANGE_EVENT, callback);
    return () => window.removeEventListener(CHANGE_EVENT, callback);
};

// { preference: "system" | "light" | "dark", theme: "light" | "dark" }
export const useTheme = () => {
    const preference = useSyncExternalStore(subscribe, getThemePreference);
    const theme = useSyncExternalStore(subscribe, () => resolveTheme(getThemePreference()));
    return { preference, theme };
};

// Accent colour: overrides --fv-primary (buttons, links, the file manager highlight). null = theme default.
const ACCENT_KEY = "fv-accent";
const ACCENT_EVENT = "fv-accent-change";
export const ACCENT_PRESETS = ["#007bff", "#6155b4", "#0d9488", "#16a34a", "#e11d48", "#ea580c", "#db2777"];

const isHex = (value) => /^#[0-9a-f]{6}$/i.test(value || "");

// As with the theme: the choice made in this page load wins over storage (undefined = none yet)
let chosenAccent;

export const getAccent = () => {
    if (chosenAccent !== undefined) return chosenAccent;
    try {
        const stored = localStorage.getItem(ACCENT_KEY);
        return isHex(stored) ? stored.toLowerCase() : null;
    } catch {
        return null;
    }
};

const shade = (hex, factor) => {
    const channel = (i) => Math.round(parseInt(hex.slice(1 + i * 2, 3 + i * 2), 16) * factor);
    return `#${[0, 1, 2].map((i) => channel(i).toString(16).padStart(2, "0")).join("")}`;
};

const readableText = (hex) => {
    const [r, g, b] = [0, 1, 2].map((i) => parseInt(hex.slice(1 + i * 2, 3 + i * 2), 16));
    return (r * 299 + g * 587 + b * 114) / 1000 > 160 ? "#111" : "#fff";
};

const applyAccent = () => {
    const style = document.documentElement.style;
    const accent = getAccent();
    if (!accent) {
        ["--fv-primary", "--fv-primary-hover", "--fv-primary-text"].forEach((name) => style.removeProperty(name));
        return;
    }
    style.setProperty("--fv-primary", accent);
    style.setProperty("--fv-primary-hover", shade(accent, 0.82));
    style.setProperty("--fv-primary-text", readableText(accent));
};

export const setAccent = (color) => {
    chosenAccent = isHex(color) ? color.toLowerCase() : null;
    try {
        if (isHex(color)) localStorage.setItem(ACCENT_KEY, color.toLowerCase());
        else localStorage.removeItem(ACCENT_KEY);
    } catch {
        // Storage unavailable: still apply for this page load
    }
    applyAccent();
    window.dispatchEvent(new Event(ACCENT_EVENT));
};

applyAccent();

const subscribeAccent = (callback) => {
    window.addEventListener(ACCENT_EVENT, callback);
    return () => window.removeEventListener(ACCENT_EVENT, callback);
};

export const useAccent = () => useSyncExternalStore(subscribeAccent, getAccent);
