import { describe, expect, it } from "vitest";
import { meetsPasswordRules, passwordRules } from "./passwordRules";
import { clearToken, getToken, isAuthenticated, isTokenExpiring, setToken } from "./auth";
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
    it("stores, reads and clears the token", () => {
        setToken("abc");
        expect(getToken()).toBe("abc");
        clearToken();
        expect(getToken()).toBeNull();
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

    it("counts an expired but readable token as a session (the client refreshes it)", () => {
        setToken(makeToken({ expiresIn: -60 }));
        expect(isAuthenticated()).toBe(true);
    });

    it("drops an unreadable token and reports no session", () => {
        expect(isAuthenticated()).toBe(false);
        setToken("garbage");
        expect(isAuthenticated()).toBe(false);
        expect(getToken()).toBeNull();
    });
});
