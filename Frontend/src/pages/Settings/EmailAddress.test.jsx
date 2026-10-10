import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import EmailAddress from "./EmailAddress";
import { cancelEmailChangeAPI, requestEmailChangeAPI, resendEmailChangeAPI } from "../../api/accountEmailAPI";

vi.mock("../../api/accountEmailAPI", () => ({
    requestEmailChangeAPI: vi.fn(),
    resendEmailChangeAPI: vi.fn(),
    cancelEmailChangeAPI: vi.fn(),
}));

const expiresAt = "2030-01-01T10:00:00Z";

describe("Email address block", () => {
    it("sends the new address with the current password and then shows it as pending", async () => {
        requestEmailChangeAPI.mockResolvedValue({ success: "We've sent a confirmation link to new@example.com", pendingEmail: "new@example.com", expiresAt });
        const { container } = render(<EmailAddress email="me@example.com" pending={null} />);

        expect(screen.getByText("me@example.com")).toBeInTheDocument();
        await userEvent.click(screen.getByRole("button", { name: "Change" }));
        const username = container.querySelector('input[autocomplete="username"]');
        expect(username).toHaveValue("me@example.com");
        await userEvent.type(screen.getByLabelText("New email address"), " new@example.com ");
        await userEvent.type(screen.getByLabelText("Current password"), "Secret123");
        await userEvent.click(screen.getByRole("button", { name: "Send confirmation link" }));

        expect(requestEmailChangeAPI).toHaveBeenCalledExactlyOnceWith("new@example.com", "Secret123");
        expect(await screen.findByText(/Waiting for you to confirm/)).toHaveTextContent("new@example.com");
        // The current address is still the one shown and used to log in
        expect(screen.getAllByText("me@example.com").length).toBeGreaterThan(0);
        expect(screen.getByRole("status")).toHaveTextContent("We've sent a confirmation link");
    });

    it("shows the server's reason when the change is refused", async () => {
        requestEmailChangeAPI.mockRejectedValue({ response: { data: { error: "Current password is incorrect" } } });
        render(<EmailAddress email="me@example.com" pending={null} />);
        await userEvent.click(screen.getByRole("button", { name: "Change" }));
        await userEvent.type(screen.getByLabelText("New email address"), "new@example.com");
        await userEvent.type(screen.getByLabelText("Current password"), "wrong");
        await userEvent.click(screen.getByRole("button", { name: "Send confirmation link" }));

        expect(await screen.findByRole("alert")).toHaveTextContent("Current password is incorrect");
        expect(screen.queryByText(/Waiting for you to confirm/)).toBeNull();
    });

    it("resends and cancels a pending change", async () => {
        resendEmailChangeAPI.mockResolvedValue({ success: "We've sent a new confirmation link to new@example.com", pendingEmail: "new@example.com", expiresAt });
        cancelEmailChangeAPI.mockResolvedValue({ success: "Email change cancelled" });
        render(<EmailAddress email="me@example.com" pending={{ email: "new@example.com", expiresAt }} />);

        expect(screen.getByText(/Waiting for you to confirm/)).toHaveTextContent("new@example.com");
        await userEvent.click(screen.getByRole("button", { name: "Resend link" }));
        expect(resendEmailChangeAPI).toHaveBeenCalledOnce();
        expect(await screen.findByRole("status")).toHaveTextContent("new confirmation link");

        await userEvent.click(screen.getByRole("button", { name: "Cancel change" }));
        expect(cancelEmailChangeAPI).toHaveBeenCalledOnce();
        expect(await screen.findByText("Email change cancelled")).toBeInTheDocument();
        expect(screen.queryByText(/Waiting for you to confirm/)).toBeNull();
    });
});
