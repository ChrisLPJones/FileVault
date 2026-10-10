import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import HostedNotice from "./HostedNotice";
import { dismissHostedNoticeAPI } from "../api/accountAPI";
import { useUserProfile } from "../hooks/useUserProfile";

vi.mock("../api/accountAPI", () => ({ dismissHostedNoticeAPI: vi.fn(async () => ({})) }));
vi.mock("../hooks/useUserProfile", () => ({ useUserProfile: vi.fn() }));

beforeEach(() => {
    dismissHostedNoticeAPI.mockReset();
    dismissHostedNoticeAPI.mockResolvedValue({});
});

describe("HostedNotice", () => {
    it("shows the notice with a mailto link to the contact address", () => {
        useUserProfile.mockReturnValue({ hostedNotice: true, hostedContactEmail: "owner@example.com" });
        render(<HostedNotice />);

        expect(screen.getByRole("status")).toHaveTextContent(
            "FileVault is a free service with limited hosting, so accounts that aren't used for 30 days are removed. " +
            "To ask for a permanent account, email owner@example.com."
        );
        expect(screen.getByRole("link", { name: "owner@example.com" })).toHaveAttribute("href", "mailto:owner@example.com");
    });

    it("uses the configured number of inactive days", () => {
        useUserProfile.mockReturnValue({ hostedNotice: true, hostedContactEmail: null, hostedInactiveDays: 90 });
        render(<HostedNotice />);

        expect(screen.getByRole("status")).toHaveTextContent("accounts that aren't used for 90 days are removed.");
    });

    it("leaves the permanent-account sentence out without a contact address", () => {
        useUserProfile.mockReturnValue({ hostedNotice: true, hostedContactEmail: null });
        render(<HostedNotice />);

        expect(screen.getByRole("status")).toHaveTextContent("accounts that aren't used for 30 days are removed.");
        expect(screen.queryByText(/permanent account/)).not.toBeInTheDocument();
        expect(screen.queryByRole("link")).not.toBeInTheDocument();
    });

    it("shows nothing when the server says not to", () => {
        useUserProfile.mockReturnValue({ hostedNotice: false, hostedContactEmail: "owner@example.com" });
        const { container } = render(<HostedNotice />);

        expect(container).toBeEmptyDOMElement();
    });

    it("hides itself and tells the server when dismissed", async () => {
        useUserProfile.mockReturnValue({ hostedNotice: true, hostedContactEmail: null });
        render(<HostedNotice />);

        await userEvent.click(screen.getByRole("button", { name: "Dismiss notice" }));

        expect(dismissHostedNoticeAPI).toHaveBeenCalledTimes(1);
        expect(screen.queryByRole("status")).not.toBeInTheDocument();
    });

    it("stays hidden even if remembering the dismissal fails", async () => {
        dismissHostedNoticeAPI.mockRejectedValue(new Error("offline"));
        useUserProfile.mockReturnValue({ hostedNotice: true, hostedContactEmail: null });
        render(<HostedNotice />);

        await userEvent.click(screen.getByRole("button", { name: "Dismiss notice" }));

        expect(screen.queryByRole("status")).not.toBeInTheDocument();
    });
});
