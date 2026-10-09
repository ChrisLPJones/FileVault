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
    it("has no explanatory note above the list", async () => {
        getSharesAPI.mockResolvedValue([]);
        const { container } = render(<SharedLinks />);

        await screen.findByRole("heading", { name: "Shared links" });
        expect(container.querySelector(".shared-links-card .settings-hint")).not.toBeInTheDocument();
    });
});
