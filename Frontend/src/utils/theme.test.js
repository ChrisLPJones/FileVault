import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { setMediaMatches } from "../test/setup";

const DARK = "(prefers-color-scheme: dark)";

// theme.js keeps the choice made in this page load in module state, so each test loads a fresh copy
const loadTheme = async () => {
    vi.resetModules();
    return import("./theme");
};

const blockStorage = () => {
    for (const method of ["getItem", "setItem", "removeItem"])
        vi.spyOn(Storage.prototype, method).mockImplementation(() => {
            throw new DOMException("The operation is insecure.", "SecurityError");
        });
};

const rootStyle = () => document.documentElement.style;

beforeEach(() => {
    delete document.documentElement.dataset.theme;
    ["--fv-primary", "--fv-primary-hover", "--fv-primary-text"].forEach((name) => rootStyle().removeProperty(name));
});

afterEach(() => vi.restoreAllMocks());

describe("theme preference", () => {
    it("defaults to following the system, and resolves it from the OS setting", async () => {
        setMediaMatches(DARK);
        const theme = await loadTheme();
        expect(theme.getThemePreference()).toBe("system");

        const { result } = renderHook(() => theme.useTheme());
        expect(result.current).toEqual({ preference: "system", theme: "dark" });
    });

    it("reads a saved choice and ignores unknown values", async () => {
        localStorage.setItem("fv-theme", "light");
        expect((await loadTheme()).getThemePreference()).toBe("light");

        localStorage.setItem("fv-theme", "purple");
        expect((await loadTheme()).getThemePreference()).toBe("system");
    });

    it("saves a choice, applies it to the page and updates subscribers", async () => {
        const theme = await loadTheme();
        const { result } = renderHook(() => theme.useTheme());

        act(() => theme.setThemePreference("dark"));
        expect(localStorage.getItem("fv-theme")).toBe("dark");
        expect(document.documentElement.dataset.theme).toBe("dark");
        expect(result.current).toEqual({ preference: "dark", theme: "dark" });

        act(() => theme.setThemePreference("light"));
        expect(document.documentElement.dataset.theme).toBe("light");
        expect(result.current.theme).toBe("light");
    });

    it("follows OS changes while set to system", async () => {
        const theme = await loadTheme();
        const { result } = renderHook(() => theme.useTheme());
        act(() => theme.setThemePreference("system"));
        expect(result.current.theme).toBe("light");

        act(() => setMediaMatches(DARK));
        expect(result.current.theme).toBe("dark");
        expect(document.documentElement.dataset.theme).toBe("dark");
    });

    it("still switches when storage is blocked (e.g. private mode)", async () => {
        blockStorage();
        const theme = await loadTheme();
        expect(theme.getThemePreference()).toBe("system");

        expect(() => theme.setThemePreference("dark")).not.toThrow();
        expect(theme.getThemePreference()).toBe("dark");
        expect(document.documentElement.dataset.theme).toBe("dark");
    });
});

describe("accent colour", () => {
    it("has no accent by default (the theme's own colour)", async () => {
        const theme = await loadTheme();
        expect(theme.getAccent()).toBeNull();
        expect(rootStyle().getPropertyValue("--fv-primary")).toBe("");
    });

    it("applies a saved accent on load, lower-cased", async () => {
        localStorage.setItem("fv-accent", "#0D9488");
        const theme = await loadTheme();
        expect(theme.getAccent()).toBe("#0d9488");
        expect(rootStyle().getPropertyValue("--fv-primary")).toBe("#0d9488");
    });

    it("ignores a saved value that isn't a #rrggbb colour", async () => {
        localStorage.setItem("fv-accent", "red; background: url(x)");
        expect((await loadTheme()).getAccent()).toBeNull();
    });

    it("sets the colour, a darker hover shade and readable text", async () => {
        const theme = await loadTheme();
        const { result } = renderHook(() => theme.useAccent());

        act(() => theme.setAccent("#6155b4"));
        expect(result.current).toBe("#6155b4");
        expect(localStorage.getItem("fv-accent")).toBe("#6155b4");
        expect(rootStyle().getPropertyValue("--fv-primary")).toBe("#6155b4");
        expect(rootStyle().getPropertyValue("--fv-primary-hover")).toBe("#504694"); // 82% of each channel
        expect(rootStyle().getPropertyValue("--fv-primary-text")).toBe("#fff");
    });

    it("uses dark text on light accents", async () => {
        const theme = await loadTheme();
        theme.setAccent("#f5e663");
        expect(rootStyle().getPropertyValue("--fv-primary-text")).toBe("#111");
    });

    it("clears the accent for null or invalid colours", async () => {
        const theme = await loadTheme();
        theme.setAccent("#16a34a");
        theme.setAccent(null);
        expect(theme.getAccent()).toBeNull();
        expect(localStorage.getItem("fv-accent")).toBeNull();
        expect(rootStyle().getPropertyValue("--fv-primary")).toBe("");

        theme.setAccent("blue");
        expect(theme.getAccent()).toBeNull();
    });

    it("still applies when storage is blocked", async () => {
        blockStorage();
        const theme = await loadTheme();
        expect(theme.getAccent()).toBeNull();

        expect(() => theme.setAccent("#e11d48")).not.toThrow();
        expect(theme.getAccent()).toBe("#e11d48");
        expect(rootStyle().getPropertyValue("--fv-primary")).toBe("#e11d48");
    });
});
