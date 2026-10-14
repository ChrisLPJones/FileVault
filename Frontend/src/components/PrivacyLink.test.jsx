import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import PrivacyLink from "./PrivacyLink";
import { getSiteInfoAPI } from "../api/siteAPI";
import { resetSiteInfo } from "../hooks/useSiteInfo";

vi.mock("../api/siteAPI", () => ({ getSiteInfoAPI: vi.fn() }));

const renderLink = () => render(<MemoryRouter><PrivacyLink /></MemoryRouter>);

beforeEach(() => resetSiteInfo());

describe("PrivacyLink", () => {
    it("links to the privacy page on a hosted site", async () => {
        getSiteInfoAPI.mockResolvedValue({ privacy: { inactiveDays: 30, warningDays: 7, trashRetentionDays: 30, loginLocation: false } });
        renderLink();

        expect(await screen.findByRole("link", { name: "Privacy" })).toHaveAttribute("href", "/privacy");
    });

    it("shows nothing on a self-hosted install", async () => {
        getSiteInfoAPI.mockResolvedValue({ privacy: null });
        const { container } = renderLink();

        await waitFor(() => expect(getSiteInfoAPI).toHaveBeenCalled());
        expect(screen.queryByRole("link")).not.toBeInTheDocument();
        expect(container).toBeEmptyDOMElement();
    });

    it("shows nothing when the site info can't be loaded", async () => {
        getSiteInfoAPI.mockRejectedValue(new Error("offline"));
        const { container } = renderLink();

        await waitFor(() => expect(getSiteInfoAPI).toHaveBeenCalled());
        expect(container).toBeEmptyDOMElement();
    });
});
