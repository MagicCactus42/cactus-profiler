using Microsoft.Extensions.Caching.Memory;
using Profiler.Api.abstractions;

namespace Profiler.Api.Services
{
    /// <summary>
    /// State for progressive identification. Evidence from each typing sample is
    /// fused with Bayesian sequential updating: treating the per-sample model output
    /// as a likelihood over users and assuming samples are conditionally independent
    /// given the user, the log-posterior is the running sum of per-sample log-
    /// probabilities (a log-opinion pool / naive Bayes over samples). This replaces
    /// the previous hand-tuned EMA-with-magic-multipliers scheme with a calibrated
    /// posterior that sharpens as consistent evidence accumulates.
    /// </summary>
    public class SessionEvidenceState
    {
        public double[] LogPosterior { get; set; }   // Unnormalized running sum of log-likelihoods.
        public int SampleCount { get; set; }
        public int NovelSampleCount { get; set; }    // Samples flagged as matching no enrolled user.
        public DateTime LastUpdate { get; set; }
        public string[] Labels { get; set; }
        public HashSet<int> EliminatedIndices { get; set; } = new HashSet<int>();
        public List<string> EliminationLog { get; set; } = new List<string>();
    }

    public class IdentificationSessionService : IIdentificationSessionService
    {
        private readonly IMemoryCache _cache;
        private readonly ILogger<IdentificationSessionService> _logger;

        private const int EliminationStartsAtSample = 3;
        private const int MinUsersToKeep = 1;

        // Progressive elimination schedule: samples 3-9 => 5%, 10-14 => 10%, etc.
        private const float BaseEliminationThreshold = 0.05f;
        private const int ThresholdIncreaseSampleInterval = 5;
        private const float ThresholdIncreaseAmount = 0.05f;

        // Per-sample probability floor. Bounds how much a single overconfident-but-wrong
        // sample can push a user's log-likelihood, keeping the fusion robust.
        private const float PerSampleFloor = 1e-3f;

        public IdentificationSessionService(IMemoryCache cache, ILogger<IdentificationSessionService> logger = null)
        {
            _cache = cache;
            _logger = logger;
        }

        public (string BestUser, float Confidence, int SamplesCount) AddEvidence(
            string sessionId, string[] allLabels, float[] newScores, bool isNovel = false)
        {
            int effectiveLength = Math.Min(allLabels.Length, newScores.Length);
            if (effectiveLength == 0)
                return ("Unknown", 0f, 0);

            var state = _cache.GetOrCreate(sessionId, entry =>
            {
                entry.SlidingExpiration = TimeSpan.FromMinutes(10);
                return InitializeState(allLabels, effectiveLength);
            });

            if (state == null || state.LogPosterior == null || state.LogPosterior.Length != effectiveLength)
            {
                state = InitializeState(allLabels, effectiveLength);
                _cache.Set(sessionId, state, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(10) });
            }

            var normalized = NormalizeScores(newScores, effectiveLength);

            // Bayesian update: accumulate log-likelihood for every user.
            for (int i = 0; i < effectiveLength; i++)
                state.LogPosterior[i] += Math.Log(Math.Max(PerSampleFloor, normalized[i]));

            state.SampleCount++;
            if (isNovel) state.NovelSampleCount++;
            state.LastUpdate = DateTime.UtcNow;
            state.Labels = allLabels.Take(effectiveLength).ToArray();

            if (state.SampleCount >= EliminationStartsAtSample)
                PerformElimination(state);

            // Open-set verdict: when most samples in this session look like no
            // enrolled user, report Unknown rather than the least-bad known user.
            // Confidence carries the novel fraction.
            if (state.SampleCount >= EliminationStartsAtSample
                && state.NovelSampleCount * 2 > state.SampleCount)
            {
                float novelRatio = (float)state.NovelSampleCount / state.SampleCount;
                _logger?.LogInformation(
                    "Session {Session}: {Novel}/{Total} samples novel -> Unknown",
                    sessionId, state.NovelSampleCount, state.SampleCount);
                return ("Unknown", novelRatio, state.SampleCount);
            }

            var (posterior, activeIndices) = ComputePosterior(state);
            if (activeIndices.Length == 0)
                return ("Unknown", 0f, state.SampleCount);

            int bestLocal = 0;
            for (int i = 1; i < posterior.Length; i++)
                if (posterior[i] > posterior[bestLocal]) bestLocal = i;

            int bestOriginal = activeIndices[bestLocal];
            string bestUser = state.Labels[bestOriginal];
            float confidence = Math.Clamp(posterior[bestLocal], 0f, 0.9999f);

            _logger?.LogDebug(
                "Sample {Sample}: best={User}, confidence={Conf:F3}, active={Active}",
                state.SampleCount, bestUser, confidence, activeIndices.Length);

            return (bestUser, confidence, state.SampleCount);
        }

        private SessionEvidenceState InitializeState(string[] labels, int length)
        {
            return new SessionEvidenceState
            {
                LogPosterior = new double[length],
                Labels = labels.Take(length).ToArray(),
                SampleCount = 0,
                LastUpdate = DateTime.UtcNow,
                EliminatedIndices = new HashSet<int>()
            };
        }

        private float[] NormalizeScores(float[] scores, int length)
        {
            var normalized = new float[length];
            float sum = 0;
            for (int i = 0; i < length && i < scores.Length; i++)
            {
                normalized[i] = Math.Max(0.0001f, scores[i]);
                sum += normalized[i];
            }

            if (sum > 0)
                for (int i = 0; i < length; i++) normalized[i] /= sum;
            else
                for (int i = 0; i < length; i++) normalized[i] = 1.0f / length;

            return normalized;
        }

        /// <summary>Softmax of the log-posterior over the currently active (non-eliminated) users.</summary>
        private (float[] Posterior, int[] ActiveIndices) ComputePosterior(SessionEvidenceState state)
        {
            var activeIndices = new List<int>();
            for (int i = 0; i < state.LogPosterior.Length; i++)
                if (!state.EliminatedIndices.Contains(i)) activeIndices.Add(i);

            if (activeIndices.Count == 0)
                return (Array.Empty<float>(), Array.Empty<int>());

            double maxLog = activeIndices.Max(i => state.LogPosterior[i]);
            var exp = activeIndices.Select(i => Math.Exp(state.LogPosterior[i] - maxLog)).ToArray();
            double total = exp.Sum();

            var posterior = new float[activeIndices.Count];
            if (total > 0 && !double.IsNaN(total) && !double.IsInfinity(total))
                for (int i = 0; i < posterior.Length; i++) posterior[i] = (float)(exp[i] / total);
            else
                for (int i = 0; i < posterior.Length; i++) posterior[i] = 1.0f / posterior.Length;

            return (posterior, activeIndices.ToArray());
        }

        /// <summary>
        /// Sample 3-9: 5%, 10-14: 10%, 15-19: 15%, ... capped at 50%.
        /// </summary>
        private float GetEliminationThreshold(int sampleCount)
        {
            if (sampleCount < 10)
                return BaseEliminationThreshold;

            int intervalsAfter10 = (sampleCount - 10) / ThresholdIncreaseSampleInterval + 1;
            float threshold = BaseEliminationThreshold + (intervalsAfter10 * ThresholdIncreaseAmount);
            return Math.Min(threshold, 0.50f);
        }

        private void PerformElimination(SessionEvidenceState state)
        {
            var (posterior, activeIndices) = ComputePosterior(state);
            int activeCount = activeIndices.Length;
            if (activeCount <= MinUsersToKeep)
                return;

            float threshold = GetEliminationThreshold(state.SampleCount);

            var candidates = new List<(int OriginalIndex, float Score)>();
            for (int i = 0; i < activeIndices.Length; i++)
                candidates.Add((activeIndices[i], posterior[i]));
            candidates.Sort((a, b) => a.Score.CompareTo(b.Score));

            int canEliminate = activeCount - MinUsersToKeep;
            int eliminated = 0;
            foreach (var candidate in candidates)
            {
                if (eliminated >= canEliminate) break;
                if (candidate.Score < threshold)
                {
                    state.EliminatedIndices.Add(candidate.OriginalIndex);
                    state.EliminationLog.Add(
                        $"Sample {state.SampleCount}: Eliminated {state.Labels[candidate.OriginalIndex]} (posterior: {candidate.Score:F3}, threshold: {threshold:F2})");
                    _logger?.LogInformation(
                        "Eliminated {User} at sample {Sample} (posterior {Score:F3} < {Threshold:F2})",
                        state.Labels[candidate.OriginalIndex], state.SampleCount, candidate.Score, threshold);
                    eliminated++;
                }
            }
        }
    }
}
