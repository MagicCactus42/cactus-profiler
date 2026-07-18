using System.Collections.Concurrent;
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
        /// <summary>
        /// Immutable view of the currently loaded model generation. Swapped atomically
        /// on reload; in-flight predictions keep using the snapshot they started with.
        /// </summary>
        private sealed class ModelSnapshot
        {
            public int Version;
            public List<ITransformer> Models = new();
            public string[] Labels = Array.Empty<string>();
            public float Temperature = 1.0f;
            public float ProbabilityFloor;
            public NoveltyModel Novelty;
        }

        private readonly MLContext _mlContext = new();
        private readonly ILogger<ModelPredictionService> _logger;
        private readonly object _lock = new object();

        private ModelSnapshot _snapshot = new();

        // PredictionEngine is not thread-safe, so callers rent an exclusive engine set
        // per call instead of serializing all inference behind one global lock.
        // Sets from an older model generation are discarded on return.
        private readonly ConcurrentBag<(int Version, List<PredictionEngine<ProfilingModelInput, ProfilingPrediction>> Engines)> _enginePool = new();

        private readonly string _modelPath = Path.Combine(AppContext.BaseDirectory, "user_typing_model.zip");
        private readonly string _ensembleDir = Path.Combine(AppContext.BaseDirectory, "ml_ensemble");
        private string ManifestPath => Path.Combine(_ensembleDir, "manifest.json");

        private const float AuthenticationThreshold = 0.90f;

        public ModelPredictionService(ILogger<ModelPredictionService> logger)
        {
            _logger = logger;
            LoadModel();
        }

        private void LoadModel()
        {
            lock (_lock)
            {
                var next = new ModelSnapshot { Version = _snapshot.Version + 1 };

                try
                {
                    if (File.Exists(ManifestPath))
                    {
                        var manifest = JsonSerializer.Deserialize<EnsembleManifest>(File.ReadAllText(ManifestPath));
                        if (manifest != null)
                        {
                            foreach (var member in manifest.Members)
                            {
                                var path = Path.Combine(_ensembleDir, member);
                                if (File.Exists(path))
                                    next.Models.Add(_mlContext.Model.Load(path, out _));
                            }
                            if (manifest.Temperature > 0) next.Temperature = manifest.Temperature;
                            next.ProbabilityFloor = manifest.ProbabilityFloor;
                            next.Novelty = manifest.Novelty;
                        }
                    }
                    else if (File.Exists(_modelPath))
                    {
                        next.Models.Add(_mlContext.Model.Load(_modelPath, out _));
                    }
                    else
                    {
                        _logger.LogWarning("No model found at {Manifest} or {Model}", ManifestPath, _modelPath);
                    }

                    if (next.Models.Count > 0)
                    {
                        var firstSet = CreateEngineSet(next.Models);
                        next.Labels = ModelTrainingService.ReadLabels(firstSet[0].OutputSchema);

                        _snapshot = next;
                        while (_enginePool.TryTake(out _)) { } // drop stale generations
                        _enginePool.Add((next.Version, firstSet));

                        _logger.LogInformation(
                            "Loaded {Count} model(s), {LabelCount} labels, T={Temp:F2}, open-set={OpenSet}.",
                            next.Models.Count, next.Labels.Length, next.Temperature, next.Novelty != null);
                    }
                    else
                    {
                        _snapshot = next;
                        while (_enginePool.TryTake(out _)) { }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load model.");
                }
            }
        }

        public void ReloadModel() => LoadModel();

        public IdentificationResult IdentifyUser(ProfilingModelInput features)
        {
            var snapshot = _snapshot;
            if (snapshot.Models.Count == 0)
            {
                LoadModel();
                snapshot = _snapshot;
                if (snapshot.Models.Count == 0)
                    return NotReady();
            }

            var engines = RentEngineSet(snapshot);
            float[] probabilities;
            try
            {
                probabilities = AverageScores(engines, features);
            }
            finally
            {
                ReturnEngineSet(snapshot.Version, engines);
            }

            if (probabilities == null)
                return NotReady();

            // Calibrate. Score is ALREADY a probability distribution, so this is
            // temperature scaling (identity at T=1) — NOT another softmax.
            probabilities = ProbabilityCalibration.TemperatureScale(probabilities, snapshot.Temperature);

            int maxIndex = 0;
            for (int i = 1; i < probabilities.Length; i++)
                if (probabilities[i] > probabilities[maxIndex]) maxIndex = i;

            float maxScore = probabilities[maxIndex];
            float noveltyScore = snapshot.Novelty != null
                ? NoveltyDetector.Score(snapshot.Novelty, features)
                : 0f;
            bool isNovel =
                (snapshot.Novelty != null && noveltyScore > snapshot.Novelty.DistanceThreshold)
                || (snapshot.ProbabilityFloor > 0 && maxScore < snapshot.ProbabilityFloor);

            return new IdentificationResult
            {
                PredictedUser = maxIndex < snapshot.Labels.Length ? snapshot.Labels[maxIndex] : "Unknown",
                Confidence = maxScore,
                IsAuthenticated = !isNovel && maxScore >= AuthenticationThreshold,
                AllProbabilities = probabilities,
                AllLabels = snapshot.Labels,
                EntropyScore = CalculateNormalizedEntropy(probabilities),
                MarginScore = CalculateMarginScore(probabilities),
                IsNovel = isNovel,
                NoveltyScore = noveltyScore
            };
        }

        // ---- Engine pool ----

        private List<PredictionEngine<ProfilingModelInput, ProfilingPrediction>> CreateEngineSet(
            List<ITransformer> models)
        {
            var set = new List<PredictionEngine<ProfilingModelInput, ProfilingPrediction>>(models.Count);
            foreach (var model in models)
                set.Add(_mlContext.Model.CreatePredictionEngine<ProfilingModelInput, ProfilingPrediction>(model));
            return set;
        }

        private List<PredictionEngine<ProfilingModelInput, ProfilingPrediction>> RentEngineSet(ModelSnapshot snapshot)
        {
            while (_enginePool.TryTake(out var entry))
            {
                if (entry.Version == snapshot.Version)
                    return entry.Engines;
                // Older generation: drop it and keep looking.
            }
            return CreateEngineSet(snapshot.Models);
        }

        private void ReturnEngineSet(
            int version, List<PredictionEngine<ProfilingModelInput, ProfilingPrediction>> engines)
        {
            if (version == _snapshot.Version)
                _enginePool.Add((version, engines));
        }

        /// <summary>
        /// Soft voting: average the members' probability vectors. Each member emits a
        /// normalized distribution, so the mean is one too.
        /// </summary>
        private static float[] AverageScores(
            List<PredictionEngine<ProfilingModelInput, ProfilingPrediction>> engines,
            ProfilingModelInput features)
        {
            float[] acc = null;
            int counted = 0;
            foreach (var engine in engines)
            {
                var score = engine.Predict(features).Score;
                if (score == null || score.Length == 0) continue;
                if (acc == null) acc = new float[score.Length];
                if (score.Length != acc.Length) continue;
                for (int i = 0; i < acc.Length; i++) acc[i] += score[i];
                counted++;
            }
            if (acc == null || counted == 0) return null;
            for (int i = 0; i < acc.Length; i++) acc[i] /= counted;
            return acc;
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
            if (temperature <= 0) return;
            lock (_lock)
            {
                var current = _snapshot;
                _snapshot = new ModelSnapshot
                {
                    Version = current.Version, // engines stay valid: models unchanged
                    Models = current.Models,
                    Labels = current.Labels,
                    Temperature = temperature,
                    ProbabilityFloor = current.ProbabilityFloor,
                    Novelty = current.Novelty
                };
            }
            _logger.LogInformation("Temperature set to {Temperature}", temperature);
        }

        public (int LabelCount, string[] Labels, float Temperature) GetModelInfo()
        {
            var snapshot = _snapshot;
            return (snapshot.Labels.Length, snapshot.Labels, snapshot.Temperature);
        }
    }
}
