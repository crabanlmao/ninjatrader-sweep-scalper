#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

// Sweep-and-reclaim scalper for MNQ. Primary series must be a 1-second chart; Days to Load >= 20
// so the baselines are seeded before the open.
namespace NinjaTrader.NinjaScript.Strategies
{
    public class DolSweepScalperLite : Strategy
    {
        private const int SEC1 = 0;
        private const int MIN1 = 1;
        private const int AM_START = 9 * 60 + 30;
        private const int AM_END = 11 * 60;
        private const int SWING_START = 9 * 60 + 32;
        private const int SWING_END = 10 * 60 + 56;
        private const int OPEN_VOL_START = AM_START + 1;
        private const int OPEN_VOL_END = AM_START + 15;
        private const int LOOKBACK_SESSIONS = 10;
        private const double ENTRY_TOL = 2.0;

        private class SweepLevel
        {
            public int side;
            public double level;
            public double entry;
            public DateTime time;
            public double prevClose = double.NaN;
        }

        private readonly List<SweepLevel> active = new List<SweepLevel>();
        private readonly Queue<List<double>> priorSessions = new Queue<List<double>>();
        private readonly Queue<double> priorAmRanges = new Queue<double>();
        private readonly List<double> sessionRanges = new List<double>();
        private DateTime sessionDate = DateTime.MinValue;
        private DateTime lastEntryDay = DateTime.MinValue;
        private int lastEntryMinute = -1;
        private double calmBar = double.NaN;
        private double am10 = double.NaN;
        private double amHigh = double.NaN, amLow = double.NaN;
        private double open930 = double.NaN, openHigh = double.NaN, openLow = double.NaN;
        private bool volLocked, quietDay;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "DolSweepScalperLite";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsInstantiatedOnEachOptimizationIteration = true;
                BarsRequiredToTrade = 20;
                DefaultQuantity = 1;
                IncludeCommission = true;
                EntryOffset = 6.0;
                TargetBeyond = 4.0;
                StopPoints = 12.0;
                HuntMinutes = 20;
                EntryEndMinute = 10 * 60 + 30;
                CalmPercentile = 50.0;
                UseVolGate = true;
                Vol15Threshold = 0.30;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 1);
            }
            else if (State == State.DataLoaded)
            {
                int stopTicks = (int)Math.Round(StopPoints / TickSize);
                int targetTicks = (int)Math.Round((EntryOffset + TargetBeyond) / TickSize);
                foreach (string tag in new[] { "DOLl", "DOLs" })
                {
                    SetStopLoss(tag, CalculationMode.Ticks, stopTicks, false);
                    SetProfitTarget(tag, CalculationMode.Ticks, targetTicks);
                }
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == MIN1) { OnMinuteBar(); return; }
            if (BarsInProgress == SEC1) OnSecondBar();
        }

        private void OnMinuteBar()
        {
            if (CurrentBars[MIN1] < 20 || !IsFirstTickOfBar) return;

            DateTime day = Times[MIN1][1].Date;
            if (day != sessionDate) StartSession(day);

            int mod = Minutes(Times[MIN1][1]);
            double high = Highs[MIN1][1], low = Lows[MIN1][1];

            if (mod >= AM_START && mod <= AM_END)
            {
                sessionRanges.Add(high - low);
                if (double.IsNaN(amHigh) || high > amHigh) amHigh = high;
                if (double.IsNaN(amLow) || low < amLow) amLow = low;
            }

            if (mod == OPEN_VOL_START) open930 = Opens[MIN1][1];
            if (mod >= OPEN_VOL_START && mod <= OPEN_VOL_END)
            {
                if (double.IsNaN(openHigh) || high > openHigh) openHigh = high;
                if (double.IsNaN(openLow) || low < openLow) openLow = low;
            }
            if (!volLocked && mod == OPEN_VOL_END) LockOpeningVolatility();

            double pivotRange = Highs[MIN1][2] - Lows[MIN1][2];
            bool calm = !double.IsNaN(calmBar) && pivotRange <= calmBar;
            int pivotMod = Minutes(Times[MIN1][2]);
            if (!calm || pivotMod < SWING_START || pivotMod > SWING_END) return;

            if (Highs[MIN1][2] > Highs[MIN1][1] && Highs[MIN1][2] > Highs[MIN1][3])
                RegisterSwing(-1, Highs[MIN1][2], Times[MIN1][2]);
            if (Lows[MIN1][2] < Lows[MIN1][1] && Lows[MIN1][2] < Lows[MIN1][3])
                RegisterSwing(+1, Lows[MIN1][2], Times[MIN1][2]);
        }

        private void OnSecondBar()
        {
            if (double.IsNaN(calmBar) || CurrentBars[MIN1] < 20) return;

            DateTime now = Times[SEC1][0];
            int mod = Minutes(now);
            double close = Closes[SEC1][0];
            bool inWindow = mod >= AM_START && mod < EntryEndMinute;

            for (int i = active.Count - 1; i >= 0; i--)
            {
                SweepLevel s = active[i];
                if ((now - s.time).TotalMinutes > HuntMinutes) { active.RemoveAt(i); continue; }

                bool had = !double.IsNaN(s.prevClose);
                bool reclaimedUp = had && s.prevClose < s.entry && close >= s.entry;
                bool reclaimedDown = had && s.prevClose > s.entry && close <= s.entry;
                bool fire = inWindow && ((s.side == +1 && reclaimedUp) || (s.side == -1 && reclaimedDown));

                if (fire && TryEnter(s, mod, now, close)) { active.RemoveAt(i); continue; }
                s.prevClose = close;
            }
        }

        private bool TryEnter(SweepLevel s, int mod, DateTime now, double px)
        {
            string side = s.side == 1 ? "LONG" : "SHORT";
            if (UseVolGate && quietDay) return false;
            if (lastEntryDay == now.Date && lastEntryMinute == mod) { Log(now, "reject one-per-minute " + side); return false; }
            if (Position.MarketPosition != MarketPosition.Flat) { Log(now, "reject in-position " + side); return false; }

            double overshoot = s.side == 1 ? px - s.entry : s.entry - px;
            if (overshoot > ENTRY_TOL) { Log(now, "reject no-chase " + side + " overshoot=" + overshoot.ToString("F2")); return false; }

            Log(now, "ENTER " + side + " entry=" + s.entry.ToString("F2") + " px=" + px.ToString("F2"));
            if (s.side == -1) EnterShort(DefaultQuantity, "DOLs");
            else EnterLong(DefaultQuantity, "DOLl");
            lastEntryDay = now.Date;
            lastEntryMinute = mod;
            return true;
        }

        private void RegisterSwing(int side, double level, DateTime time)
        {
            foreach (SweepLevel a in active)
                if (a.side == side && a.time == time && Math.Abs(a.level - level) < TickSize) return;
            double entry = side == -1 ? level + EntryOffset : level - EntryOffset;
            active.Add(new SweepLevel { side = side, level = level, entry = entry, time = time });
            Log(time, "swing " + (side == 1 ? "LONG" : "SHORT") + " level=" + level.ToString("F2") + " entry=" + entry.ToString("F2"));
        }

        private void StartSession(DateTime day)
        {
            if (sessionDate != DateTime.MinValue)
            {
                if (sessionRanges.Count > 0)
                {
                    priorSessions.Enqueue(new List<double>(sessionRanges));
                    while (priorSessions.Count > LOOKBACK_SESSIONS) priorSessions.Dequeue();
                }
                if (!double.IsNaN(amHigh) && amHigh > amLow)
                {
                    priorAmRanges.Enqueue(amHigh - amLow);
                    while (priorAmRanges.Count > LOOKBACK_SESSIONS) priorAmRanges.Dequeue();
                }
            }
            sessionRanges.Clear();
            RecomputeBaselines();
            sessionDate = day;
            active.Clear();
            amHigh = amLow = open930 = openHigh = openLow = double.NaN;
            volLocked = quietDay = false;
        }

        private void LockOpeningVolatility()
        {
            volLocked = true;
            string stamp = Times[MIN1][1].ToString("yyyy-MM-dd HH:mm");
            bool ready = !double.IsNaN(open930) && !double.IsNaN(openHigh) && !double.IsNaN(am10) && am10 > 0;
            if (!ready)
            {
                Print(stamp + " vol15=NA gate inactive (fails open) am10 banked " + priorAmRanges.Count + "/" + LOOKBACK_SESSIONS
                      + " calmBar=" + (double.IsNaN(calmBar) ? "NaN, no trades until seeded" : "ok"));
                return;
            }
            double vol15 = (openHigh - openLow + Math.Abs(Closes[MIN1][1] - open930)) / am10;
            quietDay = UseVolGate && vol15 < Vol15Threshold;
            Print(stamp + " vol15=" + vol15.ToString("F2") + " threshold=" + Vol15Threshold.ToString("F2") + (quietDay ? " quiet, skipping entries" : " ok"));
        }

        private void RecomputeBaselines()
        {
            var pool = new List<double>();
            foreach (List<double> s in priorSessions) pool.AddRange(s);
            if (pool.Count < 30) calmBar = double.NaN;
            else
            {
                pool.Sort();
                double rank = CalmPercentile / 100.0 * (pool.Count - 1);
                int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
                calmBar = pool[lo] + (rank - lo) * (pool[hi] - pool[lo]);
            }

            if (priorAmRanges.Count < LOOKBACK_SESSIONS) { am10 = double.NaN; return; }
            double sum = 0;
            foreach (double r in priorAmRanges) sum += r;
            am10 = sum / priorAmRanges.Count;
        }

        private static int Minutes(DateTime t) { return t.Hour * 60 + t.Minute; }

        private void Log(DateTime t, string message) { Print(t.ToString("yyyy-MM-dd HH:mm:ss") + " " + message); }

        #region Properties
        [NinjaScriptProperty, Range(0.25, 50.0)]
        [Display(Name = "EntryOffset", Order = 1, GroupName = "Trade")]
        public double EntryOffset { get; set; }

        [NinjaScriptProperty, Range(0.25, 50.0)]
        [Display(Name = "TargetBeyond", Order = 2, GroupName = "Trade")]
        public double TargetBeyond { get; set; }

        [NinjaScriptProperty, Range(0.25, 100.0)]
        [Display(Name = "StopPoints", Order = 3, GroupName = "Trade")]
        public double StopPoints { get; set; }

        [NinjaScriptProperty, Range(1, 120)]
        [Display(Name = "HuntMinutes", Order = 4, GroupName = "Trade")]
        public int HuntMinutes { get; set; }

        [NinjaScriptProperty, Range(571, 660)]
        [Display(Name = "EntryEndMinute", Order = 5, GroupName = "Trade", Description = "Minutes after midnight ET, exclusive")]
        public int EntryEndMinute { get; set; }

        [NinjaScriptProperty, Range(1.0, 99.0)]
        [Display(Name = "CalmPercentile", Order = 6, GroupName = "Filters")]
        public double CalmPercentile { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseVolGate", Order = 7, GroupName = "Filters")]
        public bool UseVolGate { get; set; }

        [NinjaScriptProperty, Range(0.0, 5.0)]
        [Display(Name = "Vol15Threshold", Order = 8, GroupName = "Filters")]
        public double Vol15Threshold { get; set; }
        #endregion
    }
}
