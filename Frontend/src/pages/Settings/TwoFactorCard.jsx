import { useEffect, useState } from "react";
import PropTypes from "prop-types";
import { getErrorMessage } from "../../api/api";
import {
    disableTwoFactorAPI,
    enableTwoFactorAPI,
    getTwoFactorStatusAPI,
    regenerateRecoveryCodesAPI,
    startTwoFactorSetupAPI,
} from "../../api/securityAPI";
import SettingsStatus from "./SettingsStatus";
import "./SecurityCards.css";

// "JBSWY3DPEHPK3PXP" -> "JBSW Y3DP EHPK 3PXP", easier to type into an app
const groupSecret = (secret) => secret.match(/.{1,4}/g)?.join(" ") ?? secret;

// The 6-digit code field used by every form in this card
function CodeInput({ id, value, onChange, label = "Code from your authenticator app" }) {
    return (
        <div className="form-group">
            <label htmlFor={id}>{label}</label>
            <input
                id={id}
                className="code-input"
                type="text"
                inputMode="numeric"
                autoComplete="one-time-code"
                pattern="[0-9 ]*"
                maxLength={7}
                placeholder="123456"
                value={value}
                required
                onChange={(e) => onChange(e.target.value)}
            />
        </div>
    );
}

CodeInput.propTypes = {
    id: PropTypes.string.isRequired,
    value: PropTypes.string.isRequired,
    onChange: PropTypes.func.isRequired,
    label: PropTypes.string,
};

// Recovery codes, shown once after turning 2FA on or making new ones
function RecoveryCodes({ codes, onDone }) {
    const [copied, setCopied] = useState(false);
    const text = codes.join("\n");

    const copy = async () => {
        try {
            await navigator.clipboard.writeText(text);
            setCopied(true);
        } catch {
            setCopied(false);
        }
    };

    const download = () => {
        const url = URL.createObjectURL(new Blob([`FileVault recovery codes\n\n${text}\n`], { type: "text/plain" }));
        const link = document.createElement("a");
        link.href = url;
        link.download = "filevault-recovery-codes.txt";
        link.click();
        URL.revokeObjectURL(url);
    };

    return (
        <div className="recovery-codes">
            <p className="settings-hint">
                Save these recovery codes somewhere safe. Each one can be used once to log in if you lose your
                authenticator app. <strong>They won't be shown again.</strong>
            </p>
            <ul className="recovery-code-list" aria-label="Recovery codes">
                {codes.map((code) => <li key={code}>{code}</li>)}
            </ul>
            <div className="security-actions">
                <button type="button" className="settings-button secondary" onClick={copy}>
                    {copied ? "Copied" : "Copy"}
                </button>
                <button type="button" className="settings-button secondary" onClick={download}>
                    Download
                </button>
                <button type="button" className="settings-button" onClick={onDone}>
                    I've saved them
                </button>
            </div>
        </div>
    );
}

RecoveryCodes.propTypes = {
    codes: PropTypes.arrayOf(PropTypes.string).isRequired,
    onDone: PropTypes.func.isRequired,
};

// "Two-factor authentication" card in Settings: set up, turn off, new recovery codes
function TwoFactorCard() {
    const [twoFactor, setTwoFactor] = useState(null); // { enabled, recoveryCodesLeft }
    // idle | password (setup step 1) | scan (step 2) | codes | disable | regenerate
    const [mode, setMode] = useState("idle");
    const [password, setPassword] = useState("");
    const [code, setCode] = useState("");
    const [useRecoveryCode, setUseRecoveryCode] = useState(false);
    const [setup, setSetup] = useState(null); // { secret, otpAuthUri, qr }
    const [recoveryCodes, setRecoveryCodes] = useState([]);
    const [busy, setBusy] = useState(false);
    const [status, setStatus] = useState(null);

    const refresh = async () => setTwoFactor(await getTwoFactorStatusAPI());

    useEffect(() => {
        let cancelled = false;
        getTwoFactorStatusAPI()
            .then((data) => !cancelled && setTwoFactor(data))
            .catch((err) => !cancelled &&
                setStatus({ type: "danger", message: getErrorMessage(err, "Could not load two-factor settings") }));
        return () => {
            cancelled = true;
        };
    }, []);

    const reset = (nextMode = "idle") => {
        setMode(nextMode);
        setPassword("");
        setCode("");
        setUseRecoveryCode(false);
        setSetup(null);
    };

    // Run a form action with the busy flag and error handling
    const run = (fallback, action) => async (event) => {
        event.preventDefault();
        setStatus(null);
        setBusy(true);
        try {
            await action();
        } catch (err) {
            setStatus({ type: "danger", message: getErrorMessage(err, fallback) });
        } finally {
            setBusy(false);
        }
    };

    const startSetup = run("Could not start setup", async () => {
        const info = await startTwoFactorSetupAPI(password);
        // Loaded only when needed, to keep it out of the main bundle
        const { default: QRCode } = await import("qrcode");
        const qr = await QRCode.toDataURL(info.otpAuthUri, { margin: 1, width: 200 });
        setPassword("");
        setSetup({ ...info, qr });
        setMode("scan");
    });

    const confirmSetup = run("Could not turn on two-factor authentication", async () => {
        const codes = await enableTwoFactorAPI(code.trim());
        reset("codes");
        setRecoveryCodes(codes);
        setStatus({ type: "success", message: "Two-factor authentication is on" });
        await refresh();
    });

    const disable = run("Could not turn off two-factor authentication", async () => {
        await disableTwoFactorAPI(password, useRecoveryCode ? { recoveryCode: code.trim() } : { code: code.trim() });
        reset();
        setStatus({ type: "success", message: "Two-factor authentication is off" });
        await refresh();
    });

    const regenerate = run("Could not make new recovery codes", async () => {
        const codes = await regenerateRecoveryCodesAPI(code.trim());
        reset("codes");
        setRecoveryCodes(codes);
        setStatus(null);
        await refresh();
    });

    const cancel = () => {
        reset();
        setStatus(null);
    };

    const cancelButton = (
        <button type="button" className="settings-button secondary" onClick={cancel} disabled={busy}>
            Cancel
        </button>
    );

    return (
        <section className="settings-card" aria-labelledby="two-factor-heading">
            <h2 id="two-factor-heading">
                Two-factor authentication
                {twoFactor && (
                    <span className={`security-badge ${twoFactor.enabled ? "on" : "off"}`}>
                        {twoFactor.enabled ? "On" : "Off"}
                    </span>
                )}
            </h2>

            {mode === "idle" && twoFactor && !twoFactor.enabled && (
                <>
                    <p className="settings-hint">
                        Ask for a code from an authenticator app (such as Google Authenticator, Microsoft Authenticator
                        or 1Password) as well as your password when you log in.
                    </p>
                    <button type="button" className="settings-button" onClick={() => setMode("password")}>
                        Set up two-factor authentication
                    </button>
                </>
            )}

            {mode === "idle" && twoFactor?.enabled && (
                <>
                    <p className="settings-hint">
                        You'll be asked for a code from your authenticator app when you log in.{" "}
                        {twoFactor.recoveryCodesLeft} of 10 recovery codes left.
                    </p>
                    <div className="security-actions">
                        <button type="button" className="settings-button secondary" onClick={() => setMode("regenerate")}>
                            New recovery codes
                        </button>
                        <button type="button" className="settings-button danger" onClick={() => setMode("disable")}>
                            Turn off
                        </button>
                    </div>
                </>
            )}

            {mode === "password" && (
                <form onSubmit={startSetup}>
                    <p className="settings-hint">Enter your password to start.</p>
                    <div className="form-group">
                        <label htmlFor="two-factor-password">Password</label>
                        <input
                            id="two-factor-password"
                            type="password"
                            autoComplete="current-password"
                            value={password}
                            required
                            onChange={(e) => setPassword(e.target.value)}
                        />
                    </div>
                    <div className="security-actions">
                        <button type="submit" className="settings-button" disabled={busy}>
                            {busy ? "Checking…" : "Continue"}
                        </button>
                        {cancelButton}
                    </div>
                </form>
            )}

            {mode === "scan" && setup && (
                <form onSubmit={confirmSetup}>
                    <ol className="setup-steps">
                        <li>Scan this QR code with your authenticator app.</li>
                    </ol>
                    <div className="qr-code">
                        <img src={setup.qr} width={200} height={200} alt="QR code for your authenticator app" />
                    </div>
                    <p className="settings-hint">
                        Can't scan it? Enter this key instead:{" "}
                        <code className="setup-secret">{groupSecret(setup.secret)}</code>
                    </p>
                    <ol className="setup-steps" start={2}>
                        <li>Enter the 6-digit code the app shows.</li>
                    </ol>
                    <CodeInput id="two-factor-setup-code" value={code} onChange={setCode} />
                    <div className="security-actions">
                        <button type="submit" className="settings-button" disabled={busy}>
                            {busy ? "Checking…" : "Turn on"}
                        </button>
                        {cancelButton}
                    </div>
                </form>
            )}

            {mode === "codes" && <RecoveryCodes codes={recoveryCodes} onDone={() => { setRecoveryCodes([]); cancel(); }} />}

            {mode === "regenerate" && (
                <form onSubmit={regenerate}>
                    <p className="settings-hint">Your current recovery codes will stop working.</p>
                    <CodeInput id="two-factor-regenerate-code" value={code} onChange={setCode} />
                    <div className="security-actions">
                        <button type="submit" className="settings-button" disabled={busy}>
                            {busy ? "Checking…" : "Make new codes"}
                        </button>
                        {cancelButton}
                    </div>
                </form>
            )}

            {mode === "disable" && (
                <form onSubmit={disable}>
                    <div className="form-group">
                        <label htmlFor="two-factor-disable-password">Password</label>
                        <input
                            id="two-factor-disable-password"
                            type="password"
                            autoComplete="current-password"
                            value={password}
                            required
                            onChange={(e) => setPassword(e.target.value)}
                        />
                    </div>
                    {useRecoveryCode ? (
                        <div className="form-group">
                            <label htmlFor="two-factor-disable-recovery">Recovery code</label>
                            <input
                                id="two-factor-disable-recovery"
                                type="text"
                                autoComplete="off"
                                spellCheck={false}
                                placeholder="xxxx-xxxx-xxxx"
                                value={code}
                                required
                                onChange={(e) => setCode(e.target.value)}
                            />
                        </div>
                    ) : (
                        <CodeInput id="two-factor-disable-code" value={code} onChange={setCode} />
                    )}
                    <button
                        type="button"
                        className="link-button"
                        onClick={() => { setUseRecoveryCode(!useRecoveryCode); setCode(""); }}
                    >
                        {useRecoveryCode ? "Use a code from the app" : "Use a recovery code"}
                    </button>
                    <div className="security-actions">
                        <button type="submit" className="settings-button danger" disabled={busy}>
                            {busy ? "Turning off…" : "Turn off two-factor authentication"}
                        </button>
                        {cancelButton}
                    </div>
                </form>
            )}

            <SettingsStatus status={status} />
        </section>
    );
}

export default TwoFactorCard;
