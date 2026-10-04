AwakeGuard 1.0.0
===============

RUN
Double-click AwakeGuard.exe. No installer is needed.
Keep AwakeGuard.exe and AwakeGuard.exe.config together.
Requires Windows 10 or Windows 11 with .NET Framework 4.8 or newer.
The app is a locally built, unsigned executable.

Protection starts immediately with the saved options. The first launch keeps
the computer awake and enables the shutdown/restart guard. The screen may
turn off unless you turn on "Keep screen on".

CONTROLS
- Keep computer awake: prevents idle sleep while protection is active.
- Keep screen on: prevents display timeout, when Keep computer awake is on.
- Guard shutdowns and restarts: registers a shutdown-block reason, intercepts
  ordinary shutdown/restart requests, and tries every second to cancel pending
  shutdown/restart countdowns using the current user's shutdown privilege.
- Protect for: choose an unlimited session or 30 minutes, 1, 2, 4 or 8 hours.
  Changing this duration starts a fresh timer. Pausing and starting again also
  starts a fresh session. An expired timer releases all protection.
- Pause protection: releases requests immediately. Press Start to resume.
- Hide to tray, minimize, or close the window: keeps the app running. Open it
  by double-clicking the shield in the system tray (possibly under the ^ menu).
- Exit: stops protection and closes the app. The tray menu also has Exit.
- Start at Windows sign-in: optional and off by default. Starts protection in
  the tray at your next sign-in. Place the folder in a permanent location before
  enabling this. Turn the option off before moving or deleting the app.

WHAT WINDOWS ALLOWS
Idle-sleep protection uses SetThreadExecutionState. It does not override manual
sleep, laptop lid/power-button actions, critical battery events, power-plan
request overrides, hardware failures, or power loss.

Restart protection is best effort. Windows may show an "app is preventing
shutdown" screen; choose Cancel to remain running. "Restart anyway" or
"Shut down anyway" can override the guard. Forced update restarts, zero-timeout
shutdowns, critical shutdowns, crashes and process termination may bypass it.
Sign-out and application replacement/servicing notifications are allowed.
Windows Update services and system power settings are not changed.

The status line says "requests + countdowns" when countdown cancellation is
available, and "requests only" if your account lacks the necessary privilege.
The event count records intercepted requests and cancelled countdowns; an
intercepted request does not guarantee the restart was prevented.

Keep the app running for protection. There is no permanent restart block.
Keeping a laptop awake consumes battery and requires adequate ventilation.

PREFERENCES AND REMOVAL
Option preferences are stored in %LOCALAPPDATA%\AwakeGuard\settings.xml.
Protection's paused/running state is not saved: each launch starts a new session
using the saved options. Startup is stored in this user's Windows Run registry
key under the value AwakeGuard only when explicitly enabled in the app.
To remove: turn off "Start at Windows sign-in", select Exit, then delete the
AwakeGuard folder. You may also remove the local preferences folder.

SOURCE AND BUILD
Source is in source\AwakeGuard.cs. Build.ps1 invokes the C# compiler included
with .NET Framework; no NuGet packages or internet connection are needed.
To rebuild from PowerShell, from the AwakeGuard folder:
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
This execution-policy setting applies only to that process.

VALIDATION
38 checks passed on the build machine, including native wake/display request
flags, shutdown-reason registration/removal, privilege restoration, timer expiry,
error handling, paused state, and cleanup. The rendered interface was inspected.
No real shutdown, restart, or sleep transition was initiated during validation.
Source for these checks is included in source\tests; run Test.ps1 to repeat them.

MICROSOFT API REFERENCES
https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate
https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-shutdownblockreasoncreate
https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-queryendsession
https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-abortsystemshutdownw
