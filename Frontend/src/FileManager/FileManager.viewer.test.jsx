import { beforeEach, describe, expect, it, vi } from "vitest";
import { createEvent, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import FileManager from "./FileManager";
import { fetchFileBlob } from "../api/downloadFileAPI";

vi.mock("../api/downloadFileAPI", () => ({ fetchFileBlob: vi.fn() }));
// What the upload dialog queued is all these tests look at, not the uploading itself
vi.mock("../api/trashAPI", () => ({
    getTrashAPI: vi.fn(async () => []),
    deleteFromTrashAPI: vi.fn(),
    emptyTrashAPI: vi.fn(),
    restoreTrashAPI: vi.fn(),
}));
vi.mock("./Actions/UploadFile/UploadItem", () => ({
    default: ({ fileData }) => <li data-testid="queued">{fileData.file.name}</li>,
}));

const MB = 1024 * 1024;
const file = (id, name, path, size = 100) => ({
    _id: id, name, path, isDirectory: false, size, updatedAt: "2026-01-02T00:00:00Z",
});
const files = [
    { _id: "d1", name: "Docs", path: "/Docs", isDirectory: true, size: 0, updatedAt: "2026-01-01T00:00:00Z" },
    file("f1", "a-notes.txt", "/a-notes.txt"),
    file("f2", "b-photo.png", "/b-photo.png"),
    file("f3", "c-icon.svg", "/c-icon.svg"),
    file("f4", "d-archive.bin", "/d-archive.bin", 2048),
    file("f5", "inside.txt", "/Docs/inside.txt"),
    file("big", "e-huge.pdf", "/e-huge.pdf", 40 * MB),
];

const blobs = {
    f1: () => new Blob(["Hello <b>world</b>"], { type: "text/plain" }),
    f2: () => new Blob(["png"], { type: "image/png" }),
    f3: () => new Blob(["<svg onload='alert(1)'/>"], { type: "image/svg+xml" }),
};

const renderManager = (props = {}, entry = "/") => {
    const onDownload = vi.fn();
    const onFileOpen = vi.fn();
    const utils = render(
        <MemoryRouter initialEntries={[entry]}>
            <FileManager files={files} onDownload={onDownload} onFileOpen={onFileOpen} onFileUploading={() => ({})} {...props} />
        </MemoryRouter>
    );
    return { onDownload, onFileOpen, ...utils };
};

const tile = (name) => screen.getByText(name, { selector: ".file-name" }).closest(".file-item-container");
const viewer = () => screen.queryByRole("dialog", { name: /^Viewing/ });

beforeEach(() => {
    // jsdom has no modal <dialog>
    HTMLDialogElement.prototype.showModal ??= function showModal() { this.setAttribute("open", ""); };
    HTMLDialogElement.prototype.close ??= function close() { this.removeAttribute("open"); };
    URL.createObjectURL = vi.fn(() => "blob:test");
    URL.revokeObjectURL = vi.fn();
    fetchFileBlob.mockImplementation(async (id) => (blobs[id] ? blobs[id]() : new Blob(["x"])));
});

describe("File viewer", () => {
    it("opens on double-click, shows text as plain text, and marks the file opened", async () => {
        const { onFileOpen } = renderManager();
        await userEvent.dblClick(tile("a-notes.txt"));

        const dialog = await screen.findByRole("dialog", { name: "Viewing a-notes.txt" });
        expect(dialog).toHaveAttribute("aria-modal", "true");
        expect(onFileOpen).toHaveBeenCalledWith(expect.objectContaining({ _id: "f1" }));
        // The markup in the file is text, not elements
        expect(await within(dialog).findByText("Hello <b>world</b>")).toBeInTheDocument();
        expect(dialog.querySelector("pre b")).toBeNull();
    });

    it("opens on Enter and closes on Escape, giving focus back to the item", async () => {
        renderManager();
        tile("a-notes.txt").focus();
        await userEvent.keyboard("{Enter}");
        await screen.findByRole("dialog", { name: "Viewing a-notes.txt" });

        await userEvent.keyboard("{Escape}");
        expect(viewer()).toBeNull();
        expect(tile("a-notes.txt")).toHaveFocus();
    });

    it("opens from the context menu's Open and closes with the X button", async () => {
        renderManager();
        fireEvent.contextMenu(tile("a-notes.txt"));
        await userEvent.click(await screen.findByText("Open"));
        await screen.findByRole("dialog", { name: "Viewing a-notes.txt" });

        await userEvent.click(screen.getByRole("button", { name: "Close" }));
        expect(viewer()).toBeNull();
    });

    it("still opens a folder instead of the viewer", async () => {
        renderManager();
        await userEvent.dblClick(tile("Docs"));
        expect(viewer()).toBeNull();
        expect(await screen.findByText("inside.txt", { selector: ".file-name" })).toBeInTheDocument();
    });

    it("moves to the next and previous file, in the list's order, with buttons and arrow keys", async () => {
        renderManager();
        await userEvent.dblClick(tile("a-notes.txt"));
        await screen.findByRole("dialog", { name: "Viewing a-notes.txt" });
        expect(screen.getByRole("button", { name: "Previous file" })).toBeDisabled();

        await userEvent.click(screen.getByRole("button", { name: "Next file" }));
        await screen.findByRole("dialog", { name: "Viewing b-photo.png" });
        await userEvent.keyboard("{ArrowRight}");
        await screen.findByRole("dialog", { name: "Viewing c-icon.svg" });
        await userEvent.keyboard("{ArrowLeft}");
        await screen.findByRole("dialog", { name: "Viewing b-photo.png" });
    });

    it("shows an image through <img>, and an SVG too, never inline", async () => {
        renderManager();
        await userEvent.dblClick(tile("c-icon.svg"));
        const dialog = await screen.findByRole("dialog", { name: "Viewing c-icon.svg" });

        const img = await within(dialog).findByRole("img", { name: "c-icon.svg" });
        expect(img.tagName).toBe("IMG");
        expect(img).toHaveAttribute("src", "blob:test");
        expect(dialog.querySelector("svg[onload], iframe, object, embed")).toBeNull();
    });

    it("offers a download for a type it can't show", async () => {
        const { onDownload } = renderManager();
        await userEvent.dblClick(tile("d-archive.bin"));
        const dialog = await screen.findByRole("dialog", { name: "Viewing d-archive.bin" });

        expect(within(dialog).getByText("No preview for this file type")).toBeInTheDocument();
        expect(fetchFileBlob).not.toHaveBeenCalled();
        await userEvent.click(within(dialog).getByRole("button", { name: "Download file" }));
        expect(onDownload).toHaveBeenCalledWith([expect.objectContaining({ _id: "f4" })]);
    });

    it("asks before loading a large file", async () => {
        renderManager();
        await userEvent.dblClick(tile("e-huge.pdf"));
        const dialog = await screen.findByRole("dialog", { name: "Viewing e-huge.pdf" });

        expect(fetchFileBlob).not.toHaveBeenCalled();
        await userEvent.click(within(dialog).getByRole("button", { name: "Show preview (40 MB)" }));
        await waitFor(() => expect(fetchFileBlob).toHaveBeenCalledWith("big", expect.any(AbortSignal)));
    });

    it("shows an error when the file can't be loaded", async () => {
        vi.spyOn(console, "error").mockImplementation(() => {});
        fetchFileBlob.mockRejectedValue(new Error("404"));
        renderManager();
        await userEvent.dblClick(tile("a-notes.txt"));

        expect(await screen.findByRole("alert")).toHaveTextContent("Couldn't load this file");
    });

    it("cancels the request and frees the image when moving on", async () => {
        let signal;
        fetchFileBlob.mockImplementation((id, s) => {
            signal = s;
            return new Promise(() => {});
        });
        renderManager();
        await userEvent.dblClick(tile("a-notes.txt"));
        await screen.findByRole("dialog", { name: "Viewing a-notes.txt" });
        const first = signal;

        await userEvent.keyboard("{ArrowRight}");
        await screen.findByRole("dialog", { name: "Viewing b-photo.png" });
        expect(first.aborted).toBe(true);
    });

    it("gives a PDF the PDF type whatever type the server sent", async () => {
        fetchFileBlob.mockImplementation(async () => new Blob(["<script>alert(1)</script>"], { type: "text/html" }));
        renderManager({ files: [file("p1", "x.pdf", "/x.pdf")] });
        await userEvent.dblClick(tile("x.pdf"));
        await waitFor(() => expect(URL.createObjectURL).toHaveBeenCalled());

        const blob = URL.createObjectURL.mock.calls[0][0];
        expect(blob.type).toBe("application/pdf");
    });

    it("revokes the object URL on close", async () => {
        renderManager();
        await userEvent.dblClick(tile("b-photo.png"));
        const dialog = await screen.findByRole("dialog", { name: "Viewing b-photo.png" });
        await within(dialog).findByRole("img");

        await userEvent.keyboard("{Escape}");
        expect(URL.revokeObjectURL).toHaveBeenCalledWith("blob:test");
    });

    it("keeps Tab inside the viewer", async () => {
        renderManager();
        await userEvent.dblClick(tile("d-archive.bin"));
        const dialog = await screen.findByRole("dialog", { name: "Viewing d-archive.bin" });

        for (let i = 0; i < 8; i++) {
            await userEvent.tab();
            expect(dialog).toContainElement(document.activeElement);
        }
    });
});

// A drag event carrying data, as the browser does (fireEvent can't build a real DataTransfer)
const drag = (type, target, { types = ["Files"], files: dropped = [] } = {}) => {
    const event = createEvent[type](target);
    Object.defineProperty(event, "dataTransfer", {
        value: { types, files: dropped, items: [], dropEffect: "none" },
    });
    fireEvent(target, event);
    return event;
};

describe("Dropping files from the computer", () => {
    const root = () => document.querySelector(".file-explorer");

    it("shows an overlay naming the open folder, and hides it when the drag leaves", () => {
        renderManager();
        drag("dragEnter", root());
        expect(screen.getByText("Drop to upload to Home")).toBeInTheDocument();

        // Crossing into a child fires enter before leave: still dragging
        drag("dragEnter", tile("a-notes.txt"));
        drag("dragLeave", root());
        expect(screen.getByText("Drop to upload to Home")).toBeInTheDocument();

        drag("dragLeave", tile("a-notes.txt"));
        expect(screen.queryByText(/Drop to upload/)).toBeNull();
    });

    it("names the folder that is open", async () => {
        renderManager();
        await userEvent.dblClick(tile("Docs"));
        await screen.findByText("inside.txt", { selector: ".file-name" });
        drag("dragEnter", root());
        expect(screen.getByText("Drop to upload to Docs")).toBeInTheDocument();
    });

    it("ignores rows dragged inside the app", () => {
        renderManager();
        drag("dragEnter", root(), { types: ["text/plain"] });
        expect(screen.queryByText(/Drop to upload/)).toBeNull();
    });

    it("queues dropped files in the upload dialog", async () => {
        renderManager();
        const dropped = new File(["hi"], "dropped.txt");
        drag("dragEnter", root(), { files: [dropped] });
        const event = drag("drop", root(), { files: [dropped] });

        expect(event.defaultPrevented).toBe(true);
        expect(screen.queryByText(/Drop to upload/)).toBeNull();
        expect(await screen.findByTestId("queued")).toHaveTextContent("dropped.txt");
    });

    it("hands the open folder to the upload", async () => {
        const onFileUploading = vi.fn(() => ({}));
        renderManager({ onFileUploading });
        await userEvent.dblClick(tile("Docs"));
        await screen.findByText("inside.txt", { selector: ".file-name" });

        drag("drop", root(), { files: [new File(["hi"], "dropped.txt")] });
        await screen.findByTestId("queued");
        expect(onFileUploading).toHaveBeenCalledWith(expect.objectContaining({ name: "dropped.txt" }), expect.objectContaining({ _id: "d1" }));
    });

    it("stops the browser opening files dropped where nothing takes them", async () => {
        renderManager();
        await userEvent.dblClick(tile("a-notes.txt"));
        const dialog = await screen.findByRole("dialog", { name: "Viewing a-notes.txt" });

        expect(drag("dragOver", dialog).defaultPrevented).toBe(true);
        expect(drag("drop", dialog).defaultPrevented).toBe(true);
        // Rows dragged inside the app are left alone
        expect(drag("dragOver", dialog, { types: ["text/plain"] }).defaultPrevented).toBe(false);
    });

    it("does nothing when uploads aren't allowed", () => {
        renderManager({ permissions: { upload: false } });
        drag("dragEnter", root());
        expect(screen.queryByText(/Drop to upload/)).toBeNull();
    });

    it.each([
        ["the recycle bin", "/?bin=1", /stay here until you empty the bin/],
        ["search results", "/?q=notes", /result/],
    ])("does nothing in %s", async (_name, entry, shown) => {
        renderManager({}, entry);
        await screen.findByText(shown);
        drag("dragEnter", root());
        expect(screen.queryByText(/Drop to upload/)).toBeNull();
    });
});
