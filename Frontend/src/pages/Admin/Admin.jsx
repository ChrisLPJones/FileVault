import { useCallback, useEffect, useState } from "react";
import { getErrorMessage } from "../../api/api";
import {
    createUserAPI,
    deleteUserAPI,
    getAdminStatsAPI,
    getAdminUsersAPI,
    setUserAdminAPI,
    setUserPasswordAPI,
    setUserPermanentAPI,
    setUserQuotaAPI,
} from "../../api/adminAPI";
import { formatBytes } from "../../utils/formatBytes";
import { DeleteAccountDialog, SetPasswordDialog } from "./AccountDialogs";
import CreateAccountForm from "./CreateAccountForm";
import UserAvatar from "./UserAvatar";
import "./Admin.css";

const UNITS = { MB: 1024 ** 2, GB: 1024 ** 3, TB: 1024 ** 4 };

const formatWhen = (value) =>
    value ? new Date(value).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" }) : "Never";

// A quota in the biggest unit that gives a whole-ish number, for the edit box
const toEditable = (bytes) => {
    const unit = bytes >= UNITS.TB && bytes % UNITS.TB === 0 ? "TB" : bytes >= UNITS.GB ? "GB" : "MB";
    return { amount: String(Math.round((bytes / UNITS[unit]) * 100) / 100), unit };
};

// One user's quota: shown as text, with Change to edit it and "Use default" to clear the override
function QuotaCell({ user, onSave }) {
    const [editing, setEditing] = useState(null); // { amount, unit } while editing
    const [saving, setSaving] = useState(false);
    const [error, setError] = useState(null);

    const save = async (quotaBytes) => {
        setSaving(true);
        setError(null);
        try {
            await onSave(user, quotaBytes);
            setEditing(null);
        } catch (err) {
            setError(getErrorMessage(err, "Could not change the quota"));
        } finally {
            setSaving(false);
        }
    };

    if (!editing) {
        return (
            <div className="admin-quota">
                <span>
                    {formatBytes(user.quota)}
                    {user.quotaOverride === null && <span className="admin-muted"> (default)</span>}
                </span>
                <button
                    type="button"
                    className="admin-link-button"
                    aria-label={`Change quota for ${user.email}`}
                    onClick={() => setEditing(toEditable(user.quota))}
                >
                    Change
                </button>
            </div>
        );
    }

    const amount = Number(editing.amount);
    const valid = editing.amount.trim() !== "" && Number.isFinite(amount) && amount >= 0;

    return (
        <form
            className="admin-quota-form"
            onSubmit={(event) => {
                event.preventDefault();
                if (valid) save(Math.round(amount * UNITS[editing.unit]));
            }}
        >
            <input
                type="number"
                min="0"
                step="any"
                aria-label={`Quota for ${user.email}`}
                value={editing.amount}
                onChange={(event) => setEditing({ ...editing, amount: event.target.value })}
                autoFocus
            />
            <select
                aria-label="Unit"
                value={editing.unit}
                onChange={(event) => setEditing({ ...editing, unit: event.target.value })}
            >
                {Object.keys(UNITS).map((unit) => <option key={unit}>{unit}</option>)}
            </select>
            <button type="submit" className="admin-button" disabled={!valid || saving}>Save</button>
            {user.quotaOverride !== null && (
                <button type="button" className="admin-button secondary" disabled={saving} onClick={() => save(null)}>
                    Use default
                </button>
            )}
            <button type="button" className="admin-button secondary" disabled={saving} onClick={() => setEditing(null)}>
                Cancel
            </button>
            {error && <div className="admin-error" role="alert">{error}</div>}
        </form>
    );
}

// One user's admin rights: Make admin, or Remove admin after a confirmation. The server refuses
// to remove the last administrator (409); its message is shown here.
function AdminCell({ user, onChange }) {
    const [confirming, setConfirming] = useState(false);
    const [saving, setSaving] = useState(false);
    const [error, setError] = useState(null);

    const change = async (isAdmin) => {
        setSaving(true);
        setError(null);
        try {
            await onChange(user, isAdmin);
            setConfirming(false);
        } catch (err) {
            setConfirming(false);
            setError(getErrorMessage(err, "Could not change administrator rights"));
        } finally {
            setSaving(false);
        }
    };

    return (
        <div className="admin-rights">
            {confirming ? (
                <div className="admin-confirm" role="group" aria-label={`Confirm removing admin from ${user.email}`}>
                    <span>Remove admin rights from {user.email}?</span>
                    <div className="admin-confirm-actions">
                        <button type="button" className="admin-button danger" disabled={saving} onClick={() => change(false)}>
                            Remove
                        </button>
                        <button type="button" className="admin-button secondary" disabled={saving} onClick={() => setConfirming(false)}>
                            Cancel
                        </button>
                    </div>
                </div>
            ) : user.isAdmin ? (
                <button
                    type="button"
                    className="admin-link-button"
                    aria-label={`Remove admin from ${user.email}`}
                    disabled={saving}
                    onClick={() => { setError(null); setConfirming(true); }}
                >
                    Remove admin
                </button>
            ) : (
                <button
                    type="button"
                    className="admin-link-button"
                    aria-label={`Make ${user.email} an admin`}
                    disabled={saving}
                    onClick={() => change(true)}
                >
                    Make admin
                </button>
            )}
            {error && <div className="admin-error" role="alert">{error}</div>}
        </div>
    );
}

// The "Actions" button of a row and the choices it opens: set password, permanent, delete.
// You can't set your own password or delete your own account here (Settings does that).
function RowActions({ user, isSelf, onChoose }) {
    const [open, setOpen] = useState(false);
    const name = `${user.firstName} ${user.lastName}`.trim();

    const choose = (action) => {
        setOpen(false);
        onChoose(action, user);
    };

    return (
        <div className="admin-actions">
            <button
                type="button"
                className="admin-link-button"
                aria-haspopup="menu"
                aria-expanded={open}
                aria-label={`Actions for ${user.email}`}
                onClick={() => setOpen(!open)}
                onKeyDown={(event) => {
                    if (event.key === "Escape") setOpen(false);
                }}
            >
                Actions
            </button>
            {open && (
                <div
                    className="admin-menu"
                    role="menu"
                    aria-label={`Actions for ${name || user.email}`}
                    onKeyDown={(event) => {
                        if (event.key === "Escape") setOpen(false);
                    }}
                >
                    {!isSelf && (
                        <button type="button" role="menuitem" onClick={() => choose("password")}>Set password</button>
                    )}
                    <button type="button" role="menuitem" onClick={() => choose("permanent")}>
                        {user.isPermanent ? "Remove permanent" : "Make permanent"}
                    </button>
                    {!isSelf && (
                        <button type="button" role="menuitem" className="danger" onClick={() => choose("delete")}>
                            Delete account
                        </button>
                    )}
                </div>
            )}
        </div>
    );
}

// For the server owner: every account with its usage and quota, and totals for the server.
// The API only answers administrators (checked on every request).
export default function Admin() {
    const [data, setData] = useState(null); // { users, stats }
    const [error, setError] = useState(null);
    const [version, setVersion] = useState(0); // bumped to reload (also resets the rows' admin-rights messages)
    const [dialog, setDialog] = useState(null); // { type: "password" | "delete", user }
    const [creating, setCreating] = useState(false);
    const [notice, setNotice] = useState(null); // what the last action did
    const [actionError, setActionError] = useState(null); // why the last row action failed

    useEffect(() => {
        let cancelled = false;
        Promise.all([getAdminUsersAPI(), getAdminStatsAPI()])
            .then(([users, stats]) => {
                if (cancelled) return;
                setData({ users, stats });
                setError(null);
            })
            .catch((err) => {
                if (cancelled) return;
                setError(err?.response?.status === 403
                    ? "Only administrators can see this page."
                    : getErrorMessage(err, "Could not load the admin page"));
            });
        return () => {
            cancelled = true;
        };
    }, [version]);

    const saveQuota = useCallback(async (user, quotaBytes) => {
        await setUserQuotaAPI(user.id, quotaBytes);
        setVersion((v) => v + 1);
    }, []);

    const changeAdmin = useCallback(async (user, isAdmin) => {
        await setUserAdminAPI(user.id, isAdmin);
        setVersion((v) => v + 1);
    }, []);

    const reload = () => setVersion((v) => v + 1);

    const chooseAction = async (action, user) => {
        setNotice(null);
        setActionError(null);
        if (action !== "permanent") {
            setDialog({ type: action, user });
            return;
        }
        try {
            await setUserPermanentAPI(user.id, !user.isPermanent);
            setNotice(`${user.email} is ${user.isPermanent ? "no longer" : "now"} a permanent account.`);
            reload();
        } catch (err) {
            setActionError(getErrorMessage(err, "Could not change the permanent setting"));
        }
    };

    // These throw on failure; their dialog shows the message
    const savePassword = async (user, password) => {
        await setUserPasswordAPI(user.id, password);
        setDialog(null);
        setNotice(`Password set for ${user.email}. They have been signed out everywhere.`);
    };

    const deleteAccount = async (user) => {
        await deleteUserAPI(user.id);
        setDialog(null);
        setNotice(`The account for ${user.email} was deleted.`);
        reload();
    };

    const createAccount = async (details) => {
        await createUserAPI(details);
        setCreating(false);
        setActionError(null);
        setNotice(`Account created for ${details.email}. They can sign in now.`);
        reload();
    };

    if (error && !data) {
        return (
            <div className="admin-page">
                <h1 className="admin-title">Admin</h1>
                <div className="admin-card admin-error" role="alert">{error}</div>
            </div>
        );
    }

    if (!data) return <div className="admin-page"><p className="admin-loading">Loading…</p></div>;

    const { users, stats } = data;
    const diskUsed = stats.diskTotalBytes != null && stats.diskFreeBytes != null ? stats.diskTotalBytes - stats.diskFreeBytes : null;
    const tiles = [
        ["Users", stats.userCount, `${stats.adminCount} admin${stats.adminCount === 1 ? "" : "s"}`],
        ["Files", stats.fileCount.toLocaleString(), `${stats.folderCount.toLocaleString()} folders`],
        ["Stored", formatBytes(stats.totalStoredBytes), stats.storageBytesOnDisk != null ? `${formatBytes(stats.storageBytesOnDisk)} on disk, encrypted` : "Size of all files"],
        ["Disk", diskUsed != null ? `${formatBytes(stats.diskFreeBytes)} free` : "Unknown",
            diskUsed != null ? `${formatBytes(diskUsed)} of ${formatBytes(stats.diskTotalBytes)} used` : "Couldn't read the disk"],
    ];

    return (
        <div className="admin-page">
            <h1 className="admin-title">Admin</h1>

            <section className="admin-stats" aria-label="Server totals">
                {tiles.map(([label, value, detail]) => (
                    <div key={label} className="admin-card admin-stat">
                        <div className="admin-stat-label">{label}</div>
                        <div className="admin-stat-value">{value}</div>
                        <div className="admin-muted">{detail}</div>
                    </div>
                ))}
            </section>

            <section className="admin-card" aria-labelledby="admin-users-heading">
                <div className="admin-users-header">
                    <h2 id="admin-users-heading">Users</h2>
                    {!creating && (
                        <button type="button" className="admin-button" onClick={() => { setNotice(null); setCreating(true); }}>
                            Create account
                        </button>
                    )}
                </div>
                <p className="admin-muted">
                    New accounts get {formatBytes(stats.defaultQuotaBytes)} unless you change their quota.
                </p>
                {creating && <CreateAccountForm onCreate={createAccount} onCancel={() => setCreating(false)} />}
                {notice && <div className="admin-notice" role="status">{notice}</div>}
                {actionError && <div className="admin-error" role="alert">{actionError}</div>}
                {error && <div className="admin-error" role="alert">{error}</div>}
                <div className="admin-table-scroll">
                    <table className="admin-table">
                        <thead>
                            <tr>
                                <th scope="col">Name</th>
                                <th scope="col">Created</th>
                                <th scope="col">Last login</th>
                                <th scope="col" className="numeric">Files</th>
                                <th scope="col">Storage used</th>
                                <th scope="col">Quota</th>
                                <th scope="col">Admin rights</th>
                            </tr>
                        </thead>
                        <tbody>
                            {users.map((user) => {
                                const percent = user.quota > 0 ? Math.min(100, (user.bytesUsed / user.quota) * 100) : 100;
                                return (
                                    <tr key={user.id}>
                                        <td>
                                            <div className="admin-person">
                                                <UserAvatar
                                                    userId={user.id}
                                                    name={`${user.firstName} ${user.lastName}`.trim() || user.email}
                                                    version={user.avatarUpdatedAt}
                                                />
                                                <div className="admin-person-text">
                                                    <div className="admin-name">
                                                        {`${user.firstName} ${user.lastName}`.trim()}
                                                        {user.isAdmin && <span className="admin-badge">Admin</span>}
                                                        {user.isPermanent && <span className="admin-badge permanent">Permanent</span>}
                                                    </div>
                                                    <div className="admin-muted admin-email">{user.email}</div>
                                                    <RowActions user={user} isSelf={user.id === stats.currentUserId} onChoose={chooseAction} />
                                                </div>
                                            </div>
                                        </td>
                                        <td>{formatWhen(user.createdAt)}</td>
                                        <td>{formatWhen(user.lastLogin)}</td>
                                        <td className="numeric">{user.fileCount.toLocaleString()}</td>
                                        <td>
                                            <div>{formatBytes(user.bytesUsed)}</div>
                                            <div
                                                className={`admin-usage ${percent >= 90 ? "full" : ""}`}
                                                role="progressbar"
                                                aria-label={`Storage used by ${user.email}`}
                                                aria-valuenow={Math.round(percent)}
                                                aria-valuemin={0}
                                                aria-valuemax={100}
                                            >
                                                <div style={{ width: `${percent}%` }} />
                                            </div>
                                        </td>
                                        <td><QuotaCell user={user} onSave={saveQuota} /></td>
                                        <td><AdminCell key={`${user.id}:${version}`} user={user} onChange={changeAdmin} /></td>
                                    </tr>
                                );
                            })}
                        </tbody>
                    </table>
                </div>
            </section>

            {dialog?.type === "password" && (
                <SetPasswordDialog user={dialog.user} onSave={savePassword} onClose={() => setDialog(null)} />
            )}
            {dialog?.type === "delete" && (
                <DeleteAccountDialog user={dialog.user} onDelete={deleteAccount} onClose={() => setDialog(null)} />
            )}
        </div>
    );
}
