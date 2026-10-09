import React, { useState } from "react";
import { FiArrowLeft } from "react-icons/fi";
import { Outlet, NavLink, useLocation } from "react-router-dom";
import "./Layout.css";
import { isAuthenticated } from "../../utils/auth";
import { HeaderSlotContext } from "../../contexts/HeaderSlotContext";
import UserMenu from "../../components/UserMenu";

const navClass = ({ isActive }) => `link-style${isActive ? " active" : ""}`;

const Layout = () => {
    // Re-render on navigation so the links follow the login state
    const { pathname } = useLocation();
    const loggedIn = isAuthenticated();
    // The login and register pages already link to each other, so the bar stays empty there
    const onAuthPage = pathname === "/" || pathname === "/login" || pathname === "/register";
    // Middle of the top bar; the dashboard renders the file toolbar into it
    const [headerSlot, setHeaderSlot] = useState(null);

    return (
        <div className="page">
            <header className="header-style">
                <nav className="nav-container">
                    <div className="nav-left">
                        {/* Pages other than the file list (e.g. Settings) get a way back */}
                        {loggedIn && pathname !== "/dashboard" && (
                            <NavLink to="/dashboard" className="link-style nav-back">
                                <FiArrowLeft aria-hidden="true" />
                                Back to files
                            </NavLink>
                        )}
                        {!loggedIn && !onAuthPage && (
                            <>
                                <NavLink to="/register" className={navClass}>
                                    Register
                                </NavLink>
                                <NavLink to="/login" className={navClass}>
                                    Login
                                </NavLink>
                            </>
                        )}
                    </div>

                    <div className="nav-center" ref={setHeaderSlot} />

                    {/* On the file list the account menu is at the bottom of the folder tree;
                        other pages (e.g. Settings) show it here so Log out is always reachable */}
                    <div className="nav-right">
                        {loggedIn && pathname !== "/dashboard" && <UserMenu />}
                    </div>
                </nav>
            </header>
            <main className="main-style">
                <HeaderSlotContext.Provider value={headerSlot}>
                    <Outlet />
                </HeaderSlotContext.Provider>
            </main>
        </div>
    );
};

export default Layout;
