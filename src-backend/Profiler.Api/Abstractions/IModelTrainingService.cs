using Profiler.Api.Services;

namespace Profiler.Api.abstractions
{
    public interface IModelTrainingService
    {
        void TrainAndSaveModel();
        TrainingMetrics GetLastTrainingMetrics();
    }
}