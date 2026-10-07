import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { PROFILE_CHANGED_EVENT, getUsageAPI, getUserInfoAPI } from "../../api/accountAPI";
import { formatBytes } from "../../utils/formatBytes";
import { useFiles } from "../../contexts/FilesContext";
import Avatar from "../../components/Avatar";

// Bottom of the folder tree: avatar, first name and storage used (name and usage hidden when collapsed)
export default function NavUser({ compact }) {
  const [username, setUsername] = useState("");
  const [usage, setUsage] = useState(null);
  const { files } = useFiles();

  useEffect(() => {
    let cancelled = false;
    const load = () =>
      getUserInfoAPI()
        .then((info) => !cancelled && setUsername(info.username))
        .catch(() => {});
    load();
    window.addEventListener(PROFILE_CHANGED_EVENT, load);
    return () => {
      cancelled = true;
      window.removeEventListener(PROFILE_CHANGED_EVENT, load);
    };
  }, []);

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

  const firstName = username.trim().split(/\s+/)[0];
  const usageText = usage ? `${formatBytes(usage.used)} / ${formatBytes(usage.quota)}` : "";

  return (
    <Link
      to="/settings"
      className="nav-user"
      title={[firstName, usageText].filter(Boolean).join(" · ")}
    >
      <Avatar size={28} />
      {!compact && (
        <>
          <span className="nav-user-name text-truncate">{firstName}</span>
          <span className="nav-user-usage text-truncate">{usageText}</span>
        </>
      )}
    </Link>
  );
}
