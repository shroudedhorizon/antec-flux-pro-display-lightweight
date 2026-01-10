[![Latest Release](https://github.com/archotechna/antec-flux-pro-display-lightweight/actions/workflows/dotnet-build-and-push.yml/badge.svg)](https://github.com/archotechna/antec-flux-pro-display-lightweight/actions/workflows/dotnet-build-and-push.yml)
## Antec Flux Pro Display Service
A small, lightweight app that runs in the background that gets system cpu and gpu temperature using libre hardware monitor's library. Data is displayed directly to your Antec Flux Pro display on the side of the case. 

Requires Administrator privileges in order to fetch CPU and GPU data from libre hardware monitor's library.

## Installation
1. Installation location is up to you, but you can extract and move the files to `C:\Program Files` and just launch the `FluxProDisplay.exe` program from there.
2. You will be asked to either update or install the PawnIO driver. More information can be found in the Dependencies section below.
3. That is it! The app will appear on the bottom right in the [system tray](https://dsurf.net/images/W11-HiddenIcons01-B.png) area.

## Notes and Information
### Dependencies
- [**LibreHardwareMonitor**](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor): This project uses LibreHardwareMonitor, the latest nightly release since it gets updated frequently to support the latest CPU and GPUs.
- [**PawnIO**](https://github.com/namazso/PawnIO): LibreHardwareMonitor uses the open-source PawnIO driver in order to fetch CPU temperatures. The reason being that before, LHM would use something called WinRing0 in order to fetch this data. However, WinRing0 has been found to open up your system to vulnerabilities with the way it accesses this data. More information can be found [here](https://support.microsoft.com/en-us/windows/microsoft-defender-antivirus-alert-vulnerabledriver-winnt-winring0-eb057830-d77b-41a2-9a34-015a5d203c42).

### Building Locally
To build and export the project locally, I run this.
`dotnet publish -c Release -p:SelfContained=false`
