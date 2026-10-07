// Centre-crop an image file to a square and scale it to size x size pixels.
// Done in the browser so uploads are small; returns a WebP blob (PNG where WebP isn't supported).
export const resizeImageToSquare = async (file, size = 256) => {
    let bitmap;
    try {
        bitmap = await createImageBitmap(file);
    } catch {
        throw new Error("That file isn't an image this browser can read. Try a PNG, JPEG or WebP.");
    }

    const side = Math.min(bitmap.width, bitmap.height);
    const canvas = document.createElement("canvas");
    canvas.width = size;
    canvas.height = size;

    const context = canvas.getContext("2d");
    context.imageSmoothingQuality = "high";
    context.drawImage(bitmap, (bitmap.width - side) / 2, (bitmap.height - side) / 2, side, side, 0, 0, size, size);
    bitmap.close();

    const toBlob = (type, quality) => new Promise((resolve) => canvas.toBlob(resolve, type, quality));
    const webp = await toBlob("image/webp", 0.9);
    return webp?.type === "image/webp" ? webp : toBlob("image/png");
};
