import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import Admin from "./Admin";
import { getAdminStatsAPI, getAdminUsersAPI, setUserQuotaAPI } from "../../api/adminAPI";

vi.mock("../../api/adminAPI", () => ({
    getAdminStatsAPI: vi.fn(),
    getAdminUsersAPI: vi.fn(),
    setUserQuotaAPI: vi.fn(async () => ({})),
}));

const GB = 1024 ** 3;

const users = [
    { id: "u1", firstName: "Alex", lastName: "Morgan", email: "alex@example.com", createdAt: "2026-01-02T10:00:00Z",
        lastLogin: "2026-03-04T09:30:00Z", bytesUsed: 512 * 1024 ** 2, fileCount: 42, quota: GB, quotaOverride: null, isAdmin: true },
    { id: "u2", firstName: "Sam", lastName: "", email: "sam@example.com", createdAt: "2026-02-01T10:00:00Z",
        lastLogin: null, bytesUsed: 1.9 * GB, fileCount: 7, quota: 2 * GB, quotaOverride: 2 * GB, isAdmin: false },
];

const stats = { userCount: 2, adminCount: 1, fileCount: 49, folderCount: 9, totalStoredBytes: 2.4 * GB,
    defaultQuotaBytes: GB, storageBytesOnDisk: 2.5 * GB, diskTotalBytes: 100 * GB, diskFreeBytes: 60 * GB };

const row = (email) => screen.getByText(email).closest("tr");

beforeEach(() => {
    getAdminUsersAPI.mockResolvedValue(users);
    getAdminStatsAPI.mockResolvedValue(stats);
});

describe("Admin page", () => {
    it("shows the server totals and every user's usage", async () => {
        render(<Admin />);

        const totals = await screen.findByRole("region", { name: "Server totals" });
        expect(within(totals).getByText("49")).toBeInTheDocument();
        expect(within(totals).getByText("2.4 GB")).toBeInTheDocument();
        expect(within(totals).getByText("60 GB free")).toBeInTheDocument();
        expect(within(totals).getByText("40 GB of 100 GB used")).toBeInTheDocument();

        const alex = row("alex@example.com");
        expect(within(alex).getByText("Alex Morgan")).toBeInTheDocument();
        expect(within(alex).getByText("Admin")).toBeInTheDocument();
        expect(within(alex).getByText("42")).toBeInTheDocument();
        expect(within(alex).getByText("512 MB")).toBeInTheDocument();
        expect(within(alex).getByText("(default)")).toBeInTheDocument();

        const sam = row("sam@example.com");
        expect(within(sam).getByText("Never")).toBeInTheDocument();
        expect(within(sam).getByRole("progressbar")).toHaveAttribute("aria-valuenow", "95");
        expect(within(sam).queryByText("(default)")).toBeNull();
    });

    it("changes a quota in the chosen unit and reloads", async () => {
        render(<Admin />);
        await userEvent.click(await screen.findByRole("button", { name: "Change quota for alex@example.com" }));

        const input = screen.getByLabelText("Quota for alex@example.com");
        expect(input).toHaveValue(1);
        await userEvent.clear(input);
        await userEvent.type(input, "5");
        await userEvent.click(screen.getByRole("button", { name: "Save" }));

        expect(setUserQuotaAPI).toHaveBeenCalledWith("u1", 5 * GB);
        expect(getAdminUsersAPI).toHaveBeenCalledTimes(2);
    });

    it("puts a user back on the default quota", async () => {
        render(<Admin />);
        await userEvent.click(await screen.findByRole("button", { name: "Change quota for sam@example.com" }));
        await userEvent.click(screen.getByRole("button", { name: "Use default" }));

        expect(setUserQuotaAPI).toHaveBeenCalledWith("u2", null);
    });

    it("won't save an empty or negative quota", async () => {
        render(<Admin />);
        await userEvent.click(await screen.findByRole("button", { name: "Change quota for alex@example.com" }));
        const input = screen.getByLabelText("Quota for alex@example.com");

        await userEvent.clear(input);
        expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
        await userEvent.type(input, "-1");
        expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
    });

    it("tells non-admins the page isn't for them", async () => {
        getAdminUsersAPI.mockImplementation(async () => {
            throw Object.assign(new Error("Forbidden"), { response: { status: 403, data: { error: "Administrators only" } } });
        });
        render(<Admin />);

        expect(await screen.findByRole("alert")).toHaveTextContent("Only administrators can see this page.");
        expect(screen.queryByRole("table")).toBeNull();
    });
});
