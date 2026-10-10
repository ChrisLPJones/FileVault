import { beforeEach, describe, expect, it } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import axios from "axios";
import ProtectedRoute from "./ProtectedRoute";
import PublicRoute from "./PublicRoute";
import { getToken, hasSessionHint, SESSION_HINT_KEY, setToken } from "../utils/auth";
import { makeToken } from "../test/tokens";

// A fake network for the refresh call (bare axios): `server` returns { status, data } or throws
let server;
const requests = [];
let originalAdapter;

beforeEach(() => {
    requests.length = 0;
    originalAdapter = axios.defaults.adapter;
    axios.defaults.adapter = async (config) => {
        requests.push(config.url);
        const { status = 200, data = null } = await server(config);
        const response = { status, statusText: String(status), data, headers: {}, config, request: {} };
        if (status >= 400) {
            throw new axios.AxiosError(`Request failed with status code ${status}`, "ERR_BAD_REQUEST", config, {}, response);
        }
        return response;
    };
    return () => {
        axios.defaults.adapter = originalAdapter;
    };
});

const renderProtected = () =>
    render(
        <MemoryRouter initialEntries={["/dashboard"]}>
            <Routes>
                <Route path="/dashboard" element={<ProtectedRoute><p>Secret files</p></ProtectedRoute>} />
                <Route path="/login" element={<p>Login page</p>} />
            </Routes>
        </MemoryRouter>
    );

const renderPublic = () =>
    render(
        <MemoryRouter initialEntries={["/login"]}>
            <Routes>
                <Route path="/login" element={<PublicRoute><p>Login form</p></PublicRoute>} />
                <Route path="/dashboard" element={<p>Dashboard page</p>} />
            </Routes>
        </MemoryRouter>
    );

describe("ProtectedRoute", () => {
    it("shows a loader, then the page once the refresh succeeds", async () => {
        localStorage.setItem(SESSION_HINT_KEY, "abc");
        let release;
        server = () => new Promise((resolve) => (release = () => resolve({ data: { success: makeToken() } })));
        renderProtected();

        expect(screen.getByRole("status", { name: "Loading" })).toBeInTheDocument();
        expect(screen.queryByText("Secret files")).toBeNull();

        await waitFor(() => expect(release).toBeTypeOf("function"));
        release();
        expect(await screen.findByText("Secret files")).toBeInTheDocument();
    });

    it("renders at once when this tab already holds a live token", () => {
        setToken(makeToken());
        server = () => ({ data: null });
        renderProtected();

        expect(screen.getByText("Secret files")).toBeInTheDocument();
        expect(requests).toHaveLength(0);
    });

    it("goes to login when the server rejects the session", async () => {
        localStorage.setItem(SESSION_HINT_KEY, "abc");
        server = () => ({ status: 401, data: { error: "Session expired" } });
        renderProtected();

        expect(await screen.findByText("Login page")).toBeInTheDocument();
        expect(hasSessionHint()).toBe(false);
    });

    it("goes to login without a request when there is no session hint", async () => {
        server = () => ({ data: null });
        renderProtected();

        expect(await screen.findByText("Login page")).toBeInTheDocument();
        expect(requests).toHaveLength(0);
    });

    it("does not show the page for a token that the server never issued (old localStorage token)", async () => {
        localStorage.setItem("token", makeToken());
        server = () => ({ status: 401, data: { error: "Session expired" } });
        // A hand-edited token is not read from storage at all, so nothing unlocks the page
        expect(getToken()).toBeNull();
        renderProtected();

        expect(screen.queryByText("Secret files")).toBeNull();
        expect(await screen.findByText("Login page")).toBeInTheDocument();
    });

    it("says the server can't be reached on a network error, and Retry tries again", async () => {
        localStorage.setItem(SESSION_HINT_KEY, "abc");
        let fail = true;
        server = () => {
            if (fail) throw new axios.AxiosError("Network Error", "ERR_NETWORK");
            return { data: { success: makeToken() } };
        };
        renderProtected();

        expect(await screen.findByRole("alert")).toHaveTextContent("Can't reach the server");
        expect(screen.queryByText("Login page")).toBeNull();
        expect(hasSessionHint()).toBe(true);

        fail = false;
        await userEvent.click(screen.getByRole("button", { name: "Retry" }));
        expect(await screen.findByText("Secret files")).toBeInTheDocument();
    });
});

describe("PublicRoute", () => {
    it("shows the form at once, with no request, for a visitor without a session hint", () => {
        server = () => ({ data: null });
        renderPublic();

        expect(screen.getByText("Login form")).toBeInTheDocument();
        expect(requests).toHaveLength(0);
    });

    it("sends a live session to the dashboard", async () => {
        localStorage.setItem(SESSION_HINT_KEY, "abc");
        server = () => ({ data: { success: makeToken() } });
        renderPublic();

        expect(await screen.findByText("Dashboard page")).toBeInTheDocument();
    });

    it("shows the form when the hint turns out to be stale", async () => {
        localStorage.setItem(SESSION_HINT_KEY, "abc");
        server = () => ({ status: 401, data: { error: "Session expired" } });
        renderPublic();

        expect(await screen.findByText("Login form")).toBeInTheDocument();
    });

    it("shows the form when the server can't be reached", async () => {
        localStorage.setItem(SESSION_HINT_KEY, "abc");
        server = () => {
            throw new axios.AxiosError("Network Error", "ERR_NETWORK");
        };
        renderPublic();

        expect(await screen.findByText("Login form")).toBeInTheDocument();
    });
});
