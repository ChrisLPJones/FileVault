import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import CancelEmailChange from "./CancelEmailChange";
import { cancelEmailChangeByLinkAPI } from "../../api/accountEmailAPI";

vi.mock("../../api/accountEmailAPI", () => ({ cancelEmailChangeByLinkAPI: vi.fn() }));

const renderPage = (search = "?token=abc") =>
    render(
        <MemoryRouter initialEntries={[`/cancel-email-change${search}`]}>
            <CancelEmailChange />
        </MemoryRouter>
    );

describe("Cancel email change page", () => {
    it("sends nothing until the button is clicked", async () => {
        cancelEmailChangeByLinkAPI.mockResolvedValue({ success: "The email change was cancelled." });
        renderPage();

        expect(cancelEmailChangeByLinkAPI).not.toHaveBeenCalled();
        await userEvent.click(screen.getByRole("button", { name: /cancel the change/i }));

        expect(cancelEmailChangeByLinkAPI).toHaveBeenCalledExactlyOnceWith("abc");
        expect(await screen.findByRole("status")).toHaveTextContent("The email change was cancelled.");
        expect(screen.getByRole("link", { name: "Reset your password" })).toBeInTheDocument();
    });

    it("shows the server's error for an invalid or used link", async () => {
        cancelEmailChangeByLinkAPI.mockRejectedValue({ response: { data: { error: "This link is invalid or has expired." } } });
        renderPage();
        await userEvent.click(screen.getByRole("button", { name: /cancel the change/i }));

        expect(await screen.findByRole("alert")).toHaveTextContent("This link is invalid or has expired.");
    });

    it("explains an incomplete link", () => {
        renderPage("");
        expect(screen.getByRole("alert")).toHaveTextContent("incomplete");
        expect(screen.queryByRole("button")).toBeNull();
    });
});
