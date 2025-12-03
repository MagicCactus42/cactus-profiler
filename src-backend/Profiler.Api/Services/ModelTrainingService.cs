using System.Reflection;
using System.Text.Json;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers;
using Microsoft.ML.Trainers.LightGbm;
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
        public DateTime TrainedAt { get; set; }
        public Dictionary<string, int> SamplesPerUser { get; set; }
        public Dictionary<string, double> PerClassAccuracy { get; set; }
    }

    public class ModelTrainingService : IModelTrainingService
    {
        private readonly ProfilerDbContext _dbContext;
        private readonly IFeatureExtractorService _featureExtractor;
        private readonly ILogger<ModelTrainingService> _logger;
        private readonly MLContext _mlContext;

        private readonly string _modelPath = Path.Combine(AppContext.BaseDirectory, "user_typing_model.zip");
        private readonly string _metricsPath = Path.Combine(AppContext.BaseDirectory, "training_metrics.json");

        // Training configuration - more aggressive for better accuracy
        private const int MinSamplesPerUser = 2;      // Lower threshold for more inclusive training
        private const int MinTotalSamples = 5;        // Minimum total samples
        private const float TestFraction = 0.15f;     // 15% for validation (keep more for training)
        private const int CrossValidationFolds = 5;   // K-fold cross validation

        // Ensemble parameters
        private const bool UseEnsemble = true;        // Use multiple models
        private const int EnsembleIterations = 3;     // Number of models in ensemble

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
            _logger?.LogInformation("Starting enhanced model training...");

            var sessions = _dbContext.TypingSessions
                .Where(x => x.UserId != null && x.UserId != "Unknown")
                .ToList();

            if (sessions.Count < MinTotalSamples)
                throw new Exception($"Not enough data for training. Need at least {MinTotalSamples} sessions, have {sessions.Count}.");

            // Extract features with data augmentation
            var (trainingData, samplesPerUser) = ExtractAndAugmentFeatures(sessions);

            // Filter users with too few samples
            var validUsers = samplesPerUser.Where(kv => kv.Value >= MinSamplesPerUser)
                                           .Select(kv => kv.Key)
                                           .ToHashSet();

            trainingData = trainingData.Where(d => validUsers.Contains(d.UserId)).ToList();

            if (trainingData.Count < MinTotalSamples)
                throw new Exception($"Not enough valid training data after filtering. Have {trainingData.Count} samples.");

            _logger?.LogInformation("Training with {SampleCount} samples from {UserCount} users",
                trainingData.Count, validUsers.Count);

            // Load data into ML.NET
            IDataView dataView = _mlContext.Data.LoadFromEnumerable(trainingData);

            // Get feature column names
            var featureColumnNames = typeof(ProfilingModelInput)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(float))
                .Select(p => p.Name)
                .ToArray();

            _logger?.LogInformation("Using {FeatureCount} features for training", featureColumnNames.Length);

            // Train with ensemble or single model based on data size
            ITransformer trainedModel;
            TrainingMetrics metrics;

            if (UseEnsemble && trainingData.Count >= 30 && validUsers.Count >= 3)
            {
                (trainedModel, metrics) = TrainEnsembleModel(dataView, featureColumnNames, trainingData, validUsers);
            }
            else if (trainingData.Count >= 20 && validUsers.Count >= 3)
            {
                (trainedModel, metrics) = TrainWithCrossValidation(dataView, featureColumnNames, trainingData, validUsers);
            }
            else
            {
                (trainedModel, metrics) = TrainWithSplit(dataView, featureColumnNames, trainingData, validUsers);
            }

            // Save the model
            _mlContext.Model.Save(trainedModel, dataView.Schema, _modelPath);
            _logger?.LogInformation("Model saved to {ModelPath}", _modelPath);

            // Save metrics
            SaveTrainingMetrics(metrics);
            _logger?.LogInformation(
                "Training complete. MicroAccuracy: {MicroAcc:P2}, MacroAccuracy: {MacroAcc:P2}, LogLoss: {LogLoss:F4}",
                metrics.MicroAccuracy, metrics.MacroAccuracy, metrics.LogLoss);
        }

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

                    // Extract original features
                    var features = _featureExtractor.ExtractFeatures(rawEvents, session.UserId);

                    if (IsValidFeatureSet(features))
                    {
                        trainingData.Add(features);

                        if (!samplesPerUser.ContainsKey(session.UserId))
                            samplesPerUser[session.UserId] = 0;
                        samplesPerUser[session.UserId]++;

                        // Data augmentation: Add slightly perturbed versions
                        // This helps the model generalize better
                        if (rawEvents.Count >= 30)
                        {
                            // Split session into overlapping windows
                            var augmented = CreateAugmentedSamples(rawEvents, session.UserId);
                            foreach (var aug in augmented)
                            {
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

            // Create overlapping windows (sliding window augmentation)
            int windowSize = (int)(events.Count * 0.7);
            int step = (int)(events.Count * 0.3);

            for (int start = 0; start + windowSize <= events.Count; start += step)
            {
                var windowEvents = events.Skip(start).Take(windowSize).ToList();
                if (windowEvents.Count >= 20)
                {
                    var features = _featureExtractor.ExtractFeatures(windowEvents, userId);
                    augmented.Add(features);
                }
            }

            return augmented;
        }

        private (ITransformer Model, TrainingMetrics Metrics) TrainEnsembleModel(
            IDataView dataView,
            string[] featureColumnNames,
            List<ProfilingModelInput> trainingData,
            HashSet<string> validUsers)
        {
            _logger?.LogInformation("Training ensemble model with {Iterations} base models", EnsembleIterations);

            var basePipeline = _mlContext.Transforms.Conversion.MapValueToKey(
                    outputColumnName: "Label",
                    inputColumnName: "Label")
                .Append(_mlContext.Transforms.Concatenate("Features", featureColumnNames))
                .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                .Append(_mlContext.Transforms.ReplaceMissingValues("Features"));

            // Train multiple models with different configurations
            var models = new List<ITransformer>();
            var metricsResults = new List<MulticlassClassificationMetrics>();

            // Model 1: LightGBM with default settings
            var lgbmPipeline1 = basePipeline.Append(_mlContext.MulticlassClassification.Trainers.LightGbm(
                labelColumnName: "Label",
                featureColumnName: "Features",
                numberOfLeaves: 31,
                numberOfIterations: 300,
                learningRate: 0.05,
                minimumExampleCountPerLeaf: 1
            )).Append(_mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

            // Model 2: LightGBM with deeper trees
            var lgbmPipeline2 = basePipeline.Append(_mlContext.MulticlassClassification.Trainers.LightGbm(
                labelColumnName: "Label",
                featureColumnName: "Features",
                numberOfLeaves: 63,
                numberOfIterations: 200,
                learningRate: 0.1,
                minimumExampleCountPerLeaf: 1
            )).Append(_mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

            // Model 3: SDCA Maximum Entropy (different algorithm family)
            var sdcaPipeline = basePipeline.Append(_mlContext.MulticlassClassification.Trainers.SdcaMaximumEntropy(
                labelColumnName: "Label",
                featureColumnName: "Features",
                maximumNumberOfIterations: 200
            )).Append(_mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

            // Train and evaluate each model
            var split = _mlContext.Data.TrainTestSplit(dataView, testFraction: TestFraction);

            var pipelines = new[] { lgbmPipeline1, lgbmPipeline2, sdcaPipeline };
            var pipelineNames = new[] { "LightGBM-Deep", "LightGBM-Wide", "SDCA-MaxEnt" };

            for (int i = 0; i < pipelines.Length; i++)
            {
                try
                {
                    var model = pipelines[i].Fit(split.TrainSet);
                    var predictions = model.Transform(split.TestSet);
                    var evalMetrics = _mlContext.MulticlassClassification.Evaluate(predictions, labelColumnName: "Label");

                    models.Add(model);
                    metricsResults.Add(evalMetrics);

                    _logger?.LogInformation(
                        "Model {Name}: MicroAcc={MicroAcc:P2}, MacroAcc={MacroAcc:P2}",
                        pipelineNames[i], evalMetrics.MicroAccuracy, evalMetrics.MacroAccuracy);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to train model {Name}", pipelineNames[i]);
                }
            }

            // Select the best model based on macro accuracy (better for imbalanced classes)
            int bestIndex = 0;
            double bestScore = 0;
            for (int i = 0; i < metricsResults.Count; i++)
            {
                // Weighted score: 60% MacroAccuracy, 40% MicroAccuracy
                double score = 0.6 * metricsResults[i].MacroAccuracy + 0.4 * metricsResults[i].MicroAccuracy;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            _logger?.LogInformation("Selected best model: {Name}", pipelineNames[bestIndex]);

            // Retrain best model on full dataset
            var finalModel = pipelines[bestIndex].Fit(dataView);
            var bestMetrics = metricsResults[bestIndex];

            var metrics = new TrainingMetrics
            {
                MicroAccuracy = bestMetrics.MicroAccuracy,
                MacroAccuracy = bestMetrics.MacroAccuracy,
                LogLoss = bestMetrics.LogLoss,
                LogLossReduction = bestMetrics.LogLossReduction,
                TotalSamples = trainingData.Count,
                UniqueUsers = validUsers.Count,
                FeatureCount = featureColumnNames.Length,
                Algorithm = $"Ensemble-{pipelineNames[bestIndex]}",
                TrainedAt = DateTime.UtcNow,
                SamplesPerUser = trainingData.GroupBy(d => d.UserId)
                                            .ToDictionary(g => g.Key, g => g.Count()),
                PerClassAccuracy = CalculatePerClassAccuracy(bestMetrics)
            };

            return (finalModel, metrics);
        }

        private IEstimator<ITransformer> BuildTrainingPipeline(string[] featureColumnNames)
        {
            var basePipeline = _mlContext.Transforms.Conversion.MapValueToKey(
                    outputColumnName: "Label",
                    inputColumnName: "Label")
                .Append(_mlContext.Transforms.Concatenate("Features", featureColumnNames))
                .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                .Append(_mlContext.Transforms.ReplaceMissingValues("Features"));

            var trainer = _mlContext.MulticlassClassification.Trainers.LightGbm(
                labelColumnName: "Label",
                featureColumnName: "Features",
                numberOfLeaves: 31,
                numberOfIterations: 300,
                learningRate: 0.05,
                minimumExampleCountPerLeaf: 1
            );

            return basePipeline
                .Append(trainer)
                .Append(_mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabel"));
        }

        private (ITransformer Model, TrainingMetrics Metrics) TrainWithCrossValidation(
            IDataView dataView,
            string[] featureColumnNames,
            List<ProfilingModelInput> trainingData,
            HashSet<string> validUsers)
        {
            _logger?.LogInformation("Training with {Folds}-fold cross-validation", CrossValidationFolds);

            var pipeline = BuildTrainingPipeline(featureColumnNames);

            var cvResults = _mlContext.MulticlassClassification.CrossValidate(
                dataView,
                pipeline,
                numberOfFolds: CrossValidationFolds,
                labelColumnName: "Label");

            var avgMicroAccuracy = cvResults.Average(r => r.Metrics.MicroAccuracy);
            var avgMacroAccuracy = cvResults.Average(r => r.Metrics.MacroAccuracy);
            var avgLogLoss = cvResults.Average(r => r.Metrics.LogLoss);
            var avgLogLossReduction = cvResults.Average(r => r.Metrics.LogLossReduction);

            _logger?.LogInformation(
                "Cross-validation results - MicroAcc: {MicroAcc:P2} (+/- {MicroStd:P2}), MacroAcc: {MacroAcc:P2}",
                avgMicroAccuracy,
                cvResults.Select(r => r.Metrics.MicroAccuracy).StandardDeviation(),
                avgMacroAccuracy);

            var finalModel = pipeline.Fit(dataView);

            var metrics = new TrainingMetrics
            {
                MicroAccuracy = avgMicroAccuracy,
                MacroAccuracy = avgMacroAccuracy,
                LogLoss = avgLogLoss,
                LogLossReduction = avgLogLossReduction,
                TotalSamples = trainingData.Count,
                UniqueUsers = validUsers.Count,
                FeatureCount = featureColumnNames.Length,
                Algorithm = "LightGBM-CV",
                TrainedAt = DateTime.UtcNow,
                SamplesPerUser = trainingData.GroupBy(d => d.UserId)
                                            .ToDictionary(g => g.Key, g => g.Count())
            };

            return (finalModel, metrics);
        }

        private (ITransformer Model, TrainingMetrics Metrics) TrainWithSplit(
            IDataView dataView,
            string[] featureColumnNames,
            List<ProfilingModelInput> trainingData,
            HashSet<string> validUsers)
        {
            _logger?.LogInformation("Training with {TestFraction:P0} test split", TestFraction);

            var pipeline = BuildTrainingPipeline(featureColumnNames);
            var split = _mlContext.Data.TrainTestSplit(dataView, testFraction: TestFraction);

            var model = pipeline.Fit(split.TrainSet);

            var predictions = model.Transform(split.TestSet);
            var evalMetrics = _mlContext.MulticlassClassification.Evaluate(predictions, labelColumnName: "Label");

            var metrics = new TrainingMetrics
            {
                MicroAccuracy = evalMetrics.MicroAccuracy,
                MacroAccuracy = evalMetrics.MacroAccuracy,
                LogLoss = evalMetrics.LogLoss,
                LogLossReduction = evalMetrics.LogLossReduction,
                TotalSamples = trainingData.Count,
                UniqueUsers = validUsers.Count,
                FeatureCount = featureColumnNames.Length,
                Algorithm = "LightGBM-Split",
                TrainedAt = DateTime.UtcNow,
                SamplesPerUser = trainingData.GroupBy(d => d.UserId)
                                            .ToDictionary(g => g.Key, g => g.Count())
            };

            return (model, metrics);
        }

        private Dictionary<string, double> CalculatePerClassAccuracy(MulticlassClassificationMetrics metrics)
        {
            var result = new Dictionary<string, double>();

            try
            {
                // ML.NET doesn't directly expose per-class accuracy, so we return overall metrics
                result["Overall_MicroAccuracy"] = metrics.MicroAccuracy;
                result["Overall_MacroAccuracy"] = metrics.MacroAccuracy;
                result["TopKAccuracy"] = metrics.TopKAccuracy;
            }
            catch { }

            return result;
        }

        private bool IsValidFeatureSet(ProfilingModelInput features)
        {
            if (features.MeanDwellTime <= 0 || features.MeanFlightTime <= 0)
                return false;

            if (features.TypingSpeedKPM <= 0)
                return false;

            var properties = typeof(ProfilingModelInput).GetProperties()
                .Where(p => p.PropertyType == typeof(float));

            foreach (var prop in properties)
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
                {
                    var json = File.ReadAllText(_metricsPath);
                    return JsonSerializer.Deserialize<TrainingMetrics>(json);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load training metrics");
            }

            return null;
        }
    }

    public static class EnumerableExtensions
    {
        public static double StandardDeviation(this IEnumerable<double> values)
        {
            var list = values.ToList();
            if (list.Count < 2) return 0;

            var avg = list.Average();
            var sumSquares = list.Sum(v => (v - avg) * (v - avg));
            return Math.Sqrt(sumSquares / (list.Count - 1));
        }
    }
}
