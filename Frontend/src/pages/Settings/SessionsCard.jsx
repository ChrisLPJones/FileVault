import { useEffect, useState } from "react";
import { MdComputer, MdPhoneIphone } from "react-icons/md";
import { getErrorMessage } from "../../api/api";
import { getSessionsAPI, revokeOtherSessionsAPI, revokeSessionAPI } from "../../api/securityAPI";
import SettingsStatus from "./SettingsStatus";
import "./SecurityCards.css";

const MOBILE = /iPhone|iPad|Android/;

// "just now", "5 minutes ago", "3 days ago"
const relativeTime = (iso) => {
    const seconds = Math.round((new Date(iso).getTime() - Date.now()) / 1000);
    if (Math.abs(seconds) < 60) return "just now";
    const format = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });
    for (const [unit, size] of [["day", 86400], ["hour", 3600], ["minute", 60]]) {
        if (Math.abs(seconds) >= size) return format.format(Math.round(seconds / size), unit);
    }
    return "just now";
};

const fullDate = (iso) => new Date(iso).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });

// "Where you're logged in" card in Settings: every device with a session, and signing them out
function SessionsCard() {
    const [sessions, setSessions] = useState(null);
    const [busyId, setBusyId] = useState(null); // a session ID, or "others"
    const [status, setStatus] = useState(null);

    const reload = async () => setSessions(await getSessionsAPI());

    useEffect(() => {
        let cancelled = false;
        getSessionsAPI()
            .then((data) => !cancelled && setSessions(data))
            .catch((err) => !cancelled &&
                setStatus({ type: "danger", message: getErrorMessage(err, "Could not load your sessions") }));
        return () => {
            cancelled = true;
        };
    }, []);

    const signOut = async (id) => {
        setStatus(null);
        setBusyId(id);
        try {
            if (id === "others") {
                await revokeOtherSessionsAPI();
                setStatus({ type: "success", message: "Signed out of all other sessions" });
            } else {
                await revokeSessionAPI(id);
                setStatus({ type: "success", message: "Signed out. That device will be logged out within 15 minutes." });
            }
            await reload();
        } catch (err) {
            setStatus({ type: "danger", message: getErrorMessage(err, "Could not sign out") });
        } finally {
            setBusyId(null);
        }
    };

    const others = sessions?.filter((s) => !s.isCurrent) ?? [];

    return (
        <section className="settings-card" aria-labelledby="sessions-heading">
            <h2 id="sessions-heading">Where you're logged in</h2>

            {!sessions && !status && <p className="settings-hint">Loading…</p>}

            {sessions && (
                <ul className="session-list">
                    {sessions.map((session) => {
                        const Icon = MOBILE.test(session.device) ? MdPhoneIphone : MdComputer;
                        return (
                            <li key={session.id} className="session-item">
                                <Icon className="session-icon" size={28} aria-hidden="true" />
                                <div className="session-details">
                                    <div className="session-device">
                                        {session.device}
                                        {session.isCurrent && <span className="security-badge on">This device</span>}
                                    </div>
                                    <div className="session-meta">
                                        {session.ipAddress && <>{session.ipAddress} · </>}
                                        <span title={fullDate(session.lastActiveAt)}>
                                            Last active {relativeTime(session.lastActiveAt)}
                                        </span>
                                        {" · "}
                                        <span title={fullDate(session.createdAt)}>
                                            Signed in {relativeTime(session.createdAt)}
                                        </span>
                                    </div>
                                </div>
                                {!session.isCurrent && (
                                    <button
                                        type="button"
                                        className="settings-button secondary"
                                        disabled={busyId !== null}
                                        onClick={() => signOut(session.id)}
                                        aria-label={`Sign out ${session.device}`}
                                    >
                                        {busyId === session.id ? "Signing out…" : "Sign out"}
                                    </button>
                                )}
                            </li>
                        );
                    })}
                </ul>
            )}

            {others.length > 0 && (
                <button
                    type="button"
                    className="settings-button danger"
                    disabled={busyId !== null}
                    onClick={() => signOut("others")}
                >
                    {busyId === "others" ? "Signing out…" : "Sign out of all other sessions"}
                </button>
            )}
            {sessions && others.length === 0 && (
                <p className="settings-hint">You're not logged in anywhere else.</p>
            )}

            <SettingsStatus status={status} />
        </section>
    );
}

export default SessionsCard;
