import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import axios from "axios";
import { api, getErrorMessage, refreshAccessToken } from "./api";
import { getToken, setToken } from "../utils/auth";
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

describe("getErrorMessage", () => {
    it("prefers the server's message, then the error's, then the fallback", () => {
        expect(getErrorMessage({ response: { data: { error: "Quota exceeded" } }, message: "x" })).toBe("Quota exceeded");
        expect(getErrorMessage(new Error("Network Error"))).toBe("Network Error");
        expect(getErrorMessage(null, "Could not load files")).toBe("Could not load files");
        expect(getErrorMessage(undefined)).toBe("Something went wrong");
    });
});
