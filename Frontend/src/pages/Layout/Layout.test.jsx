import { beforeEach, describe, expect, it } from "vitest";
import { act, render, screen } from "@testing-library/react";
import { Link, MemoryRouter, Route, Routes } from "react-router-dom";
import userEvent from "@testing-library/user-event";
import Layout from "./Layout";
import { setToken } from "../../utils/auth";
import { makeToken } from "../../test/tokens";

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
            setToken(makeToken());
            const { container } = renderAt(pathname);

            expect(container.querySelector("header")).toBeInTheDocument();
            expect(screen.getByRole("link", { name: "Back to files" })).toBeInTheDocument();
            expect(screen.queryByText("Account menu")).not.toBeInTheDocument();
            expect(container.querySelector(".page").style.getPropertyValue("--fv-header-height")).toBe("");
        }
    );

    it("shows the bar as soon as a token is set after the first render", () => {
        const { container } = renderAt("/settings");
        expect(container.querySelector("header")).not.toBeInTheDocument();

        act(() => setToken(makeToken()));
        expect(container.querySelector("header")).toBeInTheDocument();
    });

    it("has no Back to files on the file list", () => {
        setToken(makeToken());
        renderAt("/dashboard");

        expect(screen.queryByRole("link", { name: "Back to files" })).not.toBeInTheDocument();
    });
});

describe("Back to files", () => {
    it("returns to the folder that was open", async () => {
        setToken(makeToken());
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
        setToken(makeToken());
        renderAt("/settings");
        expect(screen.getByRole("link", { name: "Back to files" })).toHaveAttribute("href", "/dashboard");
    });
});
