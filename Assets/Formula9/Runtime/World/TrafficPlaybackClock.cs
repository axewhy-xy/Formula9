using System;
using System.Collections.Generic;

namespace Formula9.World
{
    // The clock the 3D city draws the backend's traffic on: a time on the session's clock a little
    // behind the newest frame, so there is nearly always a frame either side of it to draw between.
    //
    // Frames come about ten a second but never evenly: a request answers a little late, a tick of the
    // backend runs long, the backend falls behind the wall clock for a while. A clock that simply ran at
    // the wall clock's pace would catch the newest frame and stand still until the next, which is the
    // stutter this replaces. Instead it:
    //   - runs at the pace the frames actually come (session seconds a real second, measured over the
    //     last few seconds of arrivals), not the wall clock's;
    //   - keeps its lead over the newest frame, at its lowest lately, near a margin, by running up to
    //     MaxCorrection faster or slower: too small a change to see. The lowest lead already allows for
    //     how late frames have lately come, so the margin is only a safety above that;
    //   - widens the margin each time a frame came so late the clock nearly caught the newest, unless
    //     that was a pause (no frames for a while), and narrows it very slowly while frames come on time:
    //     drawing a little further behind the backend costs nothing to see, a stutter does;
    //   - slows to a stop as it nears the newest frame, rather than halting at it, when frames stop (a
    //     paused session) or are very late;
    //   - starts just behind the newest frame, waiting there for frames to build its lead if there are
    //     none further back yet, and starts over there when it has fallen far behind, as after a hitch.
    public sealed class TrafficPlaybackClock
    {
        // The margin kept at least, at most and at first
        public const double MinMarginS = 0.08, MaxMarginS = 0.5, StartMarginS = 0.1;
        // How much a near-stall widens the margin, and how fast it narrows again a second
        public const double MarginGrowthS = 0.05, MarginDecayPerS = 0.001;
        // How much of the gap between the lowest lead and the margin is made up a second, and the most the
        // clock runs faster or slower than the frames' pace to do it
        public const double CorrectionPerS = 0.6, MaxCorrection = 0.12;
        // Within this lead of the newest frame the clock slows to a stop; within half of it, it has
        // nearly stalled
        public const double SoftStopS = 0.03;
        // Further behind the newest frame than this, the clock starts over just behind it
        public const double MaxBehindS = 1.5;
        // The lowest lead lately rises toward the lead no faster than this a second: a late frame is
        // remembered for a few seconds
        public const double TroughRisePerS = 0.03;
        // The frames' pace is measured over arrivals this recent, once they span at least MinPaceSpanS;
        // a longer gap than PaceGapS between arrivals (a pause, a lost connection) starts it afresh
        public const double PaceWindowS = 3, MinPaceSpanS = 1, PaceGapS = 0.75;
        // The pace is taken to be between these, whatever the arrivals say
        public const double MinPace = 0.25, MaxPace = 1.5;

        private readonly List<(double RealS, double TimeS)> arrivals = new List<(double, double)>();
        private double troughS, intervalS = 0.1, lastArrivalS = double.NaN;
        // The margin before a near-stall since the last arrival, to put back if that turns out a pause
        private double marginBeforeStall = double.NaN;
        // Held at the oldest frame for want of earlier ones, waiting for its lead to build
        private bool filling;
        private bool nearlyStalled;

        // The time drawn, on the session's clock; NaN until the first frame.
        public double TimeS { get; private set; } = double.NaN;
        // The newest frame's time; NaN until the first frame.
        public double NewestS { get; private set; } = double.NaN;
        // Session seconds a real second, as the frames come.
        public double Pace { get; private set; } = 1;
        // How far behind the newest frame the clock means to stay at the least.
        public double MarginS { get; private set; } = StartMarginS;
        // Session seconds a real second the clock moved last Advance.
        public double Speed { get; private set; }
        // How many times a frame came so late the clock nearly caught the newest, pauses aside.
        public int NearStalls { get; private set; }

        public bool Started => !double.IsNaN(TimeS);

        // How far behind the newest frame the drawn time is.
        public double LeadS => Started ? NewestS - TimeS : 0;

        // -----------------------------------------------------------------------------------
        // Function: Add
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function takes a frame's time as it arrives. Frames no newer than the newest are
        //     left out. Of frames that arrive together, as in one reply, only the newest says when
        //     the backend made it, so only it times the pace.
        //
        // Args:
        //     timeS (double): The frame's time on the session's clock.
        //     arrivedS (double): When it arrived, in real seconds on any steady clock.
        public void Add(double timeS, double arrivedS)
        {
            if (!double.IsNaN(NewestS) && timeS <= NewestS) return;
            if (!double.IsNaN(NewestS)) intervalS += (Math.Min(1, timeS - NewestS) - intervalS) * 0.2;
            NewestS = timeS;

            var paused = !double.IsNaN(lastArrivalS) && arrivedS - lastArrivalS > PaceGapS;
            if (paused)
            {
                arrivals.Clear();
                // The clock nearly stalled because frames stopped, not because one was late
                if (!double.IsNaN(marginBeforeStall))
                {
                    MarginS = marginBeforeStall;
                    NearStalls--;
                }
            }
            marginBeforeStall = double.NaN;
            lastArrivalS = Math.Max(arrivedS, double.IsNaN(lastArrivalS) ? arrivedS : lastArrivalS);

            if (arrivals.Count > 0 && arrivedS <= arrivals[arrivals.Count - 1].RealS)
                arrivals[arrivals.Count - 1] = (arrivals[arrivals.Count - 1].RealS, timeS);
            else
                arrivals.Add((arrivedS, timeS));
            var drop = 0;
            while (drop < arrivals.Count - 2 && arrivedS - arrivals[drop].RealS > PaceWindowS) drop++;
            if (drop > 0) arrivals.RemoveRange(0, drop);
            MeasurePace();
        }

        // -----------------------------------------------------------------------------------
        // Function: Advance
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function moves the drawn time on by a frame of real time, never past the newest
        //     frame.
        //
        // Args:
        //     deltaS (double): Real seconds since the last Advance.
        public void Advance(double deltaS)
        {
            if (double.IsNaN(NewestS)) return;
            deltaS = Math.Max(0, deltaS);
            if (!Started || NewestS - TimeS > MaxBehindS)
            {
                StartBehindNewest();
                Speed = 0;
                return;
            }

            var lead = NewestS - TimeS;
            if (filling)
            {
                Speed = 0;
                if (lead < intervalS + MarginS - 1e-6) return;
                filling = false;
                troughS = lead;
            }
            troughS = Math.Min(troughS + deltaS * TroughRisePerS, lead);
            if (troughS > MarginS) MarginS = Math.Max(MinMarginS, MarginS - deltaS * MarginDecayPerS);

            var correction = Math.Max(-MaxCorrection, Math.Min(MaxCorrection, (troughS - MarginS) * CorrectionPerS));
            var speed = Pace * (1 + correction);
            if (lead < SoftStopS) speed *= lead / SoftStopS;
            var step = Math.Min(deltaS * speed, lead);
            TimeS += step;
            Speed = deltaS > 0 ? step / deltaS : 0;

            lead = NewestS - TimeS;
            if (!nearlyStalled && lead < SoftStopS * 0.5)
            {
                nearlyStalled = true;
                NearStalls++;
                if (double.IsNaN(marginBeforeStall)) marginBeforeStall = MarginS;
                MarginS = Math.Min(MaxMarginS, MarginS + MarginGrowthS);
            }
            else if (nearlyStalled && lead > SoftStopS) nearlyStalled = false;
        }

        // -----------------------------------------------------------------------------------
        // Function: HoldAtLeast
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function keeps the drawn time no earlier than a time, the oldest frame there is
        //     to draw from. Pulled forward to it just after starting, the clock waits there until
        //     frames have built its lead.
        //
        // Args:
        //     timeS (double): The earliest time to draw.
        public void HoldAtLeast(double timeS)
        {
            if (!Started || TimeS >= timeS) return;
            TimeS = Math.Min(timeS, NewestS);
            filling = true;
        }

        // -----------------------------------------------------------------------------------
        // Function: Restart
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function puts the drawn time back just behind the newest frame at the next
        //     Advance, as for a view that was not being drawn, keeping what it has learned of the
        //     frames' pace and lateness.
        public void Restart() => TimeS = double.NaN;

        // -----------------------------------------------------------------------------------
        // Function: Reset
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function forgets every frame, as for another session.
        public void Reset()
        {
            arrivals.Clear();
            TimeS = NewestS = lastArrivalS = marginBeforeStall = double.NaN;
            Pace = 1;
            MarginS = StartMarginS;
            Speed = 0;
            NearStalls = 0;
            intervalS = 0.1;
            filling = nearlyStalled = false;
        }

        private void StartBehindNewest()
        {
            TimeS = NewestS - (intervalS + MarginS);
            troughS = intervalS + MarginS;
            filling = nearlyStalled = false;
        }

        // The slope of the frames' times against their arrivals, by least squares: one late arrival
        // barely moves it.
        private void MeasurePace()
        {
            if (arrivals.Count < 3 || arrivals[arrivals.Count - 1].RealS - arrivals[0].RealS < MinPaceSpanS) return;
            double meanReal = 0, meanTime = 0;
            foreach (var (realS, timeS) in arrivals) { meanReal += realS; meanTime += timeS; }
            meanReal /= arrivals.Count;
            meanTime /= arrivals.Count;
            double covariance = 0, variance = 0;
            foreach (var (realS, timeS) in arrivals)
            {
                covariance += (realS - meanReal) * (timeS - meanTime);
                variance += (realS - meanReal) * (realS - meanReal);
            }
            if (variance <= 0) return;
            Pace = Math.Max(MinPace, Math.Min(MaxPace, covariance / variance));
        }
    }
}
