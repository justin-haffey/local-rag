# Local RAG Windows x64 installer

`scripts/Build-Installer.ps1` publishes and stages the self-contained Windows x64 host, verifies its embedded LanceDB native package, builds a host-free VSIX, verifies the pinned embedding assets, compiles `LocalRag.iss` with Inno Setup 6, and writes an adjacent SHA-256 file.

The per-user installer places the canonical host in `%LOCALAPPDATA%\Programs\LocalRag\Host\<version>`, installs the VSIX through `code.cmd`, and writes:

- `%LOCALAPPDATA%\LocalRag\installation.json` for installed-host discovery.
- `%LOCALAPPDATA%\LocalRag\install-settings.json` for the selected non-secret provider settings.
- `%LOCALAPPDATA%\LocalRag\models\bge-small-en-v1.5` for the verified ONNX model and vocabulary.

Fresh interactive setup recommends **Embedded LanceDB** and also offers **external local Weaviate**. LanceDB defaults to `%LOCALAPPDATA%\LocalRag\data\lancedb`; Weaviate defaults to `http://localhost:8080`. Setup supports Windows x64 only.

Fresh unattended setup must choose exactly one provider:

```powershell
.\LocalRag-Setup-0.1.0.exe /VERYSILENT /SUPPRESSMSGBOXES /PROVIDER=LanceDb /LANCEDBPATH=C:\LocalRagData\lancedb
.\LocalRag-Setup-0.1.0.exe /VERYSILENT /SUPPRESSMSGBOXES /PROVIDER=Weaviate /WEAVIATEBASEURL=http://localhost /WEAVIATEPORT=8080
```

Provider values are exactly `LanceDb` and `Weaviate`. Provider-specific parameters are used only for the selected provider. A valid existing schema-v1 record is interpreted as Weaviate and rewritten as schema v2. Repair and upgrade preserve the existing provider and settings; a conflicting `/PROVIDER` is refused before installation writes. Corrupt or newer settings also fail closed. Setup never migrates, resets, or silently changes a provider.

Schema-v2 settings are discriminated:

```json
{"schemaVersion":2,"provider":"Weaviate","weaviate":{"endpoint":"http://localhost:8080"}}
```

```json
{"schemaVersion":2,"provider":"LanceDb","lanceDb":{"path":"C:\\Users\\name\\AppData\\Local\\LocalRag\\data\\lancedb"}}
```

The staged host must contain `lancedb-native.manifest.json`, `localrag_lancedb.dll`, and every declared license/inventory file. The build validates schema/ABI/RID, x64 PE machine, exact DLL name, hashes, actual ABI exports, and license files before invoking Inno Setup. Runtime native downloads are prohibited.

VS Code is required and setup fails clearly when `code.cmd` cannot be located or the VSIX installation fails. The Weaviate readiness probe is warning-only and runs only when Weaviate is selected. Setup does not install, configure, start, or stop Weaviate and does not create a Windows service. Uninstall removes the application and extension but intentionally preserves model files, provider settings, LanceDB data, SQLite state, and other Local RAG user data.
