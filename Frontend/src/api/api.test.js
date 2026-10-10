import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import axios from "axios";
import { api, ensureSession, getErrorMessage, logout, refreshAccessToken } from "./api";
import { SESSION_HINT_KEY, getToken, hasSessionHint, setToken, startSession } from "../utils/auth";
import { makeToken } from "../test/tokens";

// A fake network: every request (from the API client and from bare axios, which the refresh
// uses) goes to `server`, which returns { status, data } or a promise of one.
let server;
const requests = [];

const fakeAdapter = async (config) => {
    requests.push({ method: config.method, url: config.url, auth: config.headers?.Authorization });
    const { status = 200, data = null } = await server(config);
    const response = { status, statusText: String(status), data, headers: {}, config, request: {} };
    if (status >= 400 && !config.validateStatus?.(status)) {
        throw new axios.AxiosError(`Request failed with status code ${status}`, "ERR_BAD_REQUEST", config, {}, response);
    }
    return response;
};

const urlOf = (config) => config.url.replace("http://api.test", "");
const bearer = (token) => `Bearer ${token}`;

let assign;
let originalAdapters;

beforeEach(() => {
    requests.length = 0;
    originalAdapters = [api.defaults.adapter, axios.defaults.adapter];
    api.defaults.adapter = fakeAdapter;
    axios.defaults.adapter = fakeAdapter;
    // Logging out navigates to /login, which jsdom can't do
    assign = vi.fn();
    vi.stubGlobal("location", { ...window.location, assign });
});

afterEach(() => {
    [api.defaults.adapter, axios.defaults.adapter] = originalAdapters;
    vi.unstubAllGlobals();
});

describe("API client", () => {
    it("sends the access token with each request", async () => {
        const token = makeToken();
        setToken(token);
        server = () => ({ data: ["file"] });

        const response = await api.get("/files");
        expect(response.data).toEqual(["file"]);
        expect(requests).toEqual([{ method: "get", url: "/files", auth: bearer(token) }]);
    });

    it("sends no token for requests that opt out (login, register)", async () => {
        setToken(makeToken());
        server = () => ({ status: 401, data: { error: "Invalid email or password" } });

        await expect(api.post("/user/login", {}, { skipAuth: true })).rejects.toMatchObject({ response: { status: 401 } });
        expect(requests).toHaveLength(1);
        expect(requests[0].auth).toBeUndefined();
        expect(assign).not.toHaveBeenCalled();
    });

    it("refreshes a token that is about to expire before sending the request", async () => {
        setToken(makeToken({ expiresIn: 10 }));
        const fresh = makeToken();
        server = (config) => (urlOf(config) === "/user/refresh" ? { data: { success: fresh } } : { data: "ok" });

        await api.get("/files");
        expect(requests.map((r) => r.url)).toEqual(["http://api.test/user/refresh", "/files"]);
        expect(requests[1].auth).toBe(bearer(fresh));
        expect(getToken()).toBe(fresh);
    });

    it("shares one refresh between concurrent requests that get a 401, then retries them", async () => {
        const stale = makeToken();
        const fresh = makeToken();
        setToken(stale);

        let refreshes = 0;
        let releaseRefresh;
        server = (config) => {
            if (urlOf(config) === "/user/refresh") {
                refreshes++;
                // Hold the refresh until every request has had its 401
                return new Promise((resolve) => (releaseRefresh = () => resolve({ data: { success: fresh } })));
            }
            return config.headers.Authorization === bearer(fresh)
                ? { data: `contents of ${config.url}` }
                : { status: 401, data: { error: "Invalid token" } };
        };

        const pending = Promise.all(["/a", "/b", "/c"].map((url) => api.get(url)));
        await vi.waitFor(() => expect(releaseRefresh).toBeTypeOf("function"));
        await vi.waitFor(() => expect(requests.filter((r) => r.auth === bearer(stale))).toHaveLength(3));
        releaseRefresh();

        const responses = await pending;
        expect(responses.map((r) => r.data)).toEqual(["contents of /a", "contents of /b", "contents of /c"]);
        expect(refreshes).toBe(1);
        expect(requests.filter((r) => r.auth === bearer(fresh)).map((r) => r.url).sort()).toEqual(["/a", "/b", "/c"]);
        expect(getToken()).toBe(fresh);
        expect(assign).not.toHaveBeenCalled();
    });

    it("logs out when the refresh fails", async () => {
        setToken(makeToken());
        server = (config) =>
            urlOf(config) === "/user/refresh"
                ? { status: 401, data: { error: "Session expired" } }
                : { status: 401, data: { error: "Invalid token" } };

        const results = await Promise.allSettled([api.get("/files"), api.get("/user/info")]);

        expect(results.every((r) => r.status === "rejected" && r.reason.response.status === 401)).toBe(true);
        expect(requests.filter((r) => r.url.endsWith("/user/refresh"))).toHaveLength(1);
        expect(getToken()).toBeNull();
        expect(assign).toHaveBeenCalledWith("/login");
    });

    it("retries a request only once", async () => {
        setToken(makeToken());
        server = (config) => (urlOf(config) === "/user/refresh" ? { data: { success: makeToken() } } : { status: 401 });

        await expect(api.get("/files")).rejects.toMatchObject({ response: { status: 401 } });
        expect(requests.map((r) => r.url)).toEqual(["/files", "http://api.test/user/refresh", "/files"]);
    });

    it("passes other errors straight through", async () => {
        setToken(makeToken());
        server = () => ({ status: 500, data: { error: "An internal error has occurred" } });

        const error = await api.get("/files").catch((e) => e);
        expect(getErrorMessage(error)).toBe("An internal error has occurred");
        expect(requests).toHaveLength(1);
    });

    it("lets a direct refresh share an in-flight one", async () => {
        const fresh = makeToken();
        server = () => ({ data: { success: fresh } });

        const [a, b] = await Promise.all([refreshAccessToken(), refreshAccessToken()]);
        expect(a).toBe(fresh);
        expect(b).toBe(fresh);
        expect(requests).toHaveLength(1);
    });
});

describe("refresh failures", () => {
    const networkError = () => new axios.AxiosError("Network Error", "ERR_NETWORK");

    it("ends the session on 401: token and hint gone", async () => {
        startSession(makeToken({ expiresIn: -60 }));
        server = () => ({ status: 401, data: { error: "Session expired" } });

        await expect(refreshAccessToken()).rejects.toMatchObject({ sessionExpired: true });
        expect(getToken()).toBeNull();
        expect(hasSessionHint()).toBe(false);
    });

    it("retries once when the 401 is a raced refresh, and keeps the session on success", async () => {
        startSession(makeToken({ expiresIn: -60 }));
        const fresh = makeToken();
        let calls = 0;
        server = () =>
            ++calls === 1 ? { status: 401, data: { error: "Session expired", raced: true } } : { data: { success: fresh } };

        await expect(refreshAccessToken()).resolves.toBe(fresh);
        expect(calls).toBe(2);
        expect(getToken()).toBe(fresh);
        expect(hasSessionHint()).toBe(true);
    });

    it("ends the session when the retry after a raced 401 is also a 401", async () => {
        startSession(makeToken({ expiresIn: -60 }));
        let calls = 0;
        server = () =>
            ++calls === 1
                ? { status: 401, data: { error: "Session expired", raced: true } }
                : { status: 401, data: { error: "Session expired" } };

        await expect(refreshAccessToken()).rejects.toMatchObject({ sessionExpired: true });
        expect(calls).toBe(2);
        expect(getToken()).toBeNull();
        expect(hasSessionHint()).toBe(false);
    });

    it("keeps the session on a network error and does not redirect", async () => {
        const token = makeToken();
        startSession(token);
        server = (config) => {
            if (urlOf(config) === "/user/refresh") throw networkError();
            return { status: 401, data: { error: "Invalid token" } };
        };

        await expect(api.get("/files")).rejects.toMatchObject({ response: { status: 401 } });
        expect(getToken()).toBe(token);
        expect(hasSessionHint()).toBe(true);
        expect(assign).not.toHaveBeenCalled();
    });

    it("keeps the session on a 429 and rejects the original error", async () => {
        const token = makeToken();
        startSession(token);
        server = (config) =>
            urlOf(config) === "/user/refresh"
                ? { status: 429, data: { error: "Too many attempts" } }
                : { status: 401, data: { error: "Invalid token" } };

        await expect(api.get("/files")).rejects.toMatchObject({ response: { status: 401 } });
        expect(getToken()).toBe(token);
        expect(hasSessionHint()).toBe(true);
        expect(assign).not.toHaveBeenCalled();
    });

    it("sends an expiring token as is when the refresh fails transiently", async () => {
        const token = makeToken({ expiresIn: 10 });
        startSession(token);
        server = (config) => {
            if (urlOf(config) === "/user/refresh") throw networkError();
            return { data: "ok" };
        };

        await api.get("/files");
        expect(requests.at(-1).auth).toBe(bearer(token));
    });
});

describe("ensureSession", () => {
    it("makes no request when a live token is in memory", async () => {
        setToken(makeToken());
        server = () => ({ data: null });

        await expect(ensureSession()).resolves.toBe("authenticated");
        expect(requests).toHaveLength(0);
    });

    it("makes no request and reports anonymous without a session hint", async () => {
        server = () => ({ data: null });

        await expect(ensureSession()).resolves.toBe("anonymous");
        expect(requests).toHaveLength(0);
    });

    it("refreshes once for several concurrent callers when there is a hint", async () => {
        localStorage.setItem(SESSION_HINT_KEY, "abc");
        const fresh = makeToken();
        server = () => ({ data: { success: fresh } });

        const results = await Promise.all([ensureSession(), ensureSession(), ensureSession()]);
        expect(results).toEqual(["authenticated", "authenticated", "authenticated"]);
        expect(requests).toHaveLength(1);
        expect(getToken()).toBe(fresh);
    });

    it("reports anonymous (and clears the hint) when the server rejects the cookie", async () => {
        localStorage.setItem(SESSION_HINT_KEY, "abc");
        server = () => ({ status: 401, data: { error: "Session expired" } });

        await expect(ensureSession()).resolves.toBe("anonymous");
        expect(hasSessionHint()).toBe(false);
    });

    it("rejects when the server can't be reached, keeping the hint", async () => {
        localStorage.setItem(SESSION_HINT_KEY, "abc");
        server = () => {
            throw new axios.AxiosError("Network Error", "ERR_NETWORK");
        };

        await expect(ensureSession()).rejects.toMatchObject({ code: "ERR_NETWORK" });
        expect(hasSessionHint()).toBe(true);
        expect(getToken()).toBeNull();
    });
});

describe("refresh lock", () => {
    afterEach(() => {
        delete navigator.locks;
    });

    it("runs the refresh under the fv-refresh Web Lock when available", async () => {
        const request = vi.fn((name, task) => task());
        Object.defineProperty(navigator, "locks", { value: { request }, configurable: true });
        server = () => ({ data: { success: makeToken() } });

        await refreshAccessToken();
        expect(request).toHaveBeenCalledWith("fv-refresh", expect.any(Function));
        expect(requests).toHaveLength(1);
    });

    it("still refreshes without Web Locks", async () => {
        expect(navigator.locks).toBeUndefined();
        server = () => ({ data: { success: makeToken() } });

        await refreshAccessToken();
        expect(requests).toHaveLength(1);
    });
});

describe("other tabs", () => {
    const storageEvent = (init) => window.dispatchEvent(new StorageEvent("storage", { key: SESSION_HINT_KEY, ...init }));

    it("signs this tab out when another tab removes the session hint", () => {
        vi.stubGlobal("location", { ...window.location, pathname: "/dashboard", assign });
        startSession(makeToken());

        storageEvent({ oldValue: "abc", newValue: null });
        expect(getToken()).toBeNull();
        expect(assign).toHaveBeenCalledWith("/login");
    });

    it("clears the token without redirecting on a public page", () => {
        vi.stubGlobal("location", { ...window.location, pathname: "/s/abc", assign });
        startSession(makeToken());

        storageEvent({ oldValue: "abc", newValue: null });
        expect(getToken()).toBeNull();
        expect(assign).not.toHaveBeenCalled();
    });

    it("reloads when another tab signs in as a different session", () => {
        const reload = vi.fn();
        vi.stubGlobal("location", { ...window.location, pathname: "/dashboard", assign, reload });

        storageEvent({ oldValue: "abc", newValue: "def" });
        expect(reload).toHaveBeenCalled();
    });

    it("ignores other storage keys", () => {
        startSession(makeToken());
        window.dispatchEvent(new StorageEvent("storage", { key: "fv-theme", oldValue: "a", newValue: null }));
        expect(getToken()).not.toBeNull();
    });

    it("logging out here removes the hint so other tabs follow", async () => {
        startSession(makeToken());
        server = () => ({ data: null });

        await logout();
        expect(hasSessionHint()).toBe(false);
        expect(getToken()).toBeNull();
    });
});

describe("getErrorMessage", () => {
    it("prefers the server's message, then the error's, then the fallback", () => {
        expect(getErrorMessage({ response: { data: { error: "Quota exceeded" } }, message: "x" })).toBe("Quota exceeded");
        expect(getErrorMessage(new Error("Network Error"))).toBe("Network Error");
        expect(getErrorMessage(null, "Could not load files")).toBe("Could not load files");
        expect(getErrorMessage(undefined)).toBe("Something went wrong");
    });
});
