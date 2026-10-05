# EPLAN Add-In Protocol 1.0

## Transport

- Windows Named Pipes only; no TCP or HTTP server.
- Pipe name: `EplanEdzManager.Desktop.<user-hash>.<session-id>`.
- Desktop server ACL: current Windows user only.
- UTF-8 JSON, one message per line.
- One request and one response per connection.
- Maximum accepted request/response size: 1 MiB.
- This protocol is independent of `EplanEdzManager.EplanBridge.Protocol`.

## Envelope

Every message contains:

```json
{
  "protocolVersion": "1.0",
  "messageType": "Hello",
  "instanceId": "eplan-1234-...",
  "timestamp": "2026-10-03T06:00:00.0000000+00:00"
}
```

Missing envelope fields are rejected. Unknown JSON fields are ignored for forward compatibility. A protocol version other than `1.0` returns error code `ADDIN001`. Unknown message types or invalid context status values return `ADDIN002`.

## Messages

| Message | Direction | Purpose |
| --- | --- | --- |
| `Hello` | Add-In/secondary Desktop -> Desktop | Identifies EPLAN version, PID and Add-In version, or establishes a secondary Desktop client. |
| `OpenManager` | Add-In/secondary Desktop -> Desktop | Activates the existing main window. |
| `EplanContext` | Add-In -> Desktop | Sends a read-only EPLAN snapshot. |
| `Ping` | client -> Desktop | Checks reachability without starting an operation. |
| `Disconnect` | Add-In -> Desktop | Marks one EPLAN instance offline. |
| `Shutdown` | reserved client -> Desktop | Protocol-defined optional message; Phase 6 does not expose a remote shutdown UI. |
| `DesktopStatus` | Desktop -> client | Successful acknowledgement and Desktop version/status. |
| `Error` | Desktop -> client | Structured local protocol error. |

## Evidence status model

Every optional scalar EPLAN context field is an object with:

```json
{
  "status": "Available",
  "value": "Example",
  "detail": ""
}
```

Exactly four status values are valid:

- `Available`: the EPLAN 2.9 API returned a usable value.
- `Unavailable`: the API is supported but the current state has no value, for example no project is open.
- `Unsupported`: Phase 6 intentionally does not expose the value, for example a SQL connection string.
- `Unknown`: the read failed or reliability could not be established.

Empty strings are not used to imply one of these states.

## Context example

```json
{
  "protocolVersion": "1.0",
  "messageType": "EplanContext",
  "instanceId": "eplan-1234-4d3c...",
  "timestamp": "2026-10-03T06:00:00Z",
  "eplanVersion": { "status": "Available", "value": "2.9.4.14642", "detail": "" },
  "eplanProcessId": { "status": "Available", "value": "1234", "detail": "" },
  "projectName": { "status": "Unavailable", "value": "", "detail": "No active project is open." },
  "projectPath": { "status": "Unavailable", "value": "", "detail": "No active project is open." },
  "projectDirectory": { "status": "Unavailable", "value": "", "detail": "No active project is open." },
  "pageName": { "status": "Unavailable", "value": "", "detail": "No page is open in the graphical editor." },
  "selectionSummary": { "status": "Available", "value": "0 object(s)", "detail": "" },
  "activePartsDatabase": { "status": "Unsupported", "value": "", "detail": "The active database is not a plain local file path; connection details are not transmitted." },
  "selectedParts": {
    "status": "Unavailable",
    "detail": "No objects are selected.",
    "items": []
  }
}
```

## Error codes

| Code | Meaning |
| --- | --- |
| `ADDIN001` | Client/Desktop protocol version mismatch. |
| `ADDIN002` | Invalid envelope, message type, or context evidence status. |
| `ADDIN101` | Desktop unavailable (reserved for client-facing diagnostics). |

Exceptions are not used as the public protocol contract. `Error` carries `errorCode`, `userMessage`, and bounded local `technicalDetails`.
