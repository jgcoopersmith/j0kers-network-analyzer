# j0kers Network Analyzer

A Windows 11 desktop app (C# / WPF) that shows live in/out throughput for every active network
interface, with two ways to read the traffic.

## Views

**Bars** — a sideways spectrum meter per direction: lit segments run left to right, coloured
green → amber → red for inbound and cyan → violet → magenta for outbound, each with a peak-hold
marker that slides back after a burst.

**Stream** — a ribbon flowing right to left, inbound swelling above the centre spine and outbound
below. The ribbon fattens with throughput and thins to a thread at idle. Scrolling is driven by
wall-clock time, so the flow stays evenly paced at any polling interval.

Toggle between them with the **View** button. In stream mode a **Flow** slider sets the scroll
speed (15–200 px/sec).

## Readouts

Each interface shows its current rate, auto-scaled (`B/s → KB/s → MB/s → GB/s`), plus the
cumulative volume moved since the app started. The footer sums current throughput across all
interfaces. A **Bits/sec** toggle switches everything to Mbps.

Bars use a logarithmic scale with an adaptive ceiling — it grows instantly to fit a burst and
decays slowly — so a slow trickle stays visible next to a saturating transfer. The current ceiling
is printed in each interface header.

## Polling

The interval is configurable from 100 ms to 5 s on the slider, or typed directly into the ms box
(clamped to 100–10000). Rates are computed from actual elapsed time rather than the nominal
interval, so accuracy holds even when a tick runs late.

Also included: pause/resume, reset totals, and filters for inactive adapters and loopback.
Adapters that appear or disappear mid-run — a VPN coming up, a USB NIC unplugged — are added and
removed automatically.

## Settings

Preferences persist across restarts in
`%APPDATA%\j0kers Network Analyzer\settings.json`: polling interval, view mode, flow speed,
unit and filter toggles, the close/tray behaviour, window size and position, the drag-ordered
interface list, and each interface's activity switch. Writes are debounced and also happen on
exit. Adapters that are filtered out or unplugged keep their saved state for next time, and a
saved window position is only restored if it still lands on a connected display.

## Build

```
dotnet build NetAnalyzer.csproj
```

Requires the .NET 10 SDK (Windows). Traffic counters come from `NetworkInterface.GetIPStatistics()`,
which counts all adapter traffic including link-local and broadcast chatter, so an idle Wi-Fi card
shows a low but nonzero floor. Rates under 512 B/s are treated as silence.
