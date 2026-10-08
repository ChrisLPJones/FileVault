import { useMemo } from "react";
import FileTypeIcon from "../../components/FileTypeIcon/FileTypeIcon";
import { useFiles } from "../../contexts/FilesContext";
import { useFileNavigation } from "../../contexts/FileNavigationContext";
import { useSearch } from "../../contexts/SearchContext";
import { getParentPath } from "../../utils/getParentPath";
import { formatBytes } from "../../utils/formatBytes";
import "./SearchResults.scss";

const MAX_RESULTS = 200;

// The name with the matching part emphasised
const Highlighted = ({ name, term }) => {
  const start = name.toLocaleLowerCase().indexOf(term.toLocaleLowerCase());
  if (start < 0) return name;
  return (
    <>
      {name.slice(0, start)}
      <mark>{name.slice(start, start + term.length)}</mark>
      {name.slice(start + term.length)}
    </>
  );
};

// Every file and folder whose name contains the search term, in any folder. Picking one opens
// the folder it's in and selects it there.
const SearchResults = () => {
  const { files } = useFiles();
  const { term, clearSearch, setRevealPath } = useSearch();
  const { setCurrentPath, onFolderChange } = useFileNavigation();

  const matches = useMemo(() => {
    const needle = term.toLocaleLowerCase();
    return (files ?? [])
      .filter((file) => file.name?.toLocaleLowerCase().includes(needle))
      .sort((a, b) => (a.isDirectory === b.isDirectory ? a.name.localeCompare(b.name) : a.isDirectory ? -1 : 1));
  }, [files, term]);

  const open = (file) => {
    const folder = getParentPath(file.path);
    setCurrentPath(folder);
    onFolderChange?.(folder);
    setRevealPath(file.path);
    clearSearch();
  };

  return (
    <div className="fm-search-results">
      <div className="fm-search-summary" role="status">
        {matches.length === 0
          ? `No files or folders match "${term}"`
          : `${matches.length} ${matches.length === 1 ? "result" : "results"} for "${term}"`}
        {matches.length > MAX_RESULTS && ` (showing the first ${MAX_RESULTS})`}
      </div>

      <ul className="fm-search-list">
        {matches.slice(0, MAX_RESULTS).map((file) => {
          const folder = getParentPath(file.path);
          return (
            <li
              key={file._id ?? file.path}
              tabIndex={0}
              title={`Show ${file.name} in ${folder || "Home"}`}
              onClick={() => open(file)}
              onKeyDown={(e) => e.key === "Enter" && open(file)}
            >
              <FileTypeIcon name={file.name} path={file.path} isDirectory={file.isDirectory} size={26} />
              <span className="fm-search-name text-truncate">
                <Highlighted name={file.name} term={term} />
              </span>
              <span className="fm-search-folder text-truncate">{folder ? folder.slice(1).replaceAll("/", " › ") : "Home"}</span>
              <span className="fm-search-size">{!file.isDirectory && file.size > 0 ? formatBytes(file.size) : ""}</span>
            </li>
          );
        })}
      </ul>
    </div>
  );
};

// Shows the search results instead of the folder view while there is a search term
export const SearchSwitch = ({ children }) => {
  const { term } = useSearch();
  return term ? <SearchResults /> : children;
};

export default SearchResults;
