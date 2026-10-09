import { describe, expect, it } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, useLocation, useNavigate } from "react-router-dom";
import LocationSync from "./LocationSync";
import { FilesProvider } from "./FilesContext";
import { FileNavigationProvider, useFileNavigation } from "./FileNavigationContext";
import { SelectionProvider, useSelection } from "./SelectionContext";
import { RecycleBinProvider, useRecycleBin } from "./RecycleBinContext";
import { SearchProvider, useSearch } from "./SearchContext";

// The open folder, recycle bin and search results live in the address (?folder=...&bin=1&q=...),
// so the browser's back and forward buttons move between them.

const files = [
    { _id: "d1", name: "Docs", isDirectory: true, path: "/Docs" },
    { _id: "d2", name: "Taxes", isDirectory: true, path: "/Docs/Taxes" },
    { _id: "d3", name: "Photos", isDirectory: true, path: "/Photos" },
    { _id: "d4", name: "A & B #1 �+", isDirectory: true, path: "/A & B #1 �+" },
    { _id: "f1", name: "a.txt", isDirectory: false, path: "/Docs/a.txt" },
];

// Stands in for the file manager's folder pane, toolbar and search box
const Probe = () => {
    const { currentPath, setCurrentPath } = useFileNavigation();
    const { isBinOpen, openBin, closeBin } = useRecycleBin();
    const { term, setSearchTerm } = useSearch();
    const { selectedFiles, setSelectedFiles } = useSelection();
    const location = useLocation();
    const navigate = useNavigate();

    return (
        <div>
            <output data-testid="view">{JSON.stringify({ currentPath, isBinOpen, term, selected: selectedFiles.length })}</output>
            <output data-testid="url">{location.pathname + location.search}</output>
            <button onClick={() => navigate(-1)}>back</button>
            <button onClick={() => navigate(1)}>forward</button>
            <button onClick={() => setCurrentPath("/Docs")}>docs</button>
            <button onClick={() => setCurrentPath("/Docs/Taxes")}>taxes</button>
            <button onClick={() => setCurrentPath("/Photos")}>photos</button>
            <button onClick={() => setCurrentPath("/A & B #1 �+")}>odd</button>
            <button onClick={openBin}>open bin</button>
            <button onClick={closeBin}>close bin</button>
            <button onClick={() => setSearchTerm("rep")}>search rep</button>
            <button onClick={() => setSearchTerm("report")}>search report</button>
            <button onClick={() => setSearchTerm("")}>clear search</button>
            <button onClick={() => setSelectedFiles([files[4]])}>select</button>
        </div>
    );
};

const tree = (entries, isLoading) => (
    <MemoryRouter initialEntries={entries} initialIndex={entries.length - 1}>
        <FilesProvider filesData={files}>
            <FileNavigationProvider>
                <SelectionProvider>
                    <RecycleBinProvider>
                        <SearchProvider>
                            <LocationSync isLoading={isLoading} source={files} />
                            <Probe />
                        </SearchProvider>
                    </RecycleBinProvider>
                </SelectionProvider>
            </FileNavigationProvider>
        </FilesProvider>
    </MemoryRouter>
);

const renderSync = (entries = ["/start", "/dashboard"], { isLoading = false } = {}) => {
    const result = render(tree(entries, isLoading));
    return { ...result, setLoading: (loading) => result.rerender(tree(entries, loading)) };
};

const view = () => JSON.parse(screen.getByTestId("view").textContent);
const url = () => screen.getByTestId("url").textContent;
const click = (name) => userEvent.click(screen.getByRole("button", { name }));
const back = () => click("back");
const forward = () => click("forward");

describe("LocationSync", () => {
    it("opens the folder named in the address", async () => {
        renderSync(["/dashboard?folder=%2FDocs%2FTaxes"]);

        await screen.findByText(/"currentPath":"\/Docs\/Taxes"/);
        expect(url()).toBe("/dashboard?folder=%2FDocs%2FTaxes");
    });

    it("falls back to home, and fixes the address, for a folder that doesn't exist", async () => {
        renderSync(["/start", "/dashboard?folder=%2FGone"]);

        await screen.findByText(/"currentPath":""/);
        expect(url()).toBe("/dashboard");
        // The bad address was replaced rather than added to
        await back();
        await waitFor(() => expect(url()).toBe("/start"));
    });

    it("leaves the address alone while the file list is loading", () => {
        renderSync(["/dashboard?folder=%2FDocs"], { isLoading: true });
        expect(view().currentPath).toBe("");
        expect(url()).toBe("/dashboard?folder=%2FDocs");
    });

    it("pushes an entry for each folder and moves between them with back and forward", async () => {
        renderSync();
        await click("docs");
        await click("taxes");
        expect(url()).toBe("/dashboard?folder=%2FDocs%2FTaxes");

        await back();
        await screen.findByText(/"currentPath":"\/Docs"/);
        expect(url()).toBe("/dashboard?folder=%2FDocs");

        await back();
        await screen.findByText(/"currentPath":""/);
        expect(url()).toBe("/dashboard");

        await forward();
        await forward();
        await screen.findByText(/"currentPath":"\/Docs\/Taxes"/);
    });

    it("doesn't add an entry for the folder that is already open", async () => {
        renderSync();
        await click("docs");
        await click("docs");

        await back();
        await screen.findByText(/"currentPath":""/);
        expect(url()).toBe("/dashboard");
        await back();
        await waitFor(() => expect(url()).toBe("/start"));
    });

    it("clears the selection when back changes the folder", async () => {
        renderSync();
        await click("docs");
        await click("select");
        expect(view().selected).toBe(1);
        await click("photos");

        await back();
        await screen.findByText(/"currentPath":"\/Docs"/);
        expect(view().selected).toBe(0);
    });

    it("opens the recycle bin as its own history entry", async () => {
        renderSync();
        await click("docs");
        await click("open bin");
        expect(url()).toBe("/dashboard?folder=%2FDocs&bin=1");
        expect(view().isBinOpen).toBe(true);

        await back();
        await screen.findByText(/"isBinOpen":false/);
        expect(view().currentPath).toBe("/Docs");

        await forward();
        await screen.findByText(/"isBinOpen":true/);
    });

    it("keeps search results in history, replacing the entry while the search text changes", async () => {
        renderSync();
        await click("docs");
        await click("search rep");
        expect(url()).toBe("/dashboard?folder=%2FDocs&q=rep");
        await click("search report");
        expect(url()).toBe("/dashboard?folder=%2FDocs&q=report");

        // One entry for the whole search: back leaves it
        await back();
        await screen.findByText(/"term":""/);
        expect(url()).toBe("/dashboard?folder=%2FDocs");

        await forward();
        await screen.findByText(/"term":"report"/);
    });

    it("restores a search from the address", async () => {
        renderSync(["/dashboard?q=tax&bin=1"]);
        await screen.findByText(/"term":"tax"/);
        expect(view().isBinOpen).toBe(true);
    });

    it("opening a folder from the results leaves the search as the previous entry", async () => {
        renderSync();
        await click("search report");
        await click("photos");
        await click("clear search");

        await back();
        await screen.findByText(/"term":"report"/);
    });

    it("keeps a change made while the file list was reloading", async () => {
        const { setLoading } = renderSync();
        setLoading(true);
        await click("docs");
        expect(view().currentPath).toBe("/Docs");

        setLoading(false);
        await waitFor(() => expect(url()).toBe("/dashboard?folder=%2FDocs"));
        expect(view().currentPath).toBe("/Docs");
    });

    it("round-trips a folder name with reserved and non-ASCII characters", async () => {
        const name = "/A & B #1 �+";
        renderSync();
        await click("odd");
        const expected = `/dashboard?folder=${new URLSearchParams({ folder: name }).toString().slice(7)}`;
        expect(url()).toBe(expected);
        expect(url()).not.toContain("&B");

        await click("photos");
        await back();
        await waitFor(() => expect(view().currentPath).toBe(name));
        expect(url()).toBe(expected);
    });

    it("opens a folder with reserved characters from the address", async () => {
        const name = "/A & B #1 �+";
        renderSync([`/dashboard?${new URLSearchParams({ folder: name })}`]);
        await waitFor(() => expect(view().currentPath).toBe(name));
    });

    it("steps back to where the search started when the search is cleared", async () => {
        renderSync();
        await click("docs");
        await click("search rep");
        await click("search report");
        await click("clear search");
        await screen.findByText(/"term":""/);
        await waitFor(() => expect(url()).toBe("/dashboard?folder=%2FDocs"));

        // One Back leaves the folder: there is no duplicate entry for it
        await back();
        await screen.findByText(/"currentPath":""/);
        expect(url()).toBe("/dashboard");
    });

    it("restores the search when going Forward after clearing it stepped back", async () => {
        renderSync();
        await click("docs");
        await click("search rep");
        await click("clear search");
        await waitFor(() => expect(url()).toBe("/dashboard?folder=%2FDocs"));
        expect(view().term).toBe("");

        await forward();
        await screen.findByText(/"term":"rep"/);
        expect(url()).toBe("/dashboard?folder=%2FDocs&q=rep");
    });

    it("replaces the entry when a search was started somewhere else", async () => {
        renderSync();
        await click("search rep");
        await click("photos");
        await click("clear search");
        await waitFor(() => expect(view().term).toBe(""));
        expect(url()).toBe("/dashboard?folder=%2FPhotos");
    });
});
