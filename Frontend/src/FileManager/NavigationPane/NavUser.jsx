import UserMenu from "../../components/UserMenu";
import { useUserProfile } from "../../hooks/useUserProfile";
import { formatBytes } from "../../utils/formatBytes";
import { useUsage } from "../../contexts/UsageContext";
import Avatar from "../../components/Avatar";

// Bottom of the folder tree: avatar, first name and storage used (name and usage hidden when collapsed)
export default function NavUser({ compact }) {
  const { firstName } = useUserProfile();
  const { usage } = useUsage();

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
