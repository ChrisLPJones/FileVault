import { useEffect, useRef, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { getErrorMessage } from "../../api/api";
import { verifyEmailAPI } from "../../api/accountEmailAPI";
import { isAuthenticated } from "../../utils/auth";
import "../Auth/Auth.css";

// Opened from the verification email (/verify-email?token=...): confirms the address straight away
function VerifyEmail() {
    const [params] = useSearchParams();
    const token = params.get("token") ?? "";
    const [result, setResult] = useState(token ? null : { ok: false, message: "This link is incomplete. Open the link from the email again." });
    const started = useRef(false);

    useEffect(() => {
        // Tokens work once: don't send it twice (React runs effects twice in development)
        if (!token || started.current) return;
        started.current = true;

        verifyEmailAPI(token)
            .then((data) => setResult({ ok: true, message: "Thanks, your email address is confirmed.", choosePassword: data?.passwordReset === true }))
            .catch((err) => setResult({ ok: false, message: getErrorMessage(err, "Could not confirm your email address") }));
    }, [token]);

    const loggedIn = isAuthenticated();

    return (
        <div className="auth-page">
            <div className="auth-card">
                <h1 className="auth-title">Confirm email</h1>
                {!result && <p className="auth-subtitle">Confirming your email address…</p>}
                {result && (
                    <>
                        <div className={`auth-alert auth-alert-top ${result.ok ? "success" : "danger"}`} role={result.ok ? "status" : "alert"}>
                            {result.message}
                        </div>
                        {result.choosePassword && (
                            <p className="auth-subtitle">
                                This is the first administrator account. As anyone could have signed up with this
                                address, its password has been cleared: <Link to="/forgot-password">choose your own</Link> to
                                log in.
                            </p>
                        )}
                        {!result.ok && (
                            <p className="auth-subtitle">
                                {loggedIn
                                    ? "You can send a new link from the banner in your files."
                                    : "Try logging in: if your address still needs confirming, you can ask for a new link there."}
                            </p>
                        )}
                        <Link to={loggedIn ? "/dashboard" : "/login"} className="auth-button auth-button-link">
                            {loggedIn ? "Go to your files" : "Log in"}
                        </Link>
                    </>
                )}
            </div>
        </div>
    );
}

export default VerifyEmail;
