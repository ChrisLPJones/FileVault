import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import TwoFactorCard from "./TwoFactorCard";
import { getTwoFactorStatusAPI } from "../../api/securityAPI";

vi.mock("../../api/securityAPI", () => ({
    getTwoFactorStatusAPI: vi.fn(),
    startTwoFactorSetupAPI: vi.fn(),
    enableTwoFactorAPI: vi.fn(),
    disableTwoFactorAPI: vi.fn(),
    regenerateRecoveryCodesAPI: vi.fn(),
}));

const usernameField = (container) => container.querySelector('input[autocomplete="username"]');

describe("Two-factor password forms", () => {
    it("include the account email as a hidden username field", async () => {
        getTwoFactorStatusAPI.mockResolvedValue({ enabled: false, recoveryCodesLeft: 0 });
        const { container } = render(<TwoFactorCard email="me@example.com" />);

        await userEvent.click(await screen.findByRole("button", { name: /set up|turn on|enable/i }));
        const field = usernameField(container);
        expect(field).toHaveValue("me@example.com");
        expect(field).toHaveAttribute("name", "username");
        expect(field).toHaveAttribute("readonly");
        expect(field.closest("form")).toContainElement(screen.getByLabelText("Password"));
    });
});
