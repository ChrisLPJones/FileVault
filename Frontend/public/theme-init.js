// Apply the saved theme before the page renders (see src/utils/theme.js).
// A separate file rather than an inline script, so the Content-Security-Policy can forbid inline scripts.
(function () {
  var preference = "system";
  try { preference = localStorage.getItem("fv-theme") || "system"; } catch { /* storage blocked: follow the system */ }
  var dark = preference === "dark" ||
    (preference !== "light" && window.matchMedia("(prefers-color-scheme: dark)").matches);
  document.documentElement.dataset.theme = dark ? "dark" : "light";
  // Mac users get Finder-blue folders (see styles/theme.css)
  var platform = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || "";
  if (/mac/i.test(platform)) document.documentElement.dataset.platform = "mac";
})();
