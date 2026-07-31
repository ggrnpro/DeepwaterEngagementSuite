# DeepwaterEngagementSuiteGGRN

Fork of [exApiTools/DeepwaterEngagementSuite](https://github.com/exApiTools/DeepwaterEngagementSuite),
renamed so it can be installed alongside the original instead of colliding with it.

## What this fork changes

**Assignment-based voyage solver** (on by default, `Use assignment solver`). The original
backtracking planner runs out of time on ordinary chart pools and reports "no valid solution found".
This one fixes the board's connection pattern first — which pins down every tile's required shape
and every border multiplier — and then solves chart placement as a linear assignment problem. It is
exact, handles per-connection borders, and finishes in milliseconds. When no fully connected board
is possible it shows the best disconnected board and explains what the chart pool is missing.

**Debug telemetry** (`Write debug dumps`), written to `config/DeepwaterEngagementSuiteGGRN/debug`:

- `events.ndjson` — one line per event. Board changes, including rerolls, are recorded automatically.
- `unknown-mods.json` — every chart/border modifier id seen in game but missing from the active
  profile, with a count. This is the ground truth for filling profile gaps.
- `snapshot-*.json` — full dump of the voyage window and the latest solver run, written on hotkey
  (F9 by default).

## Credit

All original work is by the exApiTools authors. If you like the plugin, donate to them:

BTC: bc1qke67907s6d5k3cm7lx7m020chyjp9e8ysfwtuz

ETH: 0x3A37B3f57453555C2ceabb1a2A4f55E0eB969105
