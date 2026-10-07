import { useEffect, useRef, useState } from "react";
import { Link, useLocation } from "react-router-dom";
import { FiFolder, FiLogOut, FiSettings } from "react-icons/fi";
import { PROFILE_CHANGED_EVENT, getUserInfoAPI } from "../api/accountAPI";
import Avatar from "./Avatar";
import "./UserMenu.css";

// Avatar button in the header that opens a menu with the account's name, Files, Settings and Log out
export default function UserMenu({ onLogout }) {
    const [open, setOpen] = useState(false);
    const [user, setUser] = useState({ username: "", email: "" });
    const containerRef = useRef(null);
    const buttonRef = useRef(null);
    const { pathname } = useLocation();

    // Name and email for the menu header; refreshed when the profile changes
    useEffect(() => {
        let cancelled = false;
        const load = () =>
            getUserInfoAPI()
                .then((info) => !cancelled && setUser({ username: info.username, email: info.email }))
                .catch(() => {});

        load();
        window.addEventListener(PROFILE_CHANGED_EVENT, load);
        return () => {
            cancelled = true;
            window.removeEventListener(PROFILE_CHANGED_EVENT, load);
        };
    }, []);

    // Close on a click outside the menu or on Escape (returning focus to the button)
    useEffect(() => {
        if (!open) return;

        const onPointerDown = (event) => {
            if (!containerRef.current?.contains(event.target)) setOpen(false);
        };
        const onKeyDown = (event) => {
            if (event.key === "Escape") {
                setOpen(false);
                buttonRef.current?.focus();
            }
        };

        document.addEventListener("pointerdown", onPointerDown);
        document.addEventListener("keydown", onKeyDown);
        return () => {
            document.removeEventListener("pointerdown", onPointerDown);
            document.removeEventListener("keydown", onKeyDown);
        };
    }, [open]);

    // Move focus into the menu when it opens, so keyboard users can act on it straight away
    useEffect(() => {
        if (open) containerRef.current?.querySelector('[role="menuitem"]')?.focus();
    }, [open]);

    // Arrow keys move between the menu items
    const onMenuKeyDown = (event) => {
        if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
        event.preventDefault();
        const items = [...containerRef.current.querySelectorAll('[role="menuitem"]')];
        const index = items.indexOf(document.activeElement);
        const next = event.key === "ArrowDown" ? index + 1 : index - 1;
        items[(next + items.length) % items.length]?.focus();
    };

    const close = () => setOpen(false);

    return (
        <div className="user-menu" ref={containerRef}>
            <button
                ref={buttonRef}
                type="button"
                className="user-menu-button"
                aria-haspopup="menu"
                aria-expanded={open}
                aria-label={`Account menu${user.username ? ` for ${user.username}` : ""}`}
                onClick={() => setOpen((value) => !value)}
            >
                <Avatar size={32} />
            </button>

            {open && (
                <div className="user-menu-dropdown" role="menu" aria-label="Account" onKeyDown={onMenuKeyDown}>
                    <div className="user-menu-identity">
                        <Avatar size={40} />
                        <div className="user-menu-names">
                            <span className="user-menu-username">{user.username}</span>
                            <span className="user-menu-email">{user.email}</span>
                        </div>
                    </div>

                    <div className="user-menu-separator" role="separator" />

                    <Link
                        to="/dashboard"
                        role="menuitem"
                        className={`user-menu-item ${pathname === "/dashboard" ? "current" : ""}`}
                        onClick={close}
                    >
                        <FiFolder aria-hidden="true" />
                        Files
                    </Link>
                    <Link
                        to="/settings"
                        role="menuitem"
                        className={`user-menu-item ${pathname === "/settings" ? "current" : ""}`}
                        onClick={close}
                    >
                        <FiSettings aria-hidden="true" />
                        Settings
                    </Link>
                    <button
                        type="button"
                        role="menuitem"
                        className="user-menu-item"
                        onClick={() => {
                            close();
                            onLogout();
                        }}
                    >
                        <FiLogOut aria-hidden="true" />
                        Log out
                    </button>
                </div>
            )}
        </div>
    );
}
