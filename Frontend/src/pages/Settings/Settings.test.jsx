import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import Settings from "./Settings";
import { changePasswordAPI, getUsageAPI, getUserInfoAPI } from "../../api/accountAPI";

vi.mock("../../api/accountAPI", () => ({
    changePasswordAPI: vi.fn(),
    deleteAccountAPI: vi.fn(),
    deleteAvatarAPI: vi.fn(),
    getUsageAPI: vi.fn(),
    getUserInfoAPI: vi.fn(),
    updateProfileAPI: vi.fn(),
    uploadAvatarAPI: vi.fn(),
}));
vi.mock("../../api/accountEmailAPI", () => ({
    requestEmailChangeAPI: vi.fn(),
    resendEmailChangeAPI: vi.fn(),
    cancelEmailChangeAPI: vi.fn(),
}));
vi.mock("../../components/Avatar", () => ({ default: () => null }));
vi.mock("./TwoFactorCard", () => ({ default: () => null }));
vi.mock("./SessionsCard", () => ({ default: () => null }));

describe("Settings", () => {
    it("drops the pending email note after the password is changed (the server cancels the change)", async () => {
        getUserInfoAPI.mockResolvedValue({
            firstName: "A", lastName: "B", email: "me@example.com", emailVerified: true,
            pendingEmail: "new@example.com", pendingEmailExpiresAt: "2030-01-01T10:00:00Z",
        });
        getUsageAPI.mockResolvedValue({ used: 0, quota: 1000, maxUploadBytes: 1000, maxFileBytes: 1000 });
        changePasswordAPI.mockResolvedValue({});
        render(<MemoryRouter><Settings /></MemoryRouter>);

        expect(await screen.findByText(/Waiting for you to confirm/)).toHaveTextContent("new@example.com");

        await userEvent.type(screen.getByLabelText("Current password", { selector: "#current-password" }), "OldPassw0rd");
        await userEvent.type(screen.getByLabelText("New password"), "N3wPassword!");
        await userEvent.type(screen.getByLabelText("Confirm new password"), "N3wPassword!");
        await userEvent.click(screen.getByRole("button", { name: /change password/i }));

        expect(changePasswordAPI).toHaveBeenCalledOnce();
        await vi.waitFor(() => expect(screen.queryByText(/Waiting for you to confirm/)).toBeNull());
    });
});
