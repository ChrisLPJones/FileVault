import { createFolderAPI } from "../api/createFolderAPI";
import { getAllFilesAPI } from "../api/getAllFilesAPI";

// Helpers for uploading whole folders: reading what was dropped or chosen as
// { file, dir } pairs (dir is the folder path inside the upload, "" for loose files),
// recreating those folders on the server, and limiting how many uploads run at once.

const readAllEntries = (directory) =>
    new Promise((resolve, reject) => {
        const reader = directory.createReader();
        const entries = [];
        // readEntries returns the contents in batches until it returns an empty one
        const next = () =>
            reader.readEntries((batch) => {
                if (batch.length === 0) resolve(entries);
                else {
                    entries.push(...batch);
                    next();
                }
            }, reject);
        next();
    });

const walk = async (entry, dir) => {
    if (entry.isFile) {
        const file = await new Promise((resolve, reject) => entry.file(resolve, reject));
        return [{ file, dir }];
    }
    const path = dir ? `${dir}/${entry.name}` : entry.name;
    const children = await readAllEntries(entry);
    return (await Promise.all(children.map((child) => walk(child, path)))).flat();
};

// Files (and the files inside folders) from a drop event
export const readDroppedItems = async (dataTransfer) => {
    // webkitGetAsEntry must be called before the drop event handler returns
    const entries = Array.from(dataTransfer.items ?? [])
        .filter((item) => item.kind === "file")
        .map((item) => item.webkitGetAsEntry?.())
        .filter(Boolean);

    if (entries.length === 0) return Array.from(dataTransfer.files).map((file) => ({ file, dir: "" }));
    return (await Promise.all(entries.map((entry) => walk(entry, "")))).flat();
};

// Files from an <input webkitdirectory>: their path inside the chosen folder is webkitRelativePath
export const filesFromFolderInput = (fileList) =>
    Array.from(fileList).map((file) => {
        const parts = (file.webkitRelativePath || file.name).split("/");
        return { file, dir: parts.slice(0, -1).join("/") };
    });

// Returns dir => Promise<folder id>, creating each folder under baseFolder once (or reusing a
// folder that already has that name there). Folders are created parents first.
export const createFolderResolver = (baseFolder, knownFiles) => {
    const basePath = baseFolder?.path ?? "";
    const cache = new Map([["", Promise.resolve(baseFolder?._id ?? null)]]);
    let files = knownFiles ?? [];

    const findFolder = (path) => files.find((f) => f.isDirectory && f.path === path);

    const ensure = (dir) => {
        if (cache.has(dir)) return cache.get(dir);

        const slash = dir.lastIndexOf("/");
        const parentDir = slash < 0 ? "" : dir.slice(0, slash);
        const name = dir.slice(slash + 1);
        const path = `${basePath}/${dir}`;

        const promise = ensure(parentDir).then(async (parentId) => {
            const existing = findFolder(path);
            if (existing) return existing._id;
            try {
                return (await createFolderAPI(name, parentId)).data._id;
            } catch (error) {
                // Created meanwhile (e.g. in another tab): look it up
                if (error.response?.status !== 409) throw error;
                files = (await getAllFilesAPI()).data;
                const found = findFolder(path);
                if (!found) throw error;
                return found._id;
            }
        });
        cache.set(dir, promise);
        promise.catch(() => cache.delete(dir)); // let a retry try again
        return promise;
    };

    return ensure;
};

// Run at most `limit` tasks at a time: run(task) resolves with the task's result
export const createLimiter = (limit) => {
    let active = 0;
    const queue = [];

    const next = () => {
        if (active >= limit || queue.length === 0) return;
        active++;
        const { task, resolve, reject } = queue.shift();
        Promise.resolve()
            .then(task)
            .then(resolve, reject)
            .finally(() => {
                active--;
                next();
            });
    };

    return (task) =>
        new Promise((resolve, reject) => {
            queue.push({ task, resolve, reject });
            next();
        });
};
