# Collect cache access evidence

Use `scripts/Get-LockscreenAccessReport.ps1` when application logs stop at cache discovery or an ancestor attribute check. It works in Windows PowerShell 5.1 and PowerShell 7 without the app or additional modules. Copy the single script to the affected machine.

1. Open a **normal, non-administrator PowerShell** window as the Windows user running LockscreenGif. Change to the folder containing the script and run:

   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Get-LockscreenAccessReport.ps1
   ```

2. Keep that window open. Open **PowerShell as administrator**, then paste the comparison command printed by the first run. It explicitly carries the original user's SID and output directory. This matters when the administrator account differs from the app user.
3. Share both `LockscreenAccess-normal-*.json` and `LockscreenAccess-admin-*.json` reports from the first user's Desktop, plus the app version and whether it was running elevated. Keep the machine state unchanged between runs: no new image selection or permission repairs.

`-OutputDirectory 'C:\existing\folder'` selects another report destination. The execution-policy override applies only to the launched PowerShell process; the script does not change the saved execution policy. It does not elevate itself.

Only the JSON report is written. The collector reads attributes, owner/group/access rules and the current process token; it does not change permissions, take ownership, apply an image, lock Windows, or read image contents. It uses no network APIs. Reports include local paths, SIDs, account names from `whoami /all`, and local exception messages: keep them out of source control. There are no analytics events.

## What the comparison answers

| Evidence | Interpretation |
| --- | --- |
| `RootValidation` is blocked on an ancestor but `FolderEnumeration` succeeds | The app's ancestor validation is a distinct blocker from direct cache enumeration. Inspect the denied ancestor's ACL in the administrator report. |
| Attribute reads and enumeration fail normally but succeed as administrator | Elevated inspection can reach the cache. This does not prove the app's scoped repair grants everything the normal process requires. |
| The administrator run also cannot read attributes or ACLs | Ordinary elevation is insufficient for those probes. Preserve the HRESULT and token details; the script does not try SYSTEM access or change ownership. |
| `FolderEnumeration.Status=Missing` | Required cache path could not be found. Missing is distinct from access denied. |
| `FolderEnumeration.Status=Ok` with no entries | Enumeration completed without finding matching lock-screen folders. Check `Truncated` before treating an inventory as complete. |
| A visible reparse point | Descendant inspection is skipped. Do not weaken link checks based on this report. |

The collector reproduces the app's `File.GetAttributes` ancestor checks and separately attempts direct folder enumeration. It captures ACL access rules including inheritance and numeric rights, keeping failures per probe instead of aborting the report. It does not use `Test-Path` to decide whether the cache exists: an inaccessible path must not be mislabeled as missing.

A readable cache beneath an unreadable SID parent identifies an ancestor-validation obstacle. A Read grant does not establish write access, and SYSTEM ownership does not tell when permissions changed. Administrator membership alone does not guarantee attribute access. The app helper uses temporary backup-enabled metadata inspection, grants the fixed SID parent metadata rights, and repairs only required cache targets; see [permission recovery](diagnostics.md#what-the-evidence-means). This collector deliberately retains ordinary attribute probes and never enables privileges or repairs access.

`Error.NativeErrorCode` preserves `Win32Exception` codes: native 5 is access denied even with generic HRESULT `0x80004005`; native 2/3 identifies a missing file/path. Classification uses numeric codes, never localized text. Older reports without the native code may leave an ACL probe classified as `Error`; compare independent attribute evidence instead of interpreting every generic HRESULT as access denied.

Enumeration is nonrecursive and limited to 32 matching cache folders and 128 existing variants per folder. The known main-image path is also inspected. Reads of visible links and their descendants are skipped; these sequential metadata checks are not an atomic security validation. A denied ancestor's attributes do not prevent independent direct probes of the known cache path. No file-content reads or write-access tests are performed, so successful metadata collection does not establish copy access or playback. PowerShell's .NET runtime can differ from the app's; both runtime and Windows build are recorded.

Microsoft documents the independent operations in [File.GetAttributes](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.getattributes), [Directory.EnumerateDirectories](https://learn.microsoft.com/en-us/dotnet/api/system.io.directory.enumeratedirectories), and [Get-Acl](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.security/get-acl). The script requests owner/group/DACL information, not auditing information.

Run synthetic checks with:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Tests/LockscreenAccessReport.Tests.ps1
```

The checks exercise missing versus denied paths, bounded enumeration, literal filenames, readable child caches beneath synthetic denied ancestors, visible-link rejection, preserved target SIDs, command quoting, and unchanged fixture permissions/contents. They do not exercise a real protected Windows cache or elevation.
