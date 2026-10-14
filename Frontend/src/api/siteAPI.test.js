import { describe, expect, it, vi } from "vitest";
import { api } from "./api";
import { getSiteInfoAPI } from "./siteAPI";

// The test setup replaces siteAPI for every other test
vi.unmock("./siteAPI");

describe("getSiteInfoAPI", () => {
    it("asks GET /site without the access token or cookies", async () => {
        const get = vi.spyOn(api, "get").mockResolvedValue({ data: { privacy: null } });

        await expect(getSiteInfoAPI()).resolves.toEqual({ privacy: null });
        expect(get).toHaveBeenCalledWith("/site", { skipAuth: true, withCredentials: false });
    });
});
