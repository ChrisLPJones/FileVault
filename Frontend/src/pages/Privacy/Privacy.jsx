import { Link } from "react-router-dom";
import { useSiteInfo } from "../../hooks/useSiteInfo";
import ErrorPage from "../Error/Errorpage";
import "../Auth/Auth.css";
import "./Privacy.css";

const days = (n) => `${n} ${n === 1 ? "day" : "days"}`;

// What this site keeps about you (/privacy). Hosted sites only; works without signing in. The numbers,
// the contact address and the sign-in location paragraph come from GET /site, so they follow the
// server's settings. Keep the text in step with what the backend stores (see PROJECT.md).
function Privacy() {
    const { loading, error, privacy } = useSiteInfo();

    if (loading) {
        return (
            <div className="auth-page">
                <div className="auth-card privacy-card">
                    <p className="auth-subtitle">Loading…</p>
                </div>
            </div>
        );
    }

    if (error) {
        return (
            <div className="auth-page">
                <div className="auth-card privacy-card">
                    <h1 className="auth-title">Privacy</h1>
                    <div className="auth-alert auth-alert-top danger" role="alert">Could not load this page. Try again in a moment.</div>
                    <p className="auth-switch"><Link to="/">Back to FileVault</Link></p>
                </div>
            </div>
        );
    }

    // A self-hosted install has no privacy page: the usual not-found page
    if (!privacy) return <ErrorPage />;

    const { inactiveDays, warningDays, loginLocation, contactEmail } = privacy;

    return (
        <div className="auth-page">
            <article className="auth-card privacy-card">
                <h1 className="auth-title">Privacy</h1>
                <p className="auth-subtitle">What this FileVault site keeps about you, in plain words.</p>

                <h2>What is kept</h2>
                <ul>
                    <li>Your name, email address and a scrambled (hashed) version of your password.</li>
                    <li>Your files, encrypted on the server's disk. File and folder names, sizes and dates are not encrypted.</li>
                    <li>Share links you create, and your profile picture and two-factor setup if you add them.</li>
                    <li>When you last signed in and the IP address you used. Only the latest sign-in is kept.</li>
                    <li>
                        For each device you are signed in on: a description of it (browser and operating system), its IP
                        address and when it was last used. You can see these under Active sessions in Settings.
                    </li>
                </ul>

                {loginLocation && (
                    <>
                        <h2>Sign-in location</h2>
                        <p>
                            The IP address of your latest sign-in is turned into an approximate place (country, and city
                            or region when known) using a database held on the server. The place is worked out when an
                            administrator looks at it and is never stored. No address is sent to anyone else.
                        </p>
                    </>
                )}

                <h2>How long it is kept</h2>
                <ul>
                    <li>
                        An account that is not signed in to or used for {days(inactiveDays)} is removed with its files.
                        {" "}{days(warningDays)} before that we email a warning to your address, if you have confirmed it.
                        Accounts an administrator has marked permanent are not removed.
                    </li>
                    <li>
                        You can delete your account yourself under Settings. That removes your account, files, share
                        links and sign-in records.
                    </li>
                    <li>
                        Signing out ends a device's session, but its record (device and IP address) stays until your next
                        sign-in after that session has expired, or until your account is removed.
                    </li>
                </ul>

                <h2>Who can see it</h2>
                <ul>
                    <li>
                        Administrators can see each account's name, email address, storage used, last sign-in time and
                        the IP address of the latest sign-in{loginLocation ? " with its approximate place" : ""}. The
                        admin page cannot open your files.
                    </li>
                    <li>Anyone with a share link you made can download what it shares (and needs its password, if you set one).</li>
                </ul>

                <h2>Cookies and other sites</h2>
                <ul>
                    <li>
                        One cookie, which keeps you signed in. Your browser also stores your theme, colour and folder
                        panel choices and a note that you are signed in. None of this is sent to advertisers or anyone else.
                    </li>
                    <li>No analytics, adverts or other sites' content are loaded; the font is served from this site.</li>
                    <li>Confirmation, password reset and warning emails are sent through this site's mail server.</li>
                </ul>

                <h2>Contact</h2>
                <p>
                    {contactEmail
                        ? <>Questions about your data: <a href={`mailto:${contactEmail}`}>{contactEmail}</a>.</>
                        : "This site has no contact address set."}
                </p>

                <p className="auth-switch"><Link to="/">Back to FileVault</Link></p>
            </article>
        </div>
    );
}

export default Privacy;
