# Windows Battery Life

A small native Windows (WPF/.NET) desktop app that shows your battery's health, charge cycles, and battery life estimates at a glance — no more manually running `powercfg /batteryreport` and digging through an HTML file.

Under the hood it runs `powercfg /batteryreport /xml`, parses the report, and displays:

- Battery health (design capacity vs. current full-charge capacity, degradation %)
- Cycle count
- Estimated battery life at design capacity vs. current capacity
- Capacity and cycle count history over time (chart)

## Requirements

- Windows 10/11
- .NET 8 SDK (or later) to build

## Running from source

```
dotnet run --project src/BatteryLife
```

## Building a standalone .exe

Produces a single self-contained `BatteryLife.exe` (no .NET runtime install required on the target machine):

```
dotnet publish src/BatteryLife/BatteryLife.csproj -c Release -r win-x64
```

The exe is written to `src/BatteryLife/bin/Release/net10.0-windows/win-x64/publish/BatteryLife.exe`.
