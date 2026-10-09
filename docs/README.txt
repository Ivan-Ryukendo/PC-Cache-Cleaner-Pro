PC Cache Cleaner Pro 1.3.0-beta.2 (pre-release)
===============================================

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
  Report-only, Other profiles, Who is using space, History / Settings.

LOCKED FILES (Cleanup tab)
  Files a clean had to skip because a program is using them are listed after the clean, per
  program: name, PID, how many skipped files it holds and about how many bytes. This only asks
  Windows (Restart Manager) - it changes nothing. A small window after the clean tells you how
  much was freed and how many files were locked (no tray icon, no background service).
  For each program there is an optional "Close this program" button. You are asked first, with a
  warning that unsaved work can be lost; the program is asked politely to close and given about
  5 seconds. Only if it stays open are you asked a second time whether to END it by force.
  It is never offered for Windows/system processes, services, Explorer, Defender or this program,
  never done automatically, and never for several programs at once. If Windows says access
  denied, the reason is shown and nothing more is tried. After a program closes, press "Retry
  skipped items": only the skipped files are tried again, with the same safety checks.
  (The Standard edition's security check never kills processes; this feature is separate,
  asks you every time, and exists only in Pro.)

WHO IS USING SPACE
  Read-only list of running programs with the size of their install folder plus the AppData /
  ProgramData folders that look like theirs (matched by name only, so shown as "likely"),
  biggest first, with an Open location button. Stop cancels the measuring. Junctions and
  symlinks are never followed.

COMMAND LINE (no window)
  CleanPC-Pro.exe --dryrun [--drives C,D | --all-drives]
  CleanPC-Pro.exe --auto --safe [--drives C]      deletes the pre-ticked safe items only
  CleanPC-Pro.exe --selftest                      built-in tests in a throwaway folder

FILES IT WRITES
  CleanPC-Pro-log.txt (next to the exe; previous run kept as .old)
  %APPDATA%\CleanPC-Pro\  settings.json, history.json, cleanings.json

PRIVACY
  No telemetry. The only network use is the update check, and only when you click it.

UPDATES
  Only when you press "Check for updates..." - the program never contacts the internet by itself.
