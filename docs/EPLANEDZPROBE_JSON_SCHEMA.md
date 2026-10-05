# EplanEdzProbe JSON Contract 1.0

本文件定义 Phase 1 `--json` 的稳定契约。字段名为 camelCase；枚举序列化为字符串；可缺失的业务值显式输出 `null`。

## Envelope

```json
{
  "schemaVersion": "1.0",
  "command": "inspect",
  "success": true,
  "data": {},
  "diagnostics": [],
  "runtimeMetrics": {
    "peakManagedMemoryBytes": 0,
    "peakWorkingSetBytes": 0,
    "totalElapsedMilliseconds": 0,
    "commandElapsedMilliseconds": 0,
    "gen0Collections": 0,
    "gen1Collections": 0,
    "gen2Collections": 0
  }
}
```

顶层六个字段在 schema `1.0` 中保持存在。`success=false` 不表示进程崩溃；调用方应同时读取 diagnostics 和退出码。

## DiagnosticRecord

```json
{
  "severity": "Error",
  "code": "EDZ301",
  "message": "A manifest resource reference does not exist in the archive.",
  "packageKey": "PART-1",
  "entryPath": "items/picture/missing.jpg",
  "exceptionType": null,
  "evidenceLevel": "Confirmed"
}
```

`severity` 为 `Information | Warning | Error | Fatal`；`evidenceLevel` 为 `Confirmed | Likely | Hypothesis | Unknown`。

## Command data

| command | `data` 形状 |
|---|---|
| `inspect` | object：format、archiveSize、entryCount、manifest、manifestVersion、packageCount、resourceCount、compression、isSolid、isEncrypted、missingReferenceCount、unreferencedEntryCount、timingsMilliseconds |
| `parts` | `PartRecord[]`，受 `--limit` 约束 |
| `search` | 匹配的 `PartRecord[]`，不区分大小写，受 `--limit` 约束 |
| `part` | `{ metadata, resources }`；找不到时为 `null` 且 `success=false` |
| `resources` | resource array；包不存在或没有引用时为空且 `success=false` |
| `validate` | object：archiveValid、manifestValid、entryCount、packageCount、missingReferenceCount、unreferencedEntryCount、duplicatePackageKeyCount、duplicatePathCount |

`PartRecord` 字段：`manufacturer`、`partNumber`、`typeNumber`、`orderNumber`、`description`、`productGroup`、`variant`、`variants`、`packageKey`、`sourceEdz`、`rawMetadataReference`、`unknownAttributes`、`unknownElements`。

resource 字段：`type`、`name`、`locator`、`path`、`exists`、`size`、`compressedSize`、`compression`、`referenceCount`、`unknownAttributes`。`compressedSize` 来自 archive 库的 entry metadata；对某些 7z folder 它可能为 `0`，不能当作资源实际未压缩的证据。

## Compatibility rule

- schema `1.x` 可新增字段和新的 diagnostic code；调用方必须忽略未知字段；
- 删除字段、改变现有字段类型/含义或改变 command data 根形状，需要提高 major version；
- `unknownAttributes` 和 `unknownElements` 是前向兼容数据，不保证字段顺序；
- 数组顺序在 Phase 1 中跟随 manifest/archive 顺序，但调用方不应把它当作跨版本排序契约。
