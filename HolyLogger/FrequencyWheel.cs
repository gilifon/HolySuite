using System;

namespace HolyLogger
{
    /// <summary>
    /// One notch of the mouse wheel, in kHz, wherever a frequency is shown: the Radio Control Panel's
    /// box and the LED on the main window both turn the wheel the same way because they both ask this.
    ///
    /// HOW MUCH A NOTCH IS WORTH IS DECIDED BY WHERE THE POINTER IS. Over the kHz digits - left of the
    /// decimal point - a notch is 1 kHz; over the Hz digits, right of it, a notch is 0.1 kHz. The
    /// caller works out which side the pointer is on and passes the step in.
    ///
    /// THE FIRST NOTCH TIDIES THE FREQUENCY UP. From 14250.630 a notch up goes to 14251 and a notch
    /// down to 14250 - the nearest whole kHz in the direction the wheel was turned - and every notch
    /// after that is a plain 1 kHz from there. A wheel that carried the odd 630 Hz along with it would
    /// never land on a round number at all. A radio already on a whole kHz has nothing to tidy, so its
    /// first notch is a plain step.
    ///
    /// AND IT DOES NOT WAIT FOR THE RADIO. While the wheel is being spun each notch is added to where
    /// the last notch was sent, not to the frequency read back, so a fast spin moves as far as it was
    /// spun instead of fighting a tune still on its way. A second and a half of stillness ends the
    /// spin, and the next notch starts again from what the radio actually reports.
    /// </summary>
    public class FrequencyWheel
    {
        private const double WholeKhz = 0.0005;              // closer than this to a whole kHz is one
        private static readonly TimeSpan SpinEnds = TimeSpan.FromSeconds(1.5);

        private double _targetKhz;
        private double _stepKhz;
        private DateTime _atUtc;

        /// <summary>
        /// Where the radio should go for this wheel movement, or null if there is nothing to do.
        /// <paramref name="stepKhz"/> is how much one notch is worth - 1 kHz with the pointer over the
        /// kHz digits, 0.1 kHz over the Hz digits - and the tidying is to a multiple of THAT step, so
        /// the small step lands on 14250.700 exactly as the big one lands on 14251.
        /// </summary>
        public double? Next(double rigKhz, int wheelDelta, double stepKhz)
        {
            if (rigKhz <= 0 || stepKhz <= 0) return null;

            int steps = wheelDelta / 120;
            if (steps == 0) return null;

            bool spinning = _targetKhz > 0 && (DateTime.UtcNow - _atUtc) < SpinEnds;

            // MOVING ACROSS THE DECIMAL POINT STARTS THE TIDYING AGAIN. A spin of the Hz digits leaves
            // the radio on something like 14250.700; carry on over the kHz digits and the first notch
            // there must round to 14251, exactly as it would have from a standing start. Without this
            // the spin was simply continued with a bigger step and the rounding never happened.
            bool sameStep = Math.Abs(_stepKhz - stepKhz) < 0.000001;

            // While a spin is running the radio has not caught up yet, so what it reports is behind
            // the wheel. The frequency the last notch was SENT is the honest starting point.
            double from = spinning ? _targetKhz : rigKhz;

            double target;
            if (spinning && sameStep)
            {
                // Already on a step boundary from the notch before: plain steps from there.
                target = _targetKhz + steps * stepKhz;
            }
            else
            {
                double inSteps = from / stepKhz;
                bool onAStep = Math.Abs(inSteps - Math.Round(inSteps)) < WholeKhz / stepKhz;

                if (onAStep) target = Math.Round(inSteps) * stepKhz + steps * stepKhz;
                else if (steps > 0) target = (Math.Ceiling(inSteps) + (steps - 1)) * stepKhz;
                else target = (Math.Floor(inSteps) + (steps + 1)) * stepKhz;
            }

            // Kept clean of floating-point dust: the frequency is only ever whole Hz anyway.
            target = Math.Round(target, 3);
            if (target <= 0) return null;

            _targetKhz = target;
            _stepKhz = stepKhz;
            _atUtc = DateTime.UtcNow;
            return target;
        }
    }
}
