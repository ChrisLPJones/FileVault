import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import { defineConfig, globalIgnores } from 'eslint/config'

export default defineConfig([
  globalIgnores(['dist']),
  {
    files: ['**/*.{js,jsx}'],
    extends: [
      js.configs.recommended,
      reactHooks.configs.flat.recommended,
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      ecmaVersion: 2020,
      globals: globals.browser,
      parserOptions: {
        ecmaVersion: 'latest',
        ecmaFeatures: { jsx: true },
        sourceType: 'module',
      },
    },
    rules: {
      'no-unused-vars': ['error', { varsIgnorePattern: '^[A-Z_]' }],
    },
  },
  {
    // The file manager UI was adapted from @cubone/react-file-manager (see FileManager/README.md).
    // It predates eslint-plugin-react-hooks v7's React Compiler rules and syncs state from props in
    // effects in many places. Those are style/performance findings, not bugs, and rewriting them
    // changes UI behaviour, so they're reported as warnings here until the code is refactored and
    // re-tested in the browser. Our own code (pages, api, services, utils) keeps them as errors.
    files: [
      'src/FileManager/**/*.{js,jsx}',
      'src/contexts/**/*.{js,jsx}',
      'src/components/Collapse/**/*.{js,jsx}',
      'src/components/ContextMenu/**/*.{js,jsx}',
    ],
    rules: {
      'react-hooks/set-state-in-effect': 'warn',
      // False positives: passing `someHook().ref` to a ref prop is read as a ref access during render
      'react-hooks/refs': 'warn',
    },
  },
  {
    // Context files export a Provider and its useX() hook together. This only limits Vite's
    // hot reload for these files during development; it has no effect on the built app.
    files: ['src/contexts/**/*.{js,jsx}'],
    rules: {
      'react-refresh/only-export-components': 'off',
    },
  },
])
