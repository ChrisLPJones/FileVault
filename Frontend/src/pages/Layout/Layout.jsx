import React from "react";
import { Outlet, Link } from "react-router-dom";
import "./Layout.css"
const Layout = () => {
    return (
        <div>
            <header className="header-style">
                <nav>
                    <Link to="/login" className="link-style">Login</Link>
                    <Link to="/register" className="link-style">Register</Link>
                </nav>
            </header>
            <main className="main-style">
                <Outlet />
            </main>
        </div>
    );
};

export default Layout;
