// Where to go after logging in: the page the visitor was sent from (router state `from`), if it is
// a path inside this app. Anything else, such as "//other.site" or "https://other.site", is ignored.
export const returnPathAfterLogin = (state) => {
    const from = state?.from;
    if (typeof from !== "string" || !from.startsWith("/") || from.startsWith("//") || from.includes("\\")) return "/dashboard";
    // Control characters are refused (URL parsing strips a tab or newline, so "/<tab>/x" would become "//x"),
    // and so is anything that would not resolve to this origin
    // eslint-disable-next-line no-control-regex
    if (/[\u0000-\u001f\u007f]/.test(from)) return "/dashboard";
    try {
        if (new URL(from, window.location.origin).origin !== window.location.origin) return "/dashboard";
    } catch {
        return "/dashboard";
    }
    return from;
};
