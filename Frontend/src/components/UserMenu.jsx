import { useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Link, useLocation, useNavigate } from "react-router-dom";
import { FiLogOut, FiSettings } from "react-icons/fi";
import { PROFILE_CHANGED_EVENT, getUserInfoAPI } from "../api/accountAPI";
import { logout } from "../api/api";
import Avatar from "./Avatar";
import "./UserMenu.css";

// Button that opens a menu with the account's name, Settings and Log out.
// `children` is the button's content (default: the avatar). The menu is drawn in a portal
// opening upwards from the button, so it isn't clipped by the folder tree it sits in.
export default function UserMenu({ children, className = "", title }) {
    const [open, setOpen] = useState(false);
    const [user, setUser] = useState({ name: "", email: "" });
    const containerRef = useRef(null);
    const buttonRef = useRef(null);
    const menuRef = useRef(null);
    const [position, setPosition] = useState(null);
    const { pathname } = useLocation();
    const navigate = useNavigate();

    const handleLogout = () => {
        logout();
        navigate("/login", { replace: true });
    };

    const toggle = () => {
        if (!open && buttonRef.current) {
            const rect = buttonRef.current.getBoundingClientRect();
            setPosition({ left: rect.left, bottom: window.innerHeight - rect.top + 8 });
        }
        setOpen((value) => !value);
    };

    // Name and email for the menu header; refreshed when the profile changes
    useEffect(() => {
        let cancelled = false;
        const load = () =>
            getUserInfoAPI()
                .then((info) => !cancelled && setUser({ name: `${info.firstName} ${info.lastName}`.trim(), email: info.email }))
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
            if (
                !containerRef.current?.contains(event.target) &&
                !menuRef.current?.contains(event.target)
            ) setOpen(false);
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
        if (open) menuRef.current?.querySelector('[role="menuitem"]')?.focus();
    }, [open]);

    // Arrow keys move between the menu items
    const onMenuKeyDown = (event) => {
        if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
        event.preventDefault();
        const items = [...menuRef.current.querySelectorAll('[role="menuitem"]')];
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
                className={`user-menu-button ${className}`}
                title={title}
                aria-haspopup="menu"
                aria-expanded={open}
                aria-label={`Account menu${user.name ? ` for ${user.name}` : ""}`}
                onClick={toggle}
            >
                {children ?? <Avatar size={32} />}
            </button>

            {open && position && createPortal(
                <div
                    ref={menuRef}
                    className="user-menu-dropdown"
                    style={{ left: position.left, bottom: position.bottom }}
                    role="menu"
                    aria-label="Account"
                    onKeyDown={onMenuKeyDown}
                >
                    <div className="user-menu-identity">
                        <Avatar size={40} />
                        <div className="user-menu-names">
                            <span className="user-menu-username">{user.name}</span>
                            <span className="user-menu-email">{user.email}</span>
                        </div>
                    </div>

                    <div className="user-menu-separator" role="separator" />

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
                            handleLogout();
                        }}
                    >
                        <FiLogOut aria-hidden="true" />
                        Log out
                    </button>
                </div>,
                document.body
            )}
        </div>
    );
}
