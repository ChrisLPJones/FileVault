import { useState } from "react";
import { getErrorMessage } from "../api/api";
import { resendVerificationAPI } from "../api/accountEmailAPI";
import { useUserProfile } from "../hooks/useUserProfile";
import "./VerifyEmailBanner.css";

// Shown to users who haven't confirmed their email address yet. They can still use the app;
// the banner offers to send the confirmation email again.
export default function VerifyEmailBanner() {
    const { email, emailVerified } = useUserProfile();
    const [dismissed, setDismissed] = useState(false);
    const [sending, setSending] = useState(false);
    const [message, setMessage] = useState(null);

    if (emailVerified || dismissed || !email) return null;

    const handleResend = async () => {
        setSending(true);
        try {
            setMessage((await resendVerificationAPI()).success);
        } catch (err) {
            setMessage(getErrorMessage(err, "Could not send the email"));
        } finally {
            setSending(false);
        }
    };

    return (
        <div className="verify-banner" role="status">
            <span className="verify-banner-text">
                {message ?? (
                    <>
                        Please confirm your email address, <strong>{email}</strong>, using the link we sent you.
                    </>
                )}
            </span>
            {!message && (
                <button type="button" className="verify-banner-resend" disabled={sending} onClick={handleResend}>
                    {sending ? "Sending…" : "Resend"}
                </button>
            )}
            <button type="button" className="verify-banner-close" aria-label="Dismiss" onClick={() => setDismissed(true)}>
                ×
            </button>
        </div>
    );
}
