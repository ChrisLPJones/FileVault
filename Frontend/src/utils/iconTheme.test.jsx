import { afterEach, describe, expect, it, vi } from "vitest";
import { act, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import userEvent from "@testing-library/user-event";
import FileTypeIcon from "../components/FileTypeIcon/FileTypeIcon";
import Settings from "../pages/Settings/Settings";
import { getIconTheme, normaliseIconTheme, setIconTheme } from "./iconTheme";
import { getUserInfoAPI, getUsageAPI, setIconThemeAPI } from "../api/accountAPI";

vi.mock("../api/accountAPI", async (importOriginal) => ({
    ...(await importOriginal()),
    getUserInfoAPI: vi.fn(),
    getUsageAPI: vi.fn(),
    setIconThemeAPI: vi.fn(),
}));
vi.mock("../pages/Settings/TwoFactorCard", () => ({ default: () => null }));
vi.mock("../pages/Settings/SessionsCard", () => ({ default: () => null }));
vi.mock("../components/Avatar", () => ({ default: () => null }));

afterEach(() => act(() => setIconTheme("default")));

describe("icon theme setting", () => {
    it("accepts only known themes and falls back to default", () => {
        expect(normaliseIconTheme("macos")).toBe("macos");
        expect(normaliseIconTheme("beos")).toBe("default");
        expect(normaliseIconTheme(null)).toBe("default");
        expect(normaliseIconTheme(undefined)).toBe("default");
    });

    it("switches every icon when the account's theme changes", () => {
        const { container } = render(
            <>
                <FileTypeIcon name="Docs" path="/Docs" isDirectory />
                <FileTypeIcon name="report.pdf" />
            </>
        );
        expect(container.querySelectorAll("[data-icon-theme]")).toHaveLength(0); // default look

        for (const theme of ["windows", "macos", "ubuntu"]) {
            act(() => setIconTheme(theme));
            expect(container.querySelectorAll(`svg.folder[data-icon-theme="${theme}"]`)).toHaveLength(1);
            expect(container.querySelectorAll(`svg.document[data-icon-theme="${theme}"]`)).toHaveLength(1);
        }

        act(() => setIconTheme("default"));
        expect(container.querySelectorAll("[data-icon-theme]")).toHaveLength(0);
    });

    it("keeps the type label and special folder symbols in every theme", () => {
        for (const theme of ["windows", "macos", "ubuntu"]) {
            act(() => setIconTheme(theme));
            const file = render(<FileTypeIcon name="report.pdf" size={48} />).container;
            expect(file.querySelector("text")).toHaveTextContent("PDF");
            const folder = render(<FileTypeIcon name="Pictures" path="/Pictures" isDirectory />).container;
            expect(folder.querySelector("svg.folder g[opacity]")).not.toBeNull();
            const plain = render(<FileTypeIcon name="Projects" path="/Projects" isDirectory />).container;
            expect(plain.querySelector("svg.folder g[opacity]")).toBeNull();
        }
    });

    it("lets a preview override the account theme", () => {
        const { container } = render(<FileTypeIcon name="a.txt" iconTheme="ubuntu" />);
        expect(container.querySelector('[data-icon-theme="ubuntu"]')).not.toBeNull();
        expect(getIconTheme()).toBe("default");
    });
});

describe("Settings icon choice", () => {
    const renderSettings = async () => {
        getUserInfoAPI.mockResolvedValue({ firstName: "A", lastName: "B", email: "a@example.com", iconTheme: "windows" });
        getUsageAPI.mockResolvedValue({ used: 1, quota: 10, maxUploadBytes: 5 });
        render(<MemoryRouter><Settings /></MemoryRouter>);
        return screen.findByRole("radiogroup", { name: "Icons" });
    };

    it("shows the saved theme and saves a new choice to the account", async () => {
        setIconThemeAPI.mockResolvedValue({ success: "Icon theme saved" });
        const group = await renderSettings();

        expect(group.querySelector("input[value=windows]")).toBeChecked();
        expect(getIconTheme()).toBe("windows");

        await userEvent.click(screen.getByRole("radio", { name: /macOS/ }));
        expect(setIconThemeAPI).toHaveBeenCalledWith("macos");
        expect(getIconTheme()).toBe("macos");
    });

    it("goes back to the previous theme and says so when saving fails", async () => {
        setIconThemeAPI.mockRejectedValue(new Error("nope"));
        await renderSettings();

        await userEvent.click(screen.getByRole("radio", { name: /Ubuntu/ }));
        expect(await screen.findByRole("alert")).toHaveTextContent("nope");
        expect(getIconTheme()).toBe("windows");
    });
});
