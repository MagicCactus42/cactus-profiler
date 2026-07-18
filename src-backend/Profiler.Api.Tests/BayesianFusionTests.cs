using Microsoft.Extensions.Caching.Memory;
using Profiler.Api.Services;
using Xunit;

namespace Profiler.Api.Tests;

public class BayesianFusionTests
{
    private static IdentificationSessionService NewService() =>
        new(new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public void ConsistentEvidence_IdentifiesUserAndGrowsConfidence()
    {
        var svc = NewService();
        var labels = new[] { "A", "B", "C" };
        // Every sample favours B, but never decisively on its own.
        var sample = new[] { 0.25f, 0.5f, 0.25f };

        var (_, firstConf, _) = svc.AddEvidence("s1", labels, sample);

        float lastConf = firstConf;
        string lastUser = "B";
        for (int i = 0; i < 8; i++)
        {
            var (user, conf, _) = svc.AddEvidence("s1", labels, sample);
            lastUser = user;
            lastConf = conf;
        }

        Assert.Equal("B", lastUser);
        Assert.True(lastConf > firstConf, $"confidence should grow: {firstConf} -> {lastConf}");
        Assert.True(lastConf > 0.9f, $"confidence should be high after repeated evidence, was {lastConf}");
    }

    [Fact]
    public void SingleSample_ConfidenceEqualsTopProbability()
    {
        var svc = NewService();
        var labels = new[] { "A", "B", "C" };
        var (user, conf, count) = svc.AddEvidence("s2", labels, new[] { 0.7f, 0.2f, 0.1f });

        Assert.Equal("A", user);
        Assert.Equal(1, count);
        Assert.Equal(0.7f, conf, precision: 2); // first sample == renormalized top prob
    }

    [Fact]
    public void ConflictingEvidence_StaysUncertain()
    {
        var svc = NewService();
        var labels = new[] { "A", "B" };
        // Alternating winner: neither user should dominate.
        svc.AddEvidence("s3", labels, new[] { 0.8f, 0.2f });
        var (_, conf, _) = svc.AddEvidence("s3", labels, new[] { 0.2f, 0.8f });

        Assert.True(conf < 0.75f, $"conflicting evidence should not be confident, was {conf}");
    }
}
