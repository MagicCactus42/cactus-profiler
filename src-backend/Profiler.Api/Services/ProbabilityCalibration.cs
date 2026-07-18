namespace Profiler.Api.Services
{
    /// <summary>
    /// Probability calibration helpers shared by training and prediction so both
    /// sides apply identical math.
    ///
    /// The multiclass trainers used here (LightGBM, SDCA maximum entropy) already
    /// emit a normalized probability distribution in their Score column. Applying a
    /// second softmax to those values (as the old prediction path did) re-exponentiates
    /// probabilities in [0,1] and severely flattens confidence. The correct operation
    /// on an existing probability vector is temperature scaling:
    ///
    ///     q_i = p_i^(1/T) / sum_j p_j^(1/T)
    ///
    /// which is equivalent to softmax(logit_i / T) and reduces to the identity at T = 1.
    /// T is fit on held-out validation data (Guo et al., "On Calibration of Modern
    /// Neural Networks") to correct over/under-confidence.
    /// </summary>
    public static class ProbabilityCalibration
    {
        private const double Epsilon = 1e-12;

        /// <summary>
        /// Temperature-scale an existing probability distribution. T &gt; 1 softens
        /// (less confident), T &lt; 1 sharpens (more confident), T = 1 is a no-op.
        /// Computed in log-space for numerical stability.
        /// </summary>
        public static float[] TemperatureScale(float[] probabilities, float temperature)
        {
            if (probabilities == null || probabilities.Length == 0)
                return Array.Empty<float>();

            if (temperature <= 0 || float.IsNaN(temperature))
                temperature = 1f;

            double invT = 1.0 / temperature;
            var logits = probabilities.Select(p => invT * Math.Log(Math.Max(Epsilon, p))).ToArray();

            double maxLogit = logits.Max();
            var exp = logits.Select(l => Math.Exp(l - maxLogit)).ToArray();
            double sum = exp.Sum();

            if (sum <= 0 || double.IsNaN(sum) || double.IsInfinity(sum))
                return Enumerable.Repeat(1f / probabilities.Length, probabilities.Length).ToArray();

            return exp.Select(e => (float)(e / sum)).ToArray();
        }

        /// <summary>
        /// Negative log-likelihood of the calibrated distribution against the true class.
        /// Used to select the temperature that best calibrates validation predictions.
        /// </summary>
        public static double NegativeLogLikelihood(
            IReadOnlyList<(float[] Probabilities, int TrueIndex)> samples,
            float temperature)
        {
            if (samples.Count == 0) return double.MaxValue;

            double total = 0;
            int counted = 0;
            foreach (var (probs, trueIndex) in samples)
            {
                if (trueIndex < 0 || trueIndex >= probs.Length) continue;
                var q = TemperatureScale(probs, temperature);
                total += -Math.Log(Math.Max(Epsilon, q[trueIndex]));
                counted++;
            }

            return counted > 0 ? total / counted : double.MaxValue;
        }

        /// <summary>
        /// Grid-search the temperature in [0.5, 5.0] that minimizes validation NLL.
        /// Returns 1.0 (no calibration) when there is insufficient data.
        /// </summary>
        public static float FitTemperature(
            IReadOnlyList<(float[] Probabilities, int TrueIndex)> samples)
        {
            if (samples == null || samples.Count < 5)
                return 1.0f;

            float best = 1.0f;
            double bestNll = double.MaxValue;
            for (float t = 0.5f; t <= 5.0001f; t += 0.1f)
            {
                double nll = NegativeLogLikelihood(samples, t);
                if (nll < bestNll)
                {
                    bestNll = nll;
                    best = t;
                }
            }

            return best;
        }
    }
}
