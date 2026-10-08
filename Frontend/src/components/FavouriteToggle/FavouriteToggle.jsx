import { MdStar, MdStarBorder } from "react-icons/md";
import { useFiles } from "../../contexts/FilesContext";
import { useFileActions } from "../../contexts/FileActionsContext";
import "./FavouriteToggle.css";

// Star button for a file or folder. Reads the current state from the file list, since the
// item passed in may be an older copy (e.g. the selection).
export default function FavouriteToggle({ file, size = 18, className = "" }) {
    const { files } = useFiles();
    const { setFavourite } = useFileActions();
    const current = files?.find((f) => f._id === file._id) ?? file;
    const favourite = !!current.isFavourite;
    const label = favourite ? "Remove from favourites" : "Add to favourites";

    return (
        <button
            type="button"
            className={`favourite-toggle ${favourite ? "is-favourite" : ""} ${className}`}
            title={label}
            aria-label={label}
            aria-pressed={favourite}
            onClick={(event) => {
                event.stopPropagation();
                setFavourite([current], !favourite);
            }}
            onDoubleClick={(event) => event.stopPropagation()}
        >
            {favourite ? <MdStar size={size} aria-hidden="true" /> : <MdStarBorder size={size} aria-hidden="true" />}
        </button>
    );
}
