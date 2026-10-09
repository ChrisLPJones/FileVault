import { useEffect, useMemo, useRef, useState } from "react";
import { MdHistory, MdKeyboardArrowRight, MdStar } from "react-icons/md";
import FileTypeIcon from "../../components/FileTypeIcon/FileTypeIcon";
import { useFiles } from "../../contexts/FilesContext";
import { useFileNavigation } from "../../contexts/FileNavigationContext";
import { useSelection } from "../../contexts/SelectionContext";
import { useDetailsPane } from "../../contexts/DetailsPaneContext";
import { getParentPath } from "../../utils/getParentPath";
import { favouriteItems, recentFiles } from "../../utils/quickAccess";
import "./QuickAccess.scss";

// Which sections are expanded, remembered in this browser
const STORAGE_KEY = "fv-quick-access";

const readSaved = () => {
  try {
    return { favourites: true, recent: true, ...JSON.parse(localStorage.getItem(STORAGE_KEY) || "{}") };
  } catch {
    return { favourites: true, recent: true };
  }
};

const SECTIONS = [
  { key: "favourites", title: "Favourites", icon: MdStar, empty: "Star files and folders to see them here" },
  { key: "recent", title: "Recent", icon: MdHistory, empty: "Files you open will appear here" },
];

// Favourites and Recent, above the folder tree. Clicking a folder opens it; clicking a file shows
// its folder with the file selected and shown in the details pane. In the compact (icons only)
// tree each section is one icon that expands the tree.
export default function QuickAccess({ compact, onExpand, onFileOpen }) {
  const { files } = useFiles();
  const { currentPath, setCurrentPath, currentPathFiles, onFolderChange } = useFileNavigation();
  const { setSelectedFiles } = useSelection();
  const { setDetailsOpen } = useDetailsPane();
  const [expanded, setExpanded] = useState(readSaved);
  // A file to select once its folder's contents are showing
  const pendingSelection = useRef(null);

  const items = useMemo(
    () => ({ favourites: favouriteItems(files), recent: recentFiles(files) }),
    [files]
  );

  useEffect(() => {
    if (!pendingSelection.current) return;
    const match = currentPathFiles.find((file) => file._id === pendingSelection.current);
    if (match) {
      pendingSelection.current = null;
      setSelectedFiles([match]);
    }
  }, [currentPathFiles, setSelectedFiles]);

  const toggle = (key) => {
    setExpanded((prev) => {
      const next = { ...prev, [key]: !prev[key] };
      try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
      } catch {
        // Storage unavailable: still works for this visit
      }
      return next;
    });
  };

  // A folder opens; a file is only selected (it isn't "opened", so Recent doesn't change)
  const open = (item) => {
    if (item.isDirectory) {
      onFileOpen?.(item);
      setCurrentPath(item.path);
      onFolderChange?.(item.path);
      return;
    }

    const folder = getParentPath(item.path);
    setDetailsOpen(true);
    if (folder === currentPath) {
      setSelectedFiles([currentPathFiles.find((file) => file._id === item._id) ?? item]);
    } else {
      pendingSelection.current = item._id;
      setCurrentPath(folder);
      onFolderChange?.(folder);
    }
  };

  if (compact) {
    return (
      <div className="quick-access compact">
        {SECTIONS.map((section) => {
          const Icon = section.icon;
          return (
            <button key={section.key} type="button" className="quick-access-icon" title={section.title}
              aria-label={section.title} onClick={onExpand}>
              <Icon size={20} />
            </button>
          );
        })}
      </div>
    );
  }

  return (
    <div className="quick-access">
      {SECTIONS.map(({ key, title, empty }) => (
        <section key={key} className="quick-access-section" aria-label={title}>
          <button
            type="button"
            className="quick-access-heading"
            aria-expanded={expanded[key]}
            onClick={() => toggle(key)}
          >
            <MdKeyboardArrowRight size={18} className={`quick-access-arrow ${expanded[key] ? "open" : ""}`} />
            {title}
          </button>
          {expanded[key] && (
            items[key].length > 0 ? (
              <ul className="quick-access-list">
                {items[key].map((item) => (
                  <li key={item._id}>
                    <button type="button" className="quick-access-item" title={item.path} onClick={() => open(item)}>
                      <FileTypeIcon name={item.name} path={item.path} isDirectory={item.isDirectory} size={20} />
                      <span className="text-truncate">{item.name}</span>
                    </button>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="quick-access-empty">{empty}</p>
            )
          )}
        </section>
      ))}
      <div className="quick-access-heading folders-heading">Folders</div>
    </div>
  );
}
