using System.Reflection;
using System.Text.Json;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms;
using Profiler.Api.abstractions;
using Profiler.Api.DAL;
using Profiler.Api.Entities;
using Profiler.Api.Models;

namespace Profiler.Api.Services
{
    public class TrainingMetrics
    {
        public double MicroAccuracy { get; set; }
        public double MacroAccuracy { get; set; }
        public double LogLoss { get; set; }
        public double LogLossReduction { get; set; }
        public int TotalSamples { get; set; }
        public int UniqueUsers { get; set; }
        public int FeatureCount { get; set; }
        public string Algorithm { get; set; }
        public float Temperature { get; set; } = 1.0f;
        public int EnsembleSize { get; set; } = 1;
        public bool GroupAwareValidation { get; set; }
        public DateTime TrainedAt { get; set; }
        public Dictionary<string, int> SamplesPerUser { get; set; }
        public Dictionary<string, double> PerClassAccuracy { get; set; }
    }

    /// <summary>
    /// Persisted description of a saved soft-voting ensemble: the member model files
    /// (relative to the ensemble directory) plus the fitted calibration temperature.
    /// </summary>
    public class EnsembleManifest
    {
        public List<string> Members { get; set; } = new();
        public float Temperature { get; set; } = 1.0f;
        public string Algorithm { get; set; }
        public int FeatureCount { get; set; }
        public DateTime TrainedAt { get; set; }
    }

    public class ModelTrainingService : IModelTrainingService
    {
        private readonly ProfilerDbContext _dbContext;
        private readonly IFeatureExtractorService _featureExtractor;
        private readonly ILogger<ModelTrainingService> _logger;
        private readonly MLContext _mlContext;

        private readonly string _modelPath = Path.Combine(AppContext.BaseDirectory, "user_typing_model.zip");
        private readonly string _metricsPath = Path.Combine(AppContext.BaseDirectory, "training_metrics.json");
        private readonly string _ensembleDir = Path.Combine(AppContext.BaseDirectory, "ml_ensemble");

        private string ManifestPath => Path.Combine(_ensembleDir, "manifest.json");

        // Training configuration.
        private const int MinSamplesPerUser = 2;
        private const int MinTotalSamples = 5;
        private const float TestFraction = 0.2f;      // Group-aware held-out validation.
        private const int CrossValidationFolds = 5;

        public ModelTrainingService(
            ProfilerDbContext dbContext,
            IFeatureExtractorService featureExtractor,
            ILogger<ModelTrainingService> logger = null)
        {
            _dbContext = dbContext;
            _featureExtractor = featureExtractor;
            _logger = logger;
            _mlContext = new MLContext(seed: 42);
        }

        public void TrainAndSaveModel()
        {
            _logger?.LogInformation("Starting model training...");

            var sessions = _dbContext.TypingSessions
                .Where(x => x.UserId != null && x.UserId != "Unknown")
                .ToList();

            TrainFromSessions(sessions);
        }

        /// <summary>
        /// Pure training entry point (no database access) so the ML pipeline can be
        /// exercised directly in tests. Trains an ensemble, calibrates it, and persists
        /// the ensemble + metrics. Returns the validation metrics.
        /// </summary>
        public TrainingMetrics TrainFromSessions(List<TypingSession> sessions)
        {
            if (sessions.Count < MinTotalSamples)
                throw new Exception($"Not enough data for training. Need at least {MinTotalSamples} sessions, have {sessions.Count}.");

            var (trainingData, samplesPerUser) = ExtractAndAugmentFeatures(sessions);

            var validUsers = samplesPerUser.Where(kv => kv.Value >= MinSamplesPerUser)
                                           .Select(kv => kv.Key)
                                           .ToHashSet();

            trainingData = trainingData.Where(d => validUsers.Contains(d.UserId)).ToList();

            if (trainingData.Count < MinTotalSamples)
                throw new Exception($"Not enough valid training data after filtering. Have {trainingData.Count} samples.");

            _logger?.LogInformation("Training with {SampleCount} samples from {UserCount} users",
                trainingData.Count, validUsers.Count);

            IDataView dataView = _mlContext.Data.LoadFromEnumerable(trainingData);

            var featureColumnNames = GetFeatureColumnNames();
            _logger?.LogInformation("Using {FeatureCount} features for training", featureColumnNames.Length);

            EnsembleTrainResult result;
            if (trainingData.Count >= 30 && validUsers.Count >= 3)
            {
                result = TrainEnsemble(dataView, featureColumnNames, trainingData, validUsers);
            }
            else
            {
                result = TrainSingleModel(dataView, featureColumnNames, trainingData, validUsers);
            }

            SaveEnsemble(result.Members, result.Temperature, result.Algorithm,
                dataView.Schema, featureColumnNames.Length);
            SaveTrainingMetrics(result.Metrics);

            _logger?.LogInformation(
                "Training complete. Algorithm={Algorithm}, Ensemble={Size}, Temperature={Temp:F2}, MicroAcc={MicroAcc:P2}, MacroAcc={MacroAcc:P2}, LogLoss={LogLoss:F4}",
                result.Metrics.Algorithm, result.Metrics.EnsembleSize, result.Metrics.Temperature,
                result.Metrics.MicroAccuracy, result.Metrics.MacroAccuracy, result.Metrics.LogLoss);

            return result.Metrics;
        }

        public static string[] GetFeatureColumnNames()
        {
            return typeof(ProfilingModelInput)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(float))
                .Select(p => p.Name)
                .ToArray();
        }

        private class EnsembleTrainResult
        {
            public List<ITransformer> Members { get; set; }
            public float Temperature { get; set; }
            public string Algorithm { get; set; }
            public TrainingMetrics Metrics { get; set; }
        }

        // ---- Feature extraction & augmentation ----

        private (List<ProfilingModelInput> Data, Dictionary<string, int> SamplesPerUser) ExtractAndAugmentFeatures(
            List<TypingSession> sessions)
        {
            var trainingData = new List<ProfilingModelInput>();
            var samplesPerUser = new Dictionary<string, int>();

            foreach (var session in sessions)
            {
                try
                {
                    var rawEvents = JsonSerializer.Deserialize<List<KeystrokeEvent>>(session.RawDataJson);
                    if (rawEvents == null || rawEvents.Count < 10) continue;

                    string groupKey = session.Id.ToString();

                    var features = _featureExtractor.ExtractFeatures(rawEvents, session.UserId);
                    features.GroupKey = groupKey;

                    if (IsValidFeatureSet(features))
                    {
                        trainingData.Add(features);
                        samplesPerUser[session.UserId] = samplesPerUser.GetValueOrDefault(session.UserId) + 1;

                        // Sliding-window augmentation. Every window carries the SAME
                        // GroupKey as its source session so group-aware splitting keeps
                        // them together and they never leak across train/validation.
                        if (rawEvents.Count >= 30)
                        {
                            foreach (var aug in CreateAugmentedSamples(rawEvents, session.UserId))
                            {
                                aug.GroupKey = groupKey;
                                if (IsValidFeatureSet(aug))
                                {
                                    trainingData.Add(aug);
                                    samplesPerUser[session.UserId]++;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to process session {SessionId}", session.Id);
                }
            }

            return (trainingData, samplesPerUser);
        }

        private List<ProfilingModelInput> CreateAugmentedSamples(List<KeystrokeEvent> events, string userId)
        {
            var augmented = new List<ProfilingModelInput>();

            int windowSize = (int)(events.Count * 0.7);
            int step = (int)(events.Count * 0.3);
            if (step <= 0) return augmented;

            for (int start = 0; start + windowSize <= events.Count; start += step)
            {
                var windowEvents = events.Skip(start).Take(windowSize).ToList();
                if (windowEvents.Count >= 20)
                {
                    augmented.Add(_featureExtractor.ExtractFeatures(windowEvents, userId));
                }
            }

            return augmented;
        }

        // ---- Pipeline construction ----

        private IEstimator<ITransformer> BuildMemberPipeline(
            string[] featureColumnNames, IEstimator<ITransformer> trainer)
        {
            // ByValue key ordinality makes the label ordering deterministic (sorted)
            // and identical across every ensemble member, which is what allows their
            // Score vectors to be averaged element-wise.
            return _mlContext.Transforms.Conversion.MapValueToKey(
                    outputColumnName: "Label",
                    inputColumnName: "Label",
                    keyOrdinality: ValueToKeyMappingEstimator.KeyOrdinality.ByValue)
                .Append(_mlContext.Transforms.Concatenate("Features", featureColumnNames))
                .Append(_mlContext.Transforms.ReplaceMissingValues("Features"))
                // Mean/variance standardization is far less outlier-sensitive than the
                // previous min-max scaling (a single 2s dwell no longer sets the range).
                .Append(_mlContext.Transforms.NormalizeMeanVariance("Features"))
                .Append(trainer)
                .Append(_mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabel"));
        }

        private (string Name, IEstimator<ITransformer> Trainer)[] BuildBaseTrainers()
        {
            return new (string, IEstimator<ITransformer>)[]
            {
                ("LightGBM-Wide", _mlContext.MulticlassClassification.Trainers.LightGbm(
                    labelColumnName: "Label", featureColumnName: "Features",
                    numberOfLeaves: 63, numberOfIterations: 200, learningRate: 0.1,
                    minimumExampleCountPerLeaf: 1)),
                ("LightGBM-Deep", _mlContext.MulticlassClassification.Trainers.LightGbm(
                    labelColumnName: "Label", featureColumnName: "Features",
                    numberOfLeaves: 31, numberOfIterations: 400, learningRate: 0.03,
                    minimumExampleCountPerLeaf: 1)),
                ("SDCA-MaxEnt", _mlContext.MulticlassClassification.Trainers.SdcaMaximumEntropy(
                    labelColumnName: "Label", featureColumnName: "Features",
                    maximumNumberOfIterations: 300)),
            };
        }

        // ---- Ensemble training ----

        private EnsembleTrainResult TrainEnsemble(
            IDataView dataView, string[] featureColumnNames,
            List<ProfilingModelInput> trainingData, HashSet<string> validUsers)
        {
            var trainers = BuildBaseTrainers();
            _logger?.LogInformation("Training soft-voting ensemble of {Count} base models", trainers.Length);

            // Group-aware split: all windows of one session stay on the same side.
            var split = _mlContext.Data.TrainTestSplit(
                dataView, testFraction: TestFraction, samplingKeyColumnName: nameof(ProfilingModelInput.GroupKey));

            var valStageMembers = new List<ITransformer>();
            foreach (var (name, trainer) in trainers)
            {
                try { valStageMembers.Add(BuildMemberPipeline(featureColumnNames, trainer).Fit(split.TrainSet)); }
                catch (Exception ex) { _logger?.LogWarning(ex, "Validation-stage member {Name} failed", name); }
            }

            float temperature = 1.0f;
            TrainingMetrics valMetrics = null;

            if (valStageMembers.Count > 0)
            {
                var valRows = _mlContext.Data
                    .CreateEnumerable<ProfilingModelInput>(split.TestSet, reuseRowObject: false)
                    .ToList();

                var engines = valStageMembers
                    .Select(m => _mlContext.Model.CreatePredictionEngine<ProfilingModelInput, ProfilingPrediction>(m))
                    .ToList();
                var labels = ReadLabels(engines[0].OutputSchema);

                var labelIndex = new Dictionary<string, int>();
                for (int i = 0; i < labels.Length; i++) labelIndex[labels[i]] = i;

                var samples = new List<(float[] Probabilities, int TrueIndex)>();
                foreach (var row in valRows)
                {
                    var avg = AveragePredict(engines, row);
                    if (avg == null) continue;
                    int trueIndex = labelIndex.GetValueOrDefault(row.UserId, -1);
                    samples.Add((avg, trueIndex));
                }

                temperature = ProbabilityCalibration.FitTemperature(samples);
                valMetrics = ScoreEnsemble(samples, labels.Length, temperature);
                _logger?.LogInformation(
                    "Ensemble validation: MicroAcc={Micro:P2}, MacroAcc={Macro:P2}, LogLoss={LogLoss:F4}, T={Temp:F2}",
                    valMetrics.MicroAccuracy, valMetrics.MacroAccuracy, valMetrics.LogLoss, temperature);
            }

            // Retrain every member on the full dataset for the shipped model.
            var finalMembers = new List<ITransformer>();
            foreach (var (name, trainer) in trainers)
            {
                try { finalMembers.Add(BuildMemberPipeline(featureColumnNames, trainer).Fit(dataView)); }
                catch (Exception ex) { _logger?.LogWarning(ex, "Final member {Name} failed", name); }
            }

            if (finalMembers.Count == 0)
                throw new Exception("All ensemble members failed to train.");

            var metrics = valMetrics ?? new TrainingMetrics();
            metrics.Algorithm = $"SoftVote[{string.Join(",", trainers.Select(t => t.Name))}]";
            metrics.EnsembleSize = finalMembers.Count;
            metrics.Temperature = temperature;
            metrics.GroupAwareValidation = true;
            FillCommonMetrics(metrics, trainingData, validUsers, featureColumnNames.Length);

            return new EnsembleTrainResult
            {
                Members = finalMembers,
                Temperature = temperature,
                Algorithm = metrics.Algorithm,
                Metrics = metrics
            };
        }

        // ---- Single-model training (small datasets) ----

        private EnsembleTrainResult TrainSingleModel(
            IDataView dataView, string[] featureColumnNames,
            List<ProfilingModelInput> trainingData, HashSet<string> validUsers)
        {
            var trainer = _mlContext.MulticlassClassification.Trainers.LightGbm(
                labelColumnName: "Label", featureColumnName: "Features",
                numberOfLeaves: 31, numberOfIterations: 300, learningRate: 0.05,
                minimumExampleCountPerLeaf: 1);
            var pipeline = BuildMemberPipeline(featureColumnNames, trainer);

            var metrics = new TrainingMetrics
            {
                Algorithm = "LightGBM-Single",
                EnsembleSize = 1,
                Temperature = 1.0f
            };

            var split = _mlContext.Data.TrainTestSplit(
                dataView, testFraction: TestFraction, samplingKeyColumnName: nameof(ProfilingModelInput.GroupKey));
            try
            {
                var evalModel = pipeline.Fit(split.TrainSet);
                var evalMetrics = _mlContext.MulticlassClassification.Evaluate(
                    evalModel.Transform(split.TestSet), labelColumnName: "Label");
                metrics.MicroAccuracy = evalMetrics.MicroAccuracy;
                metrics.MacroAccuracy = evalMetrics.MacroAccuracy;
                metrics.LogLoss = evalMetrics.LogLoss;
                metrics.LogLossReduction = evalMetrics.LogLossReduction;
                metrics.GroupAwareValidation = true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Group-aware validation failed; training on full data without validation metrics");
            }

            var finalModel = pipeline.Fit(dataView);
            FillCommonMetrics(metrics, trainingData, validUsers, featureColumnNames.Length);

            return new EnsembleTrainResult
            {
                Members = new List<ITransformer> { finalModel },
                Temperature = 1.0f,
                Algorithm = metrics.Algorithm,
                Metrics = metrics
            };
        }

        // ---- Ensemble helpers ----

        private static float[] AveragePredict(
            List<PredictionEngine<ProfilingModelInput, ProfilingPrediction>> engines,
            ProfilingModelInput row)
        {
            float[] acc = null;
            int counted = 0;
            foreach (var engine in engines)
            {
                var score = engine.Predict(row).Score;
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

        private static TrainingMetrics ScoreEnsemble(
            List<(float[] Probabilities, int TrueIndex)> samples, int classCount, float temperature)
        {
            var metrics = new TrainingMetrics();
            if (samples.Count == 0) return metrics;

            int correct = 0, total = 0;
            double logLoss = 0;
            var perClassTotal = new int[classCount];
            var perClassCorrect = new int[classCount];

            foreach (var (probs, trueIndex) in samples)
            {
                if (trueIndex < 0 || trueIndex >= classCount) continue;
                var q = ProbabilityCalibration.TemperatureScale(probs, temperature);

                int predicted = 0;
                for (int i = 1; i < q.Length; i++) if (q[i] > q[predicted]) predicted = i;

                total++;
                perClassTotal[trueIndex]++;
                if (predicted == trueIndex) { correct++; perClassCorrect[trueIndex]++; }
                logLoss += -Math.Log(Math.Max(1e-12, q[trueIndex]));
            }

            if (total == 0) return metrics;

            metrics.MicroAccuracy = (double)correct / total;
            var recalls = new List<double>();
            for (int c = 0; c < classCount; c++)
                if (perClassTotal[c] > 0) recalls.Add((double)perClassCorrect[c] / perClassTotal[c]);
            metrics.MacroAccuracy = recalls.Count > 0 ? recalls.Average() : 0;
            metrics.LogLoss = logLoss / total;
            metrics.PerClassAccuracy = new Dictionary<string, double>
            {
                ["Overall_MicroAccuracy"] = metrics.MicroAccuracy,
                ["Overall_MacroAccuracy"] = metrics.MacroAccuracy
            };
            return metrics;
        }

        private static void FillCommonMetrics(
            TrainingMetrics metrics, List<ProfilingModelInput> trainingData,
            HashSet<string> validUsers, int featureCount)
        {
            metrics.TotalSamples = trainingData.Count;
            metrics.UniqueUsers = validUsers.Count;
            metrics.FeatureCount = featureCount;
            metrics.TrainedAt = DateTime.UtcNow;
            metrics.SamplesPerUser = trainingData.GroupBy(d => d.UserId)
                                                 .ToDictionary(g => g.Key, g => g.Count());
        }

        internal static string[] ReadLabels(DataViewSchema schema)
        {
            var scoreColumn = schema.GetColumnOrNull("Score");
            if (!scoreColumn.HasValue) return Array.Empty<string>();

            var slotNames = new VBuffer<ReadOnlyMemory<char>>();
            scoreColumn.Value.GetSlotNames(ref slotNames);
            return slotNames.DenseValues().Select(v => v.ToString()).ToArray();
        }

        // ---- Persistence ----

        private void SaveEnsemble(
            List<ITransformer> members, float temperature, string algorithm,
            DataViewSchema schema, int featureCount)
        {
            Directory.CreateDirectory(_ensembleDir);

            foreach (var stale in Directory.EnumerateFiles(_ensembleDir, "member_*.zip"))
            {
                try { File.Delete(stale); } catch { /* best effort */ }
            }

            var manifest = new EnsembleManifest
            {
                Temperature = temperature,
                Algorithm = algorithm,
                FeatureCount = featureCount,
                TrainedAt = DateTime.UtcNow
            };

            for (int i = 0; i < members.Count; i++)
            {
                string file = $"member_{i}.zip";
                _mlContext.Model.Save(members[i], schema, Path.Combine(_ensembleDir, file));
                manifest.Members.Add(file);
            }

            // Backwards-compatible single-model file (first member).
            _mlContext.Model.Save(members[0], schema, _modelPath);

            File.WriteAllText(ManifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            _logger?.LogInformation("Saved ensemble of {Count} member(s) to {Dir}", members.Count, _ensembleDir);
        }

        private bool IsValidFeatureSet(ProfilingModelInput features)
        {
            if (features.MeanDwellTime <= 0 || features.MeanFlightTime <= 0)
                return false;
            if (features.TypingSpeedKPM <= 0)
                return false;

            foreach (var prop in typeof(ProfilingModelInput).GetProperties()
                         .Where(p => p.PropertyType == typeof(float)))
            {
                var value = (float)prop.GetValue(features);
                if (float.IsNaN(value) || float.IsInfinity(value))
                    return false;
            }

            return true;
        }

        private void SaveTrainingMetrics(TrainingMetrics metrics)
        {
            try
            {
                var json = JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_metricsPath, json);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to save training metrics");
            }
        }

        public TrainingMetrics GetLastTrainingMetrics()
        {
            try
            {
                if (File.Exists(_metricsPath))
                    return JsonSerializer.Deserialize<TrainingMetrics>(File.ReadAllText(_metricsPath));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load training metrics");
            }

            return null;
        }
    }
}
