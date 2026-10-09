import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import SharedLinks from "./SharedLinks";
import { getSharesAPI } from "../../api/shareAPI";

vi.mock("../../api/shareAPI", () => ({
    getSharesAPI: vi.fn(),
    getSharePasswordAPI: vi.fn(),
    revokeShareAPI: vi.fn(),
    shareUrl: (token) => `http://localhost/s/${token}`,
}));

describe("Shared links page", () => {
    it("explains who can open a link and that a password is also needed", async () => {
        getSharesAPI.mockResolvedValue([]);
        render(<SharedLinks />);

        expect(
            screen.getByText(
                "Anyone with one of these links can download the item. Links with a password also need the password. Revoke a link to stop it working."
            )
        ).toBeInTheDocument();
        await screen.findByRole("heading", { name: "Shared links" });
    });
});
