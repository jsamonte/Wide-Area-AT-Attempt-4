using System;

namespace TrialServer
{
    /// <summary>
    /// The frame-time MATH, with no Unity API anywhere in it. That separation is the whole reason this is
    /// its own class: everything below can be exercised in edit mode (or a plain test) by pushing a made-up
    /// list of frame times in, while <see cref="PerfMonitor"/> owns the part that can only be judged on the
    /// device (which counters actually populate on an ML2).
    ///
    /// WHY DISTRIBUTION AND NOT AVERAGES. A mean frame rate hides exactly the thing a participant notices.
    /// A session that runs at a clean 60 with four 200 ms stalls in it averages out to something that looks
    /// perfect, and the four stalls are the entire finding. So this reports p95/p99, the worst frame in the
    /// window, the 1% low, and a hitch count, and treats the mean as the least interesting number it has.
    ///
    /// The ring is a fixed array written to every frame and only READ when someone asks (the dashboard at
    /// 2 Hz, a [PERF] log line every few seconds). Adding a sample is one array store and one integer
    /// increment, with no allocation, because it happens on every frame of a recorded trial and telemetry
    /// that perturbs the thing it measures is worse than no telemetry.
    /// </summary>
    public class PerfStats
    {
        readonly float[] _ms;        // frame times, newest overwriting oldest
        readonly float[] _scratch;   // sort buffer, preallocated so Compute never allocates
        int _count;                  // samples currently held (<= _ms.Length)
        int _head;                   // next write index

        // Session-cumulative counters. These deliberately survive the ring: the ring answers "how is it
        // doing right now", these answer "how did the whole trial go", and the second question is the one
        // the session file has to be able to answer after the fact.
        long _totalFrames;
        long _framesOverBudget;
        float _worstEverMs;

        public PerfStats(int capacity)
        {
            if (capacity < 8) capacity = 8;
            _ms = new float[capacity];
            _scratch = new float[capacity];
        }

        /// <summary>Samples currently held in the rolling window.</summary>
        public int Count => _count;

        /// <summary>Every frame this session, whether or not it is still in the window.</summary>
        public long TotalFrames => _totalFrames;

        /// <summary>Session-cumulative frames slower than the budget, and the single worst frame seen.
        /// Both are measured against the FIXED budget rather than a moving median, so the number means the
        /// same thing at the start of a trial and at the end of it.</summary>
        public long FramesOverBudget => _framesOverBudget;
        public float WorstEverMs => _worstEverMs;

        public void Clear()
        {
            _count = 0;
            _head = 0;
            _totalFrames = 0;
            _framesOverBudget = 0;
            _worstEverMs = 0f;
        }

        /// <summary>Record one frame. budgetMs is the target frame time (16.7 for 60 Hz); it only feeds the
        /// cumulative over-budget counter, never the window statistics.</summary>
        public void Add(float frameMs, float budgetMs)
        {
            if (frameMs <= 0f || float.IsNaN(frameMs) || float.IsInfinity(frameMs)) return;

            _ms[_head] = frameMs;
            _head = (_head + 1) % _ms.Length;
            if (_count < _ms.Length) _count++;

            _totalFrames++;
            if (budgetMs > 0f && frameMs > budgetMs) _framesOverBudget++;
            if (frameMs > _worstEverMs) _worstEverMs = frameMs;
        }

        /// <summary>
        /// The window's shape, computed on demand. Sorts a copy of the held samples, so this is O(n log n)
        /// on a few hundred floats: cheap at 2 Hz, absolutely not something to call per frame.
        ///
        /// hitchMultiplier defines a hitch as a frame slower than that many times the window MEDIAN, which
        /// is the honest definition: a hitch is relative to how the app is currently running, so a stall
        /// still reads as a stall on a session that is running slowly overall.
        /// </summary>
        public Summary Compute(float budgetMs, float hitchMultiplier)
        {
            var s = new Summary();
            if (_count == 0) return s;

            Array.Copy(_ms, _scratch, _ms.Length);
            // Only the first _count entries are real while the ring is still filling, so compact them to the
            // front before sorting. Once it has wrapped, every slot is real and this is the whole array.
            if (_count < _ms.Length)
            {
                int start = (_head - _count + _ms.Length) % _ms.Length;
                for (int i = 0; i < _count; i++) _scratch[i] = _ms[(start + i) % _ms.Length];
            }
            Array.Sort(_scratch, 0, _count);

            double sum = 0.0;
            for (int i = 0; i < _count; i++) sum += _scratch[i];

            s.Count = _count;
            s.MeanMs = (float)(sum / _count);
            s.P50Ms = Percentile(_scratch, _count, 0.50f);
            s.P95Ms = Percentile(_scratch, _count, 0.95f);
            s.P99Ms = Percentile(_scratch, _count, 0.99f);
            s.WorstMs = _scratch[_count - 1];
            s.BestMs = _scratch[0];

            // The 1% LOW is the mean of the slowest 1% of frames, expressed as fps. It is the number the
            // games industry settled on because it tracks perceived smoothness far better than an average
            // does, and it is the one to quote when asking "can the headset carry another element".
            int tail = _count / 100;
            if (tail < 1) tail = 1;
            double tailSum = 0.0;
            for (int i = _count - tail; i < _count; i++) tailSum += _scratch[i];
            float tailMeanMs = (float)(tailSum / tail);
            s.OnePctLowFps = tailMeanMs > 0f ? 1000f / tailMeanMs : 0f;

            s.MeanFps = s.MeanMs > 0f ? 1000f / s.MeanMs : 0f;

            float hitchThreshold = s.P50Ms * (hitchMultiplier > 1f ? hitchMultiplier : 2f);
            int hitches = 0, over = 0;
            for (int i = 0; i < _count; i++)
            {
                if (_scratch[i] > hitchThreshold) hitches++;
                if (budgetMs > 0f && _scratch[i] > budgetMs) over++;
            }
            s.Hitches = hitches;
            s.HitchThresholdMs = hitchThreshold;
            s.OverBudgetPct = 100f * over / _count;

            return s;
        }

        // Nearest-rank percentile on an already-sorted array. Nearest-rank rather than interpolated on
        // purpose: an interpolated p99 invents a frame time that never happened, and every one of these
        // numbers should be a frame that actually occurred.
        static float Percentile(float[] sorted, int count, float p)
        {
            if (count <= 0) return 0f;
            int rank = (int)Math.Ceiling(p * count) - 1;
            if (rank < 0) rank = 0;
            if (rank >= count) rank = count - 1;
            return sorted[rank];
        }

        /// <summary>One computed view of the window. A plain struct so producing one allocates nothing.</summary>
        public struct Summary
        {
            public int Count;
            public float MeanMs, P50Ms, P95Ms, P99Ms, WorstMs, BestMs;
            public float MeanFps, OnePctLowFps;
            public int Hitches;
            public float HitchThresholdMs;
            public float OverBudgetPct;
        }
    }
}
