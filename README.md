# j0ker's Network Analyzer

A Windows 11 desktop app (C# / WPF) that shows live in/out throughput for every active network
interface, with two ways to read the traffic.

## Views

**Bars** — a sideways spectrum meter per direction: lit segments run left to right, coloured
green → amber → red for inbound and cyan → violet → magenta for outbound, each with a peak-hold
marker that slides back after a burst.

**Stream** — a ribbon flowing right to left, inbound swelling above the centre spine and outbound
below. The ribbon fattens with throughput and thins to a thread at idle. Scrolling is driven by
wall-clock time, so the flow stays evenly paced at any polling interval.

**Widget** — a small always-on-top panel with no window chrome, showing only the interfaces whose
activity switch is on: name, ribbon, and the in/out rates. Drag anywhere on it to move it,
double-click to leave. Its height follows the number of selected interfaces, so unchecking the
quiet ones shrinks it.

The **View** button cycles bars → stream → widget. In stream and widget modes a **Flow** slider
sets the scroll speed (15–200 px/sec).

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

**Hide filter adapters** (on by default) drops the WFP/QoS filter entries Windows stacks on top of
each real NIC. They mirror the traffic of the adapter they sit on, so leaving them visible means
seeing the same bytes several times over.

Also included: pause/resume, reset totals, and filters for inactive adapters and loopback.
Adapters that appear or disappear mid-run — a VPN coming up, a USB NIC unplugged — are added and
removed automatically.

## Top bandwidth consumers

Hovering over an interface's meters — bars, stream, or a widget row — shows the five applications
using the most bandwidth on *that* interface over the last minute, with separate down and up rates.

Each application carries a colour swatch, and the stream ribbon is split into bands in those same
colours — stacked outward from the spine, so the biggest consumer's share of the ribbon's thickness
is painted in its colour, the next in its own, and so on. Whatever the store cannot attribute is left
in the plain gradient. An application keeps its colour for as long as it stays in the top five, so
two apps trading places doesn't repaint the ribbon. Usage is sampled every 5 seconds; a change in
the mix enters at the right edge and scrolls off with the traffic it describes.

The figures come from the Windows per-app network usage store
(`ConnectionProfile.GetAttributedNetworkUsageAsync`), which is the only per-process view available
without elevation: the ETW kernel provider Task Manager uses refuses to start unless the app runs
as administrator. The trade-offs are that numbers come from an aggregated store rather than live
counters, so they lag the meters a little, and traffic is attributed per application rather than
per process. Unpackaged desktop apps report no display name, so their executable name is used.

## Settings

Preferences persist across restarts in
`%APPDATA%\j0kers Network Analyzer\settings.json`: polling interval, view mode, flow speed,
unit and filter toggles, the close/tray behaviour, window size and position, the drag-ordered
interface list, and each interface's activity switch. It also remembers how the window was left:
close it minimized or sitting in the tray and it reopens that way. Writes are debounced and also happen on
exit. Adapters that are filtered out or unplugged keep their saved state for next time, and a
saved window position is only restored if it still lands on a connected display.

## Build

```
dotnet build NetAnalyzer.csproj
```

Requires the .NET 10 SDK (Windows). Traffic counters come from `NetworkInterface.GetIPStatistics()`,
which counts all adapter traffic including link-local and broadcast chatter, so an idle Wi-Fi card
shows a low but nonzero floor. Rates under 512 B/s are treated as silence.
