import { useRef, useState } from "react";
import Button from "../../../components/Button/Button";
import { AiOutlineCloudUpload } from "react-icons/ai";
import UploadItem from "./UploadItem";
import Loader from "../../../components/Loader/Loader";
import { useFileNavigation } from "../../../contexts/FileNavigationContext";
import { getFileExtension } from "../../../utils/getFileExtension";
import { getDataSize } from "../../../utils/getDataSize";
import { useFiles } from "../../../contexts/FilesContext";
import { useTranslation } from "../../../contexts/TranslationProvider";
import { createFolderResolver, createLimiter, filesFromFolderInput, readDroppedItems } from "../../../utils/folderUpload";
import { formatBytes } from "../../../utils/formatBytes";
import { checkUploadSpace, reservedBytes, retrySpaceError } from "../../../utils/uploadSpace";
import { useUsage } from "../../../contexts/UsageContext";
import "./UploadFile.action.scss";

const UploadFileAction = ({
  fileUploadConfig,
  maxFileSize,
  acceptedFileTypes,
  onFileUploading,
  onFileUploaded,
  onClose,
}) => {
  const [files, setFiles] = useState([]);
  const [isDragging, setIsDragging] = useState(false);
  const [isUploading, setIsUploading] = useState({});
  const { currentFolder } = useFileNavigation();
  const { files: allFiles, onError } = useFiles();
  const { usage, refreshUsage } = useUsage();
  const fileInputRef = useRef(null);
  const folderResolverRef = useRef(null);
  // A folder can hold hundreds of files: upload a few at a time
  const limiterRef = useRef(null);
  const runLimited = (task) => {
    limiterRef.current ??= createLimiter(3);
    return limiterRef.current(task);
  };
  const t = useTranslation();

  // To open choose file if the "Choose File" button is focused and Enter key is pressed
  const handleChooseFileKeyDown = (e) => {
    if (e.key === "Enter") {
      fileInputRef.current.click();
    }
  };

  const checkFileError = (file) => {
    if (acceptedFileTypes) {
      const extError = !acceptedFileTypes.includes(getFileExtension(file.name));
      if (extError) return t("fileTypeNotAllowed");
    }

    const sizeError = maxFileSize && file.size > maxFileSize;
    if (sizeError) return `${t("maxUploadSize")} ${getDataSize(maxFileSize, 0)}.`;
  };

  // Folder uploads: folders are created under the folder that was open when the dialog was used
  const getFolderResolver = () => {
    folderResolverRef.current ??= createFolderResolver(currentFolder, allFiles);
    return folderResolverRef.current;
  };

  // items: [{ file, dir }] where dir is the folder path inside a folder upload ("" for loose files)
  const setSelectedFiles = (items) => {
    const key = (dir, name) => `${dir}/${name}`.toLowerCase();
    items = items.filter(
      (item) => !files.some((fileData) => key(fileData.dir ?? "", fileData.file.name) === key(item.dir, item.file.name))
    );

    if (items.length > 0) {
      // Files that pass the type and size checks must also fit in the space left, after those
      // already queued here that the server hasn't counted yet. The ones that don't fit are marked
      // and never upload; the others still do.
      const reserved = reservedBytes(files);
      const fileErrors = items.map(({ file }) => checkFileError(file));
      const spaceErrors = checkUploadSpace(
        items.map(({ file }, i) => (fileErrors[i] ? 0 : file.size)),
        usage,
        reserved
      );

      // A name that is already in the folder is given a number by the server ("report (1).pdf")
      const newFiles = items.map(({ file, dir }, i) => {
        const appendData = onFileUploading(file, currentFolder);
        const error = fileErrors[i] || spaceErrors[i];
        error && onError({ type: "upload", message: error }, file);
        return {
          file: file,
          dir,
          appendData: appendData,
          ...(error && { error: error }),
        };
      });
      setFiles((prev) => [...prev, ...newFiles]);
    }
  };

  // Dropped files and folders go through the same checks as chosen ones (setSelectedFiles)
  const handleDrop = (e) => {
    e.preventDefault();
    setIsDragging(false);
    readDroppedItems(e.dataTransfer)
      .then(setSelectedFiles)
      .catch((err) => onError({ type: "upload", message: "Could not read the dropped folder" }, err));
  };

  const handleChooseFile = (e) => {
    setSelectedFiles(Array.from(e.target.files).map((file) => ({ file, dir: "" })));
  };

  const handleChooseFolder = (e) => {
    setSelectedFiles(filesFromFolderInput(e.target.files));
  };

  // An upload finished: reload the list, and once the usage includes the file stop holding its
  // size back from the space check
  const handleUploaded = (index, response) => {
    onFileUploaded(response);
    refreshUsage().then((fresh) => {
      if (!fresh) return;
      setFiles((prev) => prev.map((file, i) => (i === index ? { ...file, counted: true } : file)));
    });
  };

  const handleFileRemove = (index) => {
    setFiles((prev) => {
      const newFiles = prev.map((file, i) => {
        if (index === i) {
          return {
            ...file,
            removed: true,
          };
        }
        return file;
      });

      // If every file is removed, empty files array
      if (newFiles.every((file) => !!file.removed)) return [];

      return newFiles;
    });
  };

  const uploadInProgress = Object.values(isUploading).some((fileUploading) => fileUploading);

  return (
    <div className={`fm-upload-file ${files.length > 0 ? "file-selcted" : ""}`}>
      {files.length === 0 && (
      <div className="select-files">
        <div
          className={`draggable-file-input ${isDragging ? "dragging" : ""}`}
          onDrop={handleDrop}
          onDragOver={(e) => e.preventDefault()}
          onDragEnter={() => setIsDragging(true)}
          onDragLeave={() => setIsDragging(false)}
        >
          <div className="input-text">
            <AiOutlineCloudUpload size={30} />
            <span>{t("dragFileToUpload")}</span>
          </div>
        </div>
        <div className="btn-choose-file">
          <Button padding="0" onKeyDown={handleChooseFileKeyDown}>
            <label htmlFor="chooseFile">{t("chooseFile")}</label>
            <input
              ref={fileInputRef}
              type="file"
              id="chooseFile"
              className="choose-file-input"
              onChange={handleChooseFile}
              multiple
              accept={acceptedFileTypes}
            />
          </Button>
          {/* Uploads a folder and everything in it, recreating its folders */}
          <Button padding="0" type="secondary">
            <label htmlFor="chooseFolder">Choose Folder</label>
            <input
              type="file"
              id="chooseFolder"
              className="choose-file-input"
              onChange={handleChooseFolder}
              webkitdirectory=""
              multiple
            />
          </Button>
        </div>
        {maxFileSize > 0 && (
          <p className="upload-limit-hint">Any file type, up to {formatBytes(maxFileSize, 0)} per file.</p>
        )}
      </div>
      )}
      {files.length > 0 && (
        <div className="files-progress">
          <div className="heading">
            {uploadInProgress ? (
              <>
                <h2>{t("uploading")}</h2>
                <Loader loading={true} className="upload-loading" />
              </>
            ) : (
              <h2>{t("completed")}</h2>
            )}
          </div>
          <ul>
            {files.map((fileData, index) => (
              <UploadItem
                index={index}
                key={index}
                fileData={fileData}
                setFiles={setFiles}
                fileUploadConfig={fileUploadConfig}
                setIsUploading={setIsUploading}
                onFileUploaded={(response) => handleUploaded(index, response)}
                handleFileRemove={handleFileRemove}
                checkSpace={() => retrySpaceError(files, index, usage)}
                resolveParentId={(dir) => getFolderResolver()(dir)}
                runLimited={runLimited}
              />
            ))}
          </ul>
          {!uploadInProgress && (
            <div className="upload-close">
              <Button onClick={onClose}>{t("close")}</Button>
            </div>
          )}
        </div>
      )}
    </div>
  );
};

export default UploadFileAction;
