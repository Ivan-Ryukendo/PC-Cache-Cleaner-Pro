PC Cache Cleaner Pro 1.3.0 (pre-release)
========================================

One file: CleanPC-Pro.exe. Nothing to install (it uses the .NET Framework 4.8 that ships with
Windows 10 and 11). Double-click it, accept the Administrator prompt, and it lists what it found.

WHAT IT DELETES
  Only regenerable cache and temp data: browser/app caches, GPU shader caches, Windows temp and
  update download caches, crash dumps, package-manager caches, stale developer build folders.
  Everything it can delete is shown in a list with its size - nothing is hidden - and RISKY items
  start unticked.

WHAT IT NEVER TOUCHES
  Personal files, browser sessions/cookies/logins/history/bookmarks/passwords, installed programs
  and their settings, saved games. Locked files are skipped, not forced. Junctions/symlinks are
  never followed. Large files and duplicates are only ever sent to the Recycle Bin, and only the
  ones YOU tick.

TABS
  Cleanup, Large files, Duplicates, Dev projects, Startup, Space history, Security check,
  Report-only, Other profiles, History / Settings.

COMMAND LINE (no window)
  CleanPC-Pro.exe --dryrun [--drives C,D | --all-drives]
  CleanPC-Pro.exe --auto --safe [--drives C]      deletes the pre-ticked safe items only
  CleanPC-Pro.exe --selftest                      built-in tests in a throwaway folder

FILES IT WRITES
  CleanPC-Pro-log.txt (next to the exe; previous run kept as .old)
  %APPDATA%\CleanPC-Pro\  settings.json, history.json, cleanings.json

UPDATES
  Only when you press "Check for updates..." - the program never contacts the internet by itself.
