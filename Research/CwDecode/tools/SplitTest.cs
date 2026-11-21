using System;
using System.Windows.Controls;
using System.Windows.Documents;

namespace HolyLogger
{
    // Hybrid's stand-alone-word rule on typed-in lines: each word of each line goes in as Plain's letters
    // (a space closes it), and the box is read back.  SplitTest.exe "line one" "line two" ...
    static class SplitTest
    {
        [STAThread]
        static int Main(string[] a)
        {
            foreach (string line in a)
            {
                var mix = new CwMixText();
                double t = 1;
                foreach (char c in line + " ") { mix.AddPlain(c.ToString(), c == ' ' ? double.NaN : t, 700, 20); t += 0.2; }
                var box = (RichTextBox)mix.Box;
                Console.WriteLine(line + "  ->  " + new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text.Trim());
            }
            return 0;
        }
    }
}
