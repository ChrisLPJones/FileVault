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

const renderDashboard = async () => {
    render(
        <MemoryRouter>
            <Dashboard />
        </MemoryRouter>
    );
    await screen.findByText("notes.txt", { selector: ".file-name" });
};

const tile = (name) => screen.getByText(name, { selector: ".file-name" }).closest(".file-item-container");
const recent = () => within(screen.getByRole("region", { name: "Recent" }));

beforeEach(() => {
    localStorage.setItem("fv-details-pane", "open");
});

describe("Recent files", () => {
    it("doesn't count selecting a file (which shows it in the details pane)", async () => {
        await renderDashboard();
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
