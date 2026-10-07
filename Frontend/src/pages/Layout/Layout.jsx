import React, { useState } from "react";
import { Outlet, NavLink, useLocation, useNavigate } from "react-router-dom";
import "./Layout.css";
import { logout } from "../../api/api";
import { isAuthenticated } from "../../utils/auth";
import UserMenu from "../../components/UserMenu";
import { HeaderSlotContext } from "../../contexts/HeaderSlotContext";

const navClass = ({ isActive }) => `link-style${isActive ? " active" : ""}`;

const Layout = () => {
    // Re-render on navigation so the links follow the login state
    useLocation();
    const navigate = useNavigate();
    const loggedIn = isAuthenticated();
    // Middle of the top bar; the dashboard renders the file toolbar into it
    const [headerSlot, setHeaderSlot] = useState(null);

    const handleLogout = () => {
        logout();
        navigate("/login", { replace: true });
    };

    return (
        <div className="page">
            <header className="header-style">
                <nav className="nav-container">
                    <div className="nav-left">
                        {loggedIn ? (
                            <NavLink to="/dashboard" className={navClass}>
                                Files
                            </NavLink>
                        ) : (
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

                    <div className="nav-right">
                        {loggedIn && <UserMenu onLogout={handleLogout} />}
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
