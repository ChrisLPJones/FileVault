import React from "react";
import ReactDOM from "react-dom/client";
import "./styles/theme.css";
import Layout from "./pages/Layout/Layout";
import Login from "./pages/Login/Login";
import Register from "./pages/Register/Register";
import Dashboard from "./pages/Dashboard/Dashboard";
import Settings from "./pages/Settings/Settings";
import Admin from "./pages/Admin/Admin";
import ErrorPage from "./pages/Error/Errorpage";
import ProtectedRoute from "./components/ProtectedRoute";
import { createBrowserRouter, RouterProvider } from "react-router-dom";
import PublicRoute from "./components/PublicRoute";


const router = createBrowserRouter([
    {
        path: "/",
        element: <Layout />,
        errorElement: <ErrorPage />,
        children: [
            {
                index: true,
                element: (
                    <PublicRoute>
                        <Login />
                    </PublicRoute>
                ),
            },
            {
                path: "login",
                element: (
                    <PublicRoute>
                        <Login />
                    </PublicRoute>
                ),
            },
            {
                path: "register",
                element: (
                    <PublicRoute>
                        <Register />
                    </PublicRoute>
                ),
            },
            {
                path: "dashboard",
                element: (
                    <ProtectedRoute>
                        <Dashboard />
                    </ProtectedRoute>
                ),
            },
            {
                path: "settings",
                element: (
                    <ProtectedRoute>
                        <Settings />
                    </ProtectedRoute>
                ),
            },
            {
                path: "admin",
                element: (
                    <ProtectedRoute>
                        <Admin />
                    </ProtectedRoute>
                ),
            },
        ],
    },
]);

ReactDOM.createRoot(document.getElementById("root")).render(
    <React.StrictMode>
        <RouterProvider router={router} />
    </React.StrictMode>
);
