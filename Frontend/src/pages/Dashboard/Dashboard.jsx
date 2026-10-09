import { useCallback, useEffect, useMemo, useState } from "react";
import { API_BASE_URL, getErrorMessage } from "../../api/api";
import { markOpenedAPI, setFavouriteAPI } from "../../api/favouritesAPI";
import { UsageProvider, useUsage } from "../../contexts/UsageContext";
import { FileActionsProvider } from "../../contexts/FileActionsContext";
import { createFolderAPI } from "../../api/createFolderAPI";
import { deleteAPI } from "../../api/deleteAPI";
import { downloadFile } from "../../api/downloadFileAPI";
import { copyItemAPI, moveItemAPI } from "../../api/fileTransferAPI";
import { getAllFilesAPI } from "../../api/getAllFilesAPI";
import { renameAPI } from "../../api/renameAPI";
import "./Dashboard.scss";
import FileManager from "../../FileManager/FileManager";
import { useHeaderSlot } from "../../contexts/HeaderSlotContext";
import VerifyEmailBanner from "../../components/VerifyEmailBanner";

// Matches the API's default Storage:MaxFileBytes until /user/usage responds. Files above 8 MB are
// uploaded in chunks, so the per-request limit (maxUploadBytes) only applies to smaller ones.
const DEFAULT_MAX_FILE_BYTES = 2 * 1024 * 1024 * 1024;

const fileUploadConfig = {
    url: `${API_BASE_URL}/upload`,
};

function DashboardPage() {
    // The file toolbar goes in the middle of the top bar
    const headerSlot = useHeaderSlot();
    const [isLoading, setIsLoading] = useState(true);
    const [files, setFiles] = useState([]);
    const [error, setError] = useState(null);
    const { usage, refreshUsage } = useUsage();
    // The server's upload limit (the space left in the quota is checked when files are chosen)
    const maxFileSize = usage ? usage.maxFileBytes ?? usage.maxUploadBytes : DEFAULT_MAX_FILE_BYTES;
    // Short confirmation (e.g. "Moved ... to the recycle bin") that hides itself
    const [notice, setNotice] = useState(null);

    useEffect(() => {
        if (!notice) return;
        const timer = setTimeout(() => setNotice(null), 6000);
        return () => clearTimeout(timer);
    }, [notice]);

    // Initial load
    useEffect(() => {
        let cancelled = false;

        getAllFilesAPI()
            .then((response) => !cancelled && setFiles(response.data))
            .catch((err) => !cancelled && setError(getErrorMessage(err, "Could not load files")))
            .finally(() => !cancelled && setIsLoading(false));

        return () => {
            cancelled = true;
        };
    }, []);

    // Usage changes whenever the list is reloaded: after uploads, deletes, emptying the recycle bin...
    useEffect(() => {
        if (!isLoading) refreshUsage();
    }, [isLoading, refreshUsage]);

    // Run an API action with the loader shown, then reload the file list.
    // Failures are shown in the error banner instead of leaving the loader stuck.
    const runAction = async (action, errorMessage) => {
        setIsLoading(true);
        setError(null);
        try {
            await action();
            const response = await getAllFilesAPI();
            setFiles(response.data);
        } catch (err) {
            console.error(err);
            setError(getErrorMessage(err, errorMessage));
        } finally {
            setIsLoading(false);
        }
    };

    const refreshFiles = () => runAction(async () => {}, "Could not load files");

    const handleCreateFolder = (name, parentFolder) =>
        runAction(() => createFolderAPI(name, parentFolder?._id), "Could not create folder");

    const handleFileUploading = (file, parentFolder) => ({ parentId: parentFolder?._id });

    const handleRename = (file, newName) =>
        runAction(() => renameAPI(file._id, newName), "Could not rename item");

    const handleDelete = (filesToDelete) =>
        runAction(async () => {
            await deleteAPI(filesToDelete.map((file) => file._id));
            const what = filesToDelete.length === 1 ? `"${filesToDelete[0].name}"` : `${filesToDelete.length} items`;
            setNotice(`Moved ${what} to the recycle bin`);
        }, "Could not delete items");

    const handlePaste = (copiedItems, destinationFolder, operationType) => {
        const ids = copiedItems.map((item) => item._id);
        return operationType === "copy"
            ? runAction(() => copyItemAPI(ids, destinationFolder?._id), "Could not copy items")
            : runAction(() => moveItemAPI(ids, destinationFolder?._id), "Could not move items");
    };

    // Change some items in the list without reloading it
    const updateFiles = useCallback((ids, changes) => {
        setFiles((prev) => prev.map((file) => (ids.includes(file._id) ? { ...file, ...changes } : file)));
    }, []);

    // Star or unstar straight away; put it back if the server refuses
    const handleSetFavourite = useCallback(async (items, favourite) => {
        const ids = items.map((item) => item._id).filter(Boolean);
        updateFiles(ids, { isFavourite: favourite });
        const results = await Promise.allSettled(ids.map((id) => setFavouriteAPI(id, favourite)));
        const failed = ids.filter((_, i) => results[i].status === "rejected");
        if (failed.length > 0) {
            updateFiles(failed, { isFavourite: !favourite });
            setError(getErrorMessage(results.find((r) => r.status === "rejected").reason, "Could not update favourites"));
        }
    }, [updateFiles]);

    // A file was opened (double-click, Enter or the context menu's Open) or downloaded: it moves
    // to the top of Recent. Just selecting a file, which shows it in the details pane, doesn't count.
    const handleOpened = useCallback((file) => {
        if (!file?._id || file.isDirectory) return;
        updateFiles([file._id], { lastOpenedAt: new Date().toISOString() });
        markOpenedAPI(file._id).catch((err) => console.error(err));
    }, [updateFiles]);

    const fileActions = useMemo(() => ({ setFavourite: handleSetFavourite }), [handleSetFavourite]);

    const handleDownload = async (filesToDownload) => {
        setError(null);
        try {
            await downloadFile(filesToDownload);
            filesToDownload.forEach(handleOpened);
        } catch (err) {
            console.error(err);
            setError(getErrorMessage(err, "Could not download"));
        }
    };

    const handleError = (err) => {
        console.error(err);
    };

    return (
        <div className="app">
            {error && (
                <div className="dashboard-error" role="alert">
                    <span>{error}</span>
                    <button type="button" onClick={() => setError(null)} aria-label="Dismiss error">
                        ×
                    </button>
                </div>
            )}
            {!error && <VerifyEmailBanner />}
            {notice && !error && (
                <div className="dashboard-notice" role="status">
                    <span>{notice}</span>
                    <button type="button" onClick={() => setNotice(null)} aria-label="Dismiss">
                        ×
                    </button>
                </div>
            )}
            <div className="file-manager-container">
                <FileActionsProvider value={fileActions}>
                    <FileManager
                        files={files}
                        fileUploadConfig={fileUploadConfig}
                        isLoading={isLoading}
                        onCreateFolder={handleCreateFolder}
                        onFileUploading={handleFileUploading}
                        onFileUploaded={refreshFiles}
                        onPaste={handlePaste}
                        onRename={handleRename}
                        onDownload={handleDownload}
                        onDelete={handleDelete}
                        onRefresh={refreshFiles}
                        onError={handleError}
                        layout="grid"
                        primaryColor="var(--fv-primary)"
                        enableFilePreview
                        maxFileSize={maxFileSize}
                        height="100%"
                        width="100%"
                        onFileOpen={handleOpened}
                        toolbarContainer={headerSlot}
                    />
                </FileActionsProvider>
            </div>
        </div>
    );
}

function Dashboard() {
    return (
        <UsageProvider>
            <DashboardPage />
        </UsageProvider>
    );
}

export default Dashboard;
