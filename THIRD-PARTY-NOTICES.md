# Third-party components

AirDesk code is MIT licensed. Dependencies retain their original licenses. The package lists the exact Windows x64 runtime dependencies in `licenses/PUBLISHED-RUNTIME.txt`. The broader restore graph is documented in [licenses/DEPENDENCIES.md](licenses/DEPENDENCIES.md).

- LibreHardwareMonitorLib 0.9.6 is used unmodified under MPL-2.0. [Corresponding source](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6)
- BlackSharp.Core 1.0.7, DiskInfoToolkit 1.1.2 and RAMSPDToolkit-NDD 1.4.2 are used unmodified under MPL-2.0. [BlackSharp source](https://github.com/Blacktempel/BlackSharp/tree/1aded1badeba275f0eab429d2a1959391d04de92), [DiskInfoToolkit source](https://github.com/Blacktempel/DiskInfoToolkit/tree/25319eae5781e75bcf141e844ceab2afe94d40ea), [RAMSPDToolkit source](https://github.com/Blacktempel/RAMSPDToolkit/tree/3b47b960e0830fef344624ad5e389675d5f0a1ce)
- HidSharp 2.6.4 and Mono.Posix.NETStandard 1.0.0 use the accompanying notices in `licenses/`. [HidSharp package](https://www.nuget.org/packages/HidSharp/2.6.4), [Mono license referenced by the package](https://go.microsoft.com/fwlink/?linkid=869050)
- The .NET runtime, Windows Desktop runtime, System.IO.Ports 10.0.3 and System.Management 10.0.2 use the MIT license. Runtime and third-party notices are copied from the restored Windows x64 runtime packs into `licenses/`. [Runtime source](https://github.com/dotnet/runtime), [System.IO.Ports package](https://www.nuget.org/packages/System.IO.Ports/10.0.3), [System.Management package](https://www.nuget.org/packages/System.Management/10.0.2)
- PawnIO is a separately installed driver and is excluded from both packages. Its upstream driver repository describes GPL-2.0-or-later with an exception. Review the exact driver and installer terms from upstream before any separate redistribution. [PawnIO](https://pawnio.eu/), [driver terms and source](https://github.com/namazso/PawnIO)
- Inno Setup builds the optional Windows installer. The compiler is an external build tool with its own licensing terms. [Inno Setup licensing and downloads](https://jrsoftware.org/inno-setup/downloads)

AirDesk is not affiliated with LibreHardwareMonitor, FanControl or Microsoft.
