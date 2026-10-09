import React, { useEffect, useState } from "react";
import { FiArrowLeft } from "react-icons/fi";
import { Outlet, NavLink, useLocation } from "react-router-dom";
import "./Layout.css";
import { isAuthenticated, LAST_FILES_KEY } from "../../utils/auth";
import { HeaderSlotContext } from "../../contexts/HeaderSlotContext";

// The file list's address (it holds the open folder) is kept so "Back to files" returns to that folder

const readLastFilesUrl = () => {
    try {
        return sessionStorage.getItem(LAST_FILES_KEY) || "/dashboard";
    } catch {
        return "/dashboard";
    }
};

const Layout = () => {
    // Re-render on navigation so the bar follows the login state
    const { pathname: rawPathname, search } = useLocation();
    // "/login/" is the same page as "/login"
    const pathname = rawPathname.replace(/\/+$/, "") || "/";
    const loggedIn = isAuthenticated();

    useEffect(() => {
        if (pathname !== "/dashboard") return;
        try {
            sessionStorage.setItem(LAST_FILES_KEY, `/dashboard${search}`);
        } catch {
            // Storage unavailable: Back to files opens the home folder
        }
    }, [pathname, search]);
    // Only logged-in users get the top bar, with no account menu: the file list has it at the
    // bottom of the folder tree and other pages reach it from there. Logged-out pages (login,
    // register, reset password, share links...) need nothing from the bar
    // Middle of the top bar; the dashboard renders the file toolbar into it
    const [headerSlot, setHeaderSlot] = useState(null);

    return (
        // Pages size themselves by --fv-header-height, so without the bar it is zero
        <div className="page" style={loggedIn ? undefined : { "--fv-header-height": "0px" }}>
            {loggedIn && (
                <header className="header-style">
                    <nav className="nav-container">
                        <div className="nav-left">
                            {/* Pages other than the file list (e.g. Settings) get a way back */}
                            {pathname !== "/dashboard" && (
                                <NavLink to={readLastFilesUrl()} className="link-style nav-back">
                                    <FiArrowLeft aria-hidden="true" />
                                    Back to files
                                </NavLink>
                            )}
                        </div>

                        <div className="nav-center" ref={setHeaderSlot} />
                    </nav>
                </header>
            )}
            <main className="main-style">
                <HeaderSlotContext.Provider value={headerSlot}>
                    <Outlet />
                </HeaderSlotContext.Provider>
            </main>
        </div>
    );
};

export default Layout;
