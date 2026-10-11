import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import SharePage from "./SharePage";
import { getPublicShareAPI } from "../../api/shareAPI";
import { useSiteInfo } from "../../hooks/useSiteInfo";

vi.mock("../../api/shareAPI", () => ({
    getPublicShareAPI: vi.fn(),
    unlockShareAPI: vi.fn(),
    shareDownloadUrl: (token) => `http://api.test/s/${token}/download`,
}));
vi.mock("../../hooks/useSiteInfo", () => ({ useSiteInfo: vi.fn(() => ({ abuseContactEmail: null })) }));

const renderPage = () =>
    render(
        <MemoryRouter initialEntries={["/s/abc"]}>
            <Routes><Route path="/s/:token" element={<SharePage />} /></Routes>
        </MemoryRouter>
    );

describe("SharePage footer", () => {
    it("shows the abuse contact on a share page", async () => {
        getPublicShareAPI.mockResolvedValue({ name: "notes.txt", isDirectory: false, size: 12, passwordRequired: false });
        useSiteInfo.mockReturnValue({ abuseContactEmail: "abuse@example.com" });
        renderPage();

        expect(await screen.findByRole("link", { name: "abuse@example.com" })).toHaveAttribute("href", "mailto:abuse@example.com");
    });

    it("also shows it when the link has expired", async () => {
        getPublicShareAPI.mockRejectedValue(Object.assign(new Error("x"), { response: { status: 404 } }));
        useSiteInfo.mockReturnValue({ abuseContactEmail: "abuse@example.com" });
        renderPage();

        expect(await screen.findByText(/doesn't exist or has expired/)).toBeInTheDocument();
        expect(screen.getByRole("link", { name: "abuse@example.com" })).toBeInTheDocument();
    });

    it("shows no abuse line without a contact", async () => {
        useSiteInfo.mockReturnValue({ abuseContactEmail: null });
        getPublicShareAPI.mockResolvedValue({ name: "notes.txt", isDirectory: false, size: 12, passwordRequired: false });
        renderPage();

        await screen.findByText("notes.txt");
        expect(screen.queryByText(/Report abuse/)).not.toBeInTheDocument();
    });
});
