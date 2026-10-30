// Saves a .NET stream as a file download. Used where a secret (a passphrase) must not travel in a URL.
window.yanmtDownload = async (fileName, streamRef) => {
    const blob = new Blob([await streamRef.arrayBuffer()], { type: "application/octet-stream" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
};
