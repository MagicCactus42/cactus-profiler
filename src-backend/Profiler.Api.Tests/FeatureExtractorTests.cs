using Profiler.Api.Entities;
using Profiler.Api.Services;
using Xunit;

namespace Profiler.Api.Tests;

public class FeatureExtractorTests
{
    private readonly FeatureExtractorService _extractor = new();

    [Fact]
    public void Extraction_IsDeterministic()
    {
        var events = SyntheticData.BuildSession(baseDwell: 80, baseFlight: 60, variance: 5, seed: 7);
        var a = _extractor.ExtractFeatures(events, "u1");
        var b = _extractor.ExtractFeatures(events, "u1");

        Assert.Equal(a.MeanDwellTime, b.MeanDwellTime);
        Assert.Equal(a.MeanFlightTime, b.MeanFlightTime);
        Assert.Equal(a.MeanUpDownFlightTime, b.MeanUpDownFlightTime);
    }

    [Fact]
    public void ReleaseToPressFlight_MatchesConstructedGap()
    {
        // Constant 100ms dwell, constant 50ms release-to-press gap.
        // Press-to-press latency should be 150ms; release-to-press should be 50ms.
        var events = new List<KeystrokeEvent>();
        long t = 0;
        const int dwell = 100, gap = 50;
        foreach (char c in "abcdef")
        {
            events.Add(new KeystrokeEvent { Key = c.ToString(), Type = "keydown", Timestamp = t });
            t += dwell;
            events.Add(new KeystrokeEvent { Key = c.ToString(), Type = "keyup", Timestamp = t });
            t += gap;
        }

        var f = _extractor.ExtractFeatures(events, "u1");
        Assert.Equal(gap, f.MeanUpDownFlightTime, precision: 1);
        Assert.Equal(dwell + gap, f.MeanFlightTime, precision: 1);
    }

    [Fact]
    public void TypingSpeed_CountsKeyPressesPerMinute()
    {
        // 6 keys, 100ms dwell + 50ms gap: first keydown t=0, last keyup t=850.
        var events = new List<KeystrokeEvent>();
        long t = 0;
        foreach (char c in "abcdef")
        {
            events.Add(new KeystrokeEvent { Key = c.ToString(), Type = "keydown", Timestamp = t });
            t += 100;
            events.Add(new KeystrokeEvent { Key = c.ToString(), Type = "keyup", Timestamp = t });
            t += 50;
        }

        var f = _extractor.ExtractFeatures(events, "u1");
        float expected = (float)(6 / (850 / 60000.0)); // 6 presses over 850ms
        Assert.Equal(expected, f.TypingSpeedKPM, precision: 1);
    }

    [Fact]
    public void CoverageFractions_ReflectObservedNgraphs()
    {
        // English-like text contains "the" -> both fractions populated, in [0,1].
        var rich = _extractor.ExtractFeatures(
            SyntheticData.BuildSession(baseDwell: 80, baseFlight: 60, variance: 5, seed: 3), "u1");
        Assert.InRange(rich.ObservedDigraphFraction, 0.1f, 1f);
        Assert.InRange(rich.ObservedTrigraphFraction, 0.05f, 1f);

        // "abcdef" contains no tracked trigraph at all.
        var events = new List<KeystrokeEvent>();
        long t = 0;
        foreach (char c in "abcdef")
        {
            events.Add(new KeystrokeEvent { Key = c.ToString(), Type = "keydown", Timestamp = t });
            t += 100;
            events.Add(new KeystrokeEvent { Key = c.ToString(), Type = "keyup", Timestamp = t });
            t += 50;
        }
        var sparse = _extractor.ExtractFeatures(events, "u1");
        Assert.Equal(0f, sparse.ObservedTrigraphFraction);
    }

    [Fact]
    public void TooFewEvents_ReturnsDefaultWithUserId()
    {
        var f = _extractor.ExtractFeatures(new List<KeystrokeEvent>(), "u9");
        Assert.Equal("u9", f.UserId);
        Assert.Equal(0, f.MeanDwellTime);
    }
}
