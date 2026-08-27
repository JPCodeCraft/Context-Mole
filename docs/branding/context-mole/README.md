# Context Mole branding

The five supplied illustrations are stored as transparent PNG source assets in [`originals`](originals). `context-mole-01-app-icon.png` is the application-icon source; files 02 through 05 are retained as visual references.

The approved originals and their source shares are:

| File | SHA-256 | Source |
| --- | --- | --- |
| `context-mole-01-app-icon.png` | `D0E7F064F962583C4166F5EB36D7927062338D0959C188AB53815250A1AF63DB` | [share](https://chatgpt.com/s/m_6a9050499c208191bd70e7a3dbe72291) |
| `context-mole-02-reference.png` | `6B9493B4D765FEAB809EAC9BBF19D717FD7DA9B6814CB83BAD7D8D33DF572F7C` | [share](https://chatgpt.com/s/m_6a9051c017dc8191aa4afb44053390c8) |
| `context-mole-03-reference.png` | `95B401CF4739255071CE2D87BE6DBB3168F5EDF877FED356A254F054C55E30D8` | [share](https://chatgpt.com/s/m_6a9051bc74c481919129f6b365b50fa0) |
| `context-mole-04-reference.png` | `39419D7D75F5E78BF15F4C04E348919350907AD27D31677DFA4F9A603134FAAF` | [share](https://chatgpt.com/s/m_6a9051dd63708191b5717786cf538071) |
| `context-mole-05-reference.png` | `A42FBB714A648F4868FE297A598344D024704C0391D714E871E74B7A0B5EC7F1` | [share](https://chatgpt.com/s/m_6a9051e9fa888191b2d4b8f00e169959) |

The Windows application and installer use `src/App.UI/Assets/context-mole.ico`. It contains 16, 24, 32, 48, 64, 128, and 256 pixel RGBA frames generated from the first illustration. Run `tools/GenerateWindowsIcon.ps1` to regenerate it and `tools/ValidateWindowsBranding.ps1` to verify the source hash, frame sizes, transparency, and application references.
