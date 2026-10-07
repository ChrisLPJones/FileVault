// 0 -> "0 B", 1536 -> "1.5 KB", 1073741823 -> "1 GB" (picks the unit after rounding)
export const formatBytes = (bytes, decimals = 1) => {
    const units = ["B", "KB", "MB", "GB", "TB"];
    let value = Math.max(0, Number(bytes) || 0);
    let unit = 0;

    while (unit < units.length - 1 && Number(value.toFixed(decimals)) >= 1024) {
        value /= 1024;
        unit++;
    }

    const rounded = unit === 0 ? Math.round(value) : Number(value.toFixed(decimals));
    return `${rounded} ${units[unit]}`;
};
