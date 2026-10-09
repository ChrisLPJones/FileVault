import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import UploadFileAction from "./UploadFile.action";
import { FilesProvider } from "../../../contexts/FilesContext";
import { FileNavigationProvider } from "../../../contexts/FileNavigationContext";
import { TranslationProvider } from "../../../contexts/TranslationProvider";

const MB = 1024 * 1024;
const GB = 1024 * MB;

// 300 MB of the 1 GB quota is free
let usage = { used: 724 * MB, quota: GB, maxUploadBytes: 100 * MB, maxFileBytes: 2 * GB };
vi.mock("../../../contexts/UsageContext", () => ({
    useUsage: () => ({ usage, refreshUsage: vi.fn(async () => usage) }),
}));

// Only what the dialog decided for each file matters here, not the upload itself
vi.mock("./UploadItem", () => ({
    default: ({ fileData }) => (
        <li data-testid="item">
            {fileData.file.name}: {fileData.error || "uploading"}
        </li>
    ),
}));

const fileOfSize = (name, size) => {
    const file = new File(["x"], name);
    Object.defineProperty(file, "size", { value: size });
    return file;
};

const renderDialog = () =>
    render(
        <TranslationProvider language="en-US">
            <FilesProvider filesData={[]} onError={vi.fn()}>
                <FileNavigationProvider>
                    <UploadFileAction
                        fileUploadConfig={{ url: "/upload" }}
                        maxFileSize={2 * GB}
                        onFileUploading={() => ({})}
                        onFileUploaded={() => {}}
                        onClose={() => {}}
                    />
                </FileNavigationProvider>
            </FilesProvider>
        </TranslationProvider>
    );

const item = (name) => screen.getByText(new RegExp(`^${name}:`), { selector: "[data-testid=item]" });

describe("Upload dialog space check", () => {
    it("refuses a file larger than the space left and uploads the ones that fit", async () => {
        renderDialog();
        await userEvent.upload(document.getElementById("chooseFile"), [
            fileOfSize("big.iso", 1.2 * GB),
            fileOfSize("small.txt", 10 * MB),
        ]);

        expect(item("big.iso")).toHaveTextContent(
            "Not enough space: this file is 1.2 GB and you have 300 MB left. " +
                "Files in the recycle bin still count, so empty it to free space."
        );
        expect(item("small.txt")).toHaveTextContent("small.txt: uploading");
    });

    it("counts files chosen together against each other, in order", async () => {
        renderDialog();
        await userEvent.upload(document.getElementById("chooseFile"), [
            fileOfSize("a.bin", 200 * MB),
            fileOfSize("b.bin", 200 * MB),
            fileOfSize("c.bin", 100 * MB),
        ]);

        expect(item("a.bin")).toHaveTextContent("uploading");
        expect(item("b.bin")).toHaveTextContent("you have 100 MB left");
        expect(item("c.bin")).toHaveTextContent("uploading");
    });

    it("applies to folder uploads too", async () => {
        renderDialog();
        const inner = fileOfSize("movie.mkv", 400 * MB);
        Object.defineProperty(inner, "webkitRelativePath", { value: "Holiday/movie.mkv" });
        await userEvent.upload(document.getElementById("chooseFolder"), [inner]);

        expect(item("movie.mkv")).toHaveTextContent("Not enough space");
    });
});
