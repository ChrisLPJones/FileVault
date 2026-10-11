import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import Register from "./Register";
import { register } from "../../services/Auth";
import { useSiteInfo } from "../../hooks/useSiteInfo";

vi.mock("../../services/Auth", () => ({ register: vi.fn() }));
vi.mock("../../components/ServerStatus", () => ({ default: () => null }));
vi.mock("../../hooks/useSiteInfo", () => ({ useSiteInfo: vi.fn(() => ({ abuseContactEmail: null })) }));

const renderRegister = () =>
    render(
        <MemoryRouter initialEntries={["/register"]}>
            <Routes>
                <Route path="/register" element={<Register />} />
                <Route path="/login" element={<p>Login page</p>} />
            </Routes>
        </MemoryRouter>
    );

const fillAndSubmit = async () => {
    await userEvent.type(screen.getByLabelText("First name"), "Sam");
    await userEvent.type(screen.getByLabelText("Email address"), "sam@example.com");
    await userEvent.type(screen.getByLabelText("Password"), "Str0ngPassword!");
    await userEvent.type(screen.getByLabelText("Confirm password"), "Str0ngPassword!");
    await userEvent.click(screen.getByRole("button", { name: "Create account" }));
};

describe("Register page", () => {
    it("shows the server's message when sign-ups are limited (429)", async () => {
        register.mockResolvedValue({ status: 429, data: { error: "New sign-ups are paused for a while. Please try again later." } });
        renderRegister();
        await fillAndSubmit();

        expect(await screen.findByRole("alert")).toHaveTextContent("New sign-ups are paused for a while. Please try again later.");
        expect(screen.getByRole("button", { name: "Create account" })).toBeEnabled();
    });

    it("shows the abuse contact when the server has one", () => {
        useSiteInfo.mockReturnValue({ abuseContactEmail: "abuse@example.com" });
        renderRegister();

        expect(screen.getByRole("link", { name: "abuse@example.com" })).toHaveAttribute("href", "mailto:abuse@example.com");
    });

    it("shows no abuse line without one", () => {
        useSiteInfo.mockReturnValue({ abuseContactEmail: null });
        renderRegister();

        expect(screen.queryByText(/Report abuse/)).not.toBeInTheDocument();
    });
});
