import { useState } from "react";
import { getErrorMessage } from "../../api/api";
import { meetsPasswordRules } from "../../utils/passwordRules";

const EMPTY = { firstName: "", lastName: "", email: "", password: "", isAdmin: false, isPermanent: false };

// Make an account that works straight away: no confirmation email, the admin types the password
export default function CreateAccountForm({ onCreate, onCancel }) {
    const [form, setForm] = useState(EMPTY);
    const [saving, setSaving] = useState(false);
    const [error, setError] = useState(null);

    const edit = (name) => (event) =>
        setForm({ ...form, [name]: event.target.type === "checkbox" ? event.target.checked : event.target.value });

    const valid = form.firstName.trim() !== "" && /\S+@\S+\.\S+/.test(form.email.trim()) && meetsPasswordRules(form.password);

    const submit = async (event) => {
        event.preventDefault();
        if (!valid || saving) return;
        setSaving(true);
        setError(null);
        try {
            await onCreate({ ...form, firstName: form.firstName.trim(), lastName: form.lastName.trim(), email: form.email.trim() });
        } catch (err) {
            setError(getErrorMessage(err, "Could not create the account"));
            setSaving(false);
        }
    };

    return (
        <form className="admin-create" onSubmit={submit} aria-label="Create account">
            <div className="admin-create-grid">
                <label className="admin-field">
                    <span>First name</span>
                    <input type="text" autoComplete="off" maxLength={50} value={form.firstName} onChange={edit("firstName")} autoFocus />
                </label>
                <label className="admin-field">
                    <span>Last name <span className="admin-muted">(optional)</span></span>
                    <input type="text" autoComplete="off" maxLength={50} value={form.lastName} onChange={edit("lastName")} />
                </label>
                <label className="admin-field">
                    <span>Email address</span>
                    <input type="email" autoComplete="off" maxLength={100} value={form.email} onChange={edit("email")} />
                </label>
                <label className="admin-field">
                    <span>Password</span>
                    <input type="password" autoComplete="new-password" value={form.password} onChange={edit("password")} />
                </label>
            </div>
            <p className="admin-muted">
                Password: at least 8 characters, with an uppercase letter, a lowercase letter and a number.
                The account can sign in straight away; no confirmation email is sent.
            </p>
            <div className="admin-create-options">
                <label className="admin-check">
                    <input type="checkbox" checked={form.isAdmin} onChange={edit("isAdmin")} />
                    <span>Administrator</span>
                </label>
                <label className="admin-check">
                    <input type="checkbox" checked={form.isPermanent} onChange={edit("isPermanent")} />
                    <span>Permanent account</span>
                </label>
            </div>
            {error && <div className="admin-error" role="alert">{error}</div>}
            <div className="admin-dialog-actions">
                <button type="submit" className="admin-button" disabled={!valid || saving}>Create account</button>
                <button type="button" className="admin-button secondary" disabled={saving} onClick={onCancel}>Cancel</button>
            </div>
        </form>
    );
}
