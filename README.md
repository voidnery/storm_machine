<img src="assets/spike/spike-256.png" width="96" align="right" alt="Storm Machine">

# Storm Machine

A network testing and diagnostics workstation for Windows — one tool instead of a pile of
utilities, with memory, scheduling and reporting.

> **Status: first public release in preparation.** All three capability levels are built
> and tested on the bench; field tests on real customer equipment come next.
> Русская версия: [README.ru.md](README.ru.md)

**Free and open source.** MIT licence, no paid editions, no licence keys, no accounts.

## Download and run

Storm Machine ships as a **portable archive only** — there is no installer.

1. Download `StormMachine-<version>-win-x64-portable.zip` from
   [Releases](https://github.com/voidnery/storm_machine/releases) and check it against
   `SHA256SUMS.txt` (`Get-FileHash <file> -Algorithm SHA256` in PowerShell).
2. Unpack it anywhere — a USB stick works — and run `StormMachine.exe`.
   `storm.exe` is the same product as a console; `storm-agent.exe` and
   `storm-agent-ui.exe` are the second measurement point.
3. Packet capture (neighbours, rogue DHCP) needs administrator rights.

The `storm.portable` file next to the program keeps the measurement database, settings
and log in the same folder, so nothing is left behind on the machine. Delete it and the
data goes to `%LOCALAPPDATA%\StormMachine` instead.

**Updating.** *Settings → About → Check* tells you whether a newer release exists and
links to it; the product never contacts GitHub unless you press the button and never
replaces itself. Unpack the new archive over the old folder — the data stays.

**Second point on Linux or macOS.** Each release also carries the agent alone:
`storm-agent-<version>-<platform>.zip` for `win-x64`, `linux-x64`, `linux-arm64`,
`osx-x64`, `osx-arm64`.

Builds are not code-signed yet (the SignPath Foundation application is pending), so
SmartScreen warns on first launch: *More info → Run anyway*. Verify the checksum first.

## What it is

Storm Machine replaces the usual scatter of `ping`, `tracert`, `nmap`, `iperf3`, `mtr`,
speed tests and SNMP browsers with a single coherent system. The difference from a
collection of utilities is three capabilities that run through everything:

- **Memory** — every measurement lands in a time series. "The network is slow" becomes a
  graph with a baseline.
- **Repeatability** — any test can be saved as a preset and put on a schedule.
- **Artifact** — any set of results becomes a PDF report citing the measurement
  methodology (RFC 3550, RFC 6349, ITU-T G.107), suitable for a dispute with an ISP.

## Planned capabilities

**Local network** — availability and latency monitoring, RFC 3550 jitter and PDV, VoIP
quality (MOS/R-factor), bufferbloat grading, throughput between points, PMTU discovery,
host inventory with MAC vendor resolution, and network topology built from weighted
evidence with manual correction that survives rescans.

**External network** — configurable probe scenarios over ICMP/TCP/UDP/HTTP/DNS/TLS,
continuous MTR with per-hop loss and ASN annotation, HTTP timing waterfall, DNS resolver
comparison, TLS inspection, NAT type detection, IPv6 readiness, and SLA monitoring.

**Throughout** — preset library, scheduling with maintenance windows, alerting, and PDF
reporting with baseline comparison.

## Capability levels

The product degrades honestly rather than demanding everything up front:

| Level | Requires | Provides |
|-------|----------|----------|
| **0 — Core** | Nothing. No admin rights, no drivers | ~80% of the value: probes, monitoring, traceroute, inventory with MAC addresses, L3 topology, presets, scheduling, reports |
| **1 — SNMP** | Device credentials | Accurate L2 topology, switch port mapping, interface error counters. **Done in iteration 17** |
| **2 — Capture** | Administrator rights | LLDP/CDP frames, our own switch port, rogue DHCP detection. **Done in iterations 18 and 25** |

Capture listens through `pktmon`, built into Windows 10 1809 and later, so no driver has
to be installed. Npcap is preferred when the user has installed it, but it is **never
redistributed** — its license forbids it.

**The MAC vendor database** (IEEE registry, 53k entries) **is bundled**: its
redistribution is unrestricted, and identifying a device's maker has to work right after
launch and without internet access. Refresh it with `tools/update-oui.cmd`.

**Address ownership data** — the AS numbers shown along a traceroute — comes from the
offline [DB-IP Lite](https://db-ip.com/db/lite.php) database. Its CC BY-SA 4.0 license
requires derivatives to inherit the same license, which is incompatible with our MIT
code, so the database is not bundled. Download `dbip-asn-lite` (and optionally
`dbip-country-lite`) in `.mmdb` format and drop it into the `geo` folder of the data
directory — next to `StormMachine.exe` in the portable copy, `%LOCALAPPDATA%\StormMachine\geo`
without the marker; no renaming needed, files are matched by pattern. Without it traceroute still works
and still resolves reverse DNS names.

## Technology

.NET 10 · Avalonia 12 · ScottPlot · QuestPDF · MSAGL · SQLite

Quartz.NET was listed here until iteration 14 and was rejected after testing: it fails
silently under trimmed publish. The scheduler is our own — [R-15](docs/02-research.md).
Velopack passed the same test — [R-19](docs/02-research.md) — and left together with the
installer when the product moved to portable-only delivery.

See [ADR-0001](docs/adr/ADR-0001-technology-stack.md) for the reasoning.

## Design documents

| Document | Contents |
|----------|----------|
| [01-analysis.md](docs/01-analysis.md) | Requirements, domain model, architecture principles, UX map |
| [02-research.md](docs/02-research.md) | Empirical findings, licence review, competitor analysis |
| [03-development-plan.md](docs/03-development-plan.md) | Iteration chain with acceptance criteria |
| [STATUS.md](docs/STATUS.md) | Where the project stands right now |
| [RELEASES.md](docs/RELEASES.md) | What each release contains, what was verified, what was not (RU) |
| [STATE.md](docs/STATE.md) | What the product keeps on disk and where (RU) |
| [FIELD-TESTS.md](docs/FIELD-TESTS.md) | Field-test checklist for real equipment (RU) |
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | Code map and boundaries |
| [GITHUB-SETUP.md](docs/GITHUB-SETUP.md) | Repository, signing and release workflow (RU) |
| [SIGNPATH.md](docs/SIGNPATH.md) | Applying to SignPath Foundation for release signing |

The [`spikes/`](spikes) directory holds the throwaway programs that produced the
measurements in the research document — they are reproducible.

## Ethics

Discovery features are for networks you own or administer. The application confirms scan
ranges, rate-limits by default, logs every active action, and contains no exploitation
tooling.

## Licence

[MIT](LICENSE). ASN and geolocation data by [DB-IP](https://db-ip.com) under CC BY-SA 4.0.
