import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import RecycleBin from "./RecycleBin";
import { getTrashAPI } from "../../api/trashAPI";

vi.mock("../../api/trashAPI", () => ({
  getTrashAPI: vi.fn(),
  restoreTrashAPI: vi.fn(),
  deleteFromTrashAPI: vi.fn(),
  emptyTrashAPI: vi.fn(),
}));
vi.mock("../../contexts/RecycleBinContext", () => ({ useRecycleBin: () => ({ isBinOpen: true }) }));
vi.mock("../../contexts/SearchContext", () => ({ useSearch: () => ({ term: "" }) }));

describe("Recycle bin", () => {
  it("explains what the permanent delete buttons do, even while they are disabled", async () => {
    getTrashAPI.mockResolvedValue([]);
    render(<RecycleBin />);
    await screen.findByText(/stay here until you empty the bin/);

    const del = screen.getByRole("button", { name: /Delete permanently/ });
    const empty = screen.getByRole("button", { name: "Empty bin" });
    expect(del).toBeDisabled();
    expect(empty).toBeDisabled();
    expect(del.parentElement).toHaveAttribute("title", "Delete the selected items for good. This can't be undone.");
    expect(empty.parentElement).toHaveAttribute("title", "Delete everything in the bin for good. This can't be undone.");
  });
});
