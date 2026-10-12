import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import FileViewer from "./FileViewer";
import { fetchFileBlob } from "../../api/downloadFileAPI";

vi.mock("../../api/downloadFileAPI", () => ({ fetchFileBlob: vi.fn() }));

let viewing = null;
vi.mock("../../contexts/FileViewerContext", () => ({
    useFileViewer: () => ({ viewing, openViewer: vi.fn(), closeViewer: vi.fn() }),
}));
vi.mock("../../contexts/FileNavigationContext", () => ({
    useFileNavigation: () => ({ currentPathFiles: viewing ? [viewing] : [] }),
}));

const clip = (name, size = 1000) => ({ _id: "v1", name, size, isDirectory: false });

beforeEach(() => {
    vi.clearAllMocks();
    URL.createObjectURL = vi.fn(() => "blob:test");
    URL.revokeObjectURL = vi.fn();
    fetchFileBlob.mockResolvedValue(new Blob(["x"], { type: "application/octet-stream" }));
});

describe("FileViewer media", () => {
    it("plays a video from an object URL typed by its extension", async () => {
        viewing = clip("holiday.mov");
        const { container } = render(<FileViewer onDownload={vi.fn()} />);

        await waitFor(() => expect(container.ownerDocument.querySelector("video")).toBeTruthy());
        const video = document.querySelector("video");
        expect(video).toHaveAttribute("src", "blob:test");
        expect(URL.createObjectURL.mock.calls[0][0].type).toBe("video/quicktime");
    });

    it("says so, with a download, when the browser can't play the video", async () => {
        viewing = clip("holiday.mov");
        const onDownload = vi.fn();
        render(<FileViewer onDownload={onDownload} />);

        await waitFor(() => expect(document.querySelector("video")).toBeTruthy());
        fireEvent.error(document.querySelector("video"));

        expect(await screen.findByRole("alert")).toHaveTextContent("This video format can't be played in your browser.");
        expect(document.querySelector("video")).toBeNull();
        await userEvent.click(screen.getByRole("button", { name: "Download file" }));
        expect(onDownload).toHaveBeenCalledWith([expect.objectContaining({ _id: "v1" })]);
    });

    it("says so for audio too", async () => {
        viewing = clip("song.flac");
        render(<FileViewer onDownload={vi.fn()} />);

        await waitFor(() => expect(document.querySelector("audio")).toBeTruthy());
        fireEvent.error(document.querySelector("audio"));

        expect(await screen.findByRole("alert")).toHaveTextContent("This audio format can't be played in your browser.");
    });

    it("warns when a video has no picture the browser can show, and keeps the player", async () => {
        viewing = clip("iphone.mov");
        render(<FileViewer onDownload={vi.fn()} />);

        await waitFor(() => expect(document.querySelector("video")).toBeTruthy());
        const video = document.querySelector("video");
        // jsdom has no decoder: videoWidth is 0, as for a codec the browser can't show
        fireEvent.loadedMetadata(video);

        expect(await screen.findByText(/No picture\?/)).toBeInTheDocument();
        expect(screen.getByRole("button", { name: "Download it" })).toBeInTheDocument();
        expect(document.querySelector("video")).toBeTruthy();
    });

    it("keeps Esc working after Show preview is pressed on a large video", async () => {
        viewing = clip("big.mp4", 50 * 1024 * 1024);
        render(<FileViewer onDownload={vi.fn()} />);

        const dialog = screen.getByRole("dialog");
        await userEvent.click(screen.getByRole("button", { name: /Show preview/ }));

        expect(dialog).toHaveFocus();
        await waitFor(() => expect(fetchFileBlob).toHaveBeenCalled());
    });
});
