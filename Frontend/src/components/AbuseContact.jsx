import { useSiteInfo } from "../hooks/useSiteInfo";
import "./AbuseContact.css";

// "Report abuse: <address>" when the operator has set an abuse contact, otherwise nothing.
// React escapes the address; the server only publishes a plain email address.
function AbuseContact({ className = "" }) {
    const { abuseContactEmail } = useSiteInfo();
    if (!abuseContactEmail) return null;

    return (
        <p className={`abuse-contact ${className}`.trim()}>
            Report abuse: <a href={`mailto:${abuseContactEmail}`}>{abuseContactEmail}</a>
        </p>
    );
}

export default AbuseContact;
