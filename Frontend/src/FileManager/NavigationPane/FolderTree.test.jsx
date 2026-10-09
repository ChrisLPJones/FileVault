import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import FolderTree from "./FolderTree";
import { FilesProvider } from "../../contexts/FilesContext";
import { FileNavigationProvider, useFileNavigation } from "../../contexts/FileNavigationContext";
import { RecycleBinProvider, useRecycleBin } from "../../contexts/RecycleBinContext";
import { SearchProvider, useSearch } from "../../contexts/SearchContext";

const tree = {
    name: "A",
    path: "/A",
    subDirectories: [
        { name: "B", path: "/A/B", subDirectories: [{ name: "C", path: "/A/B/C", subDirectories: [] }] },
    ],
};

const Controls = () => {
    const { setCurrentPath } = useFileNavigation();
    const { openBin } = useRecycleBin();
    const { setSearchTerm } = useSearch();
    return (
        <>
            <button onClick={() => setCurrentPath("/A/B/C")}>deep</button>
            <button onClick={openBin}>bin</button>
            <button onClick={() => setSearchTerm("x")}>search</button>
        </>
    );
};

const renderTree = () =>
    render(
        <FilesProvider filesData={[]} onError={vi.fn()}>
            <FileNavigationProvider>
                <RecycleBinProvider>
                    <SearchProvider>
                        <FolderTree folder={tree} onFileOpen={() => {}} />
                        <Controls />
                    </SearchProvider>
                </RecycleBinProvider>
            </FileNavigationProvider>
        </FilesProvider>
    );

const row = (name) => document.querySelector(`.sb-folders-list-item[title="${name}"]`);

describe("FolderTree", () => {
    it("opens every ancestor of the current folder", async () => {
        const { container } = renderTree();
        await userEvent.click(screen.getByRole("button", { name: "deep" }));

        // A and B are both open (their arrows point down)
        expect(container.querySelectorAll(".folder-rotate-down")).toHaveLength(2);
        expect(row("C")).toHaveClass("active-list-item");
    });

    it("highlights no folder while the recycle bin or search results are showing", async () => {
        renderTree();
        await userEvent.click(screen.getByRole("button", { name: "deep" }));
        await userEvent.click(screen.getByRole("button", { name: "bin" }));
        expect(row("C")).not.toHaveClass("active-list-item");
    });

    it("highlights no folder while search results are showing", async () => {
        renderTree();
        await userEvent.click(screen.getByRole("button", { name: "deep" }));
        await userEvent.click(screen.getByRole("button", { name: "search" }));
        expect(row("C")).not.toHaveClass("active-list-item");
    });
});
