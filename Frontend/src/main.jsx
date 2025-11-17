import React from "react";
import ReactDOM from "react-dom/client";
import "bootstrap/dist/css/bootstrap.min.css";

import ErrorPage from "./pages/Error/Error-page";
import Login from "./pages/Login/Login";
import Register from "./pages/Register/Register";

import { createBrowserRouter, RouterProvider } from "react-router-dom";

const router = createBrowserRouter([
    { path: "/", element: <div>Hello world!</div>, errorElement: <ErrorPage /> },

    { path: "/login", element: <Login /> },

    { path: "/register", element: <Register /> },
]);

ReactDOM.createRoot(document.getElementById("root")).render(
    <React.StrictMode>
        <RouterProvider router={router} />
    </React.StrictMode>
);
