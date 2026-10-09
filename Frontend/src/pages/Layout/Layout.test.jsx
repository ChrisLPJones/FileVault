import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { Link, MemoryRouter, Route, Routes } from "react-router-dom";
import userEvent from "@testing-library/user-event";
import Layout from "./Layout";
import { isAuthenticated } from "../../utils/auth";

vi.mock("../../utils/auth", async (importOriginal) => ({ ...(await importOriginal()), isAuthenticated: vi.fn() }));

const renderAt = (pathname) =>
    render(
        <MemoryRouter initialEntries={[pathname]}>
            <Routes>
                <Route element={<Layout />}>
                    <Route path="*" element={<p>Page body</p>} />
                </Route>
            </Routes>
        </MemoryRouter>
    );

beforeEach(() => sessionStorage.clear());

describe("Layout top bar when logged out", () => {
    it.each([
        "/", "/login", "/login/", "/register", "/forgot-password",
        "/reset-password", "/verify-email", "/s/abc",
    ])("has no top bar on %s", (pathname) => {
        isAuthenticated.mockReturnValue(false);
        const { container } = renderAt(pathname);

        expect(screen.getByText("Page body")).toBeInTheDocument();
        expect(container.querySelector("header")).not.toBeInTheDocument();
        expect(container.querySelector(".page").style.getPropertyValue("--fv-header-height")).toBe("0px");
    });
});

describe("Layout top bar when logged in", () => {
    it.each(["/s/abc", "/verify-email", "/settings", "/settings/", "/shared-links", "/admin"])(
        "has Back to files and no account menu on %s",
        (pathname) => {
            isAuthenticated.mockReturnValue(true);
            const { container } = renderAt(pathname);

            expect(container.querySelector("header")).toBeInTheDocument();
            expect(screen.getByRole("link", { name: "Back to files" })).toBeInTheDocument();
            expect(screen.queryByText("Account menu")).not.toBeInTheDocument();
            expect(container.querySelector(".page").style.getPropertyValue("--fv-header-height")).toBe("");
        }
    );

    it("has no Back to files on the file list", () => {
        isAuthenticated.mockReturnValue(true);
        renderAt("/dashboard");

        expect(screen.queryByRole("link", { name: "Back to files" })).not.toBeInTheDocument();
    });
});

describe("Back to files", () => {
    it("returns to the folder that was open", async () => {
        isAuthenticated.mockReturnValue(true);
        render(
            <MemoryRouter initialEntries={["/dashboard?folder=%2FDocs"]}>
                <Routes>
                    <Route element={<Layout />}>
                        <Route path="dashboard" element={<Link to="/settings">Settings</Link>} />
                        <Route path="settings" element={<p>Settings page</p>} />
                        <Route path="*" element={<p>Page body</p>} />
                    </Route>
                </Routes>
            </MemoryRouter>
        );

        await userEvent.click(screen.getByRole("link", { name: "Settings" }));
        expect(screen.getByText("Settings page")).toBeInTheDocument();
        expect(screen.getByRole("link", { name: "Back to files" })).toHaveAttribute("href", "/dashboard?folder=%2FDocs");
    });

    it("opens the home folder when no folder was remembered", () => {
        isAuthenticated.mockReturnValue(true);
        renderAt("/settings");
        expect(screen.getByRole("link", { name: "Back to files" })).toHaveAttribute("href", "/dashboard");
    });
});
