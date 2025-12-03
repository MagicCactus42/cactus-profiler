using Microsoft.ML.Data;

namespace Profiler.Api.Models
{
    public class ProfilingModelInput
    {
        [ColumnName("Label")]
        public string UserId { get; set; }

        // === CORE TIMING FEATURES ===
        public float MeanDwellTime { get; set; }
        public float MeanFlightTime { get; set; }
        public float TypingSpeedKPM { get; set; }

        // === STATISTICAL VARIANCE FEATURES ===
        // Variance captures consistency - more discriminative than means alone
        public float DwellTimeVariance { get; set; }
        public float FlightTimeVariance { get; set; }
        public float DwellTimeStdDev { get; set; }
        public float FlightTimeStdDev { get; set; }

        // === PERCENTILE FEATURES ===
        // Capture distribution shape
        public float DwellTime25thPercentile { get; set; }
        public float DwellTime50thPercentile { get; set; }
        public float DwellTime75thPercentile { get; set; }
        public float FlightTime25thPercentile { get; set; }
        public float FlightTime50thPercentile { get; set; }
        public float FlightTime75thPercentile { get; set; }

        // === RHYTHM FEATURES ===
        // Inter-key interval ratios - captures typing rhythm
        public float MeanKeypressInterval { get; set; }
        public float KeypressIntervalVariance { get; set; }
        public float TypingRhythmConsistency { get; set; } // CV of intervals

        // === PAUSE FEATURES ===
        // Pauses reveal thought patterns
        public float ShortPauseFrequency { get; set; }   // < 200ms between keys
        public float MediumPauseFrequency { get; set; }  // 200-500ms
        public float LongPauseFrequency { get; set; }    // > 500ms
        public float MeanPauseDuration { get; set; }

        // === ERROR FEATURES ===
        // Backspace usage patterns
        public float BackspaceFrequency { get; set; }
        public float ErrorCorrectionSpeed { get; set; }
        public float ConsecutiveBackspaces { get; set; }

        // === KEY TRANSITION FEATURES ===
        // Same-hand vs different-hand typing
        public float LeftHandDwellMean { get; set; }
        public float RightHandDwellMean { get; set; }
        public float SameHandTransitionTime { get; set; }
        public float DifferentHandTransitionTime { get; set; }
        public float HandTransitionRatio { get; set; }

        // === KEY POSITION FEATURES ===
        // Home row vs other rows
        public float HomeRowDwellMean { get; set; }
        public float TopRowDwellMean { get; set; }
        public float BottomRowDwellMean { get; set; }
        public float HomeRowFlightMean { get; set; }

        // === FINGER FEATURES ===
        public float IndexFingerDwellMean { get; set; }
        public float MiddleFingerDwellMean { get; set; }
        public float RingFingerDwellMean { get; set; }
        public float PinkyFingerDwellMean { get; set; }
        public float ThumbDwellMean { get; set; }

        // === TRIGRAPH FEATURES ===
        // 3-key sequences are more discriminative than digraphs
        public float Trigraph_THE { get; set; }
        public float Trigraph_AND { get; set; }
        public float Trigraph_ING { get; set; }
        public float Trigraph_ION { get; set; }
        public float Trigraph_TIO { get; set; }
        public float Trigraph_ENT { get; set; }
        public float Trigraph_FOR { get; set; }
        public float Trigraph_HER { get; set; }
        public float Trigraph_HAT { get; set; }
        public float Trigraph_HIS { get; set; }

        // === INDIVIDUAL KEY DWELL TIMES ===
        public float Dwell_Space { get; set; }
        public float Dwell_E { get; set; }
        public float Dwell_T { get; set; }
        public float Dwell_A { get; set; }
        public float Dwell_O { get; set; }
        public float Dwell_I { get; set; }
        public float Dwell_N { get; set; }
        public float Dwell_S { get; set; }
        public float Dwell_R { get; set; }
        public float Dwell_H { get; set; }
        public float Dwell_L { get; set; }
        public float Dwell_D { get; set; }
        public float Dwell_C { get; set; }
        public float Dwell_U { get; set; }
        public float Dwell_M { get; set; }

        // === DIGRAPH FLIGHT TIMES (EXTENDED) ===
        public float Flight_TH { get; set; }
        public float Flight_HE { get; set; }
        public float Flight_IN { get; set; }
        public float Flight_ER { get; set; }
        public float Flight_AN { get; set; }
        public float Flight_RE { get; set; }
        public float Flight_ND { get; set; }
        public float Flight_AT { get; set; }
        public float Flight_ON { get; set; }
        public float Flight_NT { get; set; }
        public float Flight_HA { get; set; }
        public float Flight_ES { get; set; }
        public float Flight_ST { get; set; }
        public float Flight_EN { get; set; }
        public float Flight_ED { get; set; }
        public float Flight_TO { get; set; }
        public float Flight_IT { get; set; }
        public float Flight_OU { get; set; }
        public float Flight_EA { get; set; }
        public float Flight_HI { get; set; }
        public float Flight_IS { get; set; }
        public float Flight_OR { get; set; }
        public float Flight_TI { get; set; }
        public float Flight_AS { get; set; }
        public float Flight_TE { get; set; }
        public float Flight_ET { get; set; }
        public float Flight_NG { get; set; }
        public float Flight_OF { get; set; }
        public float Flight_AL { get; set; }
        public float Flight_DE { get; set; }
        public float Flight_SE { get; set; }
        public float Flight_LE { get; set; }
        public float Flight_SA { get; set; }
        public float Flight_SI { get; set; }
        public float Flight_AR { get; set; }
        public float Flight_VE { get; set; }
        public float Flight_RA { get; set; }
        public float Flight_LD { get; set; }
        public float Flight_UR { get; set; }
        public float Flight_IE { get; set; }
        public float Flight_NE { get; set; }
        public float Flight_ME { get; set; }

        // === ADDITIONAL DIGRAPHS ===
        public float Flight_WA { get; set; }
        public float Flight_WH { get; set; }
        public float Flight_LL { get; set; }
        public float Flight_OO { get; set; }
        public float Flight_EE { get; set; }
        public float Flight_SS { get; set; }
        public float Flight_TT { get; set; }
        public float Flight_FF { get; set; }
        public float Flight_RR { get; set; }
        public float Flight_PP { get; set; }

        // === DIGRAPH VARIANCE ===
        // Consistency of specific digraph timings
        public float Flight_TH_Variance { get; set; }
        public float Flight_HE_Variance { get; set; }
        public float Flight_IN_Variance { get; set; }
        public float Flight_ER_Variance { get; set; }
        public float Flight_AN_Variance { get; set; }

        // === PRESSURE/HOLD PATTERNS ===
        // Overlap timing - keys held simultaneously
        public float KeyOverlapFrequency { get; set; }
        public float MeanOverlapDuration { get; set; }

        // === WORD BOUNDARY FEATURES ===
        public float SpaceAfterWordMeanTime { get; set; }
        public float WordStartMeanTime { get; set; }
        public float AverageWordLength { get; set; }

        // === FATIGUE INDICATORS ===
        public float TypingSpeedDecay { get; set; }  // Speed change over session
        public float ErrorRateIncrease { get; set; } // Error rate change over session
    }
}
