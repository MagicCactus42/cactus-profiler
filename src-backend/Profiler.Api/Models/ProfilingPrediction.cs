using Microsoft.ML.Data;

namespace Profiler.Api.Models
{
    public class ProfilingPrediction
    {
        [ColumnName("PredictedLabel")]
        public string PredictedUser { get; set; }

        public float[] Score { get; set; }
    }
}