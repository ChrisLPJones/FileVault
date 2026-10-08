import { useCallback, useEffect, useState } from "react";
import { MdDeleteForever, MdOutlineRestore } from "react-icons/md";
import { getErrorMessage } from "../../api/api";
import { deleteFromTrashAPI, emptyTrashAPI, getTrashAPI, restoreTrashAPI } from "../../api/trashAPI";
import FileTypeIcon from "../../components/FileTypeIcon/FileTypeIcon";
import Checkbox from "../../components/Checkbox/Checkbox";
import { useRecycleBin } from "../../contexts/RecycleBinContext";
import { useSearch } from "../../contexts/SearchContext";
import { formatBytes } from "../../utils/formatBytes";
import { formatDate } from "../../utils/formatDate";
import "./RecycleBin.scss";

const DAY_MS = 24 * 60 * 60 * 1000;

// The recycle bin view: what was deleted and where it came from. Items can be restored to their
// folder or deleted for good; the server deletes them for good after the retention period.
const RecycleBin = ({ onRefresh }) => {
  const [items, setItems] = useState(null);
  const [selected, setSelected] = useState([]);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState(null);
  const [confirm, setConfirm] = useState(null); // "delete" | "empty" while asking

  const load = useCallback(
    () =>
      getTrashAPI()
        .then((data) => {
          setItems(data);
          setSelected((prev) => prev.filter((id) => data.some((item) => item._id === id)));
        })
        .catch((err) => setStatus({ type: "error", message: getErrorMessage(err, "Could not load the recycle bin") })),
    []
  );

  useEffect(() => {
    load();
  }, [load]);

  // Run an action, then reload the bin and the file list (which also updates the storage used)
  const run = async (action, success, failure) => {
    setBusy(true);
    setConfirm(null);
    setStatus(null);
    try {
      await action();
      if (success) setStatus({ type: "success", message: success });
      setSelected([]);
    } catch (err) {
      setStatus({ type: "error", message: getErrorMessage(err, failure) });
    } finally {
      await load();
      onRefresh?.();
      setBusy(false);
    }
  };

  const count = (n) => `${n} ${n === 1 ? "item" : "items"}`;

  const handleRestore = () =>
    run(() => restoreTrashAPI(selected), `Restored ${count(selected.length)}`, "Could not restore");

  const handleDelete = () =>
    run(
      async () => {
        for (const id of selected) await deleteFromTrashAPI(id);
      },
      `Deleted ${count(selected.length)} permanently`,
      "Could not delete"
    );

  // No success message: the empty bin says so itself
  const handleEmpty = () => run(() => emptyTrashAPI(), null, "Could not empty the recycle bin");

  const toggle = (id) => setSelected((prev) => (prev.includes(id) ? prev.filter((s) => s !== id) : [...prev, id]));

  const retentionDays = items?.length
    ? Math.round((new Date(items[0].purgeAt) - new Date(items[0].deletedAt)) / DAY_MS)
    : null;

  return (
    <div className="fm-recycle-bin">
      <div className="fm-bin-header">
        <div className="fm-bin-title">
          <h2>Recycle bin</h2>
          <span>
            {retentionDays
              ? `Items are deleted for good ${retentionDays} days after they were deleted.`
              : "Deleted files and folders stay here until you empty the bin."}
          </span>
        </div>
        <div className="fm-bin-actions">
          <button type="button" className="fm-button fm-button-secondary" disabled={busy || !selected.length} onClick={handleRestore}>
            <MdOutlineRestore size={18} /> Restore
          </button>
          <button
            type="button"
            className="fm-button fm-button-secondary"
            disabled={busy || !selected.length}
            onClick={() => setConfirm("delete")}
          >
            <MdDeleteForever size={18} /> Delete permanently
          </button>
          <button type="button" className="fm-button fm-button-danger" disabled={busy || !items?.length} onClick={() => setConfirm("empty")}>
            Empty bin
          </button>
        </div>
      </div>

      {confirm && (
        <div className="fm-bin-confirm" role="alertdialog" aria-label="Confirm">
          <span>
            {confirm === "empty"
              ? `Permanently delete everything in the recycle bin (${count(items.length)})? This can't be undone.`
              : `Permanently delete ${count(selected.length)}? This can't be undone.`}
          </span>
          <button type="button" className="fm-button fm-button-secondary" onClick={() => setConfirm(null)}>
            Cancel
          </button>
          <button type="button" className="fm-button fm-button-danger" onClick={confirm === "empty" ? handleEmpty : handleDelete}>
            Delete
          </button>
        </div>
      )}

      {status && (
        <div className={`fm-bin-status ${status.type}`} role={status.type === "error" ? "alert" : "status"}>
          {status.message}
        </div>
      )}

      {items?.length === 0 && <div className="fm-bin-empty">The recycle bin is empty.</div>}

      {items?.length > 0 && (
        <ul className="fm-bin-list" aria-label="Deleted items">
          {items.map((item) => {
            const isSelected = selected.includes(item._id);
            return (
              <li
                key={item._id}
                className={isSelected ? "selected" : undefined}
                tabIndex={0}
                onClick={() => toggle(item._id)}
                onKeyDown={(e) => (e.key === " " || e.key === "Enter") && (e.preventDefault(), toggle(item._id))}
                aria-selected={isSelected}
              >
                <Checkbox
                  name={`bin-${item._id}`}
                  id={`bin-${item._id}`}
                  checked={isSelected}
                  onChange={() => toggle(item._id)}
                  onClick={(e) => e.stopPropagation()}
                />
                <FileTypeIcon name={item.name} isDirectory={item.isDirectory} size={24} />
                <span className="fm-bin-name text-truncate" title={item.name}>{item.name}</span>
                <span className="fm-bin-from text-truncate" title={`Was in ${item.originalFolder || "Home"}`}>
                  {item.originalFolder ? item.originalFolder.slice(1).replaceAll("/", " › ") : "Home"}
                </span>
                <span className="fm-bin-date">{formatDate(item.deletedAt)}</span>
                <span className="fm-bin-size">{item.size > 0 ? formatBytes(item.size) : ""}</span>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
};

// Shows the recycle bin in place of the folder view while it's open (a search takes precedence)
export const RecycleBinSwitch = ({ onRefresh, children }) => {
  const { isBinOpen } = useRecycleBin();
  const { term } = useSearch();
  return isBinOpen && !term ? <RecycleBin onRefresh={onRefresh} /> : children;
};

export default RecycleBin;
