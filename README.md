# PC Cache Cleaner Pro

A portable Windows 10/11 cache, temp and junk cleaner with a proper window, built with C# / WPF on
.NET Framework 4.8 (already part of Windows, nothing to install). It is the **fuller-featured sibling** of
[PC Cache Cleaner (Standard)](https://github.com/Ivan-Ryukendo/PC-Cache-Cleaner), the small PowerShell tool.

> **Pre-release (v1.3.0-beta.2).** Not yet tested by the owner on an elevated run. Run it with `--dryrun`
> first and read the list before cleaning.

## What it does

| Tab | What you get |
|---|---|
| **Cleanup** | Auto-detected GPU / browser / app / dev / Windows caches with sizes, risk colours, *Select safe*, presets (Safe only / Developer / Deep clean / Custom, saved to `%APPDATA%\CleanPC-Pro\settings.json`), double-click a row to see every path. Risky items start unticked and need an extra confirmation. |
| **Cleanup > Locked files** | After a clean, files that were skipped because a program is using them are listed **per program** (name, PID, files held, about how many bytes) using the Windows Restart Manager. Read-only by itself. Optional, per program: *Close this program* (confirmation naming it and warning about unsaved work; polite close first, waits about 5 s; a forced end needs a second explicit confirmation; never offered for system/critical processes or this program; nothing is ever closed automatically or in bulk). *Retry skipped items* then re-runs only the skipped files through the same guarded delete. A small non-modal popup after a clean summarises freed space and the locked count (no tray icon, no background service). |
| **Large files** | The N biggest files per selected drive; *Open location*; ticked files go to the **Recycle Bin only**. |
| **Duplicates** | Group by size, then partial hash, then full SHA-256. Never auto-selected, at least one copy per group is always kept, Recycle Bin only, system folders skipped. |
| **Dev projects** | Stale `node_modules`, Rust `target`, `.venv`, `__pycache__`, `.gradle`, `build` folders whose project was untouched for N days (adjustable). Regenerable only, unticked by default. |
| **Startup** | Run keys, startup folders, non-Microsoft scheduled tasks with publisher / signature. Disable / enable is **reversible** (StartupApproved / task disable); nothing is deleted. |
| **Space history** | Free space per drive at every run (chart + list) and a warning when it dropped by more than X GB since the last run. |
| **Security check** | Read-only heuristics (suspicious paths, look-alike system names, unsigned processes, hidden persistence, sustained CPU), SHA-256 and a *Check on VirusTotal* button (opens the hash page in your browser - **nothing is uploaded**), network connections of flagged processes, Defender status, browser extension names (names only). |
| **Report-only** | Big virtual disks (WSL/Docker/Application Guard/WSA), shadow copies, fnm Node versions, orphaned profile folders - with the correct commands shown as text, never run. Plus maintenance buttons (DISM component cleanup, Storage Sense, Disk Cleanup) that explain themselves and ask first. |
| **Other profiles** | Opt-in, DANGER ZONE: other Windows accounts' profiles; you must type the profile name; deleted through `Win32_UserProfile`. |
| **Who is using space** | Read-only. Running programs mapped to disk usage: the install folder plus per-user/per-machine folders in AppData / ProgramData named after the program (matched by name only, so always labelled *likely*), biggest first, with *Open location*. Sized on a background thread, junctions never followed, Stop cancels. |
| **History / Settings** | Cleaning history, exclusion list (never scanned, refused at delete time), light/dark/follow-Windows, restore point before cleaning, HTML/CSV export, weekly scheduled safe clean (Task Scheduler). |

Also: a low-free-space banner (< 5 GB on a selected drive), a user-clicked **update checker** (no automatic network calls ever), and a
headless mode: `CleanPC-Pro.exe --dryrun | --auto --safe [--drives C,D | --all-drives]`.

## Safety contract (identical to the Standard edition)

Deletes cache/temp **contents** only, keeping the folders. **Never** touches personal files, browser
sessions/cookies/logins/history/bookmarks/passwords, installed programs/settings, or saved games.
Locked files are skipped (logged as `SKIP path: reason`, and kept as path + reason so they can be diagnosed), junctions/symlinks are never followed, drive roots /
Program Files / Windows (except an allow-list of caches) / WinSxS / Installer / System Volume Information / user
profile roots / Documents / Desktop / Downloads / Pictures are refused by `CleanGuard` **at delete time**, and nothing in the list is
hidden from you before deletion. *Cancel* stops between items, never mid-delete.

**Closing programs (Pro only).** The Standard edition's security check never kills processes, and neither does this
edition's. The only place Pro can end a process is the *Locked files* panel, one program at a time, after you confirm it
(and confirm again for a forced end). It is not part of the Standard edition and is not run by `--auto`, the schedule or
the updater. No telemetry and no network calls are involved in any of this.

## Relationship to the Standard edition

| | Standard (`CleanPC.exe`) | Pro (`CleanPC-Pro.exe`) |
|---|---|---|
| Tech | PowerShell wrapped by ps2exe | C# WPF, .NET Framework 4.8 |
| Size | ~50 KB | a few hundred KB |
| Scope | cleanup, report-only, profiles, security check | + large files, duplicates, dev projects, startup, space history, scheduling, presets, themes |

The cleanup **target list** (names, paths, categories, risk levels, ticked defaults), the **safety rules** (`CleanGuard`) and the
user docs are kept deliberately identical so the two projects can be merged later. Both editions list each other's latest
release in their update checker (Pro channel: `releases/latest` of this repo; Standard channel shown as "the lighter edition").

## Build

Everything (SDK, NuGet caches) lives on **drive F:** - nothing is installed on C:.

```powershell
# one-time: install the SDK into F:\Dev\tools\dotnet with Microsoft's dotnet-install.ps1 (-InstallDir F:\Dev\tools\dotnet)
. .\env.ps1            # session-level env vars only (DOTNET_CLI_HOME, NUGET_PACKAGES, ... on F:)
.\build.ps1            # -> dist\CleanPC-Pro.exe   (add -SelfTest to run the built-in tests)
```

`env.ps1` does not modify PATH permanently or any machine setting. To persist it, call it from your PowerShell profile or set the same
variables with `setx` yourself.

Tests: `CleanPC-Pro.exe --selftest` (guard table, reparse/locked handling, process-safety rules, Restart Manager diagnosis with a real
child process, graceful close and retry, storage owners, duplicate finder, large-file scan, stale projects,
runner, startup toggle, updater) - it only touches a throwaway folder. Use `--dryrun --drives C` to compare with the Standard edition.

## License

MIT - see [LICENSE](LICENSE).
