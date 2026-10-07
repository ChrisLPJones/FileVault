// Connect screen: pick the FileVault server, or retry after a connection failure
const params = new URLSearchParams(location.search);
const form = document.getElementById("connect-form");
const input = document.getElementById("server");
const message = document.getElementById("message");
const connectButton = document.getElementById("connect");
const retryButton = document.getElementById("retry");

const showMessage = (text) => {
  message.textContent = text;
  message.hidden = !text;
};

input.value = params.get("server") || "";
if (params.get("message")) {
  showMessage(params.get("message"));
  retryButton.hidden = false; // only offered after a failed load
}
input.focus();
input.select();

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  showMessage("");
  connectButton.disabled = true;
  connectButton.textContent = "Connecting…";

  const result = await window.desktop.connect(input.value);
  if (!result.ok) {
    showMessage(result.error);
    connectButton.disabled = false;
    connectButton.textContent = "Connect";
  }
});

retryButton.addEventListener("click", () => {
  showMessage("");
  retryButton.disabled = true;
  window.desktop.retry();
});
