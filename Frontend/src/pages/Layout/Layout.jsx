import React from "react";
import { Outlet, Link } from "react-router-dom";
import "./Layout.css";
const Layout = () => {
    function logout() {
        localStorage.removeItem("token");
    }
    return (
        <div className="page">
            <header className="header-style">
                <nav className="nav-container">
                    <div className="nav-left">
                        <Link to="/register" className="link-style">
                            Register
                        </Link>
                        <Link to="/login" className="link-style">
                            Login
                        </Link>
                        {/* <Link to="/dashboard" className="link-style">Dashboard</Link> */}
                    </div>

                    <Link to="/login" className="link-style" onClick={logout}>
                        Logout
                    </Link>
                </nav>
            </header>
            <main className="main-style">
                <Outlet />
            </main>
        </div>
    );
};

export default Layout;
