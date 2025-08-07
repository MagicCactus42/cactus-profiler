using Profiler.Api.abstractions;
using Profiler.Api.Entities;
using Profiler.Api.Models;

namespace Profiler.Api.Services
{
    public class FeatureExtractorService : IFeatureExtractorService
    {
        private const int MaxValidIntervalMs = 2000;
        private const int ShortPauseThresholdMs = 200;
        private const int MediumPauseThresholdMs = 500;

        // QWERTY keyboard layout mappings
        private static readonly HashSet<string> LeftHandKeys = new HashSet<string>
        {
            "q", "w", "e", "r", "t", "a", "s", "d", "f", "g", "z", "x", "c", "v", "b",
            "1", "2", "3", "4", "5", "`", "~"
        };

        private static readonly HashSet<string> RightHandKeys = new HashSet<string>
        {
            "y", "u", "i", "o", "p", "h", "j", "k", "l", "n", "m",
            "6", "7", "8", "9", "0", "-", "=", "[", "]", "\\", ";", "'", ",", ".", "/"
        };

        private static readonly HashSet<string> HomeRowKeys = new HashSet<string>
        {
            "a", "s", "d", "f", "g", "h", "j", "k", "l", ";"
        };

        private static readonly HashSet<string> TopRowKeys = new HashSet<string>
        {
            "q", "w", "e", "r", "t", "y", "u", "i", "o", "p", "[", "]", "\\"
        };

        private static readonly HashSet<string> BottomRowKeys = new HashSet<string>
        {
            "z", "x", "c", "v", "b", "n", "m", ",", ".", "/"
        };

        // Finger mappings (approximate for touch typing)
        private static readonly Dictionary<string, string> KeyToFinger = new Dictionary<string, string>
        {
            // Left pinky
            {"q", "pinky"}, {"a", "pinky"}, {"z", "pinky"}, {"1", "pinky"}, {"`", "pinky"},
            // Left ring
            {"w", "ring"}, {"s", "ring"}, {"x", "ring"}, {"2", "ring"},
            // Left middle
            {"e", "middle"}, {"d", "middle"}, {"c", "middle"}, {"3", "middle"},
            // Left index
            {"r", "index"}, {"f", "index"}, {"v", "index"}, {"4", "index"},
            {"t", "index"}, {"g", "index"}, {"b", "index"}, {"5", "index"},
            // Right index
            {"y", "index"}, {"h", "index"}, {"n", "index"}, {"6", "index"},
            {"u", "index"}, {"j", "index"}, {"m", "index"}, {"7", "index"},
            // Right middle
            {"i", "middle"}, {"k", "middle"}, {",", "middle"}, {"8", "middle"},
            // Right ring
            {"o", "ring"}, {"l", "ring"}, {".", "ring"}, {"9", "ring"},
            // Right pinky
            {"p", "pinky"}, {";", "pinky"}, {"/", "pinky"}, {"0", "pinky"},
            {"[", "pinky"}, {"'", "pinky"}, {"-", "pinky"}, {"]", "pinky"}, {"=", "pinky"},
            // Thumb
            {"Space", "thumb"}, {" ", "thumb"}
        };

        public ProfilingModelInput ExtractFeatures(List<KeystrokeEvent> rawEvents, string userId = null)
        {
            if (rawEvents == null || rawEvents.Count < 2)
                return new ProfilingModelInput { UserId = userId ?? "Unknown" };

            var sortedEvents = rawEvents.OrderBy(x => x.Timestamp).ToList();

            // Data structures for analysis
            var keyDwellTimes = new Dictionary<string, List<float>>();
            var digraphFlightTimes = new Dictionary<string, List<float>>();
            var trigraphTimes = new Dictionary<string, List<float>>();
            var activeKeys = new Dictionary<string, long>();

            var allDwellTimes = new List<float>();
            var allFlightTimes = new List<float>();
            var allKeypressIntervals = new List<float>();

            // Hand and finger tracking
            var leftHandDwells = new List<float>();
            var rightHandDwells = new List<float>();
            var sameHandTransitions = new List<float>();
            var differentHandTransitions = new List<float>();

            // Position tracking
            var homeRowDwells = new List<float>();
            var topRowDwells = new List<float>();
            var bottomRowDwells = new List<float>();
            var homeRowFlights = new List<float>();

            // Finger tracking
            var fingerDwells = new Dictionary<string, List<float>>
            {
                {"index", new List<float>()},
                {"middle", new List<float>()},
                {"ring", new List<float>()},
                {"pinky", new List<float>()},
                {"thumb", new List<float>()}
            };

            // Error tracking
            var backspaceTimestamps = new List<long>();
            var consecutiveBackspaceCounts = new List<int>();
            int currentConsecutiveBackspaces = 0;

            // Overlap tracking (keys held simultaneously)
            var overlaps = new List<float>();

            // Word boundary tracking
            var spaceAfterWordTimes = new List<float>();
            var wordStartTimes = new List<float>();
            var wordLengths = new List<int>();
            int currentWordLength = 0;

            // Fatigue tracking
            var firstHalfSpeeds = new List<float>();
            var secondHalfSpeeds = new List<float>();
            int halfwayPoint = sortedEvents.Count / 2;

            string previousKey = null;
            string previousPreviousKey = null;
            long previousKeyDownTime = 0;
            bool previousWasSpace = false;

            // Process all events
            for (int i = 0; i < sortedEvents.Count; i++)
            {
                var ev = sortedEvents[i];
                string key = NormalizeKey(ev.Key);

                if (ev.Type == "keydown")
                {
                    // Track backspaces
                    if (key == "backspace")
                    {
                        backspaceTimestamps.Add(ev.Timestamp);
                        currentConsecutiveBackspaces++;
                    }
                    else
                    {
                        if (currentConsecutiveBackspaces > 0)
                        {
                            consecutiveBackspaceCounts.Add(currentConsecutiveBackspaces);
                            currentConsecutiveBackspaces = 0;
                        }
                        currentWordLength++;
                    }

                    // Check for overlaps
                    foreach (var activeKey in activeKeys)
                    {
                        float overlap = ev.Timestamp - activeKey.Value;
                        if (overlap > 0 && overlap < 100) // Simultaneous key press
                        {
                            overlaps.Add(overlap);
                        }
                    }

                    activeKeys[key] = ev.Timestamp;

                    // Flight time calculation
                    if (previousKeyDownTime > 0)
                    {
                        long flight = ev.Timestamp - previousKeyDownTime;
                        if (flight > 0 && flight < MaxValidIntervalMs)
                        {
                            allFlightTimes.Add(flight);
                            allKeypressIntervals.Add(flight);

                            // Track fatigue
                            if (i < halfwayPoint)
                                firstHalfSpeeds.Add(flight);
                            else
                                secondHalfSpeeds.Add(flight);

                            // Digraph
                            if (previousKey != null)
                            {
                                string digraph = $"{previousKey}-{key}";
                                if (!digraphFlightTimes.ContainsKey(digraph))
                                    digraphFlightTimes[digraph] = new List<float>();
                                digraphFlightTimes[digraph].Add(flight);

                                // Hand transition tracking
                                bool prevLeft = LeftHandKeys.Contains(previousKey);
                                bool currLeft = LeftHandKeys.Contains(key);
                                if ((prevLeft && currLeft) || (!prevLeft && !currLeft))
                                    sameHandTransitions.Add(flight);
                                else
                                    differentHandTransitions.Add(flight);

                                // Home row flight tracking
                                if (HomeRowKeys.Contains(previousKey) && HomeRowKeys.Contains(key))
                                    homeRowFlights.Add(flight);
                            }

                            // Trigraph
                            if (previousKey != null && previousPreviousKey != null)
                            {
                                string trigraph = $"{previousPreviousKey}-{previousKey}-{key}";
                                if (!trigraphTimes.ContainsKey(trigraph))
                                    trigraphTimes[trigraph] = new List<float>();
                                trigraphTimes[trigraph].Add(flight);
                            }

                            // Word boundary tracking
                            if (previousWasSpace && key != "Space")
                            {
                                wordStartTimes.Add(flight);
                            }
                            if (key == "Space")
                            {
                                spaceAfterWordTimes.Add(flight);
                                if (currentWordLength > 0)
                                {
                                    wordLengths.Add(currentWordLength);
                                    currentWordLength = 0;
                                }
                            }
                        }
                    }

                    previousPreviousKey = previousKey;
                    previousKey = key;
                    previousKeyDownTime = ev.Timestamp;
                    previousWasSpace = (key == "Space");
                }
                else if (ev.Type == "keyup" && activeKeys.ContainsKey(key))
                {
                    long duration = ev.Timestamp - activeKeys[key];
                    activeKeys.Remove(key);

                    if (duration > 0 && duration < MaxValidIntervalMs)
                    {
                        allDwellTimes.Add(duration);

                        // Per-key dwell times
                        if (!keyDwellTimes.ContainsKey(key))
                            keyDwellTimes[key] = new List<float>();
                        keyDwellTimes[key].Add(duration);

                        // Hand tracking
                        if (LeftHandKeys.Contains(key))
                            leftHandDwells.Add(duration);
                        else if (RightHandKeys.Contains(key))
                            rightHandDwells.Add(duration);

                        // Row tracking
                        if (HomeRowKeys.Contains(key))
                            homeRowDwells.Add(duration);
                        else if (TopRowKeys.Contains(key))
                            topRowDwells.Add(duration);
                        else if (BottomRowKeys.Contains(key))
                            bottomRowDwells.Add(duration);

                        // Finger tracking
                        if (KeyToFinger.ContainsKey(key))
                        {
                            string finger = KeyToFinger[key];
                            fingerDwells[finger].Add(duration);
                        }
                    }
                }
            }

            // Final consecutive backspace count
            if (currentConsecutiveBackspaces > 0)
                consecutiveBackspaceCounts.Add(currentConsecutiveBackspaces);

            // Build the feature input
            var input = new ProfilingModelInput
            {
                UserId = userId ?? "Unknown"
            };

            // === CORE TIMING FEATURES ===
            input.MeanDwellTime = SafeAverage(allDwellTimes);
            input.MeanFlightTime = SafeAverage(allFlightTimes);

            double totalMins = (sortedEvents.Last().Timestamp - sortedEvents.First().Timestamp) / 60000.0;
            input.TypingSpeedKPM = totalMins > 0 ? (float)((sortedEvents.Count / 2) / totalMins) : 0;

            // === STATISTICAL VARIANCE FEATURES ===
            input.DwellTimeVariance = SafeVariance(allDwellTimes);
            input.FlightTimeVariance = SafeVariance(allFlightTimes);
            input.DwellTimeStdDev = (float)Math.Sqrt(input.DwellTimeVariance);
            input.FlightTimeStdDev = (float)Math.Sqrt(input.FlightTimeVariance);

            // === PERCENTILE FEATURES ===
            input.DwellTime25thPercentile = SafePercentile(allDwellTimes, 25);
            input.DwellTime50thPercentile = SafePercentile(allDwellTimes, 50);
            input.DwellTime75thPercentile = SafePercentile(allDwellTimes, 75);
            input.FlightTime25thPercentile = SafePercentile(allFlightTimes, 25);
            input.FlightTime50thPercentile = SafePercentile(allFlightTimes, 50);
            input.FlightTime75thPercentile = SafePercentile(allFlightTimes, 75);

            // === RHYTHM FEATURES ===
            input.MeanKeypressInterval = SafeAverage(allKeypressIntervals);
            input.KeypressIntervalVariance = SafeVariance(allKeypressIntervals);
            float intervalMean = input.MeanKeypressInterval;
            float intervalStdDev = (float)Math.Sqrt(input.KeypressIntervalVariance);
            input.TypingRhythmConsistency = intervalMean > 0 ? intervalStdDev / intervalMean : 0; // Coefficient of Variation

            // === PAUSE FEATURES ===
            var shortPauses = allFlightTimes.Where(f => f < ShortPauseThresholdMs).ToList();
            var mediumPauses = allFlightTimes.Where(f => f >= ShortPauseThresholdMs && f < MediumPauseThresholdMs).ToList();
            var longPauses = allFlightTimes.Where(f => f >= MediumPauseThresholdMs).ToList();
            int totalIntervals = allFlightTimes.Count;

            input.ShortPauseFrequency = totalIntervals > 0 ? (float)shortPauses.Count / totalIntervals : 0;
            input.MediumPauseFrequency = totalIntervals > 0 ? (float)mediumPauses.Count / totalIntervals : 0;
            input.LongPauseFrequency = totalIntervals > 0 ? (float)longPauses.Count / totalIntervals : 0;
            input.MeanPauseDuration = SafeAverage(longPauses.Concat(mediumPauses).ToList());

            // === ERROR FEATURES ===
            int totalKeystrokes = sortedEvents.Count(e => e.Type == "keydown");
            input.BackspaceFrequency = totalKeystrokes > 0 ? (float)backspaceTimestamps.Count / totalKeystrokes : 0;
            input.ConsecutiveBackspaces = SafeAverage(consecutiveBackspaceCounts.Select(x => (float)x).ToList());

            // Error correction speed - time after backspace to next key
            var errorCorrectionSpeeds = new List<float>();
            for (int i = 0; i < backspaceTimestamps.Count; i++)
            {
                var nextKeyDown = sortedEvents.FirstOrDefault(e =>
                    e.Type == "keydown" &&
                    e.Timestamp > backspaceTimestamps[i] &&
                    NormalizeKey(e.Key) != "backspace");
                if (nextKeyDown != null)
                {
                    float correctionTime = nextKeyDown.Timestamp - backspaceTimestamps[i];
                    if (correctionTime < MaxValidIntervalMs)
                        errorCorrectionSpeeds.Add(correctionTime);
                }
            }
            input.ErrorCorrectionSpeed = SafeAverage(errorCorrectionSpeeds);

            // === KEY TRANSITION FEATURES ===
            input.LeftHandDwellMean = SafeAverage(leftHandDwells);
            input.RightHandDwellMean = SafeAverage(rightHandDwells);
            input.SameHandTransitionTime = SafeAverage(sameHandTransitions);
            input.DifferentHandTransitionTime = SafeAverage(differentHandTransitions);
            float totalTransitions = sameHandTransitions.Count + differentHandTransitions.Count;
            input.HandTransitionRatio = totalTransitions > 0 ? (float)differentHandTransitions.Count / totalTransitions : 0.5f;

            // === KEY POSITION FEATURES ===
            input.HomeRowDwellMean = SafeAverage(homeRowDwells);
            input.TopRowDwellMean = SafeAverage(topRowDwells);
            input.BottomRowDwellMean = SafeAverage(bottomRowDwells);
            input.HomeRowFlightMean = SafeAverage(homeRowFlights);

            // === FINGER FEATURES ===
            input.IndexFingerDwellMean = SafeAverage(fingerDwells["index"]);
            input.MiddleFingerDwellMean = SafeAverage(fingerDwells["middle"]);
            input.RingFingerDwellMean = SafeAverage(fingerDwells["ring"]);
            input.PinkyFingerDwellMean = SafeAverage(fingerDwells["pinky"]);
            input.ThumbDwellMean = SafeAverage(fingerDwells["thumb"]);

            // === TRIGRAPH FEATURES ===
            input.Trigraph_THE = GetTrigraphTime(trigraphTimes, "t-h-e", input.MeanFlightTime);
            input.Trigraph_AND = GetTrigraphTime(trigraphTimes, "a-n-d", input.MeanFlightTime);
            input.Trigraph_ING = GetTrigraphTime(trigraphTimes, "i-n-g", input.MeanFlightTime);
            input.Trigraph_ION = GetTrigraphTime(trigraphTimes, "i-o-n", input.MeanFlightTime);
            input.Trigraph_TIO = GetTrigraphTime(trigraphTimes, "t-i-o", input.MeanFlightTime);
            input.Trigraph_ENT = GetTrigraphTime(trigraphTimes, "e-n-t", input.MeanFlightTime);
            input.Trigraph_FOR = GetTrigraphTime(trigraphTimes, "f-o-r", input.MeanFlightTime);
            input.Trigraph_HER = GetTrigraphTime(trigraphTimes, "h-e-r", input.MeanFlightTime);
            input.Trigraph_HAT = GetTrigraphTime(trigraphTimes, "h-a-t", input.MeanFlightTime);
            input.Trigraph_HIS = GetTrigraphTime(trigraphTimes, "h-i-s", input.MeanFlightTime);

            // === INDIVIDUAL KEY DWELL TIMES ===
            input.Dwell_Space = GetKeyDwell(keyDwellTimes, "Space", input.MeanDwellTime);
            input.Dwell_E = GetKeyDwell(keyDwellTimes, "e", input.MeanDwellTime);
            input.Dwell_T = GetKeyDwell(keyDwellTimes, "t", input.MeanDwellTime);
            input.Dwell_A = GetKeyDwell(keyDwellTimes, "a", input.MeanDwellTime);
            input.Dwell_O = GetKeyDwell(keyDwellTimes, "o", input.MeanDwellTime);
            input.Dwell_I = GetKeyDwell(keyDwellTimes, "i", input.MeanDwellTime);
            input.Dwell_N = GetKeyDwell(keyDwellTimes, "n", input.MeanDwellTime);
            input.Dwell_S = GetKeyDwell(keyDwellTimes, "s", input.MeanDwellTime);
            input.Dwell_R = GetKeyDwell(keyDwellTimes, "r", input.MeanDwellTime);
            input.Dwell_H = GetKeyDwell(keyDwellTimes, "h", input.MeanDwellTime);
            input.Dwell_L = GetKeyDwell(keyDwellTimes, "l", input.MeanDwellTime);
            input.Dwell_D = GetKeyDwell(keyDwellTimes, "d", input.MeanDwellTime);
            input.Dwell_C = GetKeyDwell(keyDwellTimes, "c", input.MeanDwellTime);
            input.Dwell_U = GetKeyDwell(keyDwellTimes, "u", input.MeanDwellTime);
            input.Dwell_M = GetKeyDwell(keyDwellTimes, "m", input.MeanDwellTime);

            // === DIGRAPH FLIGHT TIMES ===
            input.Flight_TH = GetDigraphFlight(digraphFlightTimes, "t-h", input.MeanFlightTime);
            input.Flight_HE = GetDigraphFlight(digraphFlightTimes, "h-e", input.MeanFlightTime);
            input.Flight_IN = GetDigraphFlight(digraphFlightTimes, "i-n", input.MeanFlightTime);
            input.Flight_ER = GetDigraphFlight(digraphFlightTimes, "e-r", input.MeanFlightTime);
            input.Flight_AN = GetDigraphFlight(digraphFlightTimes, "a-n", input.MeanFlightTime);
            input.Flight_RE = GetDigraphFlight(digraphFlightTimes, "r-e", input.MeanFlightTime);
            input.Flight_ND = GetDigraphFlight(digraphFlightTimes, "n-d", input.MeanFlightTime);
            input.Flight_AT = GetDigraphFlight(digraphFlightTimes, "a-t", input.MeanFlightTime);
            input.Flight_ON = GetDigraphFlight(digraphFlightTimes, "o-n", input.MeanFlightTime);
            input.Flight_NT = GetDigraphFlight(digraphFlightTimes, "n-t", input.MeanFlightTime);
            input.Flight_HA = GetDigraphFlight(digraphFlightTimes, "h-a", input.MeanFlightTime);
            input.Flight_ES = GetDigraphFlight(digraphFlightTimes, "e-s", input.MeanFlightTime);
            input.Flight_ST = GetDigraphFlight(digraphFlightTimes, "s-t", input.MeanFlightTime);
            input.Flight_EN = GetDigraphFlight(digraphFlightTimes, "e-n", input.MeanFlightTime);
            input.Flight_ED = GetDigraphFlight(digraphFlightTimes, "e-d", input.MeanFlightTime);
            input.Flight_TO = GetDigraphFlight(digraphFlightTimes, "t-o", input.MeanFlightTime);
            input.Flight_IT = GetDigraphFlight(digraphFlightTimes, "i-t", input.MeanFlightTime);
            input.Flight_OU = GetDigraphFlight(digraphFlightTimes, "o-u", input.MeanFlightTime);
            input.Flight_EA = GetDigraphFlight(digraphFlightTimes, "e-a", input.MeanFlightTime);
            input.Flight_HI = GetDigraphFlight(digraphFlightTimes, "h-i", input.MeanFlightTime);
            input.Flight_IS = GetDigraphFlight(digraphFlightTimes, "i-s", input.MeanFlightTime);
            input.Flight_OR = GetDigraphFlight(digraphFlightTimes, "o-r", input.MeanFlightTime);
            input.Flight_TI = GetDigraphFlight(digraphFlightTimes, "t-i", input.MeanFlightTime);
            input.Flight_AS = GetDigraphFlight(digraphFlightTimes, "a-s", input.MeanFlightTime);
            input.Flight_TE = GetDigraphFlight(digraphFlightTimes, "t-e", input.MeanFlightTime);
            input.Flight_ET = GetDigraphFlight(digraphFlightTimes, "e-t", input.MeanFlightTime);
            input.Flight_NG = GetDigraphFlight(digraphFlightTimes, "n-g", input.MeanFlightTime);
            input.Flight_OF = GetDigraphFlight(digraphFlightTimes, "o-f", input.MeanFlightTime);
            input.Flight_AL = GetDigraphFlight(digraphFlightTimes, "a-l", input.MeanFlightTime);
            input.Flight_DE = GetDigraphFlight(digraphFlightTimes, "d-e", input.MeanFlightTime);
            input.Flight_SE = GetDigraphFlight(digraphFlightTimes, "s-e", input.MeanFlightTime);
            input.Flight_LE = GetDigraphFlight(digraphFlightTimes, "l-e", input.MeanFlightTime);
            input.Flight_SA = GetDigraphFlight(digraphFlightTimes, "s-a", input.MeanFlightTime);
            input.Flight_SI = GetDigraphFlight(digraphFlightTimes, "s-i", input.MeanFlightTime);
            input.Flight_AR = GetDigraphFlight(digraphFlightTimes, "a-r", input.MeanFlightTime);
            input.Flight_VE = GetDigraphFlight(digraphFlightTimes, "v-e", input.MeanFlightTime);
            input.Flight_RA = GetDigraphFlight(digraphFlightTimes, "r-a", input.MeanFlightTime);
            input.Flight_LD = GetDigraphFlight(digraphFlightTimes, "l-d", input.MeanFlightTime);
            input.Flight_UR = GetDigraphFlight(digraphFlightTimes, "u-r", input.MeanFlightTime);
            input.Flight_IE = GetDigraphFlight(digraphFlightTimes, "i-e", input.MeanFlightTime);
            input.Flight_NE = GetDigraphFlight(digraphFlightTimes, "n-e", input.MeanFlightTime);
            input.Flight_ME = GetDigraphFlight(digraphFlightTimes, "m-e", input.MeanFlightTime);

            // Additional digraphs
            input.Flight_WA = GetDigraphFlight(digraphFlightTimes, "w-a", input.MeanFlightTime);
            input.Flight_WH = GetDigraphFlight(digraphFlightTimes, "w-h", input.MeanFlightTime);
            input.Flight_LL = GetDigraphFlight(digraphFlightTimes, "l-l", input.MeanFlightTime);
            input.Flight_OO = GetDigraphFlight(digraphFlightTimes, "o-o", input.MeanFlightTime);
            input.Flight_EE = GetDigraphFlight(digraphFlightTimes, "e-e", input.MeanFlightTime);
            input.Flight_SS = GetDigraphFlight(digraphFlightTimes, "s-s", input.MeanFlightTime);
            input.Flight_TT = GetDigraphFlight(digraphFlightTimes, "t-t", input.MeanFlightTime);
            input.Flight_FF = GetDigraphFlight(digraphFlightTimes, "f-f", input.MeanFlightTime);
            input.Flight_RR = GetDigraphFlight(digraphFlightTimes, "r-r", input.MeanFlightTime);
            input.Flight_PP = GetDigraphFlight(digraphFlightTimes, "p-p", input.MeanFlightTime);

            // === DIGRAPH VARIANCE ===
            input.Flight_TH_Variance = GetDigraphVariance(digraphFlightTimes, "t-h");
            input.Flight_HE_Variance = GetDigraphVariance(digraphFlightTimes, "h-e");
            input.Flight_IN_Variance = GetDigraphVariance(digraphFlightTimes, "i-n");
            input.Flight_ER_Variance = GetDigraphVariance(digraphFlightTimes, "e-r");
            input.Flight_AN_Variance = GetDigraphVariance(digraphFlightTimes, "a-n");

            // === OVERLAP FEATURES ===
            input.KeyOverlapFrequency = totalKeystrokes > 0 ? (float)overlaps.Count / totalKeystrokes : 0;
            input.MeanOverlapDuration = SafeAverage(overlaps);

            // === WORD BOUNDARY FEATURES ===
            input.SpaceAfterWordMeanTime = SafeAverage(spaceAfterWordTimes);
            input.WordStartMeanTime = SafeAverage(wordStartTimes);
            input.AverageWordLength = wordLengths.Any() ? (float)wordLengths.Average() : 0;

            // === FATIGUE INDICATORS ===
            float firstHalfAvg = SafeAverage(firstHalfSpeeds);
            float secondHalfAvg = SafeAverage(secondHalfSpeeds);
            input.TypingSpeedDecay = firstHalfAvg > 0 ? (secondHalfAvg - firstHalfAvg) / firstHalfAvg : 0;

            // Error rate increase (compare first half vs second half backspace frequency)
            int halfwayTimestamp = sortedEvents.Count > 1 ?
                (int)((sortedEvents.First().Timestamp + sortedEvents.Last().Timestamp) / 2) : 0;
            int firstHalfBackspaces = backspaceTimestamps.Count(t => t < halfwayTimestamp);
            int secondHalfBackspaces = backspaceTimestamps.Count(t => t >= halfwayTimestamp);
            input.ErrorRateIncrease = firstHalfBackspaces > 0 ?
                (float)(secondHalfBackspaces - firstHalfBackspaces) / firstHalfBackspaces : 0;

            return input;
        }

        private string NormalizeKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            key = key.ToLower();
            if (key == " ") key = "Space";
            return key;
        }

        private float SafeAverage(List<float> values)
        {
            return values.Any() ? (float)values.Average() : 0;
        }

        private float SafeVariance(List<float> values)
        {
            if (values.Count < 2) return 0;
            double mean = values.Average();
            double sumSquaredDiff = values.Sum(v => (v - mean) * (v - mean));
            return (float)(sumSquaredDiff / (values.Count - 1));
        }

        private float SafePercentile(List<float> values, int percentile)
        {
            if (!values.Any()) return 0;
            var sorted = values.OrderBy(v => v).ToList();
            int index = (int)Math.Ceiling((percentile / 100.0) * sorted.Count) - 1;
            index = Math.Max(0, Math.Min(index, sorted.Count - 1));
            return sorted[index];
        }

        private float GetKeyDwell(Dictionary<string, List<float>> dwellTimes, string key, float defaultValue)
        {
            return dwellTimes.ContainsKey(key.ToLower()) ?
                (float)dwellTimes[key.ToLower()].Average() : defaultValue;
        }

        private float GetDigraphFlight(Dictionary<string, List<float>> flightTimes, string digraph, float defaultValue)
        {
            return flightTimes.ContainsKey(digraph) ?
                (float)flightTimes[digraph].Average() : defaultValue;
        }

        private float GetDigraphVariance(Dictionary<string, List<float>> flightTimes, string digraph)
        {
            if (!flightTimes.ContainsKey(digraph) || flightTimes[digraph].Count < 2)
                return 0;
            return SafeVariance(flightTimes[digraph]);
        }

        private float GetTrigraphTime(Dictionary<string, List<float>> trigraphTimes, string trigraph, float defaultValue)
        {
            return trigraphTimes.ContainsKey(trigraph) ?
                (float)trigraphTimes[trigraph].Average() : defaultValue;
        }
    }
}
