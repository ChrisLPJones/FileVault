import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import Dashboard from "./Dashboard";
import { markOpenedAPI } from "../../api/favouritesAPI";
import { downloadFile } from "../../api/downloadFileAPI";

// Recent should only list files the user actually opened (double-click, Enter, Open) or
// downloaded, not every file that was selected and so shown in the details pane.

const files = [
    { _id: "d1", name: "Docs", isDirectory: true, path: "/Docs", size: 0, updatedAt: "2026-01-01T00:00:00Z" },
    { _id: "f1", name: "notes.txt", isDirectory: false, path: "/notes.txt", size: 12, updatedAt: "2026-01-02T00:00:00Z" },
    { _id: "f3", name: "inside.txt", isDirectory: false, path: "/Docs/inside.txt", size: 5, updatedAt: "2026-01-02T00:00:00Z" },
    { _id: "f2", name: "plan.pdf", isDirectory: false, path: "/plan.pdf", size: 3000, updatedAt: "2026-01-03T00:00:00Z" },
];

vi.mock("../../api/getAllFilesAPI", () => ({ getAllFilesAPI: vi.fn(async () => ({ data: files })) }));
vi.mock("../../api/accountAPI", () => ({
    getUsageAPI: vi.fn(async () => ({ used: 3012, quota: 1024 ** 3, maxUploadBytes: 100 * 1024 ** 2 })),
}));
vi.mock("../../api/favouritesAPI", () => ({
    markOpenedAPI: vi.fn(async () => ({})),
    setFavouriteAPI: vi.fn(async () => ({})),
}));
vi.mock("../../api/downloadFileAPI", () => ({
    downloadFile: vi.fn(async () => {}),
    fetchFileBlob: vi.fn(async () => new Blob(["hello there"], { type: "text/plain" })),
}));
vi.mock("../../hooks/useUserProfile", () => ({
    useUserProfile: () => ({ firstName: "Alex", lastName: "Morgan", name: "Alex Morgan", email: "alex@example.com", imageUrl: null, isAdmin: false }),
}));

const renderDashboard = async (entry = "/dashboard", firstFile = "notes.txt") => {
    render(
        <MemoryRouter initialEntries={[entry]}>
            <Dashboard />
        </MemoryRouter>
    );
    await screen.findByText(firstFile, { selector: ".file-name" });
};

const tile = (name) => screen.getByText(name, { selector: ".file-name" }).closest(".file-item-container");
const recent = () => within(screen.getByRole("region", { name: "Recent" }));

const previewToggle = () => screen.getByRole("button", { name: /preview pane/i });
const detailsPane = () => screen.queryByRole("complementary", { name: "Details" });

// Favourites and Recent start collapsed; most tests need them open
const openQuickAccess = () =>
    localStorage.setItem("fv-quick-access", JSON.stringify({ favourites: true, recent: true }));

beforeEach(() => {
    localStorage.clear();
    openQuickAccess();
});

describe("Recent files", () => {
    it("doesn't count selecting a file (which shows it in the details pane)", async () => {
        await renderDashboard();
        await userEvent.click(previewToggle());
        await userEvent.click(tile("notes.txt"));

        expect(await screen.findByRole("complementary", { name: "Details" })).toHaveTextContent("notes.txt");
        await new Promise((resolve) => setTimeout(resolve, 350)); // past the double-click window
        await userEvent.click(tile("plan.pdf"));

        expect(markOpenedAPI).not.toHaveBeenCalled();
        expect(recent().getByText("Files you open will appear here")).toBeInTheDocument();
    });

    it("counts a double-click, once", async () => {
        await renderDashboard();
        await userEvent.dblClick(tile("notes.txt"));

        expect(markOpenedAPI).toHaveBeenCalledTimes(1);
        expect(markOpenedAPI).toHaveBeenCalledWith("f1");
        expect(recent().getByRole("button", { name: "notes.txt" })).toBeInTheDocument();
    });

    it("counts pressing Enter on a file", async () => {
        await renderDashboard();
        tile("plan.pdf").focus();
        await userEvent.keyboard("{Enter}");

        expect(markOpenedAPI).toHaveBeenCalledTimes(1);
        expect(markOpenedAPI).toHaveBeenCalledWith("f2");
    });

    it("doesn't count opening a folder", async () => {
        await renderDashboard();
        await userEvent.dblClick(tile("Docs"));

        expect(markOpenedAPI).not.toHaveBeenCalled();
    });

    it("counts a download once per file", async () => {
        await renderDashboard();
        await userEvent.click(tile("plan.pdf"));
        await userEvent.click(screen.getByRole("button", { name: "Download" }));

        await waitFor(() => expect(downloadFile).toHaveBeenCalledWith([expect.objectContaining({ _id: "f2" })]));
        expect(markOpenedAPI).toHaveBeenCalledTimes(1);
        expect(markOpenedAPI).toHaveBeenCalledWith("f2");
    });

    it("doesn't count choosing a file from the Recent list", async () => {
        await renderDashboard();
        await userEvent.dblClick(tile("notes.txt"));
        expect(markOpenedAPI).toHaveBeenCalledTimes(1);

        await userEvent.click(recent().getByRole("button", { name: "notes.txt" }));
        expect(markOpenedAPI).toHaveBeenCalledTimes(1);
    });
});

describe("Preview pane toggle", () => {
    it("stays closed when a file is selected or opened until the toggle is used", async () => {
        await renderDashboard();
        expect(previewToggle()).toHaveAttribute("aria-pressed", "false");

        await userEvent.click(tile("notes.txt"));
        expect(detailsPane()).toBeNull();
        await userEvent.dblClick(tile("plan.pdf"));
        expect(detailsPane()).toBeNull();
        // Choosing a file from Recent or Favourites selects it without opening the pane
        await userEvent.click(recent().getByRole("button", { name: "plan.pdf" }));
        expect(detailsPane()).toBeNull();
    });

    it("opens with a prompt, shows whichever single file is selected and stays open", async () => {
        await renderDashboard();
        await userEvent.click(previewToggle());

        expect(previewToggle()).toHaveAttribute("aria-pressed", "true");
        expect(previewToggle()).toHaveAccessibleName("Preview pane");
        expect(previewToggle()).toHaveAttribute("title", "Hide preview pane");
        expect(detailsPane()).toHaveTextContent("Select a file to preview");

        await userEvent.click(tile("notes.txt"));
        expect(detailsPane()).toHaveTextContent("notes.txt");
        await new Promise((resolve) => setTimeout(resolve, 350));
        await userEvent.click(tile("plan.pdf"));
        expect(detailsPane()).toHaveTextContent("plan.pdf");

        // A folder isn't previewed: back to the prompt rather than the pane disappearing
        await new Promise((resolve) => setTimeout(resolve, 350));
        await userEvent.click(tile("Docs"));
        expect(detailsPane()).toHaveTextContent("Select a file to preview");
        expect(localStorage.getItem("fv-details-pane")).toBe("open");
    });

    it("turns off from the toggle and from the pane's close button", async () => {
        await renderDashboard();
        await userEvent.click(previewToggle());
        await userEvent.click(tile("notes.txt"));
        expect(detailsPane()).not.toBeNull();

        await userEvent.click(screen.getByRole("button", { name: "Close details" }));
        expect(detailsPane()).toBeNull();
        expect(previewToggle()).toHaveAttribute("aria-pressed", "false");
        expect(localStorage.getItem("fv-details-pane")).toBe("closed");

        await userEvent.click(previewToggle());
        expect(detailsPane()).not.toBeNull();
        await userEvent.click(previewToggle());
        expect(detailsPane()).toBeNull();
    });

    it("has no toggle in the recycle bin", async () => {
        await renderDashboard();
        await userEvent.click(screen.getByRole("button", { name: "Recycle bin" }));
        expect(screen.queryByRole("button", { name: /preview pane/i })).toBeNull();
    });
});

describe("Folders section", () => {
    const heading = () => screen.getByRole("button", { name: "Folders" });
    const pane = () => document.querySelector(".sb-folders-scroll");

    it("starts with Favourites and Recent collapsed for a first-time user", async () => {
        localStorage.clear();
        await renderDashboard();
        expect(heading()).toHaveAttribute("aria-expanded", "true");
        expect(screen.getByRole("button", { name: "Favourites" })).toHaveAttribute("aria-expanded", "false");
        expect(screen.getByRole("button", { name: "Recent" })).toHaveAttribute("aria-expanded", "false");
    });

    it("collapses like Favourites and Recent and remembers it", async () => {
        await renderDashboard();
        expect(heading()).toHaveAttribute("aria-expanded", "true");
        expect(pane().querySelector(".sb-folders-list-item")).not.toBeNull();

        await userEvent.click(heading());
        expect(heading()).toHaveAttribute("aria-expanded", "false");
        expect(JSON.parse(localStorage.getItem("fv-quick-access"))).toMatchObject({ folders: false });
        // The other sections keep their own state
        expect(screen.getByRole("button", { name: "Favourites" })).toHaveAttribute("aria-expanded", "true");

        await userEvent.click(screen.getByRole("button", { name: "Recent" }));
        expect(JSON.parse(localStorage.getItem("fv-quick-access"))).toEqual({ folders: false, favourites: true, recent: false });
    });

    it("starts collapsed when it was left that way", async () => {
        localStorage.setItem("fv-quick-access", JSON.stringify({ folders: true, favourites: false }));
        await renderDashboard();
        expect(heading()).toHaveAttribute("aria-expanded", "true");
        expect(screen.getByRole("button", { name: "Favourites" })).toHaveAttribute("aria-expanded", "false");
    });
});

describe("Folder in the address", () => {
    it("opens the folder named in the address, and Docs shows in the breadcrumb", async () => {
        await renderDashboard("/dashboard?folder=%2FDocs", "inside.txt");
        expect(document.querySelector(".breadcrumb")).toHaveTextContent("Docs");
        expect(screen.queryByText("notes.txt", { selector: ".file-name" })).toBeNull();
    });
});
