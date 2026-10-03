using System;
using System.Globalization;

namespace HolyLogger
{
    // The new decoder's switches added 2026-10-02 (start-of-over / strong-station work), read from the
    // environment by every bench, so one build scores a change both ways. "0" turns one off.
    static class BenchSwitches
    {
        public static void Apply()
        {
            string v;
            if ((v = Environment.GetEnvironmentVariable("SPEED_FROM_EDGES")) != null && v != "") CwElementDecoder.SpeedFromEdges = v == "1";
            if ((v = Environment.GetEnvironmentVariable("STRONG_LEAK")) != null && v != "") CwElementDecoder.StrongLeak = double.Parse(v, CultureInfo.InvariantCulture);
            if ((v = Environment.GetEnvironmentVariable("WIDE_TIMING")) != null && v != "") CwElementDecoder.WideTiming = v == "1";
            if ((v = Environment.GetEnvironmentVariable("SPEED_BY_PERIOD")) != null && v != "") CwElementDecoder.SpeedByPeriod = v == "1";
            if ((v = Environment.GetEnvironmentVariable("FRESH_LEVELS")) != null && v != "") CwElementDecoder.FreshLevelsOnNewNote = v == "1";
            if ((v = Environment.GetEnvironmentVariable("STEADY_OFFSET")) != null && v != "") CwElementDecoder.SteadyAllowsOffset = v == "1";
            if ((v = Environment.GetEnvironmentVariable("LEAK_AS_CHOICE")) != null && v != "") CwElementDecoder.StrongLeakIsAChoice = v == "1";
            if ((v = Environment.GetEnvironmentVariable("EDGES_WHEN_SLOWER")) != null && v != "") CwElementDecoder.EdgesOnlyWhenSlower = v == "1";
            if ((v = Environment.GetEnvironmentVariable("EDGES_RATIO")) != null && v != "") CwElementDecoder.EdgesSlowerRatio = double.Parse(v, CultureInfo.InvariantCulture);
            if ((v = Environment.GetEnvironmentVariable("EDGES_SINCE_PAUSE")) != null && v != "") CwElementDecoder.EdgesSinceLastPause = v == "1";
        }
    }
}
