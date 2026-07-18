using System.Collections.Concurrent;
using System.Reflection;
using Profiler.Api.Models;

namespace Profiler.Api.Services
{
    /// <summary>
    /// Serializable open-set model: global feature standardization, one centroid per
    /// enrolled user in z-space, and a distance threshold fitted on the training
    /// data's own within-user distances.
    /// </summary>
    public class NoveltyModel
    {
        public string[] FeatureNames { get; set; } = Array.Empty<string>();
        public float[] Means { get; set; } = Array.Empty<float>();
        public float[] Stds { get; set; } = Array.Empty<float>();
        public Dictionary<string, float[]> Centroids { get; set; } = new();
        public float DistanceThreshold { get; set; }
    }

    /// <summary>
    /// Open-set (impostor) detection. A closed-set classifier assigns every sample to
    /// the nearest enrolled user no matter how unlike all of them it is — and the
    /// softmax can still report high probability for that nearest class. This detector
    /// measures how far a sample sits from every enrolled user's centroid in globally
    /// standardized feature space (RMS of clamped z-residuals, a diagonal Mahalanobis
    /// distance) and flags samples beyond a threshold derived from the training
    /// samples' own distances to their user centroids.
    /// </summary>
    public static class NoveltyDetector
    {
        // Per-dimension z-residuals are clamped so one corrupt feature (e.g. a single
        // outlier interval) cannot reject an otherwise-genuine sample on its own.
        private const float ZClamp = 10f;

        private static readonly ConcurrentDictionary<string, PropertyInfo> PropCache = new();

        public static NoveltyModel Fit(IReadOnlyList<ProfilingModelInput> data, string[] featureNames)
        {
            if (data == null || data.Count < 4 || featureNames == null || featureNames.Length == 0)
                return null;

            var props = ResolveProps(featureNames);
            int dims = props.Length;

            var vectors = new float[data.Count][];
            for (int r = 0; r < data.Count; r++)
                vectors[r] = RawVector(props, data[r]);

            var means = new float[dims];
            var stds = new float[dims];
            for (int c = 0; c < dims; c++)
            {
                double mean = 0;
                foreach (var v in vectors) mean += Finite(v[c]);
                mean /= data.Count;

                double variance = 0;
                foreach (var v in vectors)
                {
                    double d = Finite(v[c]) - mean;
                    variance += d * d;
                }
                variance /= data.Count;

                means[c] = (float)mean;
                stds[c] = (float)Math.Max(1e-3, Math.Sqrt(variance));
            }

            var centroids = new Dictionary<string, float[]>();
            var counts = new Dictionary<string, int>();
            for (int r = 0; r < data.Count; r++)
            {
                string user = data[r].UserId ?? "Unknown";
                if (!centroids.TryGetValue(user, out var acc))
                {
                    acc = new float[dims];
                    centroids[user] = acc;
                    counts[user] = 0;
                }
                for (int c = 0; c < dims; c++)
                    acc[c] += Z(vectors[r][c], means[c], stds[c]);
                counts[user]++;
            }
            foreach (var user in centroids.Keys.ToList())
                for (int c = 0; c < dims; c++)
                    centroids[user][c] /= counts[user];

            // Threshold from the distribution of genuine self-distances, with margin,
            // so a fresh sample from an enrolled user stays comfortably inside.
            var selfDistances = new List<float>(data.Count);
            for (int r = 0; r < data.Count; r++)
                selfDistances.Add(Distance(vectors[r], centroids[data[r].UserId ?? "Unknown"], means, stds));
            selfDistances.Sort();

            float p99 = selfDistances[(int)(0.99 * (selfDistances.Count - 1))];
            float max = selfDistances[^1];
            float threshold = Math.Max(p99 * 1.5f, max * 1.15f);

            return new NoveltyModel
            {
                FeatureNames = featureNames,
                Means = means,
                Stds = stds,
                Centroids = centroids,
                DistanceThreshold = threshold
            };
        }

        /// <summary>Distance to the nearest enrolled user's centroid; higher = more alien.</summary>
        public static float Score(NoveltyModel model, ProfilingModelInput sample)
        {
            if (model?.Centroids == null || model.Centroids.Count == 0)
                return 0;

            var props = ResolveProps(model.FeatureNames);
            var vec = RawVector(props, sample);

            float best = float.MaxValue;
            foreach (var centroid in model.Centroids.Values)
                best = Math.Min(best, Distance(vec, centroid, model.Means, model.Stds));
            return best;
        }

        public static bool IsNovel(NoveltyModel model, ProfilingModelInput sample) =>
            model != null && Score(model, sample) > model.DistanceThreshold;

        private static float Distance(float[] raw, float[] centroid, float[] means, float[] stds)
        {
            double sum = 0;
            int dims = centroid.Length;
            for (int c = 0; c < dims; c++)
            {
                double d = Z(raw[c], means[c], stds[c]) - centroid[c];
                sum += d * d;
            }
            return (float)Math.Sqrt(sum / dims);
        }

        private static float Z(float value, float mean, float std)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0;
            return Math.Clamp((value - mean) / std, -ZClamp, ZClamp);
        }

        private static double Finite(float value) =>
            float.IsNaN(value) || float.IsInfinity(value) ? 0 : value;

        private static PropertyInfo[] ResolveProps(string[] featureNames)
        {
            var props = new PropertyInfo[featureNames.Length];
            for (int i = 0; i < featureNames.Length; i++)
            {
                props[i] = PropCache.GetOrAdd(featureNames[i],
                    name => typeof(ProfilingModelInput).GetProperty(name)
                            ?? throw new ArgumentException($"Unknown feature '{name}'"));
            }
            return props;
        }

        private static float[] RawVector(PropertyInfo[] props, ProfilingModelInput sample)
        {
            var vec = new float[props.Length];
            for (int i = 0; i < props.Length; i++)
                vec[i] = (float)props[i].GetValue(sample);
            return vec;
        }
    }
}
