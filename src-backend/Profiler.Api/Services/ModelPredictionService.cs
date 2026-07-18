using System.Text.Json;
using Microsoft.ML;
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

        // Open-set: true when the sample doesn't resemble ANY enrolled user
        // (distance to every user centroid above threshold, or calibrated top
        // probability below the validation floor).
        public bool IsNovel { get; set; }
        public float NoveltyScore { get; set; }
    }

    public class ModelPredictionService : IModelPredictionService
    {
        private readonly MLContext _mlContext;
        private readonly ILogger<ModelPredictionService> _logger;

        // Soft-voting ensemble: one prediction engine per member. A single-model
        // deployment is just an ensemble of size one.
        private readonly List<PredictionEngine<ProfilingModelInput, ProfilingPrediction>> _engines = new();
        private string[] _labels = Array.Empty<string>();
        private float _temperature = 1.0f;
        private float _probabilityFloor;
        private NoveltyModel _novelty;

        private readonly string _modelPath = Path.Combine(AppContext.BaseDirectory, "user_typing_model.zip");
        private readonly string _ensembleDir = Path.Combine(AppContext.BaseDirectory, "ml_ensemble");
        private string ManifestPath => Path.Combine(_ensembleDir, "manifest.json");
        private readonly object _lock = new object();

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
                _engines.Clear();
                _labels = Array.Empty<string>();
                _temperature = 1.0f;
                _probabilityFloor = 0f;
                _novelty = null;

                try
                {
                    if (File.Exists(ManifestPath))
                        LoadEnsemble();
                    else if (File.Exists(_modelPath))
                        LoadSingle();
                    else
                        _logger.LogWarning("No model found at {Manifest} or {Model}", ManifestPath, _modelPath);

                    if (_engines.Count > 0)
                    {
                        _labels = ModelTrainingService.ReadLabels(_engines[0].OutputSchema);
                        _logger.LogInformation(
                            "Loaded {Count} model(s) with {LabelCount} labels (T={Temp:F2}).",
                            _engines.Count, _labels.Length, _temperature);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load model.");
                }
            }
        }

        private void LoadEnsemble()
        {
            var manifest = JsonSerializer.Deserialize<EnsembleManifest>(File.ReadAllText(ManifestPath));
            if (manifest == null) return;

            foreach (var member in manifest.Members)
            {
                var path = Path.Combine(_ensembleDir, member);
                if (!File.Exists(path)) continue;
                var model = _mlContext.Model.Load(path, out _);
                _engines.Add(_mlContext.Model.CreatePredictionEngine<ProfilingModelInput, ProfilingPrediction>(model));
            }

            if (manifest.Temperature > 0) _temperature = manifest.Temperature;
            _probabilityFloor = manifest.ProbabilityFloor;
            _novelty = manifest.Novelty;
        }

        private void LoadSingle()
        {
            var model = _mlContext.Model.Load(_modelPath, out _);
            _engines.Add(_mlContext.Model.CreatePredictionEngine<ProfilingModelInput, ProfilingPrediction>(model));
        }

        public void ReloadModel() => LoadModel();

        public IdentificationResult IdentifyUser(ProfilingModelInput features)
        {
            lock (_lock)
            {
                if (_engines.Count == 0)
                {
                    LoadModel();
                    if (_engines.Count == 0)
                        return NotReady();
                }

                // Average the members' probability vectors (soft voting). Each member
                // already emits a normalized distribution, so the mean is one too.
                float[] probabilities = null;
                int counted = 0;
                foreach (var engine in _engines)
                {
                    var score = engine.Predict(features).Score;
                    if (score == null || score.Length == 0) continue;
                    if (probabilities == null) probabilities = new float[score.Length];
                    if (score.Length != probabilities.Length) continue;
                    for (int i = 0; i < probabilities.Length; i++) probabilities[i] += score[i];
                    counted++;
                }

                if (probabilities == null || counted == 0)
                    return NotReady();

                for (int i = 0; i < probabilities.Length; i++) probabilities[i] /= counted;

                // Calibrate. Score is ALREADY a probability distribution, so this is
                // temperature scaling (identity at T=1) — NOT another softmax.
                probabilities = ProbabilityCalibration.TemperatureScale(probabilities, _temperature);

                int maxIndex = 0;
                for (int i = 1; i < probabilities.Length; i++)
                    if (probabilities[i] > probabilities[maxIndex]) maxIndex = i;

                float maxScore = probabilities[maxIndex];
                float entropyScore = CalculateNormalizedEntropy(probabilities);
                float marginScore = CalculateMarginScore(probabilities);

                // Open-set check: does this sample resemble ANY enrolled user?
                float noveltyScore = _novelty != null ? NoveltyDetector.Score(_novelty, features) : 0f;
                bool isNovel =
                    (_novelty != null && noveltyScore > _novelty.DistanceThreshold)
                    || (_probabilityFloor > 0 && maxScore < _probabilityFloor);

                string predictedUser = maxIndex < _labels.Length ? _labels[maxIndex] : "Unknown";

                return new IdentificationResult
                {
                    PredictedUser = predictedUser,
                    Confidence = maxScore,
                    IsAuthenticated = !isNovel && maxScore >= AuthenticationThreshold,
                    AllProbabilities = probabilities,
                    AllLabels = _labels,
                    EntropyScore = entropyScore,
                    MarginScore = marginScore,
                    IsNovel = isNovel,
                    NoveltyScore = noveltyScore
                };
            }
        }

        private IdentificationResult NotReady() => new IdentificationResult
        {
            PredictedUser = "ModelNotReady",
            Confidence = 0,
            AllProbabilities = Array.Empty<float>(),
            AllLabels = Array.Empty<string>()
        };

        /// <summary>Normalized entropy in [0,1]: 0 = certain, 1 = maximally uncertain.</summary>
        private float CalculateNormalizedEntropy(float[] probabilities)
        {
            if (probabilities.Length <= 1) return 0;

            float entropy = 0;
            const float epsilon = 1e-10f;
            foreach (var p in probabilities)
                if (p > epsilon) entropy -= p * (float)Math.Log(p);

            float maxEntropy = (float)Math.Log(probabilities.Length);
            return maxEntropy > 0 ? entropy / maxEntropy : 0;
        }

        /// <summary>Gap between the top two probabilities (larger = more decisive).</summary>
        private float CalculateMarginScore(float[] probabilities)
        {
            if (probabilities.Length < 2) return 1;
            var sorted = probabilities.OrderByDescending(p => p).ToArray();
            return sorted[0] - sorted[1];
        }

        public void SetTemperature(float temperature)
        {
            if (temperature > 0)
            {
                lock (_lock) _temperature = temperature;
                _logger.LogInformation("Temperature set to {Temperature}", temperature);
            }
        }

        public (int LabelCount, string[] Labels, float Temperature) GetModelInfo()
        {
            lock (_lock)
            {
                return (_labels.Length, _labels, _temperature);
            }
        }
    }
}
