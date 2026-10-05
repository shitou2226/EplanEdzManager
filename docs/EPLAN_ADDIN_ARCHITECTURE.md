# EPLAN 2.9 Add-In Architecture

## Scope

Phase 6 adds a thin EPLAN Electric P8 2.9 integration surface without moving catalog, SQLite, EDZ reading, export, or import logic into EPLAN.

```text
EPLAN Electric P8 2.9.4.14642
        |
        | loads IEplAddIn + IEplAction
        v
EplanEdzManager.EplanAddIn             net472 / x64
        |
        | Add-In protocol 1.0, JSON lines
        | local Named Pipe only
        v
EplanEdzManager.Desktop                net8.0-windows / x64
        |
        v
EplanEdzManager.Application -> Infrastructure.Sqlite / offline EDZ services

EplanEdzManager.EplanBridge            remains a separate net472 process
Desktop <-> Bridge uses the Bridge protocol, not the Add-In protocol.
```

The shared `EplanEdzManager.AddIn.Protocol` project targets `netstandard2.0`. It contains transport DTOs, JSON validation, local pipe naming/client framing, version compatibility, and the single-instance mutex lease. It has no EPLAN API dependency.

## Dependency boundary

| Project | Allowed direct references | Forbidden direct references |
| --- | --- | --- |
| EplanEdzManager.EplanAddIn | AddIn.Protocol, EPLAN 2.9 API assemblies | Desktop, Application, SQLite, SharpCompress, Bridge protocol |
| EplanEdzManager.Desktop | Application, AddIn.Protocol | EplanApi, EplanAddIn |
| EplanEdzManager.Application | Core/infrastructure abstractions already used by the application | EplanApi, EplanAddIn |
| Infrastructure.Sqlite | Core/application contracts already used by SQLite | EplanApi, EplanAddIn |

The Add-In does not write the EPLAN project, devices, assignments, parts database, or settings. It never assigns `PartsService.PartsDatabase`.

## EPLAN entry points

`EplanEdzManagerAddIn` implements:

- `IEplAddIn.OnRegister`: sets `bLoadOnStart = true`.
- `IEplAddIn.OnInit`: checks for EPLAN `2.9.x` and logs without throwing.
- `IEplAddIn.OnInitGui`: adds `EPLAN EDZ Manager` to the end of the Utilities menu.
- `IEplAddIn.OnExit` and `OnUnregister`: make a bounded best-effort disconnect notification.
- `IEplAddInShadowCopy.OnBeforeInit`: remembers the original registered DLL path so configuration is read beside the registered assembly, not from an EPLAN shadow-copy folder.

`OpenManagerAction` implements `IEplAction`. Its registered action name is `EplanEdzManager_Open`.

The original design example used `EplanEdzManager.Open`. That name is not used because the installed EPLAN 2.9 XML documentation for `IEplAction.OnRegister` explicitly says action names containing `.` are not allowed.

Every lifecycle and action entry point has a top-level exception boundary. Failures are logged under `%LOCALAPPDATA%\EplanEdzManager\Logs\AddIn` and do not escape into EPLAN.

## Desktop lifecycle

The Desktop owns one named mutex per Windows user/session:

```text
Local\EplanEdzManager.Desktop.<user-hash>.<session-id>
```

The matching local pipe is:

```text
EplanEdzManager.Desktop.<user-hash>.<session-id>
```

The hash avoids exposing the raw account name. The pipe server uses `.NET 8` `PipeOptions.CurrentUserOnly`; clients always connect to server `.` and no TCP listener is created.

The first Desktop process owns the mutex and starts the pipe server. A later Desktop process sends `OpenManager` to the primary and exits. A crashed process releases the kernel mutex; a new process can acquire it. A live-but-unresponsive primary is not bypassed with a second full instance: the secondary reports a local activation-channel warning and exits.

## Action flow

```text
User clicks Utilities > EPLAN EDZ Manager
  -> verify EPLAN 2.9.x
  -> load the explicit Desktop path from EplanEdzManager.AddIn.json
  -> try Hello against the existing local Desktop pipe
  -> if absent, start Desktop with UseShellExecute=false
  -> wait within the configured startup timeout
  -> send EplanContext
  -> send OpenManager
  -> Desktop updates the context bar and activates the existing window
```

`ProcessStartInfo.ArgumentList` is unavailable on .NET Framework 4.7.2. The Add-In does not need to pass any arguments, so no shell command string or custom escaping is used.

## Read-only EPLAN context

`SelectionSet` is created with both lock defaults set to `false`. The collector reads:

- EPLAN file version and PID;
- active project name and link path through `GetCurrentProject(false)`;
- current/opened page name;
- selected object count and type summary;
- selected `Function.ArticleReferences` part number and variant;
- the `PartsService.PartsDatabase` getter only.

Manufacturer is deliberately left unavailable for selected function article references because `PartNr` and `VariantNr` are the fields proven reliable on that object. The Add-In does not open the active parts database to enrich manufacturer data.

An active parts database is transmitted only when the getter returns a plain rooted local path. SQL/connection-string-shaped or unresolved values are marked `Unsupported` and are never logged or transmitted.

## Multi-instance behavior

Each EPLAN process lifetime gets a unique instance ID containing the EPLAN PID and a random component. Desktop stores connection state by instance ID. A disconnect affects only its matching instance; another connected EPLAN instance remains available and becomes the displayed current context. When more than one instance is connected, the context bar lists each PID, EPLAN version and project evidence without merging their contexts.

Phase 6 intentionally does not add background synchronization. Context is refreshed when the user executes the menu action. Closing EPLAN sends a bounded disconnect notification.
