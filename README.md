# Storm Machine

A network testing and diagnostics workstation for Windows — one tool instead of a pile of
utilities, with memory, scheduling and reporting.

> **Status: in design and early development.** No usable build yet.
> Русская версия: [README.ru.md](README.ru.md)

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
| **1 — SNMP** | Device credentials | Accurate L2 topology, switch port mapping, interface error counters |
| **2 — Capture** | Npcap installed by the user | LLDP/CDP frames, passive analysis, rogue DHCP detection |

Npcap is **never redistributed** — its license forbids it. The application detects an
existing installation and explains what is unavailable without one.

## Technology

.NET 8 · Avalonia 11 · ScottPlot · QuestPDF · SharpSnmpLib · Quartz.NET · SQLite

See [ADR-0001](docs/adr/ADR-0001-technology-stack.md) for the reasoning.

## Design documents

| Document | Contents |
|----------|----------|
| [01-analysis.md](docs/01-analysis.md) | Requirements, domain model, architecture principles, UX map |
| [02-research.md](docs/02-research.md) | Empirical findings, licence review, competitor analysis |
| [03-development-plan.md](docs/03-development-plan.md) | Iteration chain with acceptance criteria |
| [STATUS.md](docs/STATUS.md) | Where the project stands right now |
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | Code map and boundaries |
| [GITHUB-SETUP.md](docs/GITHUB-SETUP.md) | Repository, signing and release workflow (RU) |

The [`spikes/`](spikes) directory holds the throwaway programs that produced the
measurements in the research document — they are reproducible.

## Ethics

Discovery features are for networks you own or administer. The application confirms scan
ranges, rate-limits by default, logs every active action, and contains no exploitation
tooling.

## Licence

[MIT](LICENSE). ASN and geolocation data by [DB-IP](https://db-ip.com) under CC BY-SA 4.0.
