import { FiMonitor, FiMoon, FiSun } from "react-icons/fi";
import { THEME_OPTIONS, setThemePreference, useTheme } from "../utils/theme";

const LABELS = { system: "System theme", light: "Light theme", dark: "Dark theme" };
const ICONS = { system: FiMonitor, light: FiSun, dark: FiMoon };

// Header button that cycles System -> Light -> Dark
export default function ThemeToggle() {
    const { preference } = useTheme();
    const next = THEME_OPTIONS[(THEME_OPTIONS.indexOf(preference) + 1) % THEME_OPTIONS.length];
    const Icon = ICONS[preference];

    return (
        <button
            type="button"
            className="theme-toggle"
            onClick={() => setThemePreference(next)}
            title={`${LABELS[preference]} (click for ${LABELS[next].toLowerCase()})`}
            aria-label={`${LABELS[preference]}. Switch to ${LABELS[next].toLowerCase()}`}
        >
            <Icon size={18} aria-hidden="true" />
        </button>
    );
}
