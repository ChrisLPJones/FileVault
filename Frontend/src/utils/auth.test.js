import { describe, expect, it, vi } from "vitest";
import { meetsPasswordRules, passwordRules } from "./passwordRules";
import {
    SESSION_HINT_KEY,
    clearToken,
    endSession,
    getToken,
    hasSessionHint,
    isAuthenticated,
    isTokenExpiring,
    setToken,
    startSession,
    subscribeAuth,
} from "./auth";
import { makeToken } from "../test/tokens";

describe("password rules", () => {
    const failing = (password) => passwordRules.filter((rule) => !rule.test(password)).map((rule) => rule.label);

    it("accepts a password that meets every rule (same as the server)", () => {
        expect(meetsPasswordRules("Passw0rd")).toBe(true);
        expect(failing("Passw0rd")).toEqual([]);
    });

    it("names each rule a password breaks", () => {
        expect(failing("")).toEqual([
            "At least 8 characters",
            "At least one number",
            "At least one uppercase letter",
            "At least one lowercase letter",
        ]);
        expect(failing("Short1a")).toEqual(["At least 8 characters"]);
        expect(failing("password1")).toEqual(["At least one uppercase letter"]);
        expect(failing("PASSWORD1")).toEqual(["At least one lowercase letter"]);
        expect(failing("Password")).toEqual(["At least one number"]);
    });

    it("rejects passwords missing any rule", () => {
        for (const password of ["Short1a", "password1", "PASSWORD1", "Password"])
            expect(meetsPasswordRules(password)).toBe(false);
    });
});

describe("access token helpers", () => {
    it("stores, reads and clears the token in memory only", () => {
        const token = makeToken();
        setToken(token);
        expect(getToken()).toBe(token);
        expect(isAuthenticated()).toBe(true);
        expect(JSON.stringify({ ...localStorage })).not.toContain(token);
        expect(JSON.stringify({ ...sessionStorage })).not.toContain(token);

        clearToken();
        expect(getToken()).toBeNull();
        expect(isAuthenticated()).toBe(false);
    });

    it("notifies subscribers when the token changes", () => {
        const listener = vi.fn();
        const unsubscribe = subscribeAuth(listener);
        setToken(makeToken());
        clearToken();
        expect(listener).toHaveBeenCalledTimes(2);

        unsubscribe();
        setToken(makeToken());
        expect(listener).toHaveBeenCalledTimes(2);
    });

    it("does not store an unreadable token", () => {
        setToken("garbage");
        expect(getToken()).toBeNull();
        expect(isAuthenticated()).toBe(false);
    });

    it("forgets the last files address when the token is cleared", () => {
        sessionStorage.setItem("fv-last-files-url", "/dashboard?folder=%2FSecret");
        clearToken();
        expect(sessionStorage.getItem("fv-last-files-url")).toBeNull();
    });

    it("treats a token as expiring within 30 seconds of its expiry", () => {
        expect(isTokenExpiring(makeToken({ expiresIn: 600 }))).toBe(false);
        expect(isTokenExpiring(makeToken({ expiresIn: 20 }))).toBe(true);
        expect(isTokenExpiring(makeToken({ expiresIn: -60 }))).toBe(true);
    });

    it("treats unreadable tokens and tokens without an expiry as expiring", () => {
        expect(isTokenExpiring("not-a-jwt")).toBe(true);
        expect(isTokenExpiring(makeToken({ expiresIn: null }))).toBe(true);
    });

    it("counts an expired but readable token as held (the client refreshes it)", () => {
        setToken(makeToken({ expiresIn: -60 }));
        expect(isAuthenticated()).toBe(true);
    });
});

describe("session hint", () => {
    it("startSession keeps the token and sets a hint that is not the token", () => {
        const token = makeToken();
        startSession(token);
        expect(getToken()).toBe(token);
        expect(hasSessionHint()).toBe(true);
        const hint = localStorage.getItem(SESSION_HINT_KEY);
        expect(hint).toBeTruthy();
        expect(hint).not.toContain(token);
    });

    it("endSession drops the token and the hint", () => {
        startSession(makeToken());
        endSession();
        expect(getToken()).toBeNull();
        expect(hasSessionHint()).toBe(false);
    });

    it("tolerates unavailable storage (always tries a refresh)", () => {
        const spies = ["getItem", "setItem", "removeItem"].map((method) =>
            vi.spyOn(Storage.prototype, method).mockImplementation(() => {
                throw new Error("denied");
            })
        );

        expect(hasSessionHint()).toBe(true);
        expect(() => startSession(makeToken())).not.toThrow();
        expect(() => endSession()).not.toThrow();
        spies.forEach((spy) => spy.mockRestore());
    });
});

describe("legacy token migration", () => {
    it("removes the old localStorage token at load and keeps its owner signed in via the hint", async () => {
        localStorage.setItem("token", "old-long-lived-token");
        vi.resetModules();
        const fresh = await import("./auth");

        expect(localStorage.getItem("token")).toBeNull();
        expect(fresh.getToken()).toBeNull();
        expect(fresh.hasSessionHint()).toBe(true);
    });
});
