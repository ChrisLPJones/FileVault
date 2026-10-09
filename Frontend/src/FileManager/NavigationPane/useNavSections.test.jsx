import { StrictMode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { useNavSections } from "./useNavSections";

const KEY = "fv-quick-access";

describe("useNavSections", () => {
    beforeEach(() => localStorage.clear());
    afterEach(() => vi.restoreAllMocks());

    it("defaults every section to open", () => {
        const { result } = renderHook(() => useNavSections());
        expect(result.current[0]).toEqual({ folders: true, favourites: true, recent: true });
    });

    it("toggles one section and remembers it", () => {
        const { result } = renderHook(() => useNavSections());
        act(() => result.current[1]("folders"));
        expect(result.current[0].folders).toBe(false);
        expect(result.current[0].favourites).toBe(true);
        expect(JSON.parse(localStorage.getItem(KEY)).folders).toBe(false);
    });

    it("reads the saved state on mount", () => {
        localStorage.setItem(KEY, JSON.stringify({ favourites: false }));
        const { result } = renderHook(() => useNavSections());
        expect(result.current[0]).toEqual({ folders: true, favourites: false, recent: true });
    });

    it("survives corrupt storage", () => {
        localStorage.setItem(KEY, "{not json");
        const { result } = renderHook(() => useNavSections());
        expect(result.current[0].folders).toBe(true);
    });

    // The app runs under StrictMode, which calls state updaters twice in development
    it("toggles exactly once under StrictMode, including a second toggle", () => {
        const { result } = renderHook(() => useNavSections(), { wrapper: StrictMode });
        act(() => result.current[1]("folders"));
        expect(result.current[0].folders).toBe(false);
        expect(JSON.parse(localStorage.getItem(KEY)).folders).toBe(false);
        act(() => result.current[1]("folders"));
        expect(result.current[0].folders).toBe(true);
    });

    it("keeps toggling when saving fails but a saved value can still be read", () => {
        localStorage.setItem(KEY, JSON.stringify({ folders: true }));
        vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new Error("quota"); });
        const { result } = renderHook(() => useNavSections());
        act(() => result.current[1]("folders"));
        expect(result.current[0].folders).toBe(false);
        act(() => result.current[1]("folders"));
        expect(result.current[0].folders).toBe(true);
        act(() => result.current[1]("folders"));
        expect(result.current[0].folders).toBe(false);
    });

    it("can collapse and re-expand a section when storage throws", () => {
        vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => { throw new Error("blocked"); });
        vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new Error("blocked"); });
        const { result } = renderHook(() => useNavSections(), { wrapper: StrictMode });
        act(() => result.current[1]("folders"));
        expect(result.current[0].folders).toBe(false);
        act(() => result.current[1]("folders"));
        expect(result.current[0].folders).toBe(true);
    });
});
