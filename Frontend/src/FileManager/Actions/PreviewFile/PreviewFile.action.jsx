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
import "./PreviewFile.action.scss";

const imageExtensions = ["jpg", "jpeg", "png"];
const videoExtensions = ["mp4", "mov", "avi"];
const audioExtensions = ["mp3", "wav", "m4a"];
const iFrameExtensions = ["pdf"];
const textExtensions = ["txt"];

const PreviewFileAction = ({ filePreviewPath, filePreviewComponent }) => {
  const [isLoading, setIsLoading] = useState(true);
  const [hasError, setHasError] = useState(false);
  const [previewUrl, setPreviewUrl] = useState(null);
  const [textContent, setTextContent] = useState(null);

  const { selectedFiles } = useSelection();
  const fileIcons = useFileIcons(73);
  const extension = getFileExtension(selectedFiles[0].name)?.toLowerCase();
  const filePath = `${filePreviewPath}/download/${selectedFiles[0]._id}`;
  const t = useTranslation();

  const customPreview = useMemo(() => filePreviewComponent?.(selectedFiles[0]), [filePreviewComponent]);

  const handleDownload = () => {
    window.location.href = filePath;
  };

  useEffect(() => {
    let url = null;

    const fetchFile = async () => {
      try {
        setIsLoading(true);
        const token = localStorage.getItem("token");
        if (!token) throw new Error("No auth token found");

        const res = await fetch(filePath, {
          method: "GET",
          headers: { Authorization: `Bearer ${token}` },
        });

        if (!res.ok) throw new Error("Failed to fetch file");

        if (extension && textExtensions.includes(extension)) {
          const text = await res.text();
          setTextContent(text);
        } else {
          const blob = await res.blob();
          url = URL.createObjectURL(blob);
          setPreviewUrl(url);
        }

        setHasError(false);
      } catch (err) {
        console.error(err);
        setHasError(true);
      } finally {
        setIsLoading(false);
      }
    };

    fetchFile();

    // Cleanup Blob URL on unmount
    return () => {
      if (url) URL.revokeObjectURL(url);
    };
  }, [filePath, extension]);

  if (React.isValidElement(customPreview)) return customPreview;

  return (
    <section className={`file-previewer ${extension === "pdf" ? "pdf-previewer" : ""}`}>
      {hasError && (
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
      )}




      {/* IMAGE */}
      {extension && imageExtensions.includes(extension) && previewUrl && (
        <>
          <Loader isLoading={isLoading} />
          <img src={previewUrl} alt="Preview" className={`photo-popup-image ${isLoading ? "img-loading" : ""}`} />
        </>
      )}

      {/* VIDEO */}
      {extension && videoExtensions.includes(extension) && previewUrl && (
        <video src={previewUrl} controls autoPlay className="video-preview" />
      )}

      {/* AUDIO */}
      {extension && audioExtensions.includes(extension) && previewUrl && (
        <audio src={previewUrl} controls autoPlay className="audio-preview" />
      )}

      {/* PDF */}
      {extension && iFrameExtensions.includes(extension) && previewUrl && (
        <iframe src={previewUrl} frameBorder="0" className={`photo-popup-iframe ${isLoading ? "img-loading" : ""}`} />
      )}

      {/* TXT */}
      {extension && textExtensions.includes(extension) && textContent && (
        <pre className="txt-preview">{textContent}</pre>
      )}
    </section>
  );
};

export default PreviewFileAction;
