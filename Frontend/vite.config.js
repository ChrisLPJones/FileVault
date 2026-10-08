import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// https://vite.dev/config/
export default defineConfig({
    plugins: [react()],
    // Unit and component tests (npm test): Vitest with Testing Library in a simulated browser
    test: {
        environment: "jsdom",
        setupFiles: ["./src/test/setup.js"],
        include: ["src/**/*.test.{js,jsx}"],
        env: { VITE_API_BASE_URL: "http://api.test" },
        restoreMocks: true,
    },
});
