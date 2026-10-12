import { describe, expect, it } from "vitest";
import { previewKind, previewMime } from "./previewKind";

describe("previewMime", () => {
    it.each([
        ["a.mp4", "video/mp4"],
        ["a.m4v", "video/mp4"],
        ["a.webm", "video/webm"],
        ["a.mov", "video/quicktime"],
        ["a.mp3", "audio/mpeg"],
        ["a.m4a", "audio/mp4"],
        ["a.wav", "audio/wav"],
        ["a.ogg", "audio/ogg"],
        ["a.aac", "audio/aac"],
        ["a.flac", "audio/flac"],
        ["PHOTO.JPG", "image/jpeg"],
        ["a.exe", "application/octet-stream"],
    ])("%s is %s", (name, type) => {
        expect(previewMime(name)).toBe(type);
    });

    it("gives every video and audio extension a video/ or audio/ type", () => {
        for (const [name, kind] of [["a.mp4", "video"], ["a.mov", "video"], ["a.m4v", "video"], ["a.webm", "video"],
            ["a.mp3", "audio"], ["a.wav", "audio"], ["a.m4a", "audio"], ["a.ogg", "audio"], ["a.aac", "audio"], ["a.flac", "audio"]]) {
            expect(previewKind(name)).toBe(kind);
            expect(previewMime(name)).toMatch(new RegExp(`^${kind}/`));
        }
    });
});
