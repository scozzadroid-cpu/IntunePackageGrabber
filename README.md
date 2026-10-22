# IntunePackageGrabber

Windows tool (WinForms, single exe, dark theme) for technicians and Intune engineers: it archives the Win32 packages Intune Management Extension (IME) downloads when an app is installed from Company Portal, and shows the state of the Win32 apps and remediations of the device. The **Tutorial** button in the app explains every function.

IME extracts the content to `C:\Windows\IMECache\<AppId>_<version>` and deletes it when the installation ends. The tool watches the cache, waits until the extraction is complete and saves each package to the chosen folder:

- `Content\` - verified copy (file count and bytes) of the extracted content
- `<App name>.intunewin` - rebuilt with IntuneWinAppUtil, using the setup file from the IME policy
- `app-info.txt` and `policy.json` - name, version, install/uninstall commands, requirements, detection rules (decoded: MSI, file, registry, script) and return codes read from `AppWorkload.log`
- `detection.ps1` - the detection script, when the app uses one

If the `.intunewin` cannot be created, the content is saved anyway.

![Intune Package Grabber listening, with one captured package](docs/main.png)

The saved `.intunewin` is rebuilt from the content: it installs the same files, but it is not byte-identical to the original one uploaded to Intune (it is encrypted with new keys).

## Diagnostics

For technicians and Intune engineers, read-only, from the data IME keeps on the device (no connection to Intune):

- **Win32 apps** tab: every Win32 app known by IME with context (device/user), intent, detection state, last enforcement, error code and last update, read from `HKLM\SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps`. Names, commands, detection rules and requirements come from the policies in `AppWorkload*.log` (apps no longer in the logs are shown without a name). Apps with errors are listed first in red, apps without a name at the end. Double-click for details, the detection script and the policy JSON; **Export selected** saves `app-info.txt`, `policy.json` and `detection.ps1`.
- **Remediations** tab: the remediations on the device with last run, detection and remediation exit codes and error code, from `C:\Windows\IMECache\HealthScripts` and `SideCarPolicies\Scripts`. Double-click to read `detect.ps1`, `remediate.ps1` and the script output; **Export selected** saves the scripts and the result.
- **Collect logs**: creates `IntuneDiagnostics_<computer>_<date>.zip` in the archive folder with the IME and Company Portal logs, the IME registry, the MDM event log (DeviceManagement-Enterprise-Diagnostics-Provider/Admin), `dsregcmd /status` and the Win32 app and remediation states as CSV.

Click a column header to sort.

## IntuneWin Downloader

**About & credits** has the button to download and launch [IntuneWin Downloader](https://github.com/call4cloud-code/Intunewin-Downloader-Public), created by Rudy Ooms (call4cloud): it downloads and decrypts the Win32 apps assigned to the device directly from Intune, without installing them.

It is downloaded from GitHub on first use (about 85 MB) and saved to `%LOCALAPPDATA%\IntunePackageGrabber\IntuneWinDownloader\`. Its binaries are not signed, so the tool downloads a pinned version (0.6.127, from a fixed commit) and accepts it only if its SHA-256 matches. Alternatively, extract it into an `IntuneWinDownloader` folder next to the exe.

## Download

The exe is on the [Releases](../../releases/latest) page.

## Usage

1. Run `IntunePackageGrabber.exe` (requires administrator rights).
   If you need the `.intunewin` and IntuneWinAppUtil.exe is not present, press **Download IntuneWinAppUtil**: the tool downloads it from the [Microsoft repository](https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool), checks its Microsoft Authenticode signature and saves it to `%APPDATA%\IntunePackageGrabber\`.

   ![Download IntuneWinAppUtil button](docs/download-button.png)
2. Choose the archive folder.
3. Press **Listen**, then start the installation from Company Portal.
4. Press **Stop** when done.

Command line options: `--dest <folder>`, `--listen`, `--cache <folder>`, `--log <file>`, `--imelogs <folder>`, `--exit-after <seconds>`.

Settings and log: `%APPDATA%\IntunePackageGrabber\`.

## Requirements

- Intune-managed Windows 10/11, .NET Framework 4.8
- [IntuneWinAppUtil.exe](https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool), only to create the `.intunewin`. It is looked up in this order: next to the exe, in `%APPDATA%\IntunePackageGrabber\` (download button), embedded in the exe.

## Build

```powershell
# Same as the release: IntuneWinAppUtil not embedded
powershell -ExecutionPolicy Bypass -File src\build.ps1 -NoEmbed -Output .\IntunePackageGrabber.exe

# With IntuneWinAppUtil embedded (internal use)
powershell -ExecutionPolicy Bypass -File src\build.ps1 -IntuneWinAppUtil <path>\IntuneWinAppUtil.exe -Output .\IntunePackageGrabber.exe
```

Uses the C# compiler of .NET Framework included in Windows, no SDK required.

## Tests

```powershell
powershell -ExecutionPolicy Bypass -File src\test.ps1 -Work C:\temp\IPG-tests -Exe .\IntunePackageGrabber.exe
```

Simulates IME on a fake cache and `AppWorkload.log`: progressive extraction with files still open for writing, parallel packages, folders removed during the copy, retries, paths over 260 characters, unknown setup file, detection rule decoding, Listen/Stop buttons.

## Credits

Also shown in the app (**About & credits**).

- [IntuneWin Downloader](https://github.com/call4cloud-code/Intunewin-Downloader-Public): created by Rudy Ooms (call4cloud), MIT License
- [IntuneWinAppUtil](https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool) (Microsoft Win32 Content Prep Tool): Microsoft Corporation

## License

Open source under the [MIT License](LICENSE).
