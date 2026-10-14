import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import Privacy from "./Privacy";
import { getSiteInfoAPI } from "../../api/siteAPI";
import { resetSiteInfo } from "../../hooks/useSiteInfo";

vi.mock("../../api/siteAPI", () => ({ getSiteInfoAPI: vi.fn() }));

const renderPage = () => render(<MemoryRouter><Privacy /></MemoryRouter>);

const hosted = (overrides = {}) => ({
    privacy: { inactiveDays: 30, warningDays: 7, trashRetentionDays: 30, loginLocation: true, contactEmail: "owner@example.com", ...overrides },
});

beforeEach(() => resetSiteInfo());

describe("Privacy page", () => {
    it("shows the inactivity period, warning period and a mailto link to the contact address", async () => {
        getSiteInfoAPI.mockResolvedValue(hosted({ inactiveDays: 45, warningDays: 10 }));
        renderPage();

        expect(await screen.findByRole("heading", { name: "Privacy" })).toBeInTheDocument();
        expect(screen.getByText(/not signed in to or used for 45 days is removed/)).toHaveTextContent("10 days before that we email a warning");
        expect(screen.getByRole("link", { name: "owner@example.com" })).toHaveAttribute("href", "mailto:owner@example.com");
    });

    it("states the recycle bin period and that encryption does not hide files from the operators", async () => {
        getSiteInfoAPI.mockResolvedValue(hosted({ trashRetentionDays: 14 }));
        renderPage();

        expect(await screen.findByText(/removed for good after 14 days/)).toBeInTheDocument();
        expect(screen.getByText(/does not stop the people who run this site/)).toBeInTheDocument();
        expect(screen.queryByText(/cannot open your files/)).not.toBeInTheDocument();
        expect(screen.getByText("When you were last active on the site.")).toBeInTheDocument();
    });

    it("uses the singular for one day", async () => {
        getSiteInfoAPI.mockResolvedValue(hosted({ inactiveDays: 2, warningDays: 1 }));
        renderPage();

        expect(await screen.findByText(/used for 2 days is removed/)).toHaveTextContent("1 day before that");
    });

    it("handles a missing contact address", async () => {
        getSiteInfoAPI.mockResolvedValue(hosted({ contactEmail: undefined }));
        renderPage();

        expect(await screen.findByText("This site has no contact address set.")).toBeInTheDocument();
        expect(screen.queryByRole("link", { name: /@/ })).not.toBeInTheDocument();
    });

    it("describes the sign-in location when it is switched on", async () => {
        getSiteInfoAPI.mockResolvedValue(hosted());
        renderPage();

        expect(await screen.findByRole("heading", { name: "Sign-in location" })).toBeInTheDocument();
    });

    it("leaves the sign-in location out when it is switched off", async () => {
        getSiteInfoAPI.mockResolvedValue(hosted({ loginLocation: false }));
        renderPage();

        expect(await screen.findByRole("heading", { name: "Privacy" })).toBeInTheDocument();
        expect(screen.queryByRole("heading", { name: "Sign-in location" })).not.toBeInTheDocument();
        expect(screen.queryByText(/approximate place/)).not.toBeInTheDocument();
    });

    it("shows a loading state first", () => {
        getSiteInfoAPI.mockReturnValue(new Promise(() => {}));
        renderPage();

        expect(screen.getByText("Loading…")).toBeInTheDocument();
    });

    it("shows an error when the site info can't be loaded", async () => {
        getSiteInfoAPI.mockRejectedValue(new Error("offline"));
        renderPage();

        expect(await screen.findByRole("alert")).toHaveTextContent("Could not load this page");
    });

    it("shows the not-found page on a self-hosted install", async () => {
        getSiteInfoAPI.mockResolvedValue({ privacy: null });
        renderPage();

        expect(await screen.findByText("404")).toBeInTheDocument();
        expect(screen.queryByRole("heading", { name: "Privacy" })).not.toBeInTheDocument();
    });
});
