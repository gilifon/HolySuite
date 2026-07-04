using System;
using System.Windows.Threading;

namespace HolyLogger
{
    // Debounced Settings.Default.Save(). Window move/resize handlers fire per pixel, and each
    // Save() rewrites user.config on disk -- dragging the cluster window across the screen used
    // to produce hundreds of file writes. Values are still assigned to Properties.Settings
    // immediately (in memory); only the disk flush is coalesced to one write shortly after the
    // burst ends. UI-thread only (DispatcherTimer), which is where all these handlers run.
    internal static class SettingsFlush
    {
        private static DispatcherTimer _timer;

        // Schedule a flush soon; repeated calls within the window just push it out.
        internal static void RequestSave()
        {
            if (_timer == null)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
                _timer.Tick += (s, e) =>
                {
                    _timer.Stop();
                    try { Properties.Settings.Default.Save(); }
                    catch (Exception ex) { Log.Swallow(ex); }
                };
            }
            _timer.Stop();
            _timer.Start();
        }

        // Immediate flush of any pending debounced write; called from shutdown cleanup so a save
        // scheduled moments before exit is never lost.
        internal static void FlushNow()
        {
            if (_timer != null) _timer.Stop();
            try { Properties.Settings.Default.Save(); }
            catch (Exception ex) { Log.Swallow(ex); }
        }
    }
}
