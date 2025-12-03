using Microsoft.ML;
using Microsoft.ML.Data;
using Profiler.Api.abstractions;
using Profiler.Api.Models;

namespace Profiler.Api.Services
{
    public class IdentificationResult
    {
        public string PredictedUser { get; set; }
        public float Confidence { get; set; }
        public bool IsAuthenticated { get; set; }
        public float[] AllProbabilities { get; set; }
        public string[] AllLabels { get; set; }
        public float EntropyScore { get; set; }
        public float MarginScore { get; set; }
    }

    public class ModelPredictionService : IModelPredictionService
    {
        private readonly MLContext _mlContext;
        private readonly ILogger<ModelPredictionService> _logger;
        private ITransformer _trainedModel;
        private PredictionEngine<ProfilingModelInput, ProfilingPrediction> _predictionEngine;
        private string[] _labels;
        private readonly string _modelPath = Path.Combine(AppContext.BaseDirectory, "user_typing_model.zip");
        private readonly object _lock = new object();

        // Temperature scaling for calibration (lower = more confident, higher = less confident)
        // Start with 1.0 (no scaling), can be tuned based on validation data
        private float _temperature = 1.0f;

        // Confidence thresholds
        private const float HighConfidenceThreshold = 0.85f;
        private const float MediumConfidenceThreshold = 0.70f;
        private const float AuthenticationThreshold = 0.90f;

        public ModelPredictionService(ILogger<ModelPredictionService> logger)
        {
            _mlContext = new MLContext();
            _logger = logger;
            LoadModel();
        }

        private void LoadModel()
        {
            lock (_lock)
            {
                if (File.Exists(_modelPath))
                {
                    try
                    {
                        _trainedModel = _mlContext.Model.Load(_modelPath, out var modelInputSchema);
                        _predictionEngine = _mlContext.Model.CreatePredictionEngine<ProfilingModelInput, ProfilingPrediction>(_trainedModel);

                        // Extract labels from model
                        ExtractLabelsFromModel();

                        _logger.LogInformation("Model loaded successfully with {LabelCount} labels.", _labels?.Length ?? 0);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to load model.");
                    }
                }
                else
                {
                    _logger.LogWarning($"Model file not found at: {_modelPath}");
                }
            }
        }

        private void ExtractLabelsFromModel()
        {
            try
            {
                if (_trainedModel == null) return;

                var schema = _predictionEngine.OutputSchema;
                var labelColumn = schema.GetColumnOrNull("Score");

                if (labelColumn.HasValue)
                {
                    var slotNames = new VBuffer<ReadOnlyMemory<char>>();
                    labelColumn.Value.GetSlotNames(ref slotNames);

                    var denseSlotNames = slotNames.DenseValues().ToArray();
                    _labels = new string[denseSlotNames.Length];
                    for (int i = 0; i < denseSlotNames.Length; i++)
                    {
                        _labels[i] = denseSlotNames[i].ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not extract labels from model");
                _labels = Array.Empty<string>();
            }
        }

        public void ReloadModel()
        {
            LoadModel();
        }

        public IdentificationResult IdentifyUser(ProfilingModelInput features)
        {
            lock (_lock)
            {
                if (_predictionEngine == null)
                {
                    LoadModel();
                    if (_predictionEngine == null)
                    {
                        return new IdentificationResult
                        {
                            PredictedUser = "ModelNotReady",
                            Confidence = 0,
                            AllProbabilities = Array.Empty<float>(),
                            AllLabels = Array.Empty<string>()
                        };
                    }
                }

                var prediction = _predictionEngine.Predict(features);

                // Apply temperature-scaled softmax for better calibration
                var probabilities = TemperatureScaledSoftmax(prediction.Score, _temperature);

                // Calculate quality metrics
                float maxScore = probabilities.Max();
                int maxIndex = Array.IndexOf(probabilities, maxScore);
                float entropyScore = CalculateNormalizedEntropy(probabilities);
                float marginScore = CalculateMarginScore(probabilities);

                // Apply confidence adjustment based on prediction quality
                float adjustedConfidence = AdjustConfidence(maxScore, entropyScore, marginScore);

                // Determine authentication status
                bool isAuthenticated = adjustedConfidence >= AuthenticationThreshold;

                return new IdentificationResult
                {
                    PredictedUser = prediction.PredictedUser,
                    Confidence = adjustedConfidence,
                    IsAuthenticated = isAuthenticated,
                    AllProbabilities = probabilities,
                    AllLabels = _labels ?? Array.Empty<string>(),
                    EntropyScore = entropyScore,
                    MarginScore = marginScore
                };
            }
        }

        /// <summary>
        /// Temperature-scaled softmax for probability calibration.
        /// Lower temperature = sharper distribution (more confident)
        /// Higher temperature = smoother distribution (less confident)
        /// </summary>
        private float[] TemperatureScaledSoftmax(float[] scores, float temperature)
        {
            if (scores == null || scores.Length == 0)
                return Array.Empty<float>();

            // Apply temperature scaling to logits
            var scaledScores = scores.Select(s => s / temperature).ToArray();

            // Find max for numerical stability
            double maxScore = scaledScores.Max();

            // Compute exp and sum
            var exp = scaledScores.Select(x => Math.Exp(x - maxScore)).ToArray();
            var sum = exp.Sum();

            if (sum == 0 || double.IsNaN(sum) || double.IsInfinity(sum))
            {
                // Fallback to uniform distribution
                return Enumerable.Repeat(1.0f / scores.Length, scores.Length).ToArray();
            }

            return exp.Select(x => (float)(x / sum)).ToArray();
        }

        /// <summary>
        /// Calculate normalized entropy (0 = certain, 1 = maximum uncertainty)
        /// </summary>
        private float CalculateNormalizedEntropy(float[] probabilities)
        {
            if (probabilities.Length <= 1) return 0;

            float entropy = 0;
            const float epsilon = 1e-10f;

            foreach (var p in probabilities)
            {
                if (p > epsilon)
                {
                    entropy -= p * (float)Math.Log(p);
                }
            }

            float maxEntropy = (float)Math.Log(probabilities.Length);
            return maxEntropy > 0 ? entropy / maxEntropy : 0;
        }

        /// <summary>
        /// Calculate margin between top-2 predictions (higher = more confident)
        /// </summary>
        private float CalculateMarginScore(float[] probabilities)
        {
            if (probabilities.Length < 2) return 1;

            var sorted = probabilities.OrderByDescending(p => p).ToArray();
            return sorted[0] - sorted[1];
        }

        /// <summary>
        /// Adjust confidence based on prediction quality metrics
        /// </summary>
        private float AdjustConfidence(float rawConfidence, float entropyScore, float marginScore)
        {
            float adjustedConfidence = rawConfidence;

            // Penalize high entropy (uncertain) predictions
            if (entropyScore > 0.7f)
            {
                adjustedConfidence *= 0.85f;
            }
            else if (entropyScore > 0.5f)
            {
                adjustedConfidence *= 0.92f;
            }

            // Penalize low margin predictions
            if (marginScore < 0.1f)
            {
                adjustedConfidence *= 0.80f;
            }
            else if (marginScore < 0.2f)
            {
                adjustedConfidence *= 0.90f;
            }

            // Boost high-quality predictions
            if (entropyScore < 0.3f && marginScore > 0.4f)
            {
                adjustedConfidence = Math.Min(1.0f, adjustedConfidence * 1.05f);
            }

            return Math.Max(0, Math.Min(1, adjustedConfidence));
        }

        /// <summary>
        /// Set temperature for softmax calibration.
        /// Can be tuned based on validation performance.
        /// </summary>
        public void SetTemperature(float temperature)
        {
            if (temperature > 0)
            {
                _temperature = temperature;
                _logger.LogInformation("Temperature set to {Temperature}", temperature);
            }
        }

        /// <summary>
        /// Get current prediction statistics for monitoring
        /// </summary>
        public (int LabelCount, string[] Labels, float Temperature) GetModelInfo()
        {
            lock (_lock)
            {
                return (_labels?.Length ?? 0, _labels ?? Array.Empty<string>(), _temperature);
            }
        }
    }
}
