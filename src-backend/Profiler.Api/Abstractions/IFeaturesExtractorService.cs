

using Profiler.Api.Entities;
using Profiler.Api.Models;

namespace Profiler.Api.abstractions
{
    public interface IFeatureExtractorService
    {
        ProfilingModelInput ExtractFeatures(List<KeystrokeEvent> rawEvents, string userId = null);
    }
}