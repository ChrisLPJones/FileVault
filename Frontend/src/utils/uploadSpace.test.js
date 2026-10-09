import { describe, expect, it } from "vitest";
import { checkUploadSpace, reservedBytes, retrySpaceError, spaceLeft } from "./uploadSpace";

const MB = 1024 * 1024;
const GB = 1024 * MB;

describe("spaceLeft", () => {
    it("is the quota minus what is used and what is queued", () => {
        expect(spaceLeft({ used: 300 * MB, quota: GB })).toBe(GB - 300 * MB);
        expect(spaceLeft({ used: 300 * MB, quota: GB }, 100 * MB)).toBe(GB - 400 * MB);
    });

    it("never goes below zero", () => {
        expect(spaceLeft({ used: 2 * GB, quota: GB })).toBe(0);
    });

    it("is unknown without a usage or quota", () => {
        expect(spaceLeft(null)).toBeNull();
        expect(spaceLeft({ used: 5 })).toBeNull();
        expect(spaceLeft({ used: 5, quota: 0 })).toBeNull();
    });
});

describe("checkUploadSpace", () => {
    const usage = { used: 700 * MB, quota: GB }; // 324 MB left

    it("accepts files that fit, including one that fills the space exactly", () => {
        expect(checkUploadSpace([100 * MB, 224 * MB], usage)).toEqual([null, null]);
    });

    it("refuses a file that doesn't fit, naming both sizes and the recycle bin", () => {
        const [error] = checkUploadSpace([GB + GB / 5], usage);
        expect(error).toBe(
            "Not enough space: this file is 1.2 GB and you have 324 MB left. " +
                "Files in the recycle bin still count, so empty it to free space."
        );
    });

    it("checks in order: a refused file uses no space, a later smaller one still fits", () => {
        const errors = checkUploadSpace([500 * MB, 200 * MB, 100 * MB], usage);
        expect(errors[0]).toMatch(/^Not enough space/);
        expect(errors[1]).toBeNull();
        expect(errors[2]).toBeNull();
    });

    it("counts earlier accepted files against later ones", () => {
        const errors = checkUploadSpace([200 * MB, 200 * MB], usage);
        expect(errors[0]).toBeNull();
        expect(errors[1]).toMatch(/you have 124 MB left/);
    });

    it("takes queued uploads that aren't counted yet off the space", () => {
        expect(checkUploadSpace([100 * MB], usage, 300 * MB)[0]).toMatch(/you have 24 MB left/);
    });

    it("doesn't refuse anything when the usage hasn't loaded (the server still checks)", () => {
        expect(checkUploadSpace([10 * GB], null)).toEqual([null]);
    });
});

describe("reservedBytes", () => {
    it("holds back only files that are still queued, uploading or finished but not yet counted", () => {
        const f = (size, flags = {}) => ({ file: { size }, ...flags });
        expect(
            reservedBytes([
                f(10),
                f(20, { error: "too big" }),
                f(40, { removed: true }),
                f(80, { counted: true }),
                f(160, { cancelled: true }),
            ])
        ).toBe(10);
    });
});

describe("retrySpaceError", () => {
    const usage = { used: 700 * MB, quota: GB };
    const item = (size, extra) => ({ file: { size: size * MB }, ...extra });

    it("is null when the cancelled file still fits", () => {
        expect(retrySpaceError([item(200, { cancelled: true })], 0, usage)).toBeNull();
    });

    it("refuses a file that no longer fits after the other queued files", () => {
        const queued = [item(200, { cancelled: true }), item(150)];
        expect(retrySpaceError(queued, 0, usage)).toContain("you have 174 MB left");
    });

    it("ignores the file's own size when it is not marked cancelled", () => {
        expect(retrySpaceError([item(300)], 0, usage)).toBeNull();
    });
});
