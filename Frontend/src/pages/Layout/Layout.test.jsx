import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import Layout from "./Layout";
import { isAuthenticated } from "../../utils/auth";

vi.mock("../../utils/auth", () => ({ isAuthenticated: vi.fn() }));
vi.mock("../../components/UserMenu", () => ({ default: () => null }));

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
    it.each(["/", "/login", "/register"])("has no Register or Login links on %s", (pathname) => {
        isAuthenticated.mockReturnValue(false);
        renderAt(pathname);

        expect(screen.getByText("Page body")).toBeInTheDocument();
        expect(screen.queryByRole("link", { name: "Register" })).not.toBeInTheDocument();
        expect(screen.queryByRole("link", { name: "Login" })).not.toBeInTheDocument();
    });

    it("keeps the links on other logged-out pages", () => {
        isAuthenticated.mockReturnValue(false);
        renderAt("/forgot-password");

        expect(screen.getByRole("link", { name: "Register" })).toBeInTheDocument();
        expect(screen.getByRole("link", { name: "Login" })).toBeInTheDocument();
    });
});
