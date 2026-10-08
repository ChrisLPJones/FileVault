import { useState } from "react";
import { MdContentCopy } from "react-icons/md";
import { useSelection } from "../../../contexts/SelectionContext";
import { getErrorMessage } from "../../../api/api";
import { createShareAPI, shareUrl } from "../../../api/shareAPI";
import "./Share.action.scss";

const HOUR = 60 * 60 * 1000;
const EXPIRY_OPTIONS = [
  { label: "Never", ms: null },
  { label: "1 hour", ms: HOUR },
  { label: "1 day", ms: 24 * HOUR },
  { label: "7 days", ms: 7 * 24 * HOUR },
  { label: "30 days", ms: 30 * 24 * HOUR },
];
const MIN_PASSWORD_LENGTH = 6; // matches the API

// Create a public link to the selected file or folder, then show it once to copy
const ShareAction = ({ triggerAction }) => {
  const { selectedFiles } = useSelection();
  // Keep the item from when the dialog opened, even if the selection changes behind it
  const [item] = useState(() => selectedFiles[0]);
  const [expiry, setExpiry] = useState(EXPIRY_OPTIONS[0].label);
  const [password, setPassword] = useState("");
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState(null);
  const [link, setLink] = useState(null);
  const [copied, setCopied] = useState(false);

  if (!item) return null;

  const handleCreate = async (event) => {
    event.preventDefault();
    if (password && password.length < MIN_PASSWORD_LENGTH) {
      setError(`Link passwords must be at least ${MIN_PASSWORD_LENGTH} characters`);
      return;
    }

    const ms = EXPIRY_OPTIONS.find((option) => option.label === expiry)?.ms;
    setCreating(true);
    setError(null);
    try {
      const share = await createShareAPI(item._id, {
        expiresAt: ms ? new Date(Date.now() + ms).toISOString() : null,
        password,
      });
      setLink(shareUrl(share.token));
    } catch (err) {
      setError(getErrorMessage(err, "Could not create the link"));
    } finally {
      setCreating(false);
    }
  };

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(link);
      setCopied(true);
    } catch {
      // Clipboard blocked (e.g. not a secure context): select the text so it can be copied by hand
      document.getElementById("share-link")?.select();
    }
  };

  return (
    <div className="fm-share">
      <p className="fm-share-item text-truncate" title={item.name}>
        {item.isDirectory ? "Folder" : "File"}: <strong>{item.name}</strong>
      </p>

      {link ? (
        <div className="fm-share-body">
          <label htmlFor="share-link">Anyone with this link can download it</label>
          <div className="fm-share-link">
            <input id="share-link" type="text" readOnly value={link} onFocus={(e) => e.target.select()} />
            <button type="button" className="fm-button fm-button-primary" onClick={handleCopy}>
              <MdContentCopy aria-hidden="true" /> {copied ? "Copied" : "Copy"}
            </button>
          </div>
          <p className="fm-share-hint">
            Copy it now: for security the full link isn't stored, so it can't be shown again. You can revoke it
            under Settings, Shared links.
          </p>
          <div className="fm-share-actions">
            <button type="button" className="fm-button fm-button-secondary" onClick={() => triggerAction.close()}>
              Done
            </button>
          </div>
        </div>
      ) : (
        <form className="fm-share-body" onSubmit={handleCreate}>
          <label htmlFor="share-expiry">Link expires</label>
          <select id="share-expiry" value={expiry} onChange={(e) => setExpiry(e.target.value)}>
            {EXPIRY_OPTIONS.map((option) => (
              <option key={option.label} value={option.label}>
                {option.label === "Never" ? "Never" : `After ${option.label}`}
              </option>
            ))}
          </select>

          <label htmlFor="share-password">
            Password <span className="fm-share-optional">(optional)</span>
          </label>
          <input
            id="share-password"
            type="password"
            autoComplete="new-password"
            value={password}
            placeholder="No password"
            onChange={(e) => {
              setPassword(e.target.value);
              setError(null);
            }}
          />

          {error && (
            <div className="fm-share-error" role="alert">
              {error}
            </div>
          )}

          <div className="fm-share-actions">
            <button type="button" className="fm-button fm-button-secondary" onClick={() => triggerAction.close()}>
              Cancel
            </button>
            <button type="submit" className="fm-button fm-button-primary" disabled={creating}>
              {creating ? "Creating…" : "Create link"}
            </button>
          </div>
        </form>
      )}
    </div>
  );
};

export default ShareAction;
