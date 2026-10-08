import { useEffect, useState } from "react";
import UserMenu from "../../components/UserMenu";
import { getUsageAPI } from "../../api/accountAPI";
import { useUserProfile } from "../../hooks/useUserProfile";
import { formatBytes } from "../../utils/formatBytes";
import { useFiles } from "../../contexts/FilesContext";
import Avatar from "../../components/Avatar";

// Bottom of the folder tree: avatar, first name and storage used (name and usage hidden when collapsed)
export default function NavUser({ compact }) {
  const { firstName } = useUserProfile();
  const [usage, setUsage] = useState(null);
  const { files } = useFiles();

  // Usage changes whenever the file list is reloaded
  useEffect(() => {
    let cancelled = false;
    getUsageAPI()
      .then((data) => !cancelled && setUsage(data))
      .catch(() => {});
    return () => {
      cancelled = true;
    };
  }, [files]);

  const usageText = usage ? `${formatBytes(usage.used)} / ${formatBytes(usage.quota)}` : "";

  return (
    <UserMenu className="nav-user" title={[firstName, usageText].filter(Boolean).join(" · ")}>
      <Avatar size={28} />
      {!compact && (
        <>
          <span className="nav-user-name text-truncate">{firstName}</span>
          <span className="nav-user-usage text-truncate">{usageText}</span>
        </>
      )}
    </UserMenu>
  );
}
