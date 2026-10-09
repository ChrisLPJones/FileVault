import { MdClose, MdSearch } from "react-icons/md";
import { useSearch } from "../../contexts/SearchContext";
import { useSelection } from "../../contexts/SelectionContext";
import "../Search/SearchResults.scss";

// Search box in the toolbar. Matching names (in every folder) replace the file list while
// there is text in it; Esc or the clear button ends the search.
const SearchBox = () => {
  const { query, setQuery, clearSearch } = useSearch();
  const { setSelectedFiles } = useSelection();

  const handleChange = (e) => {
    // Starting a search drops the selection, so the toolbar's actions don't apply to hidden items
    if (!query && e.target.value) setSelectedFiles([]);
    setQuery(e.target.value);
  };

  const handleKeyDown = (e) => {
    if (e.key === "Escape") {
      e.preventDefault();
      clearSearch();
      e.currentTarget.blur();
    }
  };

  return (
    <div className="fm-search" role="search">
      <MdSearch className="fm-search-icon" size={18} aria-hidden="true" />
      <input
        type="search"
        className="fm-search-input"
        placeholder="Search files"
        aria-label="Search files and folders by name"
        value={query}
        maxLength={255}
        onChange={handleChange}
        onKeyDown={handleKeyDown}
      />
      {query && (
        <button type="button" className="fm-search-clear" title="Clear search" onClick={clearSearch}>
          <MdClose size={16} />
        </button>
      )}
    </div>
  );
};

export default SearchBox;
