# Context Mole branding

The seven supplied illustrations are stored as PNG source assets in [`originals`](originals). `context-mole-01-app-icon.png` is the application-icon source; files 02 through 07 are retained as visual references. All have transparent backgrounds except reference 06, whose original includes a rendered background.

The approved originals are identified by their hashes:

| File | SHA-256 |
| --- | --- |
| `context-mole-01-app-icon.png` | `D0E7F064F962583C4166F5EB36D7927062338D0959C188AB53815250A1AF63DB` |
| `context-mole-02-reference.png` | `6B9493B4D765FEAB809EAC9BBF19D717FD7DA9B6814CB83BAD7D8D33DF572F7C` |
| `context-mole-03-reference.png` | `95B401CF4739255071CE2D87BE6DBB3168F5EDF877FED356A254F054C55E30D8` |
| `context-mole-04-reference.png` | `39419D7D75F5E78BF15F4C04E348919350907AD27D31677DFA4F9A603134FAAF` |
| `context-mole-05-reference.png` | `A42FBB714A648F4868FE297A598344D024704C0391D714E871E74B7A0B5EC7F1` |
| `context-mole-06-reference.png` | `AED61BEDA6444C5C74EBA3372DC79B79E864969AEFA43FEB273F52F5BFAEFB87` |
| `context-mole-07-reference.png` | `24D2E602886767AE31FA19AC11A07D704BE214F249F03B820367EB6CB1E02E65` |

The Windows application and installer use `src/App.UI/Assets/context-mole.ico`. It contains 16, 24, 32, 48, 64, 128, and 256 pixel RGBA frames generated from the first illustration. Run `tools/GenerateWindowsIcon.ps1` to regenerate it and `tools/ValidateWindowsBranding.ps1` to verify the source hash, frame sizes, transparency, and application references.
