import { describe, expect, it } from "vitest";
import { resolveApiBaseUrl } from "./apiBaseUrl";

describe("resolveApiBaseUrl", () => {
    it("prefers the runtime config over the build-time URL", () => {
        expect(resolveApiBaseUrl({ apiUrl: "https://api.example.test" }, "http://env.test")).toBe("https://api.example.test");
    });

    it("treats an empty runtime URL as same origin", () => {
        expect(resolveApiBaseUrl({ apiUrl: "" }, "http://env.test")).toBe("");
    });

    it("falls back to the build-time URL without a usable runtime value", () => {
        expect(resolveApiBaseUrl(undefined, "http://env.test")).toBe("http://env.test");
        expect(resolveApiBaseUrl({}, "http://env.test")).toBe("http://env.test");
        expect(resolveApiBaseUrl({ apiUrl: 5 }, "http://env.test")).toBe("http://env.test");
    });

    it("returns an empty string when nothing is configured", () => {
        expect(resolveApiBaseUrl(undefined, undefined)).toBe("");
    });
});
