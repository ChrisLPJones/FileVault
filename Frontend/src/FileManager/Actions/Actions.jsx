import { useEffect, useState } from "react";
import Modal from "../../components/Modal/Modal";
import DeleteAction from "./Delete/Delete.action";
import UploadFileAction from "./UploadFile/UploadFile.action";
import { useShortcutHandler } from "../../hooks/useShortcutHandler";
import { useTranslation } from "../../contexts/TranslationProvider";

const Actions = ({
  fileUploadConfig,
  onFileUploading,
  onFileUploaded,
  onDelete,
  onRefresh,
  maxFileSize,
  acceptedFileTypes,
  triggerAction,
  permissions,
}) => {
  const [activeAction, setActiveAction] = useState(null);
  const t = useTranslation();

  // Triggers all the keyboard shortcuts based actions
  useShortcutHandler(triggerAction, onRefresh, permissions);

  const actionTypes = {
    uploadFile: {
      title: t("upload"),
      component: (
        <UploadFileAction
          fileUploadConfig={fileUploadConfig}
          maxFileSize={maxFileSize}
          acceptedFileTypes={acceptedFileTypes}
          onFileUploading={onFileUploading}
          onFileUploaded={onFileUploaded}
        />
      ),
      width: "35%",
    },
    delete: {
      title: t("delete"),
      component: <DeleteAction triggerAction={triggerAction} onDelete={onDelete} />,
      width: "25%",
    },
  };

  useEffect(() => {
    if (triggerAction.isActive) {
      setActiveAction(actionTypes[triggerAction.actionType]);
    } else {
      setActiveAction(null);
    }
  }, [triggerAction.isActive]);

  if (activeAction) {
    return (
      <Modal
        heading={activeAction.title}
        show={triggerAction.isActive}
        setShow={triggerAction.close}
        dialogWidth={activeAction.width}
      >
        {activeAction?.component}
      </Modal>
    );
  }
};

export default Actions;
