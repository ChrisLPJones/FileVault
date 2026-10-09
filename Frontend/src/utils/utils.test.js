import { describe, expect, it } from "vitest";
import { formatBytes } from "./formatBytes";
import { formatDate } from "./formatDate";
import { getDataSize } from "./getDataSize";
import { getFileExtension } from "./getFileExtension";
import { getParentPath } from "./getParentPath";
import sortFiles from "./sortFiles";
import { duplicateNameHandler } from "./duplicateNameHandler";
import { favouriteItems, recentFiles, RECENT_LIMIT } from "./quickAccess";
import { canHaveThumbnail } from "../api/thumbnailAPI";

describe("formatBytes", () => {
    it.each([
        [0, "0 B"],
        [1, "1 B"],
        [1023, "1023 B"],
        [1024, "1 KB"],
        [1536, "1.5 KB"],
        [10 * 1024 * 1024, "10 MB"],
        [1073741824, "1 GB"],
        [5 * 1024 ** 4, "5 TB"],
    ])("formats %d as %s", (bytes, expected) => {
        expect(formatBytes(bytes)).toBe(expected);
    });

    it("picks the unit after rounding, so it never shows 1024 of something", () => {
        expect(formatBytes(1024 * 1024 - 1)).toBe("1 MB");
        expect(formatBytes(1073741823)).toBe("1 GB");
    });

    it("rounds to the requested number of decimals", () => {
        expect(formatBytes(1234567, 2)).toBe("1.18 MB");
        expect(formatBytes(1234567, 0)).toBe("1 MB");
    });

    it("treats missing, negative and non-numeric input as zero", () => {
        expect(formatBytes(undefined)).toBe("0 B");
        expect(formatBytes(-5)).toBe("0 B");
        expect(formatBytes("abc")).toBe("0 B");
        expect(formatBytes("2048")).toBe("2 KB");
    });
});

describe("formatDate", () => {
    it("formats a date as M/D/YYYY h:mm AM/PM in local time", () => {
        expect(formatDate(new Date(2026, 0, 5, 9, 7).toISOString())).toBe("1/5/2026 9:07 AM");
        expect(formatDate(new Date(2026, 11, 31, 0, 30).toISOString())).toBe("12/31/2026 12:30 AM");
        expect(formatDate(new Date(2026, 6, 4, 12, 0).toISOString())).toBe("7/4/2026 12:00 PM");
        expect(formatDate(new Date(2026, 6, 4, 23, 59).toISOString())).toBe("7/4/2026 11:59 PM");
    });

    it("returns an empty string for missing or invalid dates", () => {
        expect(formatDate(null)).toBe("");
        expect(formatDate("")).toBe("");
        expect(formatDate("not a date")).toBe("");
    });
});

describe("getDataSize", () => {
    it("uses KB, MB or GB with two decimals", () => {
        expect(getDataSize(512)).toBe("0.50 KB");
        expect(getDataSize(2048)).toBe("2.00 KB");
        expect(getDataSize(5 * 1024 * 1024)).toBe("5.00 MB");
        expect(getDataSize(3 * 1024 ** 3)).toBe("3.00 GB");
        expect(getDataSize(1536, 1)).toBe("1.5 KB");
    });

    it("returns an empty string for non-numbers", () => {
        expect(getDataSize("abc")).toBe("");
    });
});

describe("path helpers", () => {
    it("getFileExtension returns the text after the last dot", () => {
        expect(getFileExtension("photo.jpg")).toBe("jpg");
        expect(getFileExtension("archive.tar.gz")).toBe("gz");
    });

    it("getParentPath drops the last segment", () => {
        expect(getParentPath("/a/b/c.txt")).toBe("/a/b");
        expect(getParentPath("/top")).toBe("");
        expect(getParentPath(undefined)).toBeUndefined();
    });
});

describe("sortFiles", () => {
    const items = [
        { name: "b.txt", isDirectory: false, size: 300, updatedAt: "2026-01-03T00:00:00Z" },
        { name: "Zeta", isDirectory: true, updatedAt: "2026-01-01T00:00:00Z" },
        { name: "a.txt", isDirectory: false, size: 100, updatedAt: "2026-01-05T00:00:00Z" },
        { name: "Alpha", isDirectory: true, updatedAt: "2026-01-02T00:00:00Z" },
        { name: "c.txt", isDirectory: false, updatedAt: null },
    ];
    const names = (list) => list.map((f) => f.name);

    it("puts folders first, each sorted by name", () => {
        expect(names(sortFiles(items))).toEqual(["Alpha", "Zeta", "a.txt", "b.txt", "c.txt"]);
        expect(names(sortFiles(items, "name", "desc"))).toEqual(["Zeta", "Alpha", "c.txt", "b.txt", "a.txt"]);
    });

    it("sorts by size (missing sizes count as 0) and by modified date", () => {
        expect(names(sortFiles(items, "size"))).toEqual(["Zeta", "Alpha", "c.txt", "a.txt", "b.txt"]);
        expect(names(sortFiles(items, "modified", "desc"))).toEqual(["Alpha", "Zeta", "a.txt", "b.txt", "c.txt"]);
    });

    it("doesn't change the list it was given", () => {
        const copy = [...items];
        sortFiles(items, "size");
        expect(items).toEqual(copy);
    });
});

describe("duplicateNameHandler", () => {
    it("keeps a free name", () => {
        expect(duplicateNameHandler("new.txt", false, [{ name: "old.txt" }])).toBe("new.txt");
    });

    it("numbers a taken file name before its extension, after the highest number used", () => {
        const files = [{ name: "report.pdf" }, { name: "report (1).pdf" }, { name: "report (4).pdf" }];
        expect(duplicateNameHandler("report.pdf", false, files)).toBe("report (5).pdf");
    });

    it("numbers a taken folder name", () => {
        const files = [{ name: "New Folder", isDirectory: true }];
        expect(duplicateNameHandler("New Folder", true, files)).toBe("New Folder (1)");
    });
});

describe("quick access lists", () => {
    const files = [
        { _id: "1", name: "zebra.png", isFavourite: true, lastOpenedAt: "2026-03-01T10:00:00Z" },
        { _id: "2", name: "Projects", isDirectory: true, isFavourite: true, lastOpenedAt: "2026-03-09T10:00:00Z" },
        { _id: "3", name: "apple.txt", isFavourite: true },
        { _id: "4", name: "notes.md", lastOpenedAt: "2026-03-05T10:00:00Z" },
        { _id: "5", name: "broken.txt", lastOpenedAt: "garbage" },
    ];

    it("lists favourites with folders first, then by name", () => {
        expect(favouriteItems(files).map((f) => f.name)).toEqual(["Projects", "apple.txt", "zebra.png"]);
        expect(favouriteItems(undefined)).toEqual([]);
    });

    it("lists opened files (not folders) newest first", () => {
        expect(recentFiles(files).map((f) => f.name)).toEqual(["notes.md", "zebra.png"]);
    });

    it("keeps only the most recent ten", () => {
        const many = Array.from({ length: 15 }, (_, i) => ({
            _id: String(i),
            name: `file${i}.txt`,
            lastOpenedAt: new Date(Date.UTC(2026, 0, i + 1)).toISOString(),
        }));
        const recent = recentFiles(many);
        expect(recent).toHaveLength(RECENT_LIMIT);
        expect(recent[0].name).toBe("file14.txt");
        expect(recent.at(-1).name).toBe("file5.txt");
    });
});

describe("canHaveThumbnail", () => {
    it("is true for saved image files the server can thumbnail", () => {
        for (const name of ["a.jpg", "b.JPEG", "c.png", "d.gif", "e.webp", "f.bmp"])
            expect(canHaveThumbnail({ _id: "x", name })).toBe(true);
    });

    it("is false for folders, other types, extensionless names and unsaved items", () => {
        expect(canHaveThumbnail({ _id: "x", name: "pics.png", isDirectory: true })).toBe(false);
        expect(canHaveThumbnail({ _id: "x", name: "vector.svg" })).toBe(false);
        expect(canHaveThumbnail({ _id: "x", name: "png" })).toBe(false);
        expect(canHaveThumbnail({ name: "new.png" })).toBe(false);
        expect(canHaveThumbnail(null)).toBe(false);
    });
});
