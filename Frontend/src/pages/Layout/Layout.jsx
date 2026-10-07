import React from "react";
import { Outlet, NavLink, useLocation, useNavigate } from "react-router-dom";
import "./Layout.css";
import { logout } from "../../api/api";
import { isAuthenticated } from "../../utils/auth";
import UserMenu from "../../components/UserMenu";

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

                    {loggedIn && (
                        <div className="nav-right">
                            <UserMenu onLogout={handleLogout} />
                        </div>
                    )}
                </nav>
            </header>
            <main className="main-style">
                <Outlet />
            </main>
        </div>
    );
};

export default Layout;
