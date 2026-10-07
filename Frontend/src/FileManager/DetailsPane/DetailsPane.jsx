import { MdClear } from "react-icons/md";
import FileTypeIcon from "../../components/FileTypeIcon/FileTypeIcon";
import { useFiles } from "../../contexts/FilesContext";
import { useFileNavigation } from "../../contexts/FileNavigationContext";
import { useSelection } from "../../contexts/SelectionContext";
import { useDetailsPane } from "../../contexts/DetailsPaneContext";
import { useTranslation } from "../../contexts/TranslationProvider";
import { getFileExtension } from "../../utils/getFileExtension";
import { formatBytes } from "../../utils/formatBytes";
import FilePreview from "./FilePreview";
import "./DetailsPane.scss";

const parentPath = (path = "") => path.slice(0, path.lastIndexOf("/"));

const typeLabel = (item) => {
  if (item.isDirectory) return "Folder";
  const ext = item.name.includes(".") ? getFileExtension(item.name) : "";
  return ext ? `${ext.toUpperCase()} file` : "File";
};

const itemCount = (count) => `${count} ${count === 1 ? "item" : "items"}`;

function InfoRows({ rows }) {
  return (
    <dl className="details-info">
      {rows
        .filter(([, value]) => value !== undefined && value !== null && value !== "")
        .map(([label, value]) => (
          <div key={label} className="details-info-row">
            <dt>{label}</dt>
            <dd>{value}</dd>
          </div>
        ))}
    </dl>
  );
}

// Explorer-style pane on the right of the file list: the selected item's name, size and dates,
// with a preview underneath. Shows the open folder when nothing is selected.
export default function DetailsPane({ formatDate, filePreviewComponent }) {
  const { files } = useFiles();
  const { currentPath, currentFolder } = useFileNavigation();
  const { selectedFiles } = useSelection();
  const { setDetailsOpen } = useDetailsPane();
  const t = useTranslation();

  const childCount = (path) => files.filter((f) => parentPath(f.path) === path).length;

  let body;
  if (selectedFiles.length > 1) {
    const fileItems = selectedFiles.filter((f) => !f.isDirectory);
    const totalSize = fileItems.reduce((sum, f) => sum + (f.size || 0), 0);
    body = (
      <>
        <div className="details-heading">
          <div className="details-icon details-icon-stack">
            <FileTypeIcon name={selectedFiles[1].name} isDirectory={selectedFiles[1].isDirectory} size={56} />
            <FileTypeIcon name={selectedFiles[0].name} isDirectory={selectedFiles[0].isDirectory} size={56} />
          </div>
          <div className="details-name">{selectedFiles.length} {t("itemsSelected")}</div>
        </div>
        <InfoRows rows={[[t("size"), fileItems.length ? formatBytes(totalSize) : null]]} />
      </>
    );
  } else {
    const selected = selectedFiles[0];
    // Nothing selected: describe the folder being viewed
    const item = selected ?? currentFolder ?? { name: t("home"), path: "", isDirectory: true };
    const isFolderView = !selected;
    const customPreview = !item.isDirectory ? filePreviewComponent?.(item) : null;

    body = (
      <>
        <div className="details-heading">
          <div className="details-icon">
            <FileTypeIcon name={item.name} path={item.path} isDirectory={item.isDirectory} size={64} open={isFolderView} />
          </div>
          <div className="details-name" title={item.name}>{item.name}</div>
          <div className="details-type">{typeLabel(item)}</div>
        </div>

        <InfoRows
          rows={[
            [t("size"), item.isDirectory ? null : formatBytes(item.size)],
            ["Contains", item.isDirectory ? itemCount(childCount(isFolderView ? currentPath : item.path)) : null],
            ["Created", item.createdAt ? formatDate(item.createdAt) : null],
            [t("modified"), item.updatedAt ? formatDate(item.updatedAt) : null],
            ["Location", item.path ? parentPath(item.path) || "/" : null],
          ]}
        />

        {!item.isDirectory && <FilePreview key={item._id} file={item} customPreview={customPreview} />}
      </>
    );
  }

  return (
    <aside className="details-pane" aria-label="Details">
      <button
        type="button"
        className="details-close"
        title={t("close")}
        aria-label="Close details"
        onClick={() => setDetailsOpen(false)}
      >
        <MdClear size={18} />
      </button>
      {body}
    </aside>
  );
}
