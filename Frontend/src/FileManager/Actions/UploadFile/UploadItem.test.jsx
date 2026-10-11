import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import UploadItem from "./UploadItem";
import { getRequestToken } from "../../../api/api";
import { FilesProvider } from "../../../contexts/FilesContext";
import { TranslationProvider } from "../../../contexts/TranslationProvider";

vi.mock("../../../api/api", () => ({
    getErrorMessage: vi.fn(),
    getRequestToken: vi.fn(() => new Promise(() => {})),
}));

// Retrying a cancelled upload checks the space again before uploading
describe("UploadItem retry", () => {
    const renderItem = (checkSpace) => {
        const setFiles = vi.fn();
        render(
            <TranslationProvider language="en-US">
                <FilesProvider filesData={[]} onError={vi.fn()}>
                    <UploadItem
                        index={0}
                        fileData={{ file: new File(["x"], "a.txt"), appendData: {} }}
                        setFiles={setFiles}
                        setIsUploading={vi.fn()}
                        fileUploadConfig={{ url: "/upload" }}
                        onFileUploaded={vi.fn()}
                        handleFileRemove={vi.fn()}
                        checkSpace={checkSpace}
                    />
                </FilesProvider>
            </TranslationProvider>
        );
        return setFiles;
    };

    it("sets the error and doesn't upload when the file no longer fits", async () => {
        const checkSpace = vi.fn(() => "Not enough space: test");
        const setFiles = renderItem(checkSpace);
        await userEvent.click(screen.getByTitle(/abort/i)); // cancel
        await userEvent.click(screen.getByTitle("Retry"));

        expect(checkSpace).toHaveBeenCalled();
        const updates = setFiles.mock.calls.map(([fn]) => fn([{}]))[1];
        expect(updates[0]).toMatchObject({ error: "Not enough space: test" });
        expect(updates[0].cancelled).not.toBe(false);
        // still offers Retry rather than starting
        expect(screen.getByTitle("Retry")).toBeInTheDocument();
    });

    it("clears the cancelled flag and uploads when it fits", async () => {
        const setFiles = renderItem(() => null);
        await userEvent.click(screen.getByTitle(/abort/i));
        await userEvent.click(screen.getByTitle("Retry"));

        const updates = setFiles.mock.calls.map(([fn]) => fn([{}]));
        expect(updates.at(-1)[0]).toMatchObject({ error: false, cancelled: false });
    });

    it("stays quiet when the session ended: no request is opened and no error is shown", async () => {
        getRequestToken.mockRejectedValue(Object.assign(new Error("Session expired"), { sessionExpired: true }));
        const open = vi.fn();
        vi.stubGlobal("XMLHttpRequest", function () {
            this.upload = {};
            this.open = open;
            this.send = vi.fn();
        });
        const onError = vi.fn();
        const checkSpace = () => null;
        render(
            <TranslationProvider language="en-US">
                <FilesProvider filesData={[]} onError={onError}>
                    <UploadItem
                        index={0}
                        fileData={{ file: new File(["x"], "a.txt"), appendData: {} }}
                        setFiles={vi.fn()}
                        setIsUploading={vi.fn()}
                        fileUploadConfig={{ url: "/upload" }}
                        onFileUploaded={vi.fn()}
                        handleFileRemove={vi.fn()}
                        checkSpace={checkSpace}
                    />
                </FilesProvider>
            </TranslationProvider>
        );
        await waitFor(() => expect(getRequestToken).toHaveBeenCalled());
        await new Promise((resolve) => setTimeout(resolve, 20));

        expect(open).not.toHaveBeenCalled();
        expect(screen.queryByText(/fail|error|lost/i)).toBeNull();
        expect(onError).not.toHaveBeenCalled();
        vi.unstubAllGlobals();
    });
});
