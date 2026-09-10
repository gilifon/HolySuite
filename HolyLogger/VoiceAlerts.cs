using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace HolyLogger
{
    // SPOKEN ALERTS. The cluster's three sound alerts could only be a beep or a Windows\Media
    // chime -- all of which say the same thing, so the operator had to look at the screen to find
    // out WHICH alert had fired. These say it out loud instead: "New country", and the matching
    // words for the other two.
    //
    // No recording ships with the program. Windows speaks the words into a WAV in the program's
    // data folder the first time the sound is used, and from then on that file is just another
    // WAV -- so it plays through the ordinary WaveOutPlayer path and lands on the output device
    // chosen in Options, exactly like a Windows\Media file does.
    //
    // WHICH SPEECH ENGINE, and why this is done by reflection: Windows has two. System.Speech
    // (the old SAPI5 one) is trivial to call but on a normal Windows 10 it can only reach the
    // "Desktop" voices, which sound mechanical. The newer engine behind Narrator has the same
    // voices in a much better rendering (Microsoft David / Zira / Mark), and every Windows 10 and
    // 11 already has them -- nothing to download. That engine is WinRT, and naming a WinRT type at
    // compile time would tie the build to a Windows SDK .winmd on whoever compiles this.
    // Reflection avoids that completely: the program still builds on a PC without the SDK, and on
    // a PC where the engine is missing at RUN time it falls back to System.Speech instead of
    // throwing.
    internal static class VoiceAlerts
    {
        // The order they appear in the sound lists (a Dictionary makes no promise about order).
        internal static readonly string[] Names =
            { "Voice: New Country", "Voice: Alert Call", "Voice: Unconfirmed" };

        // The words spoken for each name. One place, so the list, the resolver and the generator
        // can never disagree.
        internal static readonly Dictionary<string, string> Words =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Voice: New Country", "New country" },
                { "Voice: Alert Call",  "Alert call" },
                { "Voice: Unconfirmed", "Unconfirmed" },
            };

        // Used when nothing has been chosen yet: the clearest of the three on a plain Windows 10.
        // Only a starting point -- the picker in Cluster Settings overrides it.
        const string PreferredVoice = "Microsoft Zira";

        internal static bool IsVoiceName(string name)
            => !string.IsNullOrWhiteSpace(name) && Words.ContainsKey(name.Trim());

        // A RECORDING SUPPLIED WITH THE PROGRAM BEATS ANYTHING WINDOWS CAN SPEAK. Windows' voices
        // are clear but nobody mistakes them for a person. If a Voice folder next to the program
        // holds "New Country.wav" (and the other two), those are used as they are -- no speech
        // engine involved, and the "Spoken by" picker has nothing left to decide.
        //
        // The folder may hold one file, all three, or none: each phrase is looked up on its own,
        // so a single recorded phrase is fine and the rest are still spoken.
        internal static string ShippedWavFor(string name)
        {
            try
            {
                if (!IsVoiceName(name)) return null;
                string file = name.Trim();
                if (file.StartsWith("Voice:", StringComparison.OrdinalIgnoreCase))
                    file = file.Substring("Voice:".Length).Trim();   // "Voice: New Country" -> "New Country"

                string path = Path.Combine(ShippedFolder(), file + ".wav");
                return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
        }

        // True when every phrase has a recording, i.e. the speech engine is out of the picture and
        // the voice picker would be a control that changes nothing.
        internal static bool AllPhrasesAreRecorded()
            => Names.All(n => ShippedWavFor(n) != null);

        // Where the recordings live: a Voice folder beside HolyLogger.exe.
        static string ShippedFolder()
        {
            string exe = Assembly.GetExecutingAssembly().Location;
            return Path.Combine(Path.GetDirectoryName(exe) ?? string.Empty, "Voice");
        }

        // The voices this PC can actually speak with, for the picker. Empty means the newer engine
        // is not there and the old one will be used, which offers no choice worth showing.
        internal static List<string> InstalledVoiceNames()
        {
            try { return WinRt.VoiceNames(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); return new List<string>(); }
        }

        // The voice the alerts will speak with: the saved choice, or Zira, or whatever this PC has
        // first. Null when the PC has no voices at all.
        internal static string SelectedVoice()
        {
            var installed = InstalledVoiceNames();
            if (installed.Count == 0) return null;

            string saved = (Properties.Settings.Default.ClusterVoiceName ?? string.Empty).Trim();
            string match = installed.FirstOrDefault(
                v => string.Equals(v, saved, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            // Saved voice missing (never chosen, or removed from the PC since) -> the preferred one.
            return installed.FirstOrDefault(
                       v => string.Equals(v, PreferredVoice, StringComparison.OrdinalIgnoreCase))
                   ?? installed[0];
        }

        // The WAV for a voice name, made on first use. Returns null if this PC cannot speak at all,
        // so the caller falls back to the ordinary chime rather than going silent.
        internal static string WavPathFor(string name)
        {
            try
            {
                string words;
                if (!Words.TryGetValue((name ?? string.Empty).Trim(), out words)) return null;

                string recorded = ShippedWavFor(name);
                if (recorded != null) return recorded;   // a real recording, used as it is

                string voice = SelectedVoice();
                Directory.CreateDirectory(VoiceFolder());

                // The chosen voice is part of the file name, so switching voice in the settings
                // makes new files instead of playing yesterday's voice out of the cache.
                string path = Path.Combine(VoiceFolder(),
                    SafeFileName(name.Trim() + " - " + (voice ?? "default")) + ".wav");
                if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

                if (!WinRt.TrySpeakToFile(words, path, voice)) OldEngineSpeak(words, path);
                return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
        }

        // Speaking takes about a second, far too long to do when the spot arrives, so the files are
        // made ahead of time in the background. After this the alert just plays a file that is
        // already there.
        internal static void PrepareInBackground()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                foreach (string name in Names)
                {
                    try { WavPathFor(name); }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }
            });
        }

        // Throws away the cached WAVs so the next alert speaks in the newly chosen voice. Called
        // when the voice picker changes; a file in use is skipped rather than fought over.
        internal static void ClearCache()
        {
            try
            {
                if (!Directory.Exists(VoiceFolder())) return;
                foreach (string f in Directory.GetFiles(VoiceFolder(), "*.wav"))
                {
                    try { File.Delete(f); }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Same company/product folder the database and the log file use, with the voice files in
        // their own subfolder so they can be deleted or replaced without hunting.
        static string VoiceFolder()
        {
            var asm = Assembly.GetExecutingAssembly();
            var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(asm.Location);
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                fvi.CompanyName, fvi.ProductName, "Voice");
        }

        static string SafeFileName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        // LAST RESORT ONLY -- the mechanical "Desktop" voice. Reached when the newer engine is
        // absent (a stripped Windows, or a future one that drops it), because a poor voice still
        // beats an alert nobody can tell apart from the other two.
        static void OldEngineSpeak(string words, string path)
        {
            using (var synth = new System.Speech.Synthesis.SpeechSynthesizer())
            {
                synth.Rate = -1;   // two short words at full speed are easy to miss
                synth.SetOutputToWaveFile(path);
                synth.Speak(words);
                synth.SetOutputToNull();   // releases the file handle before we read it back
            }
        }

        // The newer Windows speech engine, called entirely through reflection (see the note at the
        // top of the file). Every entry point swallows and reports false, so a PC without it just
        // takes the old-engine path.
        static class WinRt
        {
            // "Windows.Media" is where the type lives on Windows 10/11; the bare "Windows"
            // assembly name is the older projection, tried second so nothing is assumed.
            static Type SynthType()
                => Type.GetType("Windows.Media.SpeechSynthesis.SpeechSynthesizer, Windows.Media, ContentType=WindowsRuntime")
                ?? Type.GetType("Windows.Media.SpeechSynthesis.SpeechSynthesizer, Windows, ContentType=WindowsRuntime");

            static Type StreamType()
                => Type.GetType("Windows.Media.SpeechSynthesis.SpeechSynthesisStream, Windows.Media, ContentType=WindowsRuntime")
                ?? Type.GetType("Windows.Media.SpeechSynthesis.SpeechSynthesisStream, Windows, ContentType=WindowsRuntime");

            internal static List<string> VoiceNames()
            {
                var names = new List<string>();
                Type synthType = SynthType();
                if (synthType == null) return names;

                var all = synthType.GetProperty("AllVoices", BindingFlags.Public | BindingFlags.Static)
                                   .GetValue(null, null) as IEnumerable;
                if (all == null) return names;
                foreach (object v in all)
                {
                    string n = v.GetType().GetProperty("DisplayName").GetValue(v, null) as string;
                    if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
                }
                return names;
            }

            // voiceName null -> whatever Windows picks. Returns false if this PC has no such
            // engine, or anything at all goes wrong, so the caller can use the old one.
            internal static bool TrySpeakToFile(string words, string path, string voiceName)
            {
                try
                {
                    Type synthType = SynthType();
                    Type streamType = StreamType();
                    if (synthType == null || streamType == null) return false;

                    object synth = Activator.CreateInstance(synthType);

                    if (!string.IsNullOrWhiteSpace(voiceName))
                    {
                        var all = synthType.GetProperty("AllVoices", BindingFlags.Public | BindingFlags.Static)
                                           .GetValue(null, null) as IEnumerable;
                        object wanted = all == null ? null : all.Cast<object>().FirstOrDefault(v =>
                            string.Equals(v.GetType().GetProperty("DisplayName").GetValue(v, null) as string,
                                          voiceName, StringComparison.OrdinalIgnoreCase));
                        if (wanted != null) synthType.GetProperty("Voice").SetValue(synth, wanted, null);
                    }

                    object op = synthType.GetMethod("SynthesizeTextToStreamAsync", new[] { typeof(string) })
                                         .Invoke(synth, new object[] { words });
                    object stream = Await(op, streamType);
                    if (stream == null) return false;

                    object input = stream.GetType().GetMethod("GetInputStreamAt")
                                         .Invoke(stream, new object[] { (ulong)0 });

                    // Written to a file rather than spoken straight out, because this engine always
                    // uses the Windows default device and these alerts have to be able to go
                    // somewhere else -- the operator often keeps the radio USB codec as default.
                    using (Stream managed = AsManagedStream(input))
                    using (var file = File.Create(path))
                        managed.CopyTo(file);

                    var disposable = synth as IDisposable;
                    if (disposable != null) disposable.Dispose();
                    return true;
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
            }

            // A WinRT async operation cannot be waited on through plain reflection (the object is a
            // COM wrapper with nothing to reflect over), so it is turned into a normal Task first.
            // AsTask lives in System.Runtime.WindowsRuntime, part of .NET Framework 4.8 itself.
            static object Await(object op, Type resultType)
            {
                Type ext = Interop().GetType("System.WindowsRuntimeSystemExtensions");
                MethodInfo asTask = ext.GetMethods().First(m =>
                    m.Name == "AsTask" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType.GetGenericTypeDefinition().Name == "IAsyncOperation`1");
                object task = asTask.MakeGenericMethod(resultType).Invoke(null, new[] { op });
                ((System.Threading.Tasks.Task)task).Wait();
                return task.GetType().GetProperty("Result").GetValue(task, null);
            }

            static Stream AsManagedStream(object inputStream)
            {
                Type ext = Interop().GetType("System.IO.WindowsRuntimeStreamExtensions");
                MethodInfo asRead = ext.GetMethods()
                                       .First(m => m.Name == "AsStreamForRead" && m.GetParameters().Length == 1);
                return (Stream)asRead.Invoke(null, new[] { inputStream });
            }

            static Assembly Interop()
                => Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
        }
    }
}
