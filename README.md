# ninjatrader-sweep-scalper

Simplified public variant of my MNQ scalper for NinjaTrader 8. It is not the version I trade or test: the parameters are placeholders and the live filters are left out. I make no performance claim for this build.

**Logic**

- 1-second primary series, 1-minute series added. Swings are 3-bar pivots on closed 1-minute bars only.
- A swing high or low is armed if its pivot bar is calm (range at or below a percentile of recent morning 1-minute ranges).
- Entry fades the sweep: price trades `EntryOffset` points beyond the swing level, then a 1-second close crosses back through it. Longs below swing lows, shorts above swing highs.
- Rejects: one entry per minute, no entry while in a position, no chasing past the level.
- Stop and target are set once in ticks, so the bracket is exact from the fill. Flat at session close.
- Opening-volatility gate: `vol15` = (range of the first 15 minutes + net drive) / trailing 10-session morning range, locked at 09:45. A quiet day blocks entries. If the baseline isn't seeded the gate fails open and prints why.

**Use**

Copy `DolSweepScalperLite.cs` to `Documents\NinjaTrader 8\bin\Custom\Strategies`, compile, and apply it to an MNQ 1-second chart with Days to Load of at least 20. Compiled against the NinjaTrader 8 assemblies with no warnings.

Related: [prop-firm-eval-sim](../prop-firm-eval-sim) for judging a daily P&L series the way a prop firm does.

Python research and NinjaTrader backtests disagreed until I reconciled the fill model; in my testing the fill model alone moved the win rate by about 6 points.
