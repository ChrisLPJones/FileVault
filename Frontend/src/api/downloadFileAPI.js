export const downloadFile = async (files) => {
    if (files.length === 0) return;

    try {
        const token = localStorage.getItem("token");
        if (!token) throw new Error("No auth token found");

        for (const file of files) {
            const url = `${import.meta.env.VITE_API_BASE_URL}/download/${file._id}`;

            // Fetch the file with Authorization header
            const response = await fetch(url, {
                headers: {
                    Authorization: `Bearer ${token}`,
                },
            });

            if (!response.ok) {
                throw new Error(`Download failed with status ${response.status}`);
            }

            const blob = await response.blob();
            const downloadUrl = URL.createObjectURL(blob);

            const link = document.createElement("a");
            link.href = downloadUrl;
            link.download = file.name || "download";
            document.body.appendChild(link);
            link.click();
            link.remove();
            URL.revokeObjectURL(downloadUrl);
        }
    } catch (error) {
        console.error(error);
        return error;
    }
};
