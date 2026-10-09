import { AiOutlineClose } from "react-icons/ai";
import Progress from "../../../components/Progress/Progress";
import FileTypeIcon from "../../../components/FileTypeIcon/FileTypeIcon";
import { useEffect, useRef, useState } from "react";
import { getDataSize } from "../../../utils/getDataSize";
import { FaRegCheckCircle } from "react-icons/fa";
import { IoMdRefresh } from "react-icons/io";
import { useFiles } from "../../../contexts/FilesContext";
import { useTranslation } from "../../../contexts/TranslationProvider";
import { getErrorMessage, getFreshToken } from "../../../api/api";
import { CHUNKED_UPLOAD_THRESHOLD, cancelChunkedUpload, uploadInChunks } from "../../../api/chunkedUploadAPI";

// One file in the upload dialog. Small files go to POST /upload in one request; bigger ones are
// sent in chunks (retried, and resumed from where they stopped when Retry is clicked). Files from
// a folder upload first get their folder created (resolveParentId). runLimited keeps only a few
// uploads running at once.
const UploadItem = ({
    index,
    fileData,
    setFiles,
    setIsUploading,
    fileUploadConfig,
    onFileUploaded,
    handleFileRemove,
    resolveParentId,
    checkSpace = () => null, // error message if the file no longer fits in the space left
    runLimited = (task) => task(),
}) => {
    const [uploadProgress, setUploadProgress] = useState(0);
    const [isUploaded, setIsUploaded] = useState(false);
    const [isCanceled, setIsCanceled] = useState(false);
    const [uploadFailed, setUploadFailed] = useState(false);
    const xhrRef = useRef(); // single-request upload
    const abortRef = useRef(null); // chunked upload
    const chunkedUploadIdRef = useRef(null); // kept after a failure so Retry resumes
    const cancelledRef = useRef(false);
    const startedRef = useRef(false);
    const { onError } = useFiles();
    const t = useTranslation();

    const setUploading = (value) =>
        setIsUploading((prev) => ({
            ...prev,
            [index]: value,
        }));

    const showUploadError = (message, response) => {
        setUploadProgress(0);
        setUploading(false);

        const error = { type: "upload", message, response };
        setFiles((prev) =>
            prev.map((file, i) => (index === i ? { ...file, error: error.message } : file))
        );
        setUploadFailed(true);
        onError(error, fileData.file);
    };

    const handleUploadError = (xhr) => {
        // Prefer the server's reason (e.g. "Not enough storage space") over the generic message
        let serverMessage = null;
        try {
            serverMessage = JSON.parse(xhr.response)?.error;
        } catch {
            // not JSON (network error, aborted, etc.)
        }

        showUploadError(serverMessage || t("uploadFail"), {
            status: xhr.status,
            statusText: xhr.statusText,
            data: xhr.response,
        });
    };

    // The whole file in one multipart request
    const sendInOneRequest = (parentId) =>
        new Promise((resolve, reject) => {
            const xhr = new XMLHttpRequest();
            xhrRef.current = xhr;

            xhr.upload.onprogress = (event) => {
                if (event.lengthComputable) {
                    setUploadProgress(Math.round((event.loaded / event.total) * 100));
                }
            };

            xhr.onload = () => {
                if (xhr.status === 200 || xhr.status === 201) resolve(xhr.response);
                else reject({ xhr });
            };
            xhr.onerror = () => reject({ xhr });

            // Refresh the access token first if it is about to expire
            getFreshToken().then((token) => {
                if (xhr.cancelled) return;

                const method = fileUploadConfig?.method || "POST";
                xhr.open(method, fileUploadConfig?.url, true);
                const headers = { ...fileUploadConfig?.headers };
                if (token) {
                    headers["Authorization"] = `Bearer ${token}`;
                }
                for (let key in headers) {
                    xhr.setRequestHeader(key, headers[key]);
                }

                const formData = new FormData();
                const appendData = { ...fileData?.appendData, parentId };
                for (let key in appendData) {
                    appendData[key] && formData.append(key, appendData[key]);
                }
                formData.append("file", fileData.file);

                xhr.send(formData);
            });
        });

    // Big files: in chunks, continuing an earlier attempt if there was one
    const sendInChunks = async (parentId) => {
        const controller = new AbortController();
        abortRef.current = controller;
        const result = await uploadInChunks(fileData.file, {
            parentId,
            uploadId: chunkedUploadIdRef.current,
            signal: controller.signal,
            onStarted: (id) => {
                chunkedUploadIdRef.current = id;
            },
            // 100% is shown once the server has put the file together
            onProgress: (fraction) => setUploadProgress(Math.min(99, Math.round(fraction * 100))),
        });
        chunkedUploadIdRef.current = null;
        return result;
    };

    const fileUpload = (data) => {
        if (data.error) return;

        cancelledRef.current = false;
        setUploading(true);

        runLimited(async () => {
            if (cancelledRef.current) return;
            // A file from a folder upload goes into its (re)created folder
            const parentId = data.dir ? await resolveParentId(data.dir) : data.appendData?.parentId;
            if (cancelledRef.current) return;

            const response =
                data.file.size > CHUNKED_UPLOAD_THRESHOLD ? await sendInChunks(parentId) : await sendInOneRequest(parentId);

            setUploading(false);
            setUploadProgress(100);
            setIsUploaded(true);
            onFileUploaded(response);
        }).catch((error) => {
            if (cancelledRef.current) return;
            if (error?.xhr) handleUploadError(error.xhr);
            else if (!error?.response) showUploadError("Connection lost. Retry to carry on where it stopped.");
            else showUploadError(getErrorMessage(error, t("uploadFail")), error.response);
        });
    };

    useEffect(() => {
        // Prevent double uploads with strict mode
        if (!startedRef.current) {
            startedRef.current = true;
            fileUpload(fileData);
        }
    }, []);

    const handleAbortUpload = () => {
        cancelledRef.current = true;
        if (xhrRef.current) {
            xhrRef.current.cancelled = true; // in case it hasn't been sent yet
            xhrRef.current.abort();
        }
        abortRef.current?.abort();
        if (chunkedUploadIdRef.current) {
            cancelChunkedUpload(chunkedUploadIdRef.current);
            chunkedUploadIdRef.current = null;
        }
        setUploading(false);
        setIsCanceled(true);
        setUploadProgress(0);
        // A cancelled file no longer holds space back from the files added after it
        setFiles((prev) => prev.map((file, i) => (i === index ? { ...file, cancelled: true } : file)));
    };

    const handleRetry = () => {
        if (fileData?.file) {
            // Space may have been used since the file was queued
            const spaceError = checkSpace();
            if (spaceError) {
                setIsCanceled(false);
                showUploadError(spaceError);
                return;
            }
            setFiles((prev) =>
                prev.map((file, i) => {
                    if (index === i) {
                        return {
                            ...file,
                            error: false,
                            cancelled: false,
                        };
                    }
                    return file;
                })
            );
            fileUpload({ ...fileData, error: false });
            setIsCanceled(false);
            setUploadFailed(false);
        }
    };

    // File was removed by the user beacuse it was unsupported or exceeds file size limit.
    if (fileData.removed) {
        return null;
    }
    //

    const displayName = fileData.dir ? `${fileData.dir}/${fileData.file?.name}` : fileData.file?.name;

    return (
        <li>
            <div className="file-icon">
                <FileTypeIcon name={fileData.file?.name} size={36} />
            </div>
            <div className="file">
                <div className="file-details">
                    <div className="file-info">
                        <span
                            className="file-name text-truncate"
                            title={displayName}
                        >
                            {displayName}
                        </span>
                        <span className="file-size">
                            {getDataSize(fileData.file?.size)}
                        </span>
                    </div>
                    {isUploaded ? (
                        <FaRegCheckCircle
                            title={t("uploaded")}
                            className="upload-success"
                        />
                    ) : isCanceled || uploadFailed ? (
                        <IoMdRefresh
                            className="retry-upload"
                            title="Retry"
                            onClick={handleRetry}
                        />
                    ) : (
                        <div
                            className="rm-file"
                            title={`${
                                fileData.error
                                    ? t("Remove")
                                    : t("abortUpload")
                            }`}
                            onClick={
                                fileData.error
                                    ? () => handleFileRemove(index)
                                    : handleAbortUpload
                            }
                        >
                            <AiOutlineClose />
                        </div>
                    )}
                </div>
                <Progress
                    percent={uploadProgress}
                    isCanceled={isCanceled}
                    isCompleted={isUploaded}
                    error={fileData.error}
                />
            </div>
        </li>
    );
};

export default UploadItem;
