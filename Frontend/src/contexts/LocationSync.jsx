import { useEffect, useRef } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useFiles } from "./FilesContext";
import { useFileNavigation } from "./FileNavigationContext";
import { useRecycleBin } from "./RecycleBinContext";
import { useSearch } from "./SearchContext";
import { useSelection } from "./SelectionContext";

// Keeps what the file manager is showing in the address, so the browser's back and forward buttons
// move between the folders visited, the recycle bin and search results, and a refresh or a
// bookmark returns to the same place:
//   ?folder=/Docs/Sub   the open folder (none = home)
//   &bin=1              the recycle bin is showing
//   &q=report           the search results for "report" are showing
// Changing the view pushes a history entry; typing in the search box only replaces the entry.

const toParams = ({ folder, bin, q }) => {
  const params = new URLSearchParams();
  if (folder) params.set("folder", folder);
  if (bin) params.set("bin", "1");
  if (q) params.set("q", q);
  return params;
};

const fromParams = (params) => ({
  folder: params.get("folder") ?? "",
  bin: params.get("bin") === "1",
  q: (params.get("q") ?? "").trim(),
});

const keyOf = (location) => toParams(location).toString();

// isLoading and source (the files the file manager was given) tell when the file list has loaded.
export default function LocationSync({ isLoading = false, source }) {
  const [searchParams, setSearchParams] = useSearchParams();
  const navigate = useNavigate();
  const { files } = useFiles();
  const { currentPath, setCurrentPath } = useFileNavigation();
  const { isBinOpen, openBin, closeBin } = useRecycleBin();
  const { term, setSearchTerm } = useSearch();
  const { setSelectedFiles } = useSelection();

  // FilesContext copies the list in an effect, so wait until it holds the loaded list
  const loaded = !isLoading && files === source;
  const state = { folder: currentPath, bin: isBinOpen, q: term };
  const stateKey = keyOf(state);
  const urlKey = searchParams.toString();

  // The location the view was last at, and one set from the address that the state hasn't reached yet
  const previous = useRef(null);
  const waitingFor = useRef(null);
  // The corrected address (an unknown folder removed) that has been asked for but isn't showing yet
  const correcting = useRef(null);
  // What this component last put in the address, so it isn't read back as a browser navigation
  const lastPushed = useRef(null);
  // The address the view was last set from, so a reload of the file list doesn't re-apply it over
  // something the user did meanwhile
  const appliedUrl = useRef(null);
  // The key of the place a search was started from, while the entry just before the search entry
  // is that place (then clearing the search can step back to it instead of adding a duplicate)
  const searchOrigin = useRef(null);

  // Address -> view: on load, and for back, forward and edited addresses
  useEffect(() => {
    if (!loaded) return;
    if (lastPushed.current === urlKey) {
      lastPushed.current = null;
      appliedUrl.current = urlKey;
      return;
    }

    // After a reload of the file list the address has already been applied. The view may have
    // moved on since, so keep it, and only handle a folder that has vanished.
    const reloaded = appliedUrl.current !== null && urlKey === appliedUrl.current;
    const wanted = reloaded ? { ...state } : fromParams(searchParams);
    // A folder that no longer exists (or never did) falls back to home
    if (wanted.folder && !files.some((file) => file.isDirectory && file.path === wanted.folder)) {
      wanted.folder = "";
    }
    const wantedKey = keyOf(wanted);
    if (reloaded && wantedKey === stateKey) return;

    // Going to a place other than the search entry made from the origin ends the origin's use
    if (searchOrigin.current !== null && !(wanted.q && keyOf({ ...wanted, q: "" }) === searchOrigin.current)) {
      searchOrigin.current = null;
    }

    if (wantedKey !== stateKey) {
      waitingFor.current = wantedKey;
      if (wanted.folder !== currentPath) {
        setCurrentPath(wanted.folder);
        setSelectedFiles([]); // the details pane mustn't show a file from the other folder
      }
      if (wanted.bin !== isBinOpen) (wanted.bin ? openBin : closeBin)();
      if (wanted.q !== term) setSearchTerm(wanted.q);
    }
    previous.current = wanted;
    if (wantedKey !== urlKey) {
      correcting.current = wantedKey;
      setSearchParams(toParams(wanted), { replace: true });
    }
    appliedUrl.current = urlKey;
    // Only the address (and the first load) drives this; the view is read as it is then
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [loaded, urlKey]);

  // View -> address: a push for a new place, a replace while only the search text changes
  useEffect(() => {
    if (!loaded || previous.current === null) return;
    if (correcting.current !== null) {
      if (correcting.current !== urlKey) return;
      correcting.current = null;
    }
    if (waitingFor.current !== null) {
      if (waitingFor.current !== stateKey) return; // the view hasn't caught up with the address yet
      waitingFor.current = null;
    }
    if (stateKey === urlKey) {
      previous.current = state;
      return;
    }

    const before = previous.current;
    const onlySearchChanged = before.folder === state.folder && before.bin === state.bin && before.q !== state.q;
    previous.current = state;

    if (onlySearchChanged && before.q) {
      // Clearing a search that was started from this same place: step back to that entry rather
      // than leave a duplicate of it
      if (!state.q && searchOrigin.current === stateKey) {
        searchOrigin.current = null;
        lastPushed.current = null; // a later Forward to the search entry is a navigation, not our push
        navigate(-1);
        return;
      }
      if (!state.q) searchOrigin.current = null;
      lastPushed.current = stateKey;
      setSearchParams(toParams(state), { replace: true });
      return;
    }

    searchOrigin.current = onlySearchChanged ? keyOf(before) : null;
    lastPushed.current = stateKey;
    setSearchParams(toParams(state));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [loaded, stateKey]);

  return null;
}
