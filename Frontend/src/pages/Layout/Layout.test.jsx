import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import Layout from "./Layout";
import { isAuthenticated } from "../../utils/auth";

vi.mock("../../utils/auth", () => ({ isAuthenticated: vi.fn() }));

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
