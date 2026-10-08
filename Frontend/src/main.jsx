import React from "react";
import ReactDOM from "react-dom/client";
import "./styles/theme.css";
import Layout from "./pages/Layout/Layout";
import Login from "./pages/Login/Login";
import Register from "./pages/Register/Register";
import Dashboard from "./pages/Dashboard/Dashboard";
import Settings from "./pages/Settings/Settings";
import ErrorPage from "./pages/Error/Errorpage";
import SharePage from "./pages/Share/SharePage";
import ForgotPassword from "./pages/ForgotPassword/ForgotPassword";
import ResetPassword from "./pages/ResetPassword/ResetPassword";
import VerifyEmail from "./pages/VerifyEmail/VerifyEmail";
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
                path: "forgot-password",
                element: (
                    <PublicRoute>
                        <ForgotPassword />
                    </PublicRoute>
                ),
            },
            {
                // Links from emails work whether or not the visitor is logged in
                path: "reset-password",
                element: <ResetPassword />,
            },
            {
                path: "verify-email",
                element: <VerifyEmail />,
            },
            {
                // Share links work whether or not the visitor is logged in
                path: "s/:token",
                element: <SharePage />,
            },
            {
                path: "settings",
                element: (
                    <ProtectedRoute>
                        <Settings />
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
