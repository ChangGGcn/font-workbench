# Workbench font management

`Management.csproj` builds `FontBook.Workbench.Management.dll` for `net10.0-windows`, with no WPF or old application dependency. The desktop uses `FontBook.Management.IManagementService`; UI tests can inject a no-write substitute via `MainWindow(string shelfStatePath, IManagementService management)`.

- The desktop supplies a read-only installed-catalogue snapshot of exact path + physical face index for each style. It caches one ownership index per snapshot and invalidates it on catalogue changes / `WM_FONTCHANGE`. Different families sharing a path and incomplete inventories cannot authorize forward operations. The library also compares each selected file's physical indices and rejects ambiguous file handles, reparse paths and hard links.
- Preflight does not create directories, keys, logs or backups. Production plans are tied to their issuing service, single-use, and checked again under the original transaction mutex at execution. Only explicitly confirmed and eligible rows run. Each family has its own recoverable transaction; reported partial failures are not rolled into a batch success.
- New journals use the original DPAPI/HMAC machinery in `%LOCALAPPDATA%\FontBook.Workbench\management\{ops,vault}` with a separate signing domain. `%LOCALAPPDATA%\FontBook\{ops,vault}` is read only; a legacy record can be recognized and displayed but never executed by the workbench. Recovery of a workbench record uses the linked engine's authenticated journal, hashes, and no-overwrite rollback.
- The catalogue and Windows registry are not a shared atomic snapshot. External processes that change font state without sending a notification can leave the catalogue stale; inspect a refreshed catalogue before retrying. The original engine's protections against concurrent writers are retained, but Windows does not provide an atomic compare-and-delete for these registry values.

## Isolated checks

Numeric registry values (DWORD/QWORD) in the Fonts keys are metadata, not file paths. They are neither candidate registrations nor undo targets. Unclassified binary/multi-string values and incomplete registry reads still fail closed with the offending hive, value and type. A read-only batch uses one registry census. Execution independently rereads and verifies the exact registrations under the existing lock. Uninstall tests read/delete access, not permission to write font contents.

The standard window offers an explicit `runas` restart for eligible machine-scope work. The bounded bookmark contains account SID, selected indices, action and an evidence digest; it is not an execution capability. The receiving window reconstructs the catalogue and the plan and asks for confirmation again. The old preview process closes after successful launch. On a confirmed uninstall the working window releases its own native font views and waits for outstanding reads before touching the registry. External mappings and permissions can still block an unlink; partial progress stays recoverable.

```powershell
dotnet run --project workbench/tests/Management.Checks/Management.Checks.csproj -c Release
```

The entry runs the legacy safety harness, batch checks, and the Windows backend against a process-private `RegLoadAppKeyW` registry file. A self-authored two-glyph TTF is created, actually unlinked, then recovered byte-for-byte with its exact registry type. No fonts are installed into Windows, no predefined registry keys are changed, and no UAC prompt is launched by the tests. Ui.Checks exercises the same round trip from the two confirmation dialogs while the synthetic font has a live DirectWrite preview. If the default temp path is not writable, point `TEMP` and `TMP` at a writable scratch directory.
