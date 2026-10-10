import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import Login from "./Login";
import { login } from "../../services/Auth";
import { getToken, hasSessionHint } from "../../utils/auth";
import { makeToken } from "../../test/tokens";

vi.mock("../../services/Auth", () => ({ login: vi.fn() }));
// The server status banner polls the API; not part of these tests
vi.mock("../../components/ServerStatus", () => ({ default: () => null }));
vi.mock("./TwoFactorStep", () => ({
    default: ({ onSuccess }) => <button onClick={() => onSuccess(twoFactorToken)}>Finish 2FA</button>,
}));

const loginToken = makeToken();
const twoFactorToken = makeToken();

const renderLogin = (state) =>
    render(
        <MemoryRouter initialEntries={[{ pathname: "/login", state }]}>
            <Routes>
                <Route path="/login" element={<Login />} />
                <Route path="/dashboard" element={<p>Dashboard page</p>} />
            </Routes>
        </MemoryRouter>
    );

const email = () => screen.getByLabelText("Email address");
const password = () => screen.getByLabelText("Password");
const submit = () => userEvent.click(screen.getByRole("button", { name: "Log in" }));

// Mocks are reset between tests by restoreMocks (vite.config.js). Calling mockReset or mockClear here
// makes Vitest 5.0 report the rejection in the network-error test as a failure even though it's caught.

describe("Login form", () => {
    it("requires an email and a password before contacting the server", async () => {
        renderLogin();
        await submit();

        expect(screen.getByText("Enter your email address")).toBeInTheDocument();
        expect(screen.getByText("Password is required")).toBeInTheDocument();
        expect(email()).toHaveAttribute("aria-invalid", "true");
        expect(email()).toHaveAccessibleDescription("Enter your email address");
        expect(login).not.toHaveBeenCalled();
    });

    it("treats a blank (spaces only) email as missing", async () => {
        renderLogin();
        await userEvent.type(email(), "   ");
        await userEvent.type(password(), "Secret123");
        await submit();

        expect(screen.getByText("Enter your email address")).toBeInTheDocument();
        expect(screen.queryByText("Password is required")).toBeNull();
        expect(login).not.toHaveBeenCalled();
    });

    it("clears a field's error as soon as it is edited", async () => {
        renderLogin();
        await submit();
        await userEvent.type(email(), "a");

        expect(screen.queryByText("Enter your email address")).toBeNull();
        expect(screen.getByText("Password is required")).toBeInTheDocument();
    });

    it("logs in with the trimmed email, stores the token and opens the files", async () => {
        login.mockResolvedValue({ status: 200, data: { success: loginToken } });
        renderLogin();
        await userEvent.type(email(), "  alex@example.com ");
        await userEvent.type(password(), "Passw0rd");
        await submit();

        expect(login).toHaveBeenCalledWith("alex@example.com", "Passw0rd");
        expect(await screen.findByText("Dashboard page")).toBeInTheDocument();
        expect(getToken()).toBe(loginToken);
        expect(hasSessionHint()).toBe(true);
        expect(localStorage.getItem("token")).toBeNull();
    });

    it("keeps the token from the two-factor step in memory and sets the session hint", async () => {
        login.mockResolvedValue({ status: 200, data: { twoFactorRequired: true, challengeToken: "challenge" } });
        renderLogin();
        await userEvent.type(email(), "alex@example.com");
        await userEvent.type(password(), "Passw0rd");
        await submit();
        expect(getToken()).toBeNull();

        await userEvent.click(await screen.findByRole("button", { name: "Finish 2FA" }));
        expect(await screen.findByText("Dashboard page")).toBeInTheDocument();
        expect(getToken()).toBe(twoFactorToken);
        expect(hasSessionHint()).toBe(true);
    });

    it("tells a suspended account's owner it is suspended, without offering to resend a confirmation", async () => {
        login.mockResolvedValue({ status: 403, data: { error: "This account has been suspended", suspended: true } });
        renderLogin();
        await userEvent.type(email(), "alex@example.com");
        await userEvent.type(password(), "Passw0rd");
        await submit();

        const alert = await screen.findByRole("alert");
        expect(alert).toHaveTextContent("This account has been suspended");
        expect(alert).toHaveTextContent("Contact an administrator");
        expect(screen.queryByRole("button", { name: "Resend confirmation email" })).toBeNull();
        expect(getToken()).toBeNull();
    });

    it("shows the server's error, e.g. a wrong password", async () => {
        login.mockResolvedValue({ status: 401, data: { error: "Invalid email or password" } });
        renderLogin();
        await userEvent.type(email(), "alex@example.com");
        await userEvent.type(password(), "wrong");
        await submit();

        expect(await screen.findByRole("alert")).toHaveTextContent("Invalid email or password");
        expect(screen.getByRole("button", { name: "Log in" })).toBeEnabled();
        expect(getToken()).toBeNull();
        expect(hasSessionHint()).toBe(false);
    });

    it("says when the server can't be reached", async () => {
        login.mockImplementation(async () => {
            throw new Error("Network Error");
        });
        renderLogin();
        await userEvent.type(email(), "alex@example.com");
        await userEvent.type(password(), "Passw0rd");
        await submit();

        expect(await screen.findByRole("alert")).toHaveTextContent("Can't reach the server");
    });

    it("confirms a new account until the first attempt", async () => {
        renderLogin({ registrationSuccess: true });
        expect(screen.getByRole("status")).toHaveTextContent("Account created");

        await submit();
        expect(screen.queryByRole("status")).toBeNull();
    });
});
