import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import ConfirmEmailChange from "./ConfirmEmailChange";
import { confirmEmailChangeAPI } from "../../api/accountEmailAPI";
import { useSession } from "../../components/useSession";
import { clearToken, setToken } from "../../utils/auth";
import { makeToken } from "../../test/tokens";

vi.mock("../../api/accountEmailAPI", () => ({ confirmEmailChangeAPI: vi.fn() }));
vi.mock("../../components/useSession", () => ({ useSession: vi.fn() }));

// Shows where the "Log in" link goes and the state it carries
const LoginStub = () => <p>Login page from {useLocation().state?.from}</p>;

const renderPage = () =>
    render(
        <MemoryRouter initialEntries={["/confirm-email?token=abc123"]}>
            <Routes>
                <Route path="/confirm-email" element={<ConfirmEmailChange />} />
                <Route path="/login" element={<LoginStub />} />
            </Routes>
        </MemoryRouter>
    );

beforeEach(() => clearToken());

describe("Confirm new email page", () => {
    it("asks a signed-out visitor to log in, and comes back to this link afterwards", async () => {
        useSession.mockReturnValue({ status: "anonymous", retry: vi.fn() });
        renderPage();

        expect(screen.queryByRole("button", { name: /confirm new email/i })).toBeNull();
        await userEvent.click(screen.getByRole("link", { name: "Log in" }));
        expect(await screen.findByText("Login page from /confirm-email?token=abc123")).toBeInTheDocument();
        expect(confirmEmailChangeAPI).not.toHaveBeenCalled();
    });

    it("waits for the session check before showing anything", () => {
        useSession.mockReturnValue({ status: "pending", retry: vi.fn() });
        renderPage();

        expect(screen.getByRole("status")).toHaveTextContent("Checking your session");
        expect(screen.queryByRole("link", { name: "Log in" })).toBeNull();
    });

    it("confirms once, on a click, when signed in", async () => {
        setToken(makeToken());
        useSession.mockReturnValue({ status: "authenticated", retry: vi.fn() });
        confirmEmailChangeAPI.mockResolvedValue({ email: "new@example.com", token: "t" });
        renderPage();

        expect(confirmEmailChangeAPI).not.toHaveBeenCalled();
        await userEvent.click(screen.getByRole("button", { name: /confirm new email/i }));

        expect(confirmEmailChangeAPI).toHaveBeenCalledExactlyOnceWith("abc123");
        expect(await screen.findByRole("status")).toHaveTextContent("Your email address is now new@example.com");
    });

    it("shows why a link was refused", async () => {
        setToken(makeToken());
        useSession.mockReturnValue({ status: "authenticated", retry: vi.fn() });
        confirmEmailChangeAPI.mockRejectedValue({ response: { data: { error: "That address now belongs to another account" } } });
        renderPage();
        await userEvent.click(screen.getByRole("button", { name: /confirm new email/i }));

        expect(await screen.findByRole("alert")).toHaveTextContent("That address now belongs to another account");
    });
});
