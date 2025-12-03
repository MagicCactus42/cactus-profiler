using Profiler.Api.Models;
using Profiler.Api.Services;

namespace Profiler.Api.abstractions
{
    public interface IModelPredictionService
    {
        IdentificationResult IdentifyUser(ProfilingModelInput features);
        void ReloadModel();
    }
}