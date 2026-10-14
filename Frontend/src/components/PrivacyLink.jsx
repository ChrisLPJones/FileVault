import { Link } from "react-router-dom";
import { useSiteInfo } from "../hooks/useSiteInfo";
import "./PrivacyLink.css";

// A small "Privacy" link for the foot of the login, sign-up, share and settings pages. Only a hosted
// site has a privacy page, so nothing shows until the server says so.
export default function PrivacyLink() {
    const { privacy } = useSiteInfo();
    if (!privacy) return null;

    return (
        <p className="privacy-link">
            <Link to="/privacy">Privacy</Link>
        </p>
    );
}
