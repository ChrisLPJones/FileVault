import { useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Link, useLocation, useNavigate } from "react-router-dom";
import { FiLink, FiLogOut, FiSettings } from "react-icons/fi";
import { logout } from "../api/api";
import Avatar from "./Avatar";
import { useUserProfile } from "../hooks/useUserProfile";
import "./UserMenu.css";

// Button that opens a menu with the account's name, Settings and Log out.
// `children` is the button's content (default: the avatar). The menu is drawn in a portal so it
// isn't clipped by the folder tree; it opens upwards from a button low on the screen and
// downwards (right-aligned) from one near the top, like the header.
export default function UserMenu({ children, className = "", title }) {
    const [open, setOpen] = useState(false);
    const user = useUserProfile();
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

    // Where the menu goes, next to the button
    const place = () => {
        const rect = buttonRef.current?.getBoundingClientRect();
        if (!rect) return;
        setPosition(
            rect.top < window.innerHeight / 2
                ? { top: rect.bottom + 8, right: window.innerWidth - rect.right }
                : { left: rect.left, bottom: window.innerHeight - rect.top + 8 }
        );
    };

    const toggle = () => {
        if (!open) place();
        setOpen((value) => !value);
    };

    // Stay attached to the button if the window is resized or the button moves (e.g. the
    // folder tree collapsing) while the menu is open
    useEffect(() => {
        if (!open) return undefined;
        window.addEventListener("resize", place);
        const observer = new ResizeObserver(place);
        if (buttonRef.current) observer.observe(buttonRef.current);
        return () => {
            window.removeEventListener("resize", place);
            observer.disconnect();
        };
    }, [open]);

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
                    style={position}
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
                    <Link
                        to="/shared-links"
                        role="menuitem"
                        className={`user-menu-item ${pathname === "/shared-links" ? "current" : ""}`}
                        onClick={close}
                    >
                        <FiLink aria-hidden="true" />
                        Shared links
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
