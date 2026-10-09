import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import DetailsPane from "./DetailsPane";
import { DetailsPaneProvider } from "../../contexts/DetailsPaneContext";
import { FilesProvider } from "../../contexts/FilesContext";
import { FileActionsProvider } from "../../contexts/FileActionsContext";
import { TranslationProvider } from "../../contexts/TranslationProvider";
import { fetchFileBlob } from "../../api/downloadFileAPI";
import { formatDate } from "../../utils/formatDate";

vi.mock("../../api/downloadFileAPI", () => ({ fetchFileBlob: vi.fn() }));

const file = {
    _id: "f1",
    name: "Budget 2026.txt",
    path: "/Documents/Budget 2026.txt",
    isDirectory: false,
    size: 1536,
    createdAt: "2026-02-03T10:15:00Z",
    updatedAt: "2026-02-04T16:45:00Z",
    isFavourite: false,
};

const renderPane = (item = file, actions = {}) => {
    const fileActions = { setFavourite: vi.fn(), ...actions };
    render(
        <TranslationProvider language="en-US">
            <FilesProvider filesData={[item]}>
                <FileActionsProvider value={fileActions}>
                    <DetailsPaneProvider>
                        <DetailsPane file={item} formatDate={formatDate} />
                    </DetailsPaneProvider>
                </FileActionsProvider>
            </FilesProvider>
        </TranslationProvider>
    );
    return fileActions;
};

const row = (label) => screen.getByText(label, { selector: "dt" }).nextElementSibling;

beforeEach(() => {
    fetchFileBlob.mockResolvedValue(new Blob(["Rent: 1200\nFood: 300"], { type: "text/plain" }));
});

describe("DetailsPane", () => {
    it("shows the file's name, type, size, dates and folder", async () => {
        renderPane();

        const pane = screen.getByRole("complementary", { name: "Details" });
        expect(within(pane).getByText("Budget 2026.txt", { selector: ".details-name" })).toBeInTheDocument();
        expect(within(pane).getByText("TXT file")).toBeInTheDocument();
        expect(row("Size")).toHaveTextContent("1.5 KB");
        expect(row("Created")).toHaveTextContent(formatDate(file.createdAt));
        expect(row("Modified")).toHaveTextContent(formatDate(file.updatedAt));
        expect(row("Location")).toHaveTextContent("/Documents");

        // Text files get a preview of their contents
        expect(await within(pane).findByText(/Rent: 1200/)).toBeInTheDocument();
        expect(fetchFileBlob).toHaveBeenCalledWith("f1");
    });

    it("labels files without an extension and leaves out unknown dates", () => {
        renderPane({ ...file, name: "README", path: "/README", createdAt: null });

        expect(screen.getByText("File", { selector: ".details-type" })).toBeInTheDocument();
        expect(screen.queryByText("Created", { selector: "dt" })).toBeNull();
        expect(row("Location")).toHaveTextContent("/");
        expect(screen.getByText("No preview available")).toBeInTheDocument();
    });

    it("asks before downloading a large file for its preview", async () => {
        renderPane({ ...file, name: "huge.txt", size: 50 * 1024 * 1024 });

        const button = screen.getByRole("button", { name: "Show preview (50 MB)" });
        expect(fetchFileBlob).not.toHaveBeenCalled();
        await userEvent.click(button);
        await waitFor(() => expect(fetchFileBlob).toHaveBeenCalledWith("f1"));
    });

    it("stars the file", async () => {
        const actions = renderPane();
        await userEvent.click(screen.getByRole("button", { name: "Add to favourites" }));
        expect(actions.setFavourite).toHaveBeenCalledWith([expect.objectContaining({ _id: "f1" })], true);
    });

    it("closes and remembers that it was closed", async () => {
        renderPane();
        await userEvent.click(screen.getByRole("button", { name: "Close details" }));
        expect(localStorage.getItem("fv-details-pane")).toBe("closed");
    });
});
