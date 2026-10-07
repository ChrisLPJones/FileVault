# File manager UI

The file manager on the dashboard is adapted from
[@cubone/react-file-manager](https://github.com/Saifullah-dev/react-file-manager)
by Saifullah Zubair, used under the MIT License (reproduced below).

## Which code came from it

Copied from the package and then modified for FileVault:

- `src/FileManager/` (toolbar, breadcrumb, navigation pane, file list, actions)
- `src/contexts/` (files, navigation, selection, clipboard, layout, translation)
- `src/components/` (except `ProtectedRoute`, `PublicRoute` and `ServerStatus`)
- `src/hooks/`, `src/validators/`, `src/constants/`, `src/locales/`
- `src/utils/` (except `auth.js` and `passwordRules.js`)

## FileVault changes

- Uploads send the JWT and refresh it before it expires
- Previews and downloads load through the authenticated API client, by file ID
- Rename waits for the API instead of removing the item first
- Upload errors show the server's message (e.g. storage quota exceeded)
- The preview dialog is titled with the selected file's name
- Removed the unused `filePreviewPath` prop

Some of this code still syncs state from props inside effects, which
eslint-plugin-react-hooks v7 reports; see the override in `eslint.config.js`.

## License

MIT License

Copyright (c) 2024 Saifullah Zubair

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
