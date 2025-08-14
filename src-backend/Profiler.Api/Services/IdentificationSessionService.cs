using Microsoft.Extensions.Caching.Memory;
using Profiler.Api.abstractions;

namespace Profiler.Api.Services
{
    /// <summary>
    /// Session state for progressive identification with user elimination
    /// </summary>
    public class SessionEvidenceState
    {
        public List<float[]> ScoreHistory { get; set; } = new List<float[]>();
        public float[] CumulativeScores { get; set; }
        public int SampleCount { get; set; }
        public DateTime LastUpdate { get; set; }
        public string[] Labels { get; set; }
        public HashSet<int> EliminatedIndices { get; set; } = new HashSet<int>();
        public List<string> EliminationLog { get; set; } = new List<string>();
    }

    public class IdentificationSessionService : IIdentificationSessionService
    {
        private readonly IMemoryCache _cache;
        private readonly ILogger<IdentificationSessionService> _logger;

        // Configuration
        private const int EliminationStartsAtSample = 3;      // Start eliminating after this many samples
        private const int MinUsersToKeep = 1;                 // Always keep at least 1 user (the winner)
        private const float HighConfidenceThreshold = 0.70f;  // Consider high confidence above this

        // Progressive elimination thresholds
        // Sample 3-9: 5%, Sample 10-14: 10%, Sample 15-19: 15%, etc.
        private const float BaseEliminationThreshold = 0.05f;
        private const int ThresholdIncreaseSampleInterval = 5; // Increase threshold every 5 samples after 10
        private const float ThresholdIncreaseAmount = 0.05f;   // Increase by 5% each interval

        public IdentificationSessionService(IMemoryCache cache, ILogger<IdentificationSessionService> logger = null)
        {
            _cache = cache;
            _logger = logger;
        }

        public (string BestUser, float Confidence, int SamplesCount) AddEvidence(string sessionId, string[] allLabels, float[] newScores)
        {
            var state = _cache.GetOrCreate(sessionId, entry =>
            {
                entry.SlidingExpiration = TimeSpan.FromMinutes(10);
                return InitializeState(allLabels, newScores.Length);
            });

            if (state == null)
                state = InitializeState(allLabels, newScores.Length);

            // Handle dimension mismatch - use minimum of both
            int effectiveLength = Math.Min(allLabels.Length, newScores.Length);

            // Re-initialize if dimensions changed
            if (state.CumulativeScores == null || state.CumulativeScores.Length != effectiveLength)
            {
                state = InitializeState(allLabels, effectiveLength);
            }

            // Log incoming scores for debugging
            _logger?.LogDebug("Sample {Sample}: Raw scores: [{Scores}]",
                state.SampleCount + 1,
                string.Join(", ", newScores.Take(effectiveLength).Select(s => s.ToString("F3"))));

            // Normalize incoming scores
            var normalizedScores = NormalizeScores(newScores, effectiveLength);

            // Store history
            state.ScoreHistory.Add(normalizedScores);
            state.SampleCount++;
            state.LastUpdate = DateTime.UtcNow;
            state.Labels = allLabels.Take(effectiveLength).ToArray();

            // Update cumulative scores with recency weighting
            UpdateCumulativeScores(state, normalizedScores);

            // Progressive elimination after enough samples
            if (state.SampleCount >= EliminationStartsAtSample)
            {
                PerformElimination(state);
            }

            // Calculate final probabilities considering eliminations
            var (finalScores, activeIndices) = GetActiveScores(state);

            // Find best prediction
            if (activeIndices.Length == 0)
            {
                return ("Unknown", 0f, state.SampleCount);
            }

            int bestActiveIndex = 0;
            float bestScore = finalScores[0];
            for (int i = 1; i < finalScores.Length; i++)
            {
                if (finalScores[i] > bestScore)
                {
                    bestScore = finalScores[i];
                    bestActiveIndex = i;
                }
            }

            int bestOriginalIndex = activeIndices[bestActiveIndex];
            string bestUser = state.Labels[bestOriginalIndex];

            // Calculate confidence based on margin and sample count
            float confidence = CalculateFinalConfidence(finalScores, state.SampleCount, activeIndices.Length);

            _logger?.LogDebug("Sample {Sample}: Best user={User}, Raw confidence={Raw:F3}, Final confidence={Final:F3}, Active users={Active}",
                state.SampleCount, bestUser, bestScore, confidence, activeIndices.Length);

            return (bestUser, confidence, state.SampleCount);
        }

        private SessionEvidenceState InitializeState(string[] labels, int length)
        {
            return new SessionEvidenceState
            {
                CumulativeScores = new float[length],
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

            // Copy and sum
            for (int i = 0; i < length && i < scores.Length; i++)
            {
                // Handle negative scores or zeros
                normalized[i] = Math.Max(0.0001f, scores[i]);
                sum += normalized[i];
            }

            // Normalize to sum to 1
            if (sum > 0)
            {
                for (int i = 0; i < length; i++)
                {
                    normalized[i] /= sum;
                }
            }
            else
            {
                // Uniform distribution if all zeros
                float uniform = 1.0f / length;
                for (int i = 0; i < length; i++)
                {
                    normalized[i] = uniform;
                }
            }

            return normalized;
        }

        private void UpdateCumulativeScores(SessionEvidenceState state, float[] newScores)
        {
            // Use exponential moving average with increasing weight for newer samples
            // This allows confidence to grow as more evidence accumulates
            float alpha = 0.3f + (0.4f * Math.Min(state.SampleCount, 5) / 5f); // Grows from 0.3 to 0.7

            for (int i = 0; i < state.CumulativeScores.Length && i < newScores.Length; i++)
            {
                if (state.SampleCount == 1)
                {
                    // First sample - just copy
                    state.CumulativeScores[i] = newScores[i];
                }
                else
                {
                    // EMA update
                    state.CumulativeScores[i] = alpha * newScores[i] + (1 - alpha) * state.CumulativeScores[i];
                }
            }

            // Re-normalize cumulative scores
            float sum = state.CumulativeScores.Sum();
            if (sum > 0)
            {
                for (int i = 0; i < state.CumulativeScores.Length; i++)
                {
                    state.CumulativeScores[i] /= sum;
                }
            }
        }

        /// <summary>
        /// Calculate the progressive elimination threshold based on sample count.
        /// Sample 3-9: 5%, Sample 10-14: 10%, Sample 15-19: 15%, Sample 20-24: 20%, etc.
        /// </summary>
        private float GetEliminationThreshold(int sampleCount)
        {
            if (sampleCount < 10)
                return BaseEliminationThreshold; // 5% for samples 3-9

            // Calculate how many intervals past sample 10
            int intervalsAfter10 = (sampleCount - 10) / ThresholdIncreaseSampleInterval + 1;
            float threshold = BaseEliminationThreshold + (intervalsAfter10 * ThresholdIncreaseAmount);

            // Cap at 50% to avoid eliminating everyone
            return Math.Min(threshold, 0.50f);
        }

        private void PerformElimination(SessionEvidenceState state)
        {
            // Count active users
            int activeCount = state.CumulativeScores.Length - state.EliminatedIndices.Count;

            if (activeCount <= MinUsersToKeep)
                return;

            // Get the progressive threshold for current sample count
            float currentThreshold = GetEliminationThreshold(state.SampleCount);

            // Find users to eliminate
            var candidates = new List<(int Index, float Score)>();
            for (int i = 0; i < state.CumulativeScores.Length; i++)
            {
                if (!state.EliminatedIndices.Contains(i))
                {
                    candidates.Add((i, state.CumulativeScores[i]));
                }
            }

            // Sort by score ascending
            candidates = candidates.OrderBy(c => c.Score).ToList();

            // Eliminate users below threshold, keeping minimum
            int canEliminate = activeCount - MinUsersToKeep;
            int eliminated = 0;

            foreach (var candidate in candidates)
            {
                if (eliminated >= canEliminate)
                    break;

                // Only eliminate if below the progressive threshold
                if (candidate.Score < currentThreshold)
                {
                    state.EliminatedIndices.Add(candidate.Index);
                    state.EliminationLog.Add($"Sample {state.SampleCount}: Eliminated {state.Labels[candidate.Index]} (score: {candidate.Score:F3}, threshold: {currentThreshold:F2})");
                    _logger?.LogInformation("Eliminated user {User} at sample {Sample} with score {Score:F3} (threshold: {Threshold:F2})",
                        state.Labels[candidate.Index], state.SampleCount, candidate.Score, currentThreshold);
                    eliminated++;
                }
            }

            // After elimination, redistribute probabilities among remaining users
            if (eliminated > 0)
            {
                RedistributeProbabilities(state);
            }
        }

        private void RedistributeProbabilities(SessionEvidenceState state)
        {
            float activeSum = 0;
            for (int i = 0; i < state.CumulativeScores.Length; i++)
            {
                if (!state.EliminatedIndices.Contains(i))
                {
                    activeSum += state.CumulativeScores[i];
                }
            }

            if (activeSum > 0)
            {
                for (int i = 0; i < state.CumulativeScores.Length; i++)
                {
                    if (state.EliminatedIndices.Contains(i))
                    {
                        state.CumulativeScores[i] = 0;
                    }
                    else
                    {
                        state.CumulativeScores[i] /= activeSum;
                    }
                }
            }
        }

        private (float[] Scores, int[] ActiveIndices) GetActiveScores(SessionEvidenceState state)
        {
            var activeIndices = new List<int>();
            var activeScores = new List<float>();

            for (int i = 0; i < state.CumulativeScores.Length; i++)
            {
                if (!state.EliminatedIndices.Contains(i))
                {
                    activeIndices.Add(i);
                    activeScores.Add(state.CumulativeScores[i]);
                }
            }

            // Normalize active scores
            float sum = activeScores.Sum();
            if (sum > 0)
            {
                for (int i = 0; i < activeScores.Count; i++)
                {
                    activeScores[i] /= sum;
                }
            }

            return (activeScores.ToArray(), activeIndices.ToArray());
        }

        private float CalculateFinalConfidence(float[] activeScores, int sampleCount, int activeUserCount)
        {
            if (activeScores.Length == 0)
                return 0f;

            float maxScore = activeScores.Max();

            // Base confidence is the top score
            float confidence = maxScore;

            // Boost confidence based on margin to second place
            if (activeScores.Length >= 2)
            {
                var sorted = activeScores.OrderByDescending(s => s).ToArray();
                float margin = sorted[0] - sorted[1];

                // Larger margin = higher confidence boost
                confidence += margin * 0.3f;
            }

            // Boost confidence based on sample count (more evidence = more certainty)
            float sampleBoost = Math.Min(0.15f, sampleCount * 0.03f);
            confidence += sampleBoost;

            // Boost confidence when fewer users remain (elimination has narrowed it down)
            if (activeUserCount <= 3)
            {
                confidence *= 1.1f;
            }
            if (activeUserCount == 2)
            {
                confidence *= 1.15f;
            }

            // Ensure confidence is in valid range
            confidence = Math.Max(0.05f, Math.Min(0.99f, confidence));

            return confidence;
        }
    }
}
