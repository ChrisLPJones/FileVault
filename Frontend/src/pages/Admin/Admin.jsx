import { useCallback, useEffect, useState } from "react";
import { getErrorMessage } from "../../api/api";
import { getAdminStatsAPI, getAdminUsersAPI, setUserAdminAPI, setUserQuotaAPI } from "../../api/adminAPI";
import { formatBytes } from "../../utils/formatBytes";
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

// For the server owner: every account with its usage and quota, and totals for the server.
// The API only answers administrators (checked on every request).
export default function Admin() {
    const [data, setData] = useState(null); // { users, stats }
    const [error, setError] = useState(null);
    const [version, setVersion] = useState(0); // bumped to reload (also resets the rows' admin-rights messages)

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
                <h2 id="admin-users-heading">Users</h2>
                <p className="admin-muted">
                    New accounts get {formatBytes(stats.defaultQuotaBytes)} unless you change their quota.
                </p>
                {error && <div className="admin-error" role="alert">{error}</div>}
                <div className="admin-table-scroll">
                    <table className="admin-table">
                        <thead>
                            <tr>
                                <th scope="col">Name</th>
                                <th scope="col">Created</th>
                                <th scope="col">Last login</th>
                                <th scope="col">Login location</th>
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
                                            <div className="admin-name">
                                                {`${user.firstName} ${user.lastName}`.trim()}
                                                {user.isAdmin && <span className="admin-badge">Admin</span>}
                                            </div>
                                            <div className="admin-muted">{user.email}</div>
                                        </td>
                                        <td>{formatWhen(user.createdAt)}</td>
                                        <td>{formatWhen(user.lastLogin)}</td>
                                        <td>
                                            {user.lastLoginIp ? (
                                                <>
                                                    <div>{user.lastLoginIp}</div>
                                                    <div className="admin-muted">{user.lastLoginCountry || "Unknown"}</div>
                                                </>
                                            ) : (
                                                <span className="admin-muted">Unknown</span>
                                            )}
                                        </td>
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
                <p className="admin-muted admin-footnote">
                    Login countries use GeoLite2 data created by MaxMind, available from{" "}
                    <a href="https://www.maxmind.com" target="_blank" rel="noopener noreferrer">maxmind.com</a>.
                </p>
            </section>
        </div>
    );
}
