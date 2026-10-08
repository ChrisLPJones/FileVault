import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, render, screen, waitFor } from "@testing-library/react";
import { useUserProfile } from "./useUserProfile";
import { getAvatarBlobAPI, getUserInfoAPI, notifyProfileChanged } from "../api/accountAPI";

vi.mock("../api/accountAPI", async (importOriginal) => ({
    ...(await importOriginal()),
    getUserInfoAPI: vi.fn(),
    getAvatarBlobAPI: vi.fn(),
}));

function Name({ label }) {
    const profile = useUserProfile();
    return <p data-testid={label}>{profile.name || "(none)"}|{profile.imageUrl ?? "no image"}</p>;
}

let urls;

beforeEach(() => {
    urls = 0;
    vi.spyOn(URL, "createObjectURL").mockImplementation(() => `blob:avatar-${++urls}`);
    vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => {});
    getUserInfoAPI.mockResolvedValue({ firstName: "Alex", lastName: "Morgan", email: "alex@example.com", avatarUpdatedAt: null });
    getAvatarBlobAPI.mockResolvedValue(new Blob(["png"], { type: "image/png" }));
});

afterEach(() => vi.restoreAllMocks());

describe("useUserProfile", () => {
    it("loads the profile once for everything that shows it", async () => {
        render(
            <>
                <Name label="a" />
                <Name label="b" />
                <Name label="c" />
            </>
        );

        await waitFor(() => expect(screen.getByTestId("a")).toHaveTextContent("Alex Morgan|no image"));
        expect(screen.getByTestId("b")).toHaveTextContent("Alex Morgan");
        expect(screen.getByTestId("c")).toHaveTextContent("Alex Morgan");
        expect(getUserInfoAPI).toHaveBeenCalledTimes(1);
        expect(getAvatarBlobAPI).not.toHaveBeenCalled();
    });

    it("loads the picture when there is one, and reloads when the profile changes", async () => {
        getUserInfoAPI.mockResolvedValue({ firstName: "Alex", lastName: "", email: "a@example.com", avatarUpdatedAt: "2026-01-01T00:00:00Z" });
        render(<Name label="a" />);
        await waitFor(() => expect(screen.getByTestId("a")).toHaveTextContent("Alex|blob:avatar-1"));

        getUserInfoAPI.mockResolvedValue({ firstName: "Sam", lastName: "Lee", email: "a@example.com", avatarUpdatedAt: "2026-02-01T00:00:00Z" });
        act(() => notifyProfileChanged());

        await waitFor(() => expect(screen.getByTestId("a")).toHaveTextContent("Sam Lee|blob:avatar-2"));
        expect(getUserInfoAPI).toHaveBeenCalledTimes(2);
        expect(URL.revokeObjectURL).toHaveBeenCalledWith("blob:avatar-1"); // the old picture is released
    });

    it("forgets the profile once nothing shows it, and loads again for the next subscriber", async () => {
        getUserInfoAPI.mockResolvedValue({ firstName: "Alex", lastName: "Morgan", email: "a@example.com", avatarUpdatedAt: "2026-01-01T00:00:00Z" });
        const first = render(
            <>
                <Name label="a" />
                <Name label="b" />
            </>
        );
        await waitFor(() => expect(screen.getByTestId("a")).toHaveTextContent("Alex Morgan|blob:avatar-1"));

        first.unmount();
        expect(URL.revokeObjectURL).toHaveBeenCalledWith("blob:avatar-1");

        // e.g. someone else logs in: they never see the previous account's details
        getUserInfoAPI.mockResolvedValue({ firstName: "Sam", lastName: "Lee", email: "sam@example.com", avatarUpdatedAt: null });
        render(<Name label="next" />);
        expect(screen.getByTestId("next")).toHaveTextContent("(none)|no image");
        await waitFor(() => expect(screen.getByTestId("next")).toHaveTextContent("Sam Lee|no image"));
        expect(getUserInfoAPI).toHaveBeenCalledTimes(2);
    });

    it("keeps one subscriber's profile when another unmounts", async () => {
        const { rerender } = render(
            <>
                <Name label="a" />
                <Name label="b" />
            </>
        );
        await waitFor(() => expect(screen.getByTestId("a")).toHaveTextContent("Alex Morgan"));

        rerender(<Name label="a" />);
        expect(screen.getByTestId("a")).toHaveTextContent("Alex Morgan");
        expect(getUserInfoAPI).toHaveBeenCalledTimes(1);
    });

    it("ignores a load that finishes after everyone has unsubscribed", async () => {
        let finish;
        getUserInfoAPI.mockReturnValue(new Promise((resolve) => (finish = resolve)));
        const { unmount } = render(<Name label="a" />);
        unmount();

        await act(async () => finish({ firstName: "Late", lastName: "", email: "", avatarUpdatedAt: null }));

        getUserInfoAPI.mockResolvedValue({ firstName: "Fresh", lastName: "", email: "", avatarUpdatedAt: null });
        render(<Name label="b" />);
        expect(screen.getByTestId("b")).toHaveTextContent("(none)");
        await waitFor(() => expect(screen.getByTestId("b")).toHaveTextContent("Fresh"));
    });

    it("keeps showing the last profile if a reload fails", async () => {
        render(<Name label="a" />);
        await waitFor(() => expect(screen.getByTestId("a")).toHaveTextContent("Alex Morgan"));

        getUserInfoAPI.mockImplementation(async () => {
            throw new Error("offline");
        });
        await act(async () => notifyProfileChanged());
        expect(screen.getByTestId("a")).toHaveTextContent("Alex Morgan");
    });
});
