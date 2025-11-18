import React from "react";
import ReactDOM from "react-dom/client";
import "bootstrap/dist/css/bootstrap.min.css";

import ErrorPage from "./pages/Error/Errorpage";
import Login from "./pages/Login/Login";
import Register from "./pages/Register/Register";
import Layout from "./pages/Layout/Layout";

import { createBrowserRouter, RouterProvider } from "react-router-dom";

const router = createBrowserRouter([
    {
    path: "/",
    element: <Layout />,
    errorElement: <ErrorPage />,
    children: [
      { index: true, element: <Login />}, // default for "/"
      { path: "login", element: <Login /> },
      { path: "register", element: <Register /> },
    ],
  },
]);``

ReactDOM.createRoot(document.getElementById("root")).render(
    <React.StrictMode>
        <RouterProvider router={router} />
    </React.StrictMode>
);
