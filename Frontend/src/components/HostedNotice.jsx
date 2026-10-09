import { useState } from "react";
import { dismissHostedNoticeAPI } from "../api/accountAPI";
import { useUserProfile } from "../hooks/useUserProfile";
import "./HostedNotice.css";

// Hosted mode only: a dismissible note in the bottom right (a bottom sheet on phones) saying
// unused accounts are removed. The server decides who sees it (not admins or permanent
// accounts, and not again once dismissed) and which address to email.
export default function HostedNotice() {
    const { hostedNotice, hostedContactEmail } = useUserProfile();
    const [dismissed, setDismissed] = useState(false);

    if (!hostedNotice || dismissed) return null;

    const dismiss = () => {
        setDismissed(true);
        // Hidden for this visit even if remembering it fails; it shows again next time
        dismissHostedNoticeAPI().catch(() => {});
    };

    return (
        <div className="hosted-notice" role="status">
            <p className="hosted-notice-text">
                FileVault is a free service with limited hosting, so accounts that aren't used for 30 days are removed.
                {hostedContactEmail && (
                    <>
                        {" "}To ask for a permanent account, email <a href={`mailto:${hostedContactEmail}`}>{hostedContactEmail}</a>.
                    </>
                )}
            </p>
            <button type="button" className="hosted-notice-close" aria-label="Dismiss notice" onClick={dismiss}>
                ×
            </button>
        </div>
    );
}
