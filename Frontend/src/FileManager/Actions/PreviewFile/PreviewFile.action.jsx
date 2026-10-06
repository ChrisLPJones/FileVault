import React, { useMemo, useState, useEffect } from "react";
import { getFileExtension } from "../../../utils/getFileExtension";
import Loader from "../../../components/Loader/Loader";
import { useSelection } from "../../../contexts/SelectionContext";
import Button from "../../../components/Button/Button";
import { getDataSize } from "../../../utils/getDataSize";
import { MdOutlineFileDownload } from "react-icons/md";
import { useFileIcons } from "../../../hooks/useFileIcons";
import { FaRegFileAlt } from "react-icons/fa";
import { useTranslation } from "../../../contexts/TranslationProvider";
import { downloadFile, fetchFileBlob } from "../../../api/downloadFileAPI";
import "./PreviewFile.action.scss";

const imageExtensions = ["jpg", "jpeg", "png"];
const videoExtensions = ["mp4", "mov", "avi"];
const audioExtensions = ["mp3", "wav", "m4a"];
const iFrameExtensions = ["txt", "pdf"];

const PreviewFileAction = ({ filePreviewPath, filePreviewComponent }) => {
  const [isLoading, setIsLoading] = useState(true);
  const [hasError, setHasError] = useState(false);
  const [fileURL, setFileURL] = useState(null);
  const { selectedFiles } = useSelection();
  const fileIcons = useFileIcons(73);
  const extension = getFileExtension(selectedFiles[0].name)?.toLowerCase();
  const t = useTranslation();
  const fileId = selectedFiles[0]._id;

  // Custom file preview component
  const customPreview = useMemo(
    () => filePreviewComponent?.(selectedFiles[0]),
    [filePreviewComponent]
  );

  const handleImageLoad = () => {
    setIsLoading(false);
    setHasError(false);
  };

  const handleImageError = () => {
    setIsLoading(false);
    setHasError(true);
  };

  // Fetch the file through the authenticated API client
  useEffect(() => {
    let url = null;
    let cancelled = false;

    const fetchFile = async () => {
      try {
        setIsLoading(true);
        const blob = await fetchFileBlob(fileId);
        if (cancelled) return;
        url = URL.createObjectURL(blob);
        setFileURL(url);
      } catch (err) {
        console.error(err);
        if (!cancelled) setHasError(true);
      } finally {
        if (!cancelled) setIsLoading(false);
      }
    };

    fetchFile();

    // Release the object URL created by this effect
    return () => {
      cancelled = true;
      if (url) URL.revokeObjectURL(url);
    };
  }, [fileId]);

  const handleDownload = async () => {
    try {
      await downloadFile([selectedFiles[0]]);
    } catch (err) {
      console.error(err);
    }
  };

  if (React.isValidElement(customPreview)) {
    return customPreview;
  }

  return (
    <section className={`file-previewer ${extension === "pdf" ? "pdf-previewer" : ""}`}>
      {hasError ||
        (![...imageExtensions, ...videoExtensions, ...audioExtensions, ...iFrameExtensions].includes(extension) && (
          <div className="preview-error">
            <span className="error-icon">{fileIcons[extension] ?? <FaRegFileAlt size={73} />}</span>
            <span className="error-msg">{t("previewUnavailable")}</span>
            <div className="file-info">
              <span className="file-name">{selectedFiles[0].name}</span>
              {selectedFiles[0].size && <span>-</span>}
              <span className="file-size">{getDataSize(selectedFiles[0].size)}</span>
            </div>
            <Button onClick={handleDownload} padding="0.45rem .9rem">
              <div className="download-btn">
                <MdOutlineFileDownload size={18} />
                <span>{t("download")}</span>
              </div>
            </Button>
          </div>
        ))}

      {imageExtensions.includes(extension) && (
        <>
          <Loader isLoading={isLoading} />
          {fileURL && (
            <img
              src={fileURL}
              alt="Preview Unavailable"
              className={`photo-popup-image ${isLoading ? "img-loading" : ""}`}
              onLoad={handleImageLoad}
              onError={handleImageError}
              loading="lazy"
            />
          )}
        </>
      )}

      {videoExtensions.includes(extension) && fileURL && (
        <video src={fileURL} className="video-preview" controls autoPlay />
      )}

      {audioExtensions.includes(extension) && fileURL && (
        <audio src={fileURL} controls autoPlay className="audio-preview" />
      )}

      {iFrameExtensions.includes(extension) && fileURL && (
        <iframe
          src={fileURL}
          onLoad={handleImageLoad}
          onError={handleImageError}
          frameBorder="0"
          className={`photo-popup-iframe ${isLoading ? "img-loading" : ""}`}
        />
      )}
    </section>
  );
};

export default PreviewFileAction;
