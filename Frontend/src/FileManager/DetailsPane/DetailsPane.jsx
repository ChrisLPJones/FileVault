import { MdClear } from "react-icons/md";
import FileTypeIcon from "../../components/FileTypeIcon/FileTypeIcon";
import FavouriteToggle from "../../components/FavouriteToggle/FavouriteToggle";
import { useDetailsPane } from "../../contexts/DetailsPaneContext";
import { useTranslation } from "../../contexts/TranslationProvider";
import { getFileExtension } from "../../utils/getFileExtension";
import { formatBytes } from "../../utils/formatBytes";
import FilePreview from "./FilePreview";
import "./DetailsPane.scss";

const parentPath = (path = "") => path.slice(0, path.lastIndexOf("/"));

const typeLabel = (name) => {
  const ext = name.includes(".") ? getFileExtension(name) : "";
  return ext ? `${ext.toUpperCase()} file` : "File";
};

const CloseButton = () => {
  const { setDetailsOpen } = useDetailsPane();
  const t = useTranslation();
  return (
    <button
      type="button"
      className="details-close"
      title={t("close")}
      aria-label="Close details"
      onClick={() => setDetailsOpen(false)}
    >
      <MdClear size={18} />
    </button>
  );
};

// Explorer-style pane on the right of the file list for the selected file: its name, size
// and dates, with a preview underneath. Without a single selected file (nothing, several items or
// a folder) it only asks for one, so the pane doesn't come and go while it is switched on.
export default function DetailsPane({ file, formatDate, filePreviewComponent }) {
  if (!file) {
    return (
      <aside className="details-pane" aria-label="Details">
        <CloseButton />
        <p className="details-empty">Select a file to preview</p>
      </aside>
    );
  }
  return <FileDetails file={file} formatDate={formatDate} filePreviewComponent={filePreviewComponent} />;
}

function FileDetails({ file, formatDate, filePreviewComponent }) {
  const t = useTranslation();
  const customPreview = filePreviewComponent?.(file);

  const rows = [
    [t("size"), formatBytes(file.size)],
    ["Created", file.createdAt ? formatDate(file.createdAt) : null],
    [t("modified"), file.updatedAt ? formatDate(file.updatedAt) : null],
    ["Location", parentPath(file.path) || "/"],
  ].filter(([, value]) => value);

  return (
    <aside className="details-pane" aria-label="Details">
      <CloseButton />

      <div className="details-heading">
        <div className="details-icon">
          <FileTypeIcon name={file.name} path={file.path} size={64} />
        </div>
        <div className="details-name" title={file.name}>{file.name}</div>
        <div className="details-type">
          {typeLabel(file.name)}
          <FavouriteToggle file={file} size={18} className="details-favourite" />
        </div>
      </div>

      <dl className="details-info">
        {rows.map(([label, value]) => (
          <div key={label} className="details-info-row">
            <dt>{label}</dt>
            <dd>{value}</dd>
          </div>
        ))}
      </dl>

      <FilePreview key={file._id} file={file} customPreview={customPreview} />
    </aside>
  );
}
