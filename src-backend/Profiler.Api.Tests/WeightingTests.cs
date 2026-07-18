using Profiler.Api.Models;
using Profiler.Api.Services;
using Xunit;

namespace Profiler.Api.Tests;

public class WeightingTests
{
    [Fact]
    public void FeatureColumns_ExcludeWeight_IncludeNewFeatures()
    {
        var names = ModelTrainingService.GetFeatureColumnNames();

        // Weight is a training-weight column, never a biometric feature.
        Assert.DoesNotContain(nameof(ProfilingModelInput.Weight), names);

        Assert.Contains(nameof(ProfilingModelInput.ObservedDigraphFraction), names);
        Assert.Contains(nameof(ProfilingModelInput.ObservedTrigraphFraction), names);
        Assert.Contains(nameof(ProfilingModelInput.MeanUpDownFlightTime), names);
    }

    [Fact]
    public void ExampleWeights_AreInverseClassFrequency()
    {
        var data = new List<ProfilingModelInput>();
        for (int i = 0; i < 6; i++) data.Add(new ProfilingModelInput { UserId = "big" });
        for (int i = 0; i < 2; i++) data.Add(new ProfilingModelInput { UserId = "small" });

        ModelTrainingService.ApplyExampleWeights(data);

        // total=8, classes=2: big -> 8/(2*6) = 0.667, small -> 8/(2*2) = 2.0
        Assert.Equal(8f / 12f, data.First(d => d.UserId == "big").Weight, precision: 3);
        Assert.Equal(2.0f, data.First(d => d.UserId == "small").Weight, precision: 3);

        // Every class contributes the same total weighted mass.
        float bigMass = data.Where(d => d.UserId == "big").Sum(d => d.Weight);
        float smallMass = data.Where(d => d.UserId == "small").Sum(d => d.Weight);
        Assert.Equal(bigMass, smallMass, precision: 3);
    }
}
