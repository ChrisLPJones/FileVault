// True while the thing being dragged is files from the computer, not one of our own rows
// (those carry no "Files" type, so moving and copying inside the app are left alone)
export const isOsFileDrag = (e) => Array.from(e.dataTransfer?.types ?? []).includes("Files");
