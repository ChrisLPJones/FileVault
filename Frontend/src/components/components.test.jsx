import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import FileTypeIcon from "./FileTypeIcon/FileTypeIcon";
import Avatar from "./Avatar";
import { useUserProfile } from "../hooks/useUserProfile";

vi.mock("../hooks/useUserProfile", () => ({ useUserProfile: vi.fn() }));

const profile = (overrides) => ({ firstName: "", lastName: "", name: "", email: "", imageUrl: null, ...overrides });

describe("Avatar", () => {
    it("shows the first letter of the name when there's no picture", () => {
        useUserProfile.mockReturnValue(profile({ name: "alex Morgan" }));
        const { container } = render(<Avatar size={40} />);

        const initial = container.querySelector(".avatar-initial");
        expect(initial).toHaveTextContent("A");
        expect(initial).toHaveStyle({ width: "40px", height: "40px", fontSize: "18px" });
        expect(screen.queryByRole("img")).not.toBeInTheDocument();
    });

    it("shows a question mark before the profile has loaded", () => {
        useUserProfile.mockReturnValue(profile());
        const { container } = render(<Avatar />);
        expect(container.querySelector(".avatar-initial")).toHaveTextContent("?");
    });

    it("shows the picture when there is one", () => {
        useUserProfile.mockReturnValue(profile({ name: "Alex Morgan", imageUrl: "blob:avatar" }));
        render(<Avatar size={28} />);

        const image = screen.getByRole("img", { name: "Alex Morgan's profile picture" });
        expect(image).toHaveAttribute("src", "blob:avatar");
        expect(image).toHaveStyle({ width: "28px", height: "28px" });
    });
});

describe("FileTypeIcon", () => {
    it("draws a folder for directories, without an extension label", () => {
        const { container } = render(<FileTypeIcon name="report.pdf" path="/report.pdf" isDirectory size={48} />);
        expect(container.querySelector("svg.folder")).toBeInTheDocument();
        expect(container.querySelector("svg.document")).not.toBeInTheDocument();
        expect(container.querySelector("text")).toBeNull();
    });

    it("marks the top-level special folders with a symbol", () => {
        const symbol = (name, path) =>
            render(<FileTypeIcon name={name} path={path} isDirectory />).container.querySelector("svg.folder g[opacity]");

        expect(symbol("Pictures", "/Pictures")).not.toBeNull();
        expect(symbol("pictures", "/pictures")).not.toBeNull();
        expect(symbol("Pictures", "/Holiday/Pictures")).toBeNull(); // only at the top level
        expect(symbol("Projects", "/Projects")).toBeNull();
    });

    it("draws a page with the extension label for files", () => {
        const { container } = render(<FileTypeIcon name="Quarterly report.PDF" size={48} />);
        expect(container.querySelector("svg.document")).toBeInTheDocument();
        expect(container.querySelector("text")).toHaveTextContent("PDF");
        // Coloured by type
        expect(container.querySelector("rect[fill='#e5252a']")).not.toBeNull();
    });

    it("shortens long extensions and uses a generic look for unknown ones", () => {
        const { container } = render(<FileTypeIcon name="data.parquet" size={48} />);
        expect(container.querySelector("text")).toHaveTextContent("PARQ");
        expect(container.querySelector("rect[fill='#6c757d']")).not.toBeNull();
    });

    it("leaves the label out at small sizes and for names without an extension", () => {
        expect(render(<FileTypeIcon name="photo.jpg" size={20} />).container.querySelector("text")).toBeNull();
        expect(render(<FileTypeIcon name="Makefile" size={48} />).container.querySelector("text")).toBeNull();
        expect(render(<FileTypeIcon name=".env" size={48} />).container.querySelector("text")).toBeNull();
    });
});
