import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import Admin from "./Admin";
import {
    createUserAPI,
    deleteUserAPI,
    getAdminStatsAPI,
    getAdminUsersAPI,
    getUserAvatarBlobAPI,
    setUserAdminAPI,
    setUserPasswordAPI,
    setUserPermanentAPI,
    setUserQuotaAPI,
    setUserSuspendedAPI,
} from "../../api/adminAPI";

vi.mock("../../api/adminAPI", () => ({
    getAdminStatsAPI: vi.fn(),
    getAdminUsersAPI: vi.fn(),
    setUserQuotaAPI: vi.fn(async () => ({})),
    setUserAdminAPI: vi.fn(async () => ({})),
    createUserAPI: vi.fn(async () => ({})),
    deleteUserAPI: vi.fn(async () => ({})),
    setUserPasswordAPI: vi.fn(async () => ({})),
    setUserPermanentAPI: vi.fn(async () => ({})),
    setUserSuspendedAPI: vi.fn(async () => ({})),
    getUserAvatarBlobAPI: vi.fn(),
}));

const GB = 1024 ** 3;

const users = [
    { id: "u1", firstName: "Alex", lastName: "Morgan", email: "alex@example.com", createdAt: "2026-01-02T10:00:00Z",
        lastLogin: "2026-03-04T09:30:00Z", bytesUsed: 512 * 1024 ** 2, fileCount: 42, quota: GB, quotaOverride: null, isAdmin: true,
        isPermanent: false, avatarUpdatedAt: null },
    { id: "u2", firstName: "Sam", lastName: "", email: "sam@example.com", createdAt: "2026-02-01T10:00:00Z",
        lastLogin: null, bytesUsed: 1.9 * GB, fileCount: 7, quota: 2 * GB, quotaOverride: 2 * GB, isAdmin: false,
        isPermanent: true, avatarUpdatedAt: "2026-02-02T10:00:00Z" },
];

const stats = { userCount: 2, adminCount: 1, fileCount: 49, folderCount: 9, totalStoredBytes: 2.4 * GB,
    defaultQuotaBytes: GB, storageBytesOnDisk: 2.5 * GB, diskTotalBytes: 100 * GB, diskFreeBytes: 60 * GB,
    currentUserId: "u1" };

const row = (email) => screen.getByText(email).closest("tr");

const failure = (status, error) => Object.assign(new Error("Request failed"), { response: { status, data: { error } } });

// Open a row's Actions menu and pick an item
const chooseAction = async (email, item) => {
    await userEvent.click(await screen.findByRole("button", { name: `Actions for ${email}` }));
    await userEvent.click(screen.getByRole("menuitem", { name: item }));
};

beforeEach(() => {
    [setUserAdminAPI, createUserAPI, deleteUserAPI, setUserPasswordAPI, setUserPermanentAPI, setUserSuspendedAPI, getUserAvatarBlobAPI].forEach((mock) => {
        mock.mockReset();
        mock.mockResolvedValue({});
    });
    getUserAvatarBlobAPI.mockResolvedValue(new Blob(["png"], { type: "image/png" }));
    getAdminUsersAPI.mockResolvedValue(users);
    getAdminStatsAPI.mockResolvedValue(stats);
    URL.createObjectURL = vi.fn(() => "blob:avatar-sam");
    URL.revokeObjectURL = vi.fn();
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

    it("makes a user an admin straight away and reloads", async () => {
        render(<Admin />);
        await userEvent.click(await screen.findByRole("button", { name: "Make sam@example.com an admin" }));

        expect(setUserAdminAPI).toHaveBeenCalledWith("u2", true);
        expect(getAdminUsersAPI).toHaveBeenCalledTimes(2);
    });

    it("asks before removing admin, and does nothing if cancelled", async () => {
        render(<Admin />);
        await userEvent.click(await screen.findByRole("button", { name: "Remove admin from alex@example.com" }));

        const confirm = screen.getByRole("group", { name: "Confirm removing admin from alex@example.com" });
        expect(setUserAdminAPI).not.toHaveBeenCalled();
        await userEvent.click(within(confirm).getByRole("button", { name: "Cancel" }));

        expect(setUserAdminAPI).not.toHaveBeenCalled();
        expect(screen.getByRole("button", { name: "Remove admin from alex@example.com" })).toBeInTheDocument();
    });

    it("removes admin once confirmed", async () => {
        render(<Admin />);
        await userEvent.click(await screen.findByRole("button", { name: "Remove admin from alex@example.com" }));
        await userEvent.click(screen.getByRole("button", { name: "Remove" }));

        expect(setUserAdminAPI).toHaveBeenCalledWith("u1", false);
        expect(getAdminUsersAPI).toHaveBeenCalledTimes(2);
    });

    it("shows the server's message when the last admin can't be removed", async () => {
        setUserAdminAPI.mockRejectedValue(Object.assign(new Error("Conflict"), {
            response: { status: 409, data: { error: "There must always be at least one administrator" } },
        }));
        render(<Admin />);
        await userEvent.click(await screen.findByRole("button", { name: "Remove admin from alex@example.com" }));
        await userEvent.click(screen.getByRole("button", { name: "Remove" }));

        expect(await within(row("alex@example.com")).findByRole("alert"))
            .toHaveTextContent("There must always be at least one administrator");
        expect(getAdminUsersAPI).toHaveBeenCalledTimes(1);
        // Back to the normal button, so it can be tried again
        expect(screen.getByRole("button", { name: "Remove admin from alex@example.com" })).toBeInTheDocument();
    });

    it("clears a row's refusal message once the list reloads after another row's change", async () => {
        setUserAdminAPI.mockImplementation(async (id) => {
            if (id === "u1")
                throw Object.assign(new Error("Conflict"), {
                    response: { status: 409, data: { error: "There must always be at least one administrator" } },
                });
            return {};
        });
        render(<Admin />);
        await userEvent.click(await screen.findByRole("button", { name: "Remove admin from alex@example.com" }));
        await userEvent.click(screen.getByRole("button", { name: "Remove" }));
        expect(await within(row("alex@example.com")).findByRole("alert")).toBeInTheDocument();

        await userEvent.click(screen.getByRole("button", { name: "Make sam@example.com an admin" }));

        await vi.waitFor(() => expect(getAdminUsersAPI).toHaveBeenCalledTimes(2));
        expect(within(row("alex@example.com")).queryByRole("alert")).not.toBeInTheDocument();
    });

    it("tells non-admins the page isn't for them", async () => {
        getAdminUsersAPI.mockImplementation(async () => {
            throw Object.assign(new Error("Forbidden"), { response: { status: 403, data: { error: "Administrators only" } } });
        });
        render(<Admin />);

        expect(await screen.findByRole("alert")).toHaveTextContent("Only administrators can see this page.");
        expect(screen.queryByRole("table")).toBeNull();
    });

    describe("avatars", () => {
        it("fetches a picture through the API client and shows it, with initials for users without one", async () => {
            render(<Admin />);

            expect(await screen.findByRole("img", { name: "Sam's profile picture" })).toHaveAttribute("src", "blob:avatar-sam");
            expect(getUserAvatarBlobAPI).toHaveBeenCalledTimes(1);
            expect(getUserAvatarBlobAPI).toHaveBeenCalledWith("u2");

            // Alex has no picture: no request, an initial instead
            expect(within(row("alex@example.com")).queryByRole("img")).toBeNull();
            expect(within(row("alex@example.com")).getByText("A", { selector: ".avatar-initial" })).toBeInTheDocument();
        });

        it("falls back to the initial when the picture can't be loaded", async () => {
            getUserAvatarBlobAPI.mockRejectedValue(failure(404, "No profile picture"));
            render(<Admin />);

            await screen.findByText("sam@example.com");
            await vi.waitFor(() => expect(getUserAvatarBlobAPI).toHaveBeenCalled());
            expect(screen.queryByRole("img", { name: "Sam's profile picture" })).toBeNull();
            expect(within(row("sam@example.com")).getByText("S", { selector: ".avatar-initial" })).toBeInTheDocument();
        });
    });

    describe("row actions", () => {
        it("lists set password, permanent and delete for another user", async () => {
            render(<Admin />);
            await userEvent.click(await screen.findByRole("button", { name: "Actions for sam@example.com" }));

            const menu = screen.getByRole("menu");
            expect(within(menu).getByRole("menuitem", { name: "Set password" })).toBeInTheDocument();
            expect(within(menu).getByRole("menuitem", { name: "Remove permanent" })).toBeInTheDocument();
            expect(within(menu).getByRole("menuitem", { name: "Delete account" })).toBeInTheDocument();
        });

        it("keeps only one row's menu open at a time", async () => {
            render(<Admin />);
            await userEvent.click(await screen.findByRole("button", { name: "Actions for sam@example.com" }));
            await userEvent.click(screen.getByRole("button", { name: "Actions for alex@example.com" }));
            expect(screen.getAllByRole("menu")).toHaveLength(1);
            expect(screen.getByRole("button", { name: "Actions for sam@example.com" })).toHaveAttribute("aria-expanded", "false");
        });

        it("returns focus to the Actions button when its dialog closes", async () => {
            render(<Admin />);
            await chooseAction("sam@example.com", "Set password");
            await userEvent.keyboard("{Escape}");
            await vi.waitFor(() => expect(screen.getByRole("button", { name: "Actions for sam@example.com" })).toHaveFocus());
        });

        it("leaves set password and delete out of your own row", async () => {
            render(<Admin />);
            await userEvent.click(await screen.findByRole("button", { name: "Actions for alex@example.com" }));

            const menu = screen.getByRole("menu");
            expect(within(menu).getByRole("menuitem", { name: "Make permanent" })).toBeInTheDocument();
            expect(within(menu).queryByRole("menuitem", { name: "Set password" })).toBeNull();
            expect(within(menu).queryByRole("menuitem", { name: "Delete account" })).toBeNull();
        });

        it("shows a Permanent badge", async () => {
            render(<Admin />);
            await screen.findByText("sam@example.com");

            expect(within(row("sam@example.com")).getByText("Permanent")).toBeInTheDocument();
            expect(within(row("alex@example.com")).queryByText("Permanent")).toBeNull();
        });

        it("toggles permanent straight away and reloads", async () => {
            render(<Admin />);
            await chooseAction("alex@example.com", "Make permanent");

            expect(setUserPermanentAPI).toHaveBeenCalledWith("u1", true);
            await vi.waitFor(() => expect(getAdminUsersAPI).toHaveBeenCalledTimes(2));
            expect(await screen.findByRole("status")).toHaveTextContent("alex@example.com is now a permanent account.");
        });

        it("shows why a permanent change failed", async () => {
            setUserPermanentAPI.mockRejectedValue(failure(404, "User not found"));
            render(<Admin />);
            await chooseAction("sam@example.com", "Remove permanent");

            expect(await screen.findByRole("alert")).toHaveTextContent("User not found");
            expect(getAdminUsersAPI).toHaveBeenCalledTimes(1);
        });
    });

    describe("suspending", () => {
        const suspendedUsers = [users[0], { ...users[1], suspendedAt: "2026-05-01T10:00:00Z" }];

        it("shows a Suspended badge only on suspended accounts", async () => {
            getAdminUsersAPI.mockResolvedValue(suspendedUsers);
            getAdminStatsAPI.mockResolvedValue({ ...stats, suspendedCount: 1 });
            render(<Admin />);
            await screen.findByText("sam@example.com");

            expect(within(row("sam@example.com")).getByText("Suspended")).toBeInTheDocument();
            expect(within(row("alex@example.com")).queryByText("Suspended")).toBeNull();
            expect(screen.getByText(/1 suspended/)).toBeInTheDocument();
        });

        it("suspends straight away and reloads", async () => {
            render(<Admin />);
            await chooseAction("sam@example.com", "Suspend account");

            expect(setUserSuspendedAPI).toHaveBeenCalledWith("u2", true);
            await vi.waitFor(() => expect(getAdminUsersAPI).toHaveBeenCalledTimes(2));
            expect(await screen.findByRole("status")).toHaveTextContent("sam@example.com is suspended");
        });

        it("labels a suspended administrator as Admin (suspended)", async () => {
            getAdminUsersAPI.mockResolvedValue([users[0], { ...users[1], isAdmin: true, suspendedAt: "2026-05-01T10:00:00Z" }]);
            render(<Admin />);
            await screen.findByText("sam@example.com");

            const samRow = within(row("sam@example.com"));
            expect(samRow.getByText("Admin (suspended)")).toBeInTheDocument();
            expect(samRow.getByText("Suspended")).toBeInTheDocument();
            expect(within(row("alex@example.com")).getByText("Admin")).toBeInTheDocument();
        });

        it("offers Unsuspend for a suspended account and lifts the suspension", async () => {
            getAdminUsersAPI.mockResolvedValue(suspendedUsers);
            render(<Admin />);
            await chooseAction("sam@example.com", "Unsuspend account");

            expect(setUserSuspendedAPI).toHaveBeenCalledWith("u2", false);
            expect(await screen.findByRole("status")).toHaveTextContent("sam@example.com is no longer suspended.");
        });

        it("leaves suspending out of your own row", async () => {
            render(<Admin />);
            await userEvent.click(await screen.findByRole("button", { name: "Actions for alex@example.com" }));

            expect(within(screen.getByRole("menu")).queryByRole("menuitem", { name: /suspend/i })).toBeNull();
        });

        it("shows why suspending failed", async () => {
            setUserSuspendedAPI.mockRejectedValue(failure(409, "There must always be at least one administrator"));
            render(<Admin />);
            await chooseAction("sam@example.com", "Suspend account");

            expect(await screen.findByRole("alert")).toHaveTextContent("There must always be at least one administrator");
            expect(getAdminUsersAPI).toHaveBeenCalledTimes(1);
        });
    });

    describe("set password", () => {
        it("only enables Set password once the password meets the rules", async () => {
            render(<Admin />);
            await chooseAction("sam@example.com", "Set password");

            const dialog = screen.getByRole("dialog", { name: "Set password for sam@example.com" });
            const save = within(dialog).getByRole("button", { name: "Set password" });
            expect(save).toBeDisabled();
            await userEvent.type(within(dialog).getByLabelText("New password"), "weak");
            expect(save).toBeDisabled();
            await userEvent.clear(within(dialog).getByLabelText("New password"));
            await userEvent.type(within(dialog).getByLabelText("New password"), "N3wPassword");
            expect(save).toBeEnabled();
        });

        it("sets the password, closes and says so", async () => {
            render(<Admin />);
            await chooseAction("sam@example.com", "Set password");
            await userEvent.type(screen.getByLabelText("New password"), "N3wPassword");
            await userEvent.click(screen.getByRole("button", { name: "Set password" }));

            expect(setUserPasswordAPI).toHaveBeenCalledWith("u2", "N3wPassword");
            expect(await screen.findByRole("status")).toHaveTextContent("Password set for sam@example.com");
            expect(screen.queryByRole("dialog")).toBeNull();
        });

        it("shows the server's message and stays open when it refuses", async () => {
            setUserPasswordAPI.mockRejectedValue(failure(400, "Password must be 72 bytes or fewer"));
            render(<Admin />);
            await chooseAction("sam@example.com", "Set password");
            await userEvent.type(screen.getByLabelText("New password"), "N3wPassword");
            await userEvent.click(screen.getByRole("button", { name: "Set password" }));

            expect(await within(screen.getByRole("dialog")).findByRole("alert")).toHaveTextContent("Password must be 72 bytes or fewer");
        });

        it("closes on Cancel and on Escape without saving", async () => {
            render(<Admin />);
            await chooseAction("sam@example.com", "Set password");
            await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
            expect(screen.queryByRole("dialog")).toBeNull();

            await chooseAction("sam@example.com", "Set password");
            await userEvent.keyboard("{Escape}");
            expect(screen.queryByRole("dialog")).toBeNull();
            expect(setUserPasswordAPI).not.toHaveBeenCalled();
        });
    });

    describe("delete account", () => {
        it("needs the user's email typed before Delete account is enabled", async () => {
            render(<Admin />);
            await chooseAction("sam@example.com", "Delete account");

            const dialog = screen.getByRole("dialog", { name: "Delete account" });
            const confirm = within(dialog).getByRole("button", { name: "Delete account" });
            expect(confirm).toBeDisabled();
            await userEvent.type(within(dialog).getByLabelText("Type sam@example.com to confirm"), "sam@exampl");
            expect(confirm).toBeDisabled();
            await userEvent.type(within(dialog).getByLabelText("Type sam@example.com to confirm"), "e.com");
            expect(confirm).toBeEnabled();
            expect(deleteUserAPI).not.toHaveBeenCalled();
        });

        it("deletes the account and reloads the list", async () => {
            render(<Admin />);
            await chooseAction("sam@example.com", "Delete account");
            await userEvent.type(screen.getByLabelText("Type sam@example.com to confirm"), "sam@example.com");
            await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Delete account" }));

            expect(deleteUserAPI).toHaveBeenCalledWith("u2");
            await vi.waitFor(() => expect(getAdminUsersAPI).toHaveBeenCalledTimes(2));
            expect(await screen.findByRole("status")).toHaveTextContent("The account for sam@example.com was deleted.");
            expect(screen.queryByRole("dialog")).toBeNull();
        });

        it("shows the server's refusal (the last administrator) and keeps the dialog open", async () => {
            deleteUserAPI.mockRejectedValue(failure(409, "There must always be at least one administrator"));
            render(<Admin />);
            await chooseAction("sam@example.com", "Delete account");
            await userEvent.type(screen.getByLabelText("Type sam@example.com to confirm"), "sam@example.com");
            await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Delete account" }));

            expect(await within(screen.getByRole("dialog")).findByRole("alert"))
                .toHaveTextContent("There must always be at least one administrator");
            expect(getAdminUsersAPI).toHaveBeenCalledTimes(1);
        });
    });

    describe("create account", () => {
        const fill = async ({ first = "Jo", last = "Bloggs", email = "jo@example.com", password = "N3wPassword" } = {}) => {
            await userEvent.click(await screen.findByRole("button", { name: "Create account" }));
            const form = screen.getByRole("form", { name: "Create account" });
            if (first) await userEvent.type(within(form).getByLabelText("First name"), first);
            if (last) await userEvent.type(within(form).getByLabelText(/Last name/), last);
            if (email) await userEvent.type(within(form).getByLabelText("Email address"), email);
            if (password) await userEvent.type(within(form).getByLabelText("Password"), password);
            return form;
        };

        it("keeps Create account disabled until the details are valid", async () => {
            render(<Admin />);
            const form = await fill({ password: "weak" });
            const submit = within(form).getByRole("button", { name: "Create account" });
            expect(submit).toBeDisabled();

            await userEvent.clear(within(form).getByLabelText("Password"));
            await userEvent.type(within(form).getByLabelText("Password"), "N3wPassword");
            expect(submit).toBeEnabled();
        });

        it("creates the account with the chosen flags and reloads", async () => {
            render(<Admin />);
            const form = await fill();
            await userEvent.click(within(form).getByLabelText("Permanent account"));
            await userEvent.click(within(form).getByRole("button", { name: "Create account" }));

            expect(createUserAPI).toHaveBeenCalledWith({
                firstName: "Jo", lastName: "Bloggs", email: "jo@example.com", password: "N3wPassword", isAdmin: false, isPermanent: true,
            });
            await vi.waitFor(() => expect(getAdminUsersAPI).toHaveBeenCalledTimes(2));
            expect(await screen.findByRole("status")).toHaveTextContent("Account created for jo@example.com");
            expect(screen.queryByRole("form", { name: "Create account" })).toBeNull();
        });

        it("shows the duplicate-email message and keeps the form", async () => {
            createUserAPI.mockRejectedValue(failure(409, "Email already exists"));
            render(<Admin />);
            const form = await fill();
            await userEvent.click(within(form).getByRole("button", { name: "Create account" }));

            expect(await within(form).findByRole("alert")).toHaveTextContent("Email already exists");
            expect(getAdminUsersAPI).toHaveBeenCalledTimes(1);
            expect(within(form).getByLabelText("Email address")).toHaveValue("jo@example.com");
        });

        it("can be cancelled", async () => {
            render(<Admin />);
            const form = await fill();
            await userEvent.click(within(form).getByRole("button", { name: "Cancel" }));

            expect(screen.queryByRole("form", { name: "Create account" })).toBeNull();
            expect(createUserAPI).not.toHaveBeenCalled();
        });
    });
});
