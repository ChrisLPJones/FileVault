import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import AbuseContact from "./AbuseContact";
import { getSiteInfoAPI } from "../api/siteAPI";
import { resetSiteInfo } from "../hooks/useSiteInfo";

vi.mock("../api/siteAPI", () => ({ getSiteInfoAPI: vi.fn() }));

beforeEach(() => resetSiteInfo());

describe("AbuseContact", () => {
    it("shows a mailto link to the abuse contact", async () => {
        getSiteInfoAPI.mockResolvedValue({ abuseContactEmail: "abuse@example.com" });
        render(<AbuseContact />);

        const link = await screen.findByRole("link", { name: "abuse@example.com" });
        expect(link).toHaveAttribute("href", "mailto:abuse@example.com");
        expect(link.closest("p")).toHaveTextContent("Report abuse: abuse@example.com");
    });

    it("shows nothing when no contact is set", async () => {
        getSiteInfoAPI.mockResolvedValue({ abuseContactEmail: null });
        const { container } = render(<AbuseContact />);

        await vi.waitFor(() => expect(getSiteInfoAPI).toHaveBeenCalled());
        expect(container).toBeEmptyDOMElement();
    });

    it("shows nothing when the request fails", async () => {
        getSiteInfoAPI.mockRejectedValue(new Error("Network Error"));
        const { container } = render(<AbuseContact />);

        await vi.waitFor(() => expect(getSiteInfoAPI).toHaveBeenCalled());
        await Promise.resolve();
        expect(container).toBeEmptyDOMElement();
    });

    it("asks the server once per page load, however many places show it", async () => {
        getSiteInfoAPI.mockResolvedValue({ abuseContactEmail: "abuse@example.com" });
        render(<><AbuseContact /><AbuseContact /></>);

        expect(await screen.findAllByRole("link", { name: "abuse@example.com" })).toHaveLength(2);
        expect(getSiteInfoAPI).toHaveBeenCalledTimes(1);
    });
});
