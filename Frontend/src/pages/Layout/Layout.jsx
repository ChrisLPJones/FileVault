import React from "react";
import { Outlet, NavLink, useLocation, useNavigate } from "react-router-dom";
import "./Layout.css";
import { logout } from "../../api/api";
import { isAuthenticated } from "../../utils/auth";
import ThemeToggle from "../../components/ThemeToggle";

const navClass = ({ isActive }) => `link-style${isActive ? " active" : ""}`;

const Layout = () => {
    // Re-render on navigation so the links follow the login state
    useLocation();
    const navigate = useNavigate();
    const loggedIn = isAuthenticated();

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
                            <>
                                <NavLink to="/dashboard" className={navClass}>
                                    Files
                                </NavLink>
                                <NavLink to="/settings" className={navClass}>
                                    Settings
                                </NavLink>
                            </>
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

                    <div className="nav-right">
                        <ThemeToggle />
                        {loggedIn && (
                            <button type="button" className="link-style logout-button" onClick={handleLogout}>
                                Logout
                            </button>
                        )}
                    </div>
                </nav>
            </header>
            <main className="main-style">
                <Outlet />
            </main>
        </div>
    );
};

export default Layout;
