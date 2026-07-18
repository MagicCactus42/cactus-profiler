using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Profiler.Api.Services;
using Xunit;

namespace Profiler.Api.Tests;

/// <summary>
/// Trains the ensemble on synthetic data, then drives the full identify path
/// (extract -> predict -> Bayesian fuse) against held-out sessions.
/// </summary>
public class EndToEndTests
{
    [Fact]
    public void Train_Then_Identify_RecognizesHeldOutUser()
    {
        var extractor = new FeatureExtractorService();
        var trainer = new ModelTrainingService(null!, extractor);

        var sessions = SyntheticData.BuildSessions(sessionsPerUser: 10);
        var metrics = trainer.TrainFromSessions(sessions);

        Assert.True(metrics.EnsembleSize >= 1);
        Assert.True(metrics.GroupAwareValidation);
        // With well-separated users, group-honest validation accuracy should still be strong.
        Assert.True(metrics.MicroAccuracy > 0.7,
            $"micro accuracy unexpectedly low: {metrics.MicroAccuracy:P1}");

        var prediction = new ModelPredictionService(NullLogger<ModelPredictionService>.Instance);
        var sessionSvc = new IdentificationSessionService(new MemoryCache(new MemoryCacheOptions()));

        // A brand-new session for a known user (unseen seed).
        var target = SyntheticData.Users[2]; // carol
        var heldOut = SyntheticData.BuildSession(target.BaseDwell, target.BaseFlight, target.Variance, seed: 9999);
        var features = extractor.ExtractFeatures(heldOut);

        var result = prediction.IdentifyUser(features);

        Assert.Equal(target.Nick, result.PredictedUser);
        // Probabilities must remain a real distribution (no double-softmax collapse).
        Assert.Equal(1.0f, result.AllProbabilities.Sum(), precision: 3);
        Assert.True(result.AllProbabilities.Max() > 0.5f,
            $"top probability collapsed: {result.AllProbabilities.Max():F3}");

        // Fusing several held-out samples should authenticate the correct user.
        string finalUser = target.Nick;
        float finalConf = 0;
        for (int i = 0; i < 6; i++)
        {
            var s = SyntheticData.BuildSession(target.BaseDwell, target.BaseFlight, target.Variance, seed: 20000 + i);
            var r = prediction.IdentifyUser(extractor.ExtractFeatures(s));
            (finalUser, finalConf, _) = sessionSvc.AddEvidence("e2e", r.AllLabels, r.AllProbabilities);
        }

        Assert.Equal(target.Nick, finalUser);
        Assert.True(finalConf > 0.75f, $"fused confidence too low: {finalConf:F3}");

        // Open-set: a genuine enrolled user is not flagged as novel...
        Assert.False(result.IsNovel, $"genuine sample flagged novel (dist {result.NoveltyScore:F3})");

        // ...but an impostor whose timing matches no enrolled user is, even though
        // the closed-set softmax still names some nearest user.
        var impostorSession = SyntheticData.BuildSession(baseDwell: 320, baseFlight: 500, variance: 40, seed: 31337);
        var impostorResult = prediction.IdentifyUser(extractor.ExtractFeatures(impostorSession));
        Assert.True(impostorResult.IsNovel,
            $"impostor not flagged (dist {impostorResult.NoveltyScore:F3})");
        Assert.False(impostorResult.IsAuthenticated);
    }

    [Fact]
    public void SmallDataset_UsesSingleModelPath_AndStillTrains()
    {
        var extractor = new FeatureExtractorService();
        var trainer = new ModelTrainingService(null!, extractor);

        // Two users => fewer than the 3 required for the ensemble path.
        var sessions = new List<Profiler.Api.Entities.TypingSession>();
        sessions.AddRange(SyntheticData.BuildSessions(sessionsPerUser: 3).Where(s => s.UserId is "alice" or "bob"));

        var metrics = trainer.TrainFromSessions(sessions);

        Assert.Equal(1, metrics.EnsembleSize);
        Assert.Contains("Single", metrics.Algorithm);
        Assert.Equal(2, metrics.UniqueUsers);
    }
}
