using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace HolyLogger
{
    // OUR OWN CW NETWORK, RUN INSIDE HOLYLOGGER (2026-10-16, on test - not for release).
    //
    // A small conformer: a convolutional front end over a spectrogram (300-1100 Hz, 15 ms steps), a few
    // blocks of feed-forward + self-attention over the whole 15 s window + depthwise convolution, and a CTC
    // head that writes letters. It was trained in Research/CwDecode/deepnet on generated CW laid over real
    // band noise; the learned numbers are read from Data\CwDeep.bin (written by deepnet/export_cs.py).
    //
    // NO LIBRARY: the forward pass is written out here, layer by layer, exactly as PyTorch computes it, so
    // it runs in the 32-bit program as it is. Checked against PyTorch on a real recording (CwDeepCheck).
    public sealed class CwDeepNet
    {
        public const string WeightsFileName = "CwDeep.bin";
        public int D, Heads, Ff, Blocks, K, C1, C2, C3, NBins, NClass, FBins;
        public bool Loaded { get; private set; }

        /// <summary>The network reads frames centred on the followed note (file "CWD2"), not 300-1100 Hz.</summary>
        public bool Centred { get; private set; }

        // How many processors one window may use. Using all of them starved the waterfall's drawing for
        // the second the window took (measured 2026-10-17 on his i7, 8 processors: all 8 = 3-6% of frames
        // late in bursts every 3 s, worst 97 ms; 4 = worst 46 ms; 2 = none late, worst 22 ms). With 2 a
        // window takes 1.5 s instead of 0.8 s - still well inside the 3 s between windows.
        public static int Threads = Math.Max(1, Math.Min(2, Environment.ProcessorCount / 2));

        static ParallelOptions Opts() { return new ParallelOptions { MaxDegreeOfParallelism = Threads }; }

        float[] f0w, f0b, f1w, f1b, f2w, f2b, projW, projB, headW, headB;
        Block[] _blocks;

        sealed class Block
        {
            public float[] ff1LnW, ff1LnB, ff1W1, ff1B1, ff1W2, ff1B2;
            public float[] attLnW, attLnB, inW, inB, outW, outB;
            public float[] cLnW, cLnB, pw1W, pw1B, dwW, dwB, gnW, gnB, pw2W, pw2B;
            public float[] ff2LnW, ff2LnB, ff2W1, ff2B1, ff2W2, ff2B2;
            public float[] lnW, lnB;
        }

        public bool Load(string path, out string error)
        {
            error = null;
            try
            {
                using (var r = new BinaryReader(File.OpenRead(path)))
                {
                    // "CWD2" (2026-10-19): the same network, trained to read frames centred on the station being
                    // followed - see CwDeepDecoder.SpectrogramCentred.
                    string magic = new string(r.ReadChars(4));
                    if (magic != "CWD1" && magic != "CWD2") { error = "not a CW network file"; return false; }
                    Centred = magic == "CWD2";
                    D = r.ReadInt32(); Heads = r.ReadInt32(); Ff = r.ReadInt32(); Blocks = r.ReadInt32(); K = r.ReadInt32();
                    C1 = r.ReadInt32(); C2 = r.ReadInt32(); C3 = r.ReadInt32(); NBins = r.ReadInt32(); NClass = r.ReadInt32(); FBins = r.ReadInt32();
                    Func<int, float[]> read = n =>
                    {
                        var a = new float[n];
                        var bytes = r.ReadBytes(n * 4);
                        Buffer.BlockCopy(bytes, 0, a, 0, bytes.Length);
                        return a;
                    };
                    f0w = read(C1 * 9); f0b = read(C1);
                    f1w = read(C2 * C1 * 9); f1b = read(C2);
                    f2w = read(C3 * C2 * 9); f2b = read(C3);
                    projW = read(D * C3 * FBins); projB = read(D);
                    _blocks = new Block[Blocks];
                    int h = D * Ff;
                    for (int b = 0; b < Blocks; b++)
                    {
                        var k = new Block();
                        k.ff1LnW = read(D); k.ff1LnB = read(D); k.ff1W1 = read(h * D); k.ff1B1 = read(h); k.ff1W2 = read(D * h); k.ff1B2 = read(D);
                        k.attLnW = read(D); k.attLnB = read(D); k.inW = read(3 * D * D); k.inB = read(3 * D); k.outW = read(D * D); k.outB = read(D);
                        k.cLnW = read(D); k.cLnB = read(D); k.pw1W = read(2 * D * D); k.pw1B = read(2 * D); k.dwW = read(D * K); k.dwB = read(D);
                        k.gnW = read(D); k.gnB = read(D); k.pw2W = read(D * D); k.pw2B = read(D);
                        k.ff2LnW = read(D); k.ff2LnB = read(D); k.ff2W1 = read(h * D); k.ff2B1 = read(h); k.ff2W2 = read(D * h); k.ff2B2 = read(D);
                        k.lnW = read(D); k.lnB = read(D);
                        _blocks[b] = k;
                    }
                    headW = read(NClass * D); headB = read(NClass);
                    if (r.BaseStream.Position != r.BaseStream.Length) { error = "the network file has the wrong length"; return false; }
                }
                Loaded = true;
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // THE WORKING ARRAYS ARE KEPT, NOT THROWN AWAY (2026-10-21). One reading made dozens of arrays, many far
        // over 85 KB (the front end's first layer alone is megabytes), every 3 s - and .NET clears arrays that
        // size away with a collection that stops EVERY thread of the program. That broke his COM-port keying
        // (see CwDeepDecoder.HoldForKeying) and made the waterfall skip frames all the time (his frame log: about
        // 15 skipped frames every 10 s, one stop of 0.74 s). Now a reading rents its arrays from here and hands
        // them all back at the end, and the next reading uses the same ones. Every array rented is written whole
        // before it is read, so none needs clearing.
        readonly Dictionary<int, Stack<float[]>> _spare = new Dictionary<int, Stack<float[]>>();
        readonly List<float[]> _lent = new List<float[]>();
        readonly object _forwardGate = new object();

        float[] Rent(int n)
        {
            Stack<float[]> stack;
            float[] a = _spare.TryGetValue(n, out stack) && stack.Count > 0 ? stack.Pop() : new float[n];
            _lent.Add(a);
            return a;
        }

        void GiveBackAll()
        {
            foreach (float[] a in _lent)
            {
                Stack<float[]> stack;
                if (!_spare.TryGetValue(a.Length, out stack)) _spare[a.Length] = stack = new Stack<float[]>();
                stack.Push(a);
            }
            _lent.Clear();
        }

        /// <summary>Spectrogram [t, NBins] (row-major) -> log-probabilities [t', NClass], t' = steps after the front end.</summary>
        public float[] Forward(float[] spec, int t, out int tOut)
        {
            lock (_forwardGate)
            {
                try { return ForwardInner(spec, t, out tOut); }
                finally { GiveBackAll(); }
            }
        }

        float[] ForwardInner(float[] spec, int t, out int tOut)
        {
            // ---- the front end: three 3x3 convolutions, the last two halving time and frequency ----
            int tA = t, fA = NBins;
            float[] m = Conv2d(spec, 1, tA, fA, f0w, f0b, C1, 1, out tA, out fA);
            Gelu(m);
            m = Conv2d(m, C1, tA, fA, f1w, f1b, C2, 2, out tA, out fA);
            Gelu(m);
            m = Conv2d(m, C2, tA, fA, f2w, f2b, C3, 2, out tA, out fA);
            Gelu(m);
            tOut = tA;
            int T = tA, inWidth = C3 * fA;
            // [C3][T][F] -> [T][C3*F] (channel, then frequency)
            var flat = Rent(T * inWidth);
            for (int c = 0; c < C3; c++)
                for (int i = 0; i < T; i++)
                    Array.Copy(m, (c * T + i) * fA, flat, i * inWidth + c * fA, fA);
            float[] x = Linear(flat, T, inWidth, projW, projB, D);
            for (int i = 0; i < T; i++)
                for (int j = 0; j < D; j += 2)
                {
                    double ang = i / Math.Pow(10000.0, (double)j / D);
                    x[i * D + j] += (float)(0.3 * Math.Sin(ang));
                    if (j + 1 < D) x[i * D + j + 1] += (float)(0.3 * Math.Cos(ang));
                }

            foreach (Block b in _blocks)
            {
                AddScaled(x, FeedForward(x, T, b.ff1LnW, b.ff1LnB, b.ff1W1, b.ff1B1, b.ff1W2, b.ff1B2), 0.5f);
                AddScaled(x, Attention(LayerNorm(x, T, D, b.attLnW, b.attLnB), T, b), 1f);
                AddScaled(x, ConvModule(x, T, b), 1f);
                AddScaled(x, FeedForward(x, T, b.ff2LnW, b.ff2LnB, b.ff2W1, b.ff2B1, b.ff2W2, b.ff2B2), 0.5f);
                x = LayerNorm(x, T, D, b.lnW, b.lnB);
            }

            // The answer is the caller's to keep, so it is a new array (a small one) - not a rented one.
            float[] logits = (float[])Linear(x, T, D, headW, headB, NClass).Clone();
            for (int i = 0; i < T; i++)
            {
                double max = double.MinValue;
                for (int c = 0; c < NClass; c++) max = Math.Max(max, logits[i * NClass + c]);
                double sum = 0;
                for (int c = 0; c < NClass; c++) sum += Math.Exp(logits[i * NClass + c] - max);
                double lse = max + Math.Log(sum);
                for (int c = 0; c < NClass; c++) logits[i * NClass + c] = (float)(logits[i * NClass + c] - lse);
            }
            return logits;
        }

        // ---- the layers ----

        // in [cin][t][f], weight [cout][cin][3][3], padding 1 -> out [cout][t'][f']
        float[] Conv2d(float[] input, int cin, int t, int f, float[] w, float[] bias, int cout, int stride, out int tOut, out int fOut)
        {
            int to = (t + 2 - 3) / stride + 1, fo = (f + 2 - 3) / stride + 1;
            var output = Rent(cout * to * fo);
            Parallel.For(0, cout, Opts(), co =>
            {
                for (int i = 0; i < to; i++)
                    for (int j = 0; j < fo; j++)
                    {
                        double s = bias[co];
                        for (int ci = 0; ci < cin; ci++)
                            for (int di = 0; di < 3; di++)
                            {
                                int ti = i * stride + di - 1;
                                if (ti < 0 || ti >= t) continue;
                                int rowBase = (ci * t + ti) * f;
                                int wBase = ((co * cin + ci) * 3 + di) * 3;
                                for (int dj = 0; dj < 3; dj++)
                                {
                                    int fj = j * stride + dj - 1;
                                    if (fj < 0 || fj >= f) continue;
                                    s += w[wBase + dj] * input[rowBase + fj];
                                }
                            }
                        output[(co * to + i) * fo + j] = (float)s;
                    }
            });
            tOut = to; fOut = fo;
            return output;
        }

        // x [rows][inW], w [outW][inW] (PyTorch's layout) -> [rows][outW]
        float[] Linear(float[] x, int rows, int inW, float[] w, float[] b, int outW)
        {
            var y = Rent(rows * outW);
            Parallel.For(0, rows, Opts(), r =>
            {
                int xb = r * inW;
                for (int o = 0; o < outW; o++)
                {
                    int wb = o * inW;
                    float s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                    int i = 0;
                    for (; i + 3 < inW; i += 4)
                    {
                        s0 += w[wb + i] * x[xb + i];
                        s1 += w[wb + i + 1] * x[xb + i + 1];
                        s2 += w[wb + i + 2] * x[xb + i + 2];
                        s3 += w[wb + i + 3] * x[xb + i + 3];
                    }
                    for (; i < inW; i++) s0 += w[wb + i] * x[xb + i];
                    y[r * outW + o] = s0 + s1 + s2 + s3 + b[o];
                }
            });
            return y;
        }

        float[] LayerNorm(float[] x, int rows, int width, float[] g, float[] b)
        {
            var y = Rent(x.Length);
            for (int r = 0; r < rows; r++)
            {
                double mean = 0, var = 0;
                for (int i = 0; i < width; i++) mean += x[r * width + i];
                mean /= width;
                for (int i = 0; i < width; i++) { double d = x[r * width + i] - mean; var += d * d; }
                var /= width;
                double inv = 1.0 / Math.Sqrt(var + 1e-5);
                for (int i = 0; i < width; i++) y[r * width + i] = (float)((x[r * width + i] - mean) * inv * g[i] + b[i]);
            }
            return y;
        }

        float[] FeedForward(float[] x, int T, float[] lnW, float[] lnB, float[] w1, float[] b1, float[] w2, float[] b2)
        {
            float[] h = Linear(LayerNorm(x, T, D, lnW, lnB), T, D, w1, b1, D * Ff);
            for (int i = 0; i < h.Length; i++) h[i] = Silu(h[i]);
            return Linear(h, T, D * Ff, w2, b2, D);
        }

        float[] Attention(float[] y, int T, Block b)
        {
            float[] qkv = Linear(y, T, D, b.inW, b.inB, 3 * D);
            int dh = D / Heads;
            var outp = Rent(T * D);
            float scale = (float)(1.0 / Math.Sqrt(dh));
            // One score row per worker, not one per row worked (that was thousands of small arrays a reading).
            Parallel.For(0, Heads * T, Opts(), () => new double[T], (job, state, score) =>
            {
                int h = job / T, i = job % T;
                double max = double.MinValue;
                int qb = i * 3 * D + h * dh;
                for (int j = 0; j < T; j++)
                {
                    int kb = j * 3 * D + D + h * dh;
                    double s = 0;
                    for (int e = 0; e < dh; e++) s += qkv[qb + e] * qkv[kb + e];
                    s *= scale;
                    score[j] = s;
                    if (s > max) max = s;
                }
                double sum = 0;
                for (int j = 0; j < T; j++) { score[j] = Math.Exp(score[j] - max); sum += score[j]; }
                for (int e = 0; e < dh; e++)
                {
                    double acc = 0;
                    for (int j = 0; j < T; j++) acc += score[j] * qkv[j * 3 * D + 2 * D + h * dh + e];
                    outp[i * D + h * dh + e] = (float)(acc / sum);
                }
                return score;
            }, score => { });
            return Linear(outp, T, D, b.outW, b.outB, D);
        }

        float[] ConvModule(float[] x, int T, Block b)
        {
            float[] y = Linear(LayerNorm(x, T, D, b.cLnW, b.cLnB), T, D, b.pw1W, b.pw1B, 2 * D);
            var g = Rent(T * D);
            for (int i = 0; i < T; i++)
                for (int c = 0; c < D; c++)
                    g[i * D + c] = y[i * 2 * D + c] * Sigmoid(y[i * 2 * D + D + c]);
            // depthwise convolution over time, k taps, zero padding k/2
            var z = Rent(T * D);
            int pad = K / 2;
            for (int c = 0; c < D; c++)
                for (int i = 0; i < T; i++)
                {
                    double s = b.dwB[c];
                    for (int k = 0; k < K; k++)
                    {
                        int ti = i + k - pad;
                        if (ti < 0 || ti >= T) continue;
                        s += b.dwW[c * K + k] * g[ti * D + c];
                    }
                    z[i * D + c] = (float)s;
                }
            // GroupNorm with one group: mean and variance over every channel and step together
            double mean = 0, var = 0;
            for (int i = 0; i < z.Length; i++) mean += z[i];
            mean /= z.Length;
            for (int i = 0; i < z.Length; i++) { double d = z[i] - mean; var += d * d; }
            var /= z.Length;
            double inv = 1.0 / Math.Sqrt(var + 1e-5);
            for (int i = 0; i < T; i++)
                for (int c = 0; c < D; c++)
                    z[i * D + c] = Silu((float)((z[i * D + c] - mean) * inv * b.gnW[c] + b.gnB[c]));
            return Linear(z, T, D, b.pw2W, b.pw2B, D);
        }

        static void AddScaled(float[] x, float[] y, float k)
        {
            for (int i = 0; i < x.Length; i++) x[i] += k * y[i];
        }

        static float Sigmoid(float v) { return (float)(1.0 / (1.0 + Math.Exp(-v))); }
        static float Silu(float v) { return v * Sigmoid(v); }

        static void Gelu(float[] a)
        {
            for (int i = 0; i < a.Length; i++) a[i] = (float)(0.5 * a[i] * (1 + Erf(a[i] / Math.Sqrt(2))));
        }

        // erf to about 1e-7 (Numerical Recipes' erfc by Chebyshev), enough for float.
        static double Erf(double x)
        {
            double z = Math.Abs(x), t = 1.0 / (1.0 + 0.5 * z);
            double r = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 + t * (-0.18628806 +
                       t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
            return x >= 0 ? 1 - r : r - 1;
        }
    }
}
