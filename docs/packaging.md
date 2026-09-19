# Offline Windows packaging

The release targets Windows 10 (32-bit and 64-bit) and Windows 11 (64-bit via WOW64), with the included .NET Framework 4.6 or later enabled (recent Windows versions include 4.8). The application, installer, and PDFium DLL are x86. The installer does not download .NET, a browser runtime, or any other component, and does not require PowerShell on the user's machine.

Build on Windows from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

`-SkipInstaller` skips the large setup executable but still builds the portable directory and zip. It does not bypass missing dictionary or native-library checks. The compiler is Windows' `Microsoft.NET\Framework\v4.0.30319\csc.exe`; no NuGet restore or runtime download occurs. Required inputs are `src/*.cs`, `assets/tem4.json`, `native/pdfium.dll`, and the installer source. The script checks the native DLL's x86 PE machine and verifies that the dictionary is valid JSON.

Outputs:

- `dist/MoyuWord-x86/`: portable application; start `MoyuWord.exe`. The included `portable.flag` selects the adjacent `data/` directory for settings, favorites and imported libraries.
- `dist/MoyuWord-x86.zip`: complete portable files, including native binaries and bundled notices.
- `dist/MoyuWord-Setup-x86.exe`: standalone offline installer with the zip embedded as a resource.

The build prints SHA-256 checksums. Extract the whole portable zip, rather than only its executable. Copy the entire portable directory, including `data/` and `portable.flag`, to carry learning data to another machine. A read-only portable directory produces an error instead of falling back to the host profile.

The installer embeds a separate payload without `portable.flag` or personal data and keeps using `%LOCALAPPDATA%\MoyuWord`. Published ZIP/setup artifacts are clean. When rebuilding an existing local portable directory, the build preserves its `data/` tree after creating those archives; locked personal files or a conflicting stage abort without overwriting either copy.

Close a running portable app or installer before rebuilding. The build exclusively opens every existing output file before compilation and checks again before publication; any lock stops the build without changing existing outputs. Portable publication renames the complete old directory to a backup, moves the complete staged directory into place, and then removes that backup. A failed directory move restores the old name. If backup cleanup is blocked, its path is reported and retained. Run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\BuildTests.ps1` for isolated file-lock/publication tests; they do not launch or modify an installed app.

## Installation and removal

The graphical installer defaults to `%LOCALAPPDATA%\Programs\MoyuWord`, creates current-user Desktop and Start Menu shortcuts, and registers an uninstall entry in the current user's Apps list. All installation files remain under one application directory. A `.moyu-install.json` manifest records owned files. Existing files without this ownership record are not overwritten.

An upgrade requires the application to be closed. The installer checks the running process and file locks, backs up replaced files, and rolls back file changes if copying fails. Ownership records are written to a unique sibling file, flushed to disk, then atomically published with a same-volume move or replacement; failed replacement leaves the previous record intact. Optional shortcut/registry failures are logged without removing a successfully installed app. Same-name shortcuts targeting another application, and another installation's uninstall registration, are preserved. Detailed results are written to `%TEMP%\MoyuWord-setup.log`.

Launch `Uninstall.exe`, or choose the app in Windows Settings, to remove it. Only manifest-listed files, matching shortcuts, and this installation's registration are removed. Other files within the installation directory remain. Personal data is preserved. Installation/uninstallation rejects path traversal, symbolic links, junctions, broad system/user directory targets, and unmanaged nonempty installation directories.

To release its own installed executable, `Uninstall.exe` relaunches a small helper under `%TEMP%` and exits. The helper waits for that process to close before removal. Windows may retain that small temporary helper until normal TEMP cleanup (deletion at restart is attempted when permitted). It does not contain the embedded application archive.

## Silent and isolated verification

```powershell
# Normal per-user installation; does not launch the application in silent mode.
Start-Process .\dist\MoyuWord-Setup-x86.exe -ArgumentList '/silent' -Wait -PassThru

# Isolated installation without changing current-user shortcuts or uninstall registration.
$testInstall = Join-Path $env:TEMP 'MoyuWord-Package-Test'
$installArgs = '/silent /no-shortcuts /no-register /install-dir "' + $testInstall + '"'
Start-Process .\dist\MoyuWord-Setup-x86.exe -ArgumentList $installArgs -Wait -PassThru

# Uninstall using the external setup for a synchronous exit code.
$removeArgs = '/silent /uninstall /no-shortcuts /no-register /install-dir "' + $testInstall + '"'
Start-Process .\dist\MoyuWord-Setup-x86.exe -ArgumentList $removeArgs -Wait -PassThru
```

Exit codes: `0` succeeded, `1` failed (inspect the setup log), `2` the graphical user cancelled. When invoking the installed uninstaller itself, the first process hands off to a helper; wait for that helper or verify the installation directory before claiming uninstall completion. Do not use the isolated example path if it already contains unrelated data.

These binaries are unsigned. Windows may show an unknown-publisher/SmartScreen prompt; no signing identity or certificate is embedded or installed.
