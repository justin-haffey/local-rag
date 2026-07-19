# Local RAG Windows installer

`scripts/Build-Installer.ps1` publishes the self-contained Windows x64 host, builds a host-free VSIX, verifies the pinned embedding assets, compiles `LocalRag.iss` with Inno Setup 6, and writes an adjacent SHA-256 file.

The per-user installer places the canonical host in `%LOCALAPPDATA%\Programs\LocalRag\Host\<version>`, installs the VSIX through `code.cmd --install-extension ... --force`, and writes:

- `%LOCALAPPDATA%\LocalRag\installation.json` for installed-host discovery.
- `%LOCALAPPDATA%\LocalRag\install-settings.json` for the non-secret external Weaviate endpoint.
- `%LOCALAPPDATA%\LocalRag\models\bge-small-en-v1.5` for the verified ONNX model and vocabulary.

Interactive setup asks for a base URL and port, defaulting to `http://localhost` and `8080`. Silent deployment uses standard Inno switches plus `/WEAVIATEBASEURL=<url>` and `/WEAVIATEPORT=<port>`; for example:

```powershell
.\LocalRag-Setup-0.1.0.exe /VERYSILENT /SUPPRESSMSGBOXES /WEAVIATEBASEURL=http://localhost /WEAVIATEPORT=8080
```

VS Code is required and setup fails clearly when `code.cmd` cannot be located or the VSIX installation fails. The final Weaviate readiness probe is warning-only. Setup never installs, configures, starts, or stops Weaviate and does not create a Windows service. Uninstall removes the application and VS Code extension but intentionally preserves model files, `install-settings.json`, and other Local RAG user data.
