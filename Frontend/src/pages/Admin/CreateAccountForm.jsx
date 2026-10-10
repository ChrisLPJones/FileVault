import { useState } from "react";
import { getErrorMessage } from "../../api/api";
import { meetsPasswordRules } from "../../utils/passwordRules";
import { UNITS } from "./quotaUnits";

const EMPTY = { firstName: "", lastName: "", email: "", password: "", isAdmin: false, isPermanent: false, quotaAmount: "", quotaUnit: "GB" };

// Make an account that works straight away: no confirmation email, the admin types the password
export default function CreateAccountForm({ onCreate, onCancel }) {
    const [form, setForm] = useState(EMPTY);
    const [saving, setSaving] = useState(false);
    const [error, setError] = useState(null);

    const edit = (name) => (event) =>
        setForm({ ...form, [name]: event.target.type === "checkbox" ? event.target.checked : event.target.value });

    // Blank quota = the server default
    const quotaAmount = Number(form.quotaAmount);
    const quotaBlank = form.quotaAmount.trim() === "";
    const quotaValid = quotaBlank || (Number.isFinite(quotaAmount) && quotaAmount >= 0);

    const valid = form.firstName.trim() !== "" && /\S+@\S+\.\S+/.test(form.email.trim()) && meetsPasswordRules(form.password) && quotaValid;

    const submit = async (event) => {
        event.preventDefault();
        if (!valid || saving) return;
        setSaving(true);
        setError(null);
        try {
            await onCreate({
                firstName: form.firstName.trim(),
                lastName: form.lastName.trim(),
                email: form.email.trim(),
                password: form.password,
                isAdmin: form.isAdmin,
                isPermanent: form.isAdmin || form.isPermanent, // administrators are always permanent
                quotaBytes: quotaBlank ? null : Math.round(quotaAmount * UNITS[form.quotaUnit]),
            });
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
                <div className="admin-field">
                    <label htmlFor="create-quota-amount">Quota <span className="admin-muted">(optional, blank = default)</span></label>
                    <div className="admin-quota-form">
                        <input
                            id="create-quota-amount"
                            type="number"
                            min="0"
                            step="any"
                            placeholder="Default"
                            value={form.quotaAmount}
                            onChange={edit("quotaAmount")}
                        />
                        <select aria-label="Quota unit" value={form.quotaUnit} onChange={edit("quotaUnit")}>
                            {Object.keys(UNITS).map((unit) => <option key={unit}>{unit}</option>)}
                        </select>
                    </div>
                </div>
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
                    <input type="checkbox" checked={form.isAdmin || form.isPermanent} disabled={form.isAdmin} onChange={edit("isPermanent")} />
                    <span>Permanent account{form.isAdmin && <span className="admin-muted"> (always for administrators)</span>}</span>
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
