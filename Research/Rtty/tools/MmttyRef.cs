//Copyright+LGPL
//-----------------------------------------------------------------------------------------------------------------------------------------------
// Copyright 2000-2013 Makoto Mori, Nobuyuki Oba
//-----------------------------------------------------------------------------------------------------------------------------------------------
// This file is a C# translation of the receive demodulator of MMTTY (Rtty.cpp, Fir.cpp, ComLib.cpp).
//
// MMTTY is free software: you can redistribute it and/or modify it under the terms of the GNU Lesser General Public License
// as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//
// MMTTY is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public License along with MMTTY.  If not, see
// <http://www.gnu.org/licenses/>.
//-----------------------------------------------------------------------------------------------------------------------------------------------
//
// WHY THIS FILE EXISTS: it is the yardstick on the RTTY test bench, and nothing else. It is never
// compiled into HolyLogger. MMTTY itself is a Borland C++ Builder program and there is no C++
// compiler on this machine, so its receive path was translated line for line from the source at
// github.com/n5ac/mmtty (the MM-Open release): the same filters, the same limiter, the same ATC,
// the same squelch, the same majority-logic bit sampling and the same Baudot tables, with the
// same constants. Anything a translation could get wrong is checked on clean generated signals,
// which every one of MMTTY's demodulators must copy perfectly.
//
// Left out, because none of it touches what comes out of a received signal on the bench: the
// scopes, the AFC (the bench tunes every file exactly), the TX side, the RX BPF and LMS notch
// (both off in MMTTY's shipped settings), the AA6YQ front filter (off by default) and the
// over-sampling limiter (off by default).

using System;
using System.Collections.Generic;
using System.Text;

namespace RttyBench
{
    /// <summary>MMTTY's sample-rate bookkeeping (ComLib.cpp InitSampType).</summary>
    sealed class MmRates
    {
        public readonly double SampFreq;
        public readonly double DemSamp;
        public readonly bool DemOver;

        public MmRates(double sampFreq)
        {
            SampFreq = sampFreq;
            if (sampFreq >= 10000.0) { DemSamp = sampFreq * 0.5; DemOver = true; }
            else { DemSamp = sampFreq; DemOver = false; }
        }
    }

    static class MmFir
    {
        public const int ffLPF = 0, ffHPF = 1, ffBPF = 2, ffBEF = 3;

        public static double DoFIR(double[] hp, double[] zp, double d, int tap)
        {
            Array.Copy(zp, 1, zp, 0, tap);
            zp[tap] = d;
            d = 0.0;
            for (int i = 0; i <= tap; i++) d += zp[i] * hp[i];
            return d;
        }

        static double I0(double x)
        {
            double sum = 1.0, xj = 1.0;
            int j = 1;
            while (true)
            {
                xj *= ((0.5 * x) / (double)j);
                sum += (xj * xj);
                j++;
                if (((0.00000001 * sum) - (xj * xj)) > 0) break;
            }
            return sum;
        }

        public static void MakeFilter(double[] HP, int tap, int type, double fs, double fcl, double fch, double att, double gain)
        {
            double fc;
            if (type == ffHPF) fc = 0.5 * fs - fcl;
            else if (type != ffLPF) fc = (fch - fcl) / 2.0;
            else fc = fcl;

            double alpha;
            if (att >= 50.0) alpha = 0.1102 * (att - 8.7);
            else if (att >= 21) alpha = (0.5842 * Math.Pow(att - 21.0, 0.4)) + (0.07886 * (att - 21.0));
            else alpha = 0.0;

            var hp = new double[tap / 2 + 1];
            double sum = Math.PI * 2.0 * fc / fs;
            for (int j = 0; j <= tap / 2; j++)
            {
                if (j == 0) { hp[j] = fc * 2.0 / fs; continue; }
                double v = (1.0 / (Math.PI * (double)j)) * Math.Sin((double)j * sum);
                if (att >= 21)
                {
                    double fm = (double)(2 * j) / (double)tap;
                    v *= I0(alpha * Math.Sqrt(1.0 - (fm * fm))) / I0(alpha);
                }
                hp[j] = v;
            }
            sum = hp[0];
            for (int j = 1; j <= tap / 2; j++) sum += 2.0 * hp[j];
            if (sum > 0.0) for (int j = 0; j <= tap / 2; j++) hp[j] /= sum;

            if (type == ffHPF)
            {
                for (int j = 0; j <= tap / 2; j++) hp[j] *= Math.Cos((double)j * Math.PI);
            }
            else if (type != ffLPF)
            {
                double w0 = Math.PI * (fcl + fch) / fs;
                if (type == ffBPF)
                {
                    for (int j = 0; j <= tap / 2; j++) hp[j] *= 2.0 * Math.Cos((double)j * w0);
                }
                else
                {
                    hp[0] = 1.0 - (2.0 * hp[0]);
                    for (int j = 1; j <= tap / 2; j++) hp[j] *= -2.0 * Math.Cos((double)j * w0);
                }
            }
            int k = 0;
            for (int j = tap / 2; j >= 0; j--) HP[k++] = hp[j] * gain;
            for (int j = 1; j <= tap / 2; j++) HP[k++] = hp[j] * gain;
        }

        public static void MakeHilbert(double[] H, int N, double fs, double fc1, double fc2)
        {
            int L = N / 2;
            double T = 1.0 / fs;
            double W1 = 2 * Math.PI * fc1;
            double W2 = 2 * Math.PI * fc2;
            for (int n = 0; n <= N; n++)
            {
                double x1, x2;
                if (n == L) { x1 = x2 = 0.0; }
                else
                {
                    x1 = ((n - L) * W1 * T); x1 = Math.Cos(x1) / x1;
                    x2 = ((n - L) * W2 * T); x2 = Math.Cos(x2) / x2;
                }
                double w = 0.54 - 0.46 * Math.Cos(2 * Math.PI * n / (N));
                H[n] = -(2 * fc2 * T * x2 - 2 * fc1 * T * x1) * w;
            }
            if (N < 8)
            {
                double w = 0;
                for (int n = 0; n <= N; n++) w += Math.Abs(H[n]);
                if (w != 0) { w = 1.0 / w; for (int n = 0; n <= N; n++) H[n] *= w; }
            }
        }

        public static void MakeIIR(double[] A, double[] B, double fc, double fs, int order, int bc, double rp)
        {
            double u = 0, w0, zt;
            if (bc != 0) u = 1.0 / (double)order * Asinh(1.0 / Math.Sqrt(Math.Pow(10.0, 0.1 * rp) - 1.0));
            double wa = Math.Tan(Math.PI * fc / fs);
            w0 = 1.0;
            int n = (order & 1) + 1;
            int pA = 0, pB = 0;
            for (int j = 1; j <= order / 2; j++, pA += 3, pB += 2)
            {
                if (bc != 0)
                {
                    double d1 = Math.Sinh(u) * Math.Cos(n * Math.PI / (2 * order));
                    double d2 = Math.Cosh(u) * Math.Sin(n * Math.PI / (2 * order));
                    w0 = Math.Sqrt(d1 * d1 + d2 * d2);
                    zt = Math.Sinh(u) * Math.Cos(n * Math.PI / (2 * order)) / w0;
                }
                else
                {
                    w0 = 1.0;
                    zt = Math.Cos(n * Math.PI / (2 * order));
                }
                A[pA + 0] = 1 + wa * w0 * 2 * zt + wa * w0 * wa * w0;
                A[pA + 1] = -2 * (wa * w0 * wa * w0 - 1) / A[pA + 0];
                A[pA + 2] = -(1.0 - wa * w0 * 2 * zt + wa * w0 * wa * w0) / A[pA + 0];
                B[pB + 0] = wa * w0 * wa * w0 / A[pA + 0];
                B[pB + 1] = 2 * B[pB + 0];
                n += 2;
            }
            if (bc != 0 && (order & 1) == 0)
            {
                double x = Math.Pow(1.0 / Math.Pow(10.0, rp / 20.0), 1 / (double)(order / 2));
                pB = 0;
                for (int j = 1; j <= order / 2; j++, pB += 2) { B[pB] *= x; B[pB + 1] *= x; }
            }
            if ((order & 1) != 0)
            {
                if (bc != 0) w0 = Math.Sinh(u);
                int j = (order / 2);
                pA = j * 3; pB = j * 2;
                A[pA + 0] = 1 + wa * w0;
                A[pA + 1] = -(wa * w0 - 1) / A[pA + 0];
                B[pB + 0] = wa * w0 / A[pA + 0];
                B[pB + 1] = B[pB + 0];
            }
        }

        static double Asinh(double x) { return Math.Log(x + Math.Sqrt(x * x + 1.0)); }
    }

    sealed class MmIIR
    {
        const int IIRMAX = 16;
        readonly double[] A = new double[IIRMAX * 3];
        readonly double[] B = new double[IIRMAX * 2];
        readonly double[] Z = new double[IIRMAX * 2];
        int m_order;

        public void MakeIIR(double fc, double fs, int order, int bc, double rp)
        {
            m_order = order;
            MmFir.MakeIIR(A, B, fc, fs, order, bc, rp);
        }

        public double Do(double d)
        {
            int pA = 0, pB = 0, pZ = 0;
            double o;
            for (int i = 0; i < m_order / 2; i++, pA += 3, pB += 2, pZ += 2)
            {
                d += Z[pZ] * A[pA + 1] + Z[pZ + 1] * A[pA + 2];
                o = d * B[pB] + Z[pZ] * B[pB + 1] + Z[pZ + 1] * B[pB];
                Z[pZ + 1] = Z[pZ];
                if (Math.Abs(d) < 1e-37) d = 0.0;
                Z[pZ] = d;
                d = o;
            }
            if ((m_order & 1) != 0)
            {
                d += Z[pZ] * A[pA + 1];
                o = d * B[pB] + Z[pZ] * B[pB];
                if (Math.Abs(d) < 1e-37) d = 0.0;
                Z[pZ] = d;
                d = o;
            }
            return d;
        }
    }

    sealed class MmIIRTank
    {
        double b1, b2, a0, z1, z2;

        public void SetFreq(double f, double smp, double bw)
        {
            double lb1 = 2 * Math.Exp(-Math.PI * bw / smp) * Math.Cos(2 * Math.PI * f / smp);
            double lb2 = -Math.Exp(-2 * Math.PI * bw / smp);
            double la0 = bw != 0
                ? Math.Sin(2 * Math.PI * f / smp) / ((smp / 6.0) / bw)
                : Math.Sin(2 * Math.PI * f / smp);
            b1 = lb1; b2 = lb2; a0 = la0;
        }

        public double Do(double d)
        {
            d *= a0;
            d += (z1 * b1);
            d += (z2 * b2);
            z2 = z1;
            if (Math.Abs(d) < 1e-37) d = 0.0;
            z1 = d;
            return d;
        }
    }

    /// <summary>The half-rate decimator MMTTY runs at 11025 Hz (Fir.cpp CDECM2).</summary>
    sealed class MmDecm2
    {
        readonly double[] H = new double[37];
        readonly double[] Z1 = new double[19];
        readonly double[] Z2 = new double[18];

        public MmDecm2(double sampFreq)
        {
            MmFir.MakeFilter(H, 36, MmFir.ffLPF, sampFreq, 2900, 3000, 70, 1.0);
        }

        public double Do(double d1, double d2)
        {
            Array.Copy(Z1, 1, Z1, 0, 18);
            Array.Copy(Z2, 1, Z2, 0, 17);
            Z1[18] = d1;
            Z2[17] = d2;
            double a = 0;
            for (int i = 0; i < 18; i++)
            {
                a += Z1[i] * H[36 - 2 * i];
                a += Z2[i] * H[35 - 2 * i];
            }
            a += Z1[18] * H[0];
            return a;
        }
    }

    sealed class MmSmooz
    {
        double[] bp = new double[60];
        int Wp, Max = 60, Cnt;

        public void SetCount(int n)
        {
            bp = new double[n];
            Max = n;
            Cnt = Wp = 0;
        }

        public double Avg(double d)
        {
            bp[Wp] = d;
            Wp++;
            if (Wp >= Max) Wp = 0;
            if (Cnt < Max) Cnt++;
            double s = 0.0;
            for (int i = 0; i < Cnt; i++) s += bp[i];
            return Cnt != 0 ? s / (double)Cnt : 0;
        }
    }

    sealed class MmATC
    {
        const int ATCMAX = 17;
        const double ATCC = 8192.0;
        const double ATCW = 1024.0;
        double m_Low, m_High = 16384.0;
        double m_CurLow = double.MaxValue, m_CurHigh = -double.MaxValue;
        readonly double[] m_LowList = new double[ATCMAX];
        readonly double[] m_HighList = new double[ATCMAX];
        int m_Cnt;
        readonly MmIIR m_iir = new MmIIR();
        public int m_Max = 4;

        public MmATC(double demSamp)
        {
            for (int i = 0; i < ATCMAX; i++) { m_LowList[i] = m_Low; m_HighList[i] = m_High; }
            m_iir.MakeIIR(100, demSamp, 3, 0, 0);
        }

        public double Do(double d)
        {
            if (m_CurLow > d) m_CurLow = d;
            if (m_CurHigh < d) m_CurHigh = d;
            if (m_Cnt == 0)
            {
                m_Cnt = 64;
                if (m_CurLow > (ATCC - ATCW)) m_CurLow = (ATCC - ATCW);
                if (m_CurHigh < (ATCC + ATCW)) m_CurHigh = (ATCC + ATCW);
                if (m_Max != 0)
                {
                    Array.Copy(m_LowList, 1, m_LowList, 0, m_Max);
                    Array.Copy(m_HighList, 1, m_HighList, 0, m_Max);
                }
                m_LowList[m_Max] = m_CurLow;
                m_HighList[m_Max] = m_CurHigh;
                m_Low = m_LowList[0];
                m_High = m_HighList[0];
                for (int i = 1; i <= m_Max; i++)
                {
                    if (m_Low > m_LowList[i]) m_Low = m_LowList[i];
                    if (m_High < m_HighList[i]) m_High = m_HighList[i];
                }
                m_CurLow = double.MaxValue;
                m_CurHigh = -double.MaxValue;
            }
            m_Cnt--;
            double th = ((m_High + m_Low) * 0.5);
            th = m_iir.Do(th);
            if (m_High > m_Low) d = (d - th) * 1.1 + th;
            d += (ATCC - th);
            return d;
        }
    }

    sealed class MmVCO
    {
        double m_c1, m_c2, m_z;
        int m_TableSize;
        double[] pSinTbl;
        double m_FreeFreq = 1900.0, m_SampleFreq;

        public MmVCO(double sampFreq)
        {
            m_SampleFreq = sampFreq;
            Build((int)(sampFreq * 2));
            m_c1 = m_TableSize / 16.0;
            m_c2 = (int)((double)m_TableSize * m_FreeFreq / m_SampleFreq);
        }

        void Build(int size)
        {
            m_TableSize = size;
            pSinTbl = new double[size];
            double pi2t = 2 * Math.PI / (double)size;
            for (int i = 0; i < size; i++) pSinTbl[i] = Math.Sin((double)i * pi2t);
        }

        public void SetGain(double gain) { m_c1 = (double)m_TableSize * gain / m_SampleFreq; }

        public void SetSampleFreq(double f)
        {
            m_SampleFreq = f;
            int size = (int)(m_SampleFreq * 2);
            if (m_TableSize != size) Build(size);
            SetFreeFreq(m_FreeFreq);
        }

        public void SetFreeFreq(double f)
        {
            m_FreeFreq = f;
            m_c2 = (double)m_TableSize * m_FreeFreq / m_SampleFreq;
        }

        public double Do(double d)
        {
            m_z += (d * m_c1 + m_c2) + 0.5;
            while (m_z >= m_TableSize) m_z -= m_TableSize;
            while (m_z < 0) m_z += m_TableSize;
            return pSinTbl[(int)m_z];
        }
    }

    sealed class MmPLL
    {
        readonly MmIIR loopLPF = new MmIIR();
        readonly MmIIR outLPF = new MmIIR();
        double m_err, m_out, m_vcoout, m_SampleFreq, m_FreeFreq, m_Shift = 170.0;
        public int m_loopOrder = 2, m_outOrder = 4;
        public double m_loopFC = 250.0, m_outFC = 200.0, m_vcogain = 3.0;
        public readonly MmVCO vco;

        public MmPLL(MmRates r)
        {
            vco = new MmVCO(r.SampFreq);
            m_SampleFreq = r.SampFreq * 0.5;
            m_FreeFreq = (2125 + 2295) / 2;
            SetFreeFreq(2125, 2295);
            SetSampleFreq(m_SampleFreq);
        }

        public void SetVcoGain(double g) { m_vcogain = g; vco.SetGain(-m_Shift * g); }
        public void SetFreeFreq(double f1, double f2)
        {
            m_FreeFreq = (f1 + f2) / 2.0;
            m_Shift = f2 - f1;
            vco.SetFreeFreq(m_FreeFreq);
            vco.SetGain(-m_Shift * m_vcogain);
        }
        public void SetSampleFreq(double f)
        {
            m_SampleFreq = f;
            vco.SetSampleFreq(f);
            vco.SetFreeFreq(m_FreeFreq);
            SetVcoGain(3.0);
            loopLPF.MakeIIR(m_loopFC, m_SampleFreq, m_loopOrder, 0, 0);
            outLPF.MakeIIR(m_outFC, m_SampleFreq, m_outOrder, 0, 0);
        }
        public double Do(double d)
        {
            d /= 32768.0;
            m_out = loopLPF.Do(m_err);
            m_vcoout = vco.Do(m_out);
            m_err = m_vcoout * d;
            return outLPF.Do(m_out) * 32768.0;
        }
    }

    struct MmClx
    {
        public double r, j;
        public double vAbs() { return r * r + j * j; }
        public static MmClx Mul(MmClx a, MmClx b)
        {
            return new MmClx { r = a.r * b.r - a.j * b.j, j = a.r * b.j + a.j * b.r };
        }
    }

    /// <summary>MMTTY's shared 48000-entry sine table (CSinTable) and the oscillator that reads it.</summary>
    sealed class MmVCOX
    {
        static readonly double[] Sin = MakeTable();
        static double[] MakeTable()
        {
            var t = new double[48000];
            double pi2t = 2 * Math.PI / 48000.0;
            for (int i = 0; i < t.Length; i++) t[i] = Math.Sin(i * pi2t);
            return t;
        }

        double m_c2, m_z, m_FreeFreq = 2000.0, m_SampleFreq;
        const int m_TableSize = 48000;
        const double m_TableCOS = 48000 / 4;

        public MmVCOX(double sampFreq) { m_SampleFreq = sampFreq; m_c2 = m_TableSize * m_FreeFreq / m_SampleFreq; }
        public void SetSampleFreq(double f) { m_SampleFreq = f; SetFreeFreq(m_FreeFreq); }
        public void SetFreeFreq(double f)
        {
            m_FreeFreq = f;
            m_c2 = (double)m_TableSize * m_FreeFreq / m_SampleFreq;
            if (f < 1.0) m_z = 0;
        }
        public double Do()
        {
            m_z += m_c2;
            if (m_z >= m_TableSize) m_z -= m_TableSize;
            if (m_z < 0) m_z += m_TableSize;
            return Sin[(int)m_z];
        }
        public double DoCos()
        {
            double z = m_z + m_TableCOS;
            if (z >= m_TableSize) z -= m_TableSize;
            return Sin[(int)z];
        }
    }

    /// <summary>The double-buffered FIR used as MMTTY's Hilbert transformer (CFIR2).</summary>
    sealed class MmFir2
    {
        int m_Tap, m_TapHalf, m_W;
        double[] m_pZ, m_pH;

        public void CreateHilbert(int tap, double fs, double fcl, double fch)
        {
            if (m_Tap != tap || m_pZ == null)
            {
                m_pZ = new double[(tap + 1) * 2];
                m_pH = new double[tap + 1];
                m_W = 0;
            }
            m_Tap = tap;
            m_TapHalf = tap / 2;
            MmFir.MakeHilbert(m_pH, tap, fs, fcl, fch);
        }

        public void Clear() { if (m_pZ != null) Array.Clear(m_pZ, 0, m_pZ.Length); m_W = 0; }

        public MmClx Do(double d)
        {
            int dp1 = m_W + m_Tap + 1;
            m_pZ[dp1] = d;
            m_pZ[m_W] = d;
            d = 0;
            for (int i = 0; i <= m_Tap; i++) d += m_pZ[dp1 - i] * m_pH[i];
            var z = new MmClx { j = d, r = m_pZ[m_W + m_TapHalf + 1] };
            m_W++;
            if (m_W > m_Tap) m_W = 0;
            return z;
        }
    }

    sealed class MmSlideFFT
    {
        const double SLIDE_WINDOW_COEFF = 0.9999;
        int m_Length, m_Tones, m_Cur;
        MmClx[] m_pBase;
        readonly MmClx[] m_tData = new MmClx[16];
        readonly MmClx[] m_tWindow = new MmClx[16];
        double m_kWindow;

        public void Create(int len, int @base, int tones)
        {
            if (m_pBase == null || len != m_Length) m_pBase = new MmClx[len];
            Array.Clear(m_pBase, 0, m_pBase.Length);
            Array.Clear(m_tWindow, 0, 16);
            Array.Clear(m_tData, 0, 16);
            m_Length = len;
            m_Tones = tones;
            double k = 2.0 * Math.PI / (double)len;
            for (int i = 0; i < tones; i++)
            {
                m_tWindow[i].r = Math.Cos((i + @base) * k) * SLIDE_WINDOW_COEFF;
                m_tWindow[i].j = Math.Sin((i + @base) * k) * SLIDE_WINDOW_COEFF;
            }
            m_kWindow = Math.Pow(SLIDE_WINDOW_COEFF, len);
            m_Cur = 0;
        }

        public MmClx[] Do(MmClx zIn)
        {
            if (m_Cur >= m_Length) m_Cur = 0;
            MmClx z = m_pBase[m_Cur];
            m_pBase[m_Cur] = zIn;
            m_Cur++;
            z.r *= m_kWindow; z.j *= m_kWindow;
            for (int i = 0; i < m_Tones; i++)
            {
                m_tData[i].r -= z.r; m_tData[i].j -= z.j;
                m_tData[i].r += zIn.r; m_tData[i].j += zIn.j;
                m_tData[i] = MmClx.Mul(m_tData[i], m_tWindow[i]);
            }
            return m_tData;
        }
    }

    /// <summary>MMTTY's "FFT" demodulator (CPHASE, added by JE3HHT in 2010).</summary>
    sealed class MmPhase
    {
        readonly MmVCOX m_VCO;
        readonly MmFir2 m_Hilbert = new MmFir2();
        readonly MmSlideFFT m_SlideFFT = new MmSlideFFT();
        public int m_TONES = 4;
        double m_SHIFT = 170.0, m_fftSHIFT, m_SymbolLen, m_MixerFreq, m_CarrierFreq = 1750, m_SampleFreq;
        int m_BASEPOINT;
        public double m_dm, m_ds;

        public MmPhase(double sampFreq)
        {
            m_VCO = new MmVCOX(sampFreq);
            m_SampleFreq = 11025.0 * 0.5;
        }

        public void SetSampleFreq(double f) { m_SampleFreq = f; Create(); }

        public void Create()
        {
            m_fftSHIFT = m_SHIFT * m_TONES / (m_TONES - 1);
            m_SymbolLen = m_TONES * m_SampleFreq / m_fftSHIFT;
            m_BASEPOINT = (int)(0.0 * m_TONES / m_fftSHIFT);
            m_MixerFreq = (double)m_BASEPOINT * m_SampleFreq / m_SymbolLen;
            m_VCO.SetSampleFreq(m_SampleFreq);
            m_Hilbert.CreateHilbert(20, m_SampleFreq, 25.0, m_SampleFreq * 0.5 - 25.0);
            m_Hilbert.Clear();
            SetCarrierFreq(m_CarrierFreq);
            m_SlideFFT.Create((int)(m_SymbolLen + 0.5), m_BASEPOINT, m_TONES);
        }

        public void SetShift(double f) { m_SHIFT = f; Create(); }

        public void SetCarrierFreq(double f)
        {
            m_CarrierFreq = f;
            m_VCO.SetFreeFreq(m_CarrierFreq - m_MixerFreq);
        }

        public void DoFSK(double d)
        {
            MmClx sig = m_Hilbert.Do(d);
            var z = new MmClx { r = m_VCO.Do(), j = m_VCO.DoCos() };
            z = MmClx.Mul(z, sig);
            MmClx[] fft = m_SlideFFT.Do(z);
            m_dm = fft[0].vAbs();
            m_ds = fft[m_TONES - 1].vAbs();
        }
    }

    /// <summary>MMTTY's FSK demodulator, CFSKDEM, receive path only.</summary>
    sealed class MmttyDem
    {
        const int TAPMAX = 512;
        const int DEMBUFMAX = 512;

        readonly MmRates R;
        readonly double[] HMark = new double[TAPMAX + 1], ZMark = new double[TAPMAX + 1];
        readonly double[] HSpace = new double[TAPMAX + 1], ZSpace = new double[TAPMAX + 1];
        int Count;
        double i2;
        readonly MmDecm2 DECM2;
        int m_Smooz;
        readonly MmSmooz avgMark = new MmSmooz(), avgSpace = new MmSmooz();
        double m_BaudRate;
        int m_Count, m_ReCount;
        int m_mode;
        public int m_inv;
        public int m_sq;
        public double m_SQLevel;
        int m_Tap;
        double m_FilWidth, m_MarkFreq, m_SpaceFreq, m_AFCMarkFreq, m_AFCSpaceFreq;
        int m_DataCount;
        byte m_Data;
        readonly Queue<byte> m_Buff = new Queue<byte>();
        double m_dMark, m_dSpace;
        int m_mark, m_space;
        double m_SmoozFreq;
        int m_SumParity;
        readonly MmIIRTank m_iirm = new MmIIRTank(), m_iirs = new MmIIRTank();
        int m_sqcount;
        readonly MmSmooz SmoozSQ = new MmSmooz();

        public int m_type;
        public double m_iirfw;
        public int m_BitLen = 5;
        public int m_StopLen = 4;
        public int m_Parity;
        public int m_majority = 1;
        public int m_ignoreFream;
        public int m_Limit = 1;
        public int m_LimitAGC = 1;
        public double m_LimitGain = 10.0;
        double m_limitMax = 1, m_limitMin = -1, m_d, m_limitagc;
        double m_deff;
        public double m_avgdeff;

        readonly MmPLL m_pll;
        public int m_lpf;
        public int m_lpfOrder = 5;
        double m_lpffreq;
        readonly MmIIR LpfMark = new MmIIR(), LpfSpace = new MmIIR();
        public int m_atc;
        public readonly MmATC m_atcMark, m_atcSpace, m_atcPLL;
        readonly MmPhase m_Phase;

        /// <summary>Last soft decision (mark minus space), for the trace tools.</summary>
        public double LastMark { get { return m_dMark; } }
        public double LastSpace { get { return m_dSpace; } }

        public MmttyDem(double sampFreq, int type)
        {
            R = new MmRates(sampFreq);
            DECM2 = new MmDecm2(R.SampFreq);
            m_pll = new MmPLL(R);
            m_Phase = new MmPhase(R.SampFreq);
            m_atcMark = new MmATC(R.DemSamp);
            m_atcSpace = new MmATC(R.DemSamp);
            m_atcPLL = new MmATC(R.DemSamp);

            SetLPFFreq(40.0);
            m_pll.SetSampleFreq(R.DemSamp);
            m_Phase.SetSampleFreq(R.DemSamp);

            SetSmoozFreq(70.0);
            m_SQLevel = 600.0;
            m_Tap = 72;
            m_FilWidth = GetFilWidth(m_Tap);
            SetBaudRate(45.45);
            m_MarkFreq = 2125;
            m_SpaceFreq = 2295;
            SetMarkFreq(2125);
            SetSpaceFreq(2295);
            m_iirfw = 60.0;
            SetIIR(60.0);
            SmoozSQ.SetCount(8);
            m_atcPLL.m_Max = 12;

            m_type = type;
        }

        public void SetIIR(double bw)
        {
            m_iirfw = bw;
            m_iirm.SetFreq(m_AFCMarkFreq, R.DemSamp, m_iirfw);
            m_iirs.SetFreq(m_AFCSpaceFreq, R.DemSamp, m_iirfw);
            m_pll.SetFreeFreq(m_AFCMarkFreq, m_AFCSpaceFreq);
        }

        public void SetBaudRate(double b)
        {
            if (b >= 1.0)
            {
                m_BaudRate = b;
                m_Count = m_ReCount = (int)(R.DemSamp / b + 0.5);
            }
        }

        public void SetSmoozFreq(double f)
        {
            m_SmoozFreq = f;
            m_Smooz = (int)(R.DemSamp / f + 0.5);
            avgMark.SetCount(m_Smooz);
            avgSpace.SetCount(m_Smooz);
        }

        public void SetLPFFreq(double f)
        {
            m_lpffreq = f;
            LpfMark.MakeIIR(f, R.DemSamp, m_lpfOrder, 0, 1.0);
            LpfSpace.MakeIIR(f, R.DemSamp, m_lpfOrder, 0, 1.0);
        }

        public void SetMarkFreq(double d)
        {
            if ((m_AFCSpaceFreq - d) < 4000)
            {
                m_AFCMarkFreq = m_MarkFreq = d;
                MmFir.MakeFilter(HMark, m_Tap, MmFir.ffBPF, R.DemSamp, m_MarkFreq - m_FilWidth, m_MarkFreq + m_FilWidth, 60, 1.0);
                SetIIR(m_iirfw);
                m_Phase.SetCarrierFreq(m_AFCMarkFreq);
                if (m_AFCSpaceFreq > m_AFCMarkFreq) m_Phase.SetShift(m_AFCSpaceFreq - m_AFCMarkFreq);
            }
        }

        public void SetSpaceFreq(double d)
        {
            if ((d - m_AFCMarkFreq) < 4000)
            {
                m_AFCSpaceFreq = m_SpaceFreq = d;
                MmFir.MakeFilter(HSpace, m_Tap, MmFir.ffBPF, R.DemSamp, m_SpaceFreq - m_FilWidth, m_SpaceFreq + m_FilWidth, 60, 1.0);
                SetIIR(m_iirfw);
                m_Phase.SetCarrierFreq(m_AFCMarkFreq);
                if (m_AFCSpaceFreq > m_AFCMarkFreq) m_Phase.SetShift(m_AFCSpaceFreq - m_AFCMarkFreq);
            }
        }

        static double GetFilWidth(int tap)
        {
            if (tap >= 256) return 20;
            if (tap >= 192) return 30;
            return 40;
        }

        public void SetFilterTap(int tap)
        {
            if (tap != m_Tap)
            {
                m_Tap = tap;
                m_FilWidth = GetFilWidth(tap);
                MmFir.MakeFilter(HSpace, m_Tap, MmFir.ffBPF, R.DemSamp, m_SpaceFreq - m_FilWidth, m_SpaceFreq + m_FilWidth, 60, 1.0);
                MmFir.MakeFilter(HMark, m_Tap, MmFir.ffBPF, R.DemSamp, m_MarkFreq - m_FilWidth, m_MarkFreq + m_FilWidth, 60, 1.0);
            }
        }

        void Put(byte d) { if (m_Buff.Count < DEMBUFMAX) m_Buff.Enqueue(d); }

        void DoFSK()
        {
            int b = m_dMark >= m_dSpace ? (m_inv != 0 ? 0 : 1) : (m_inv != 0 ? 1 : 0);
            double deff = Math.Abs(m_dMark - m_dSpace);
            if (m_deff < deff) m_deff = deff;
            if (m_sqcount == 0)
            {
                m_avgdeff = SmoozSQ.Avg(m_deff);
                m_deff = 0;
                m_sqcount = (int)(R.DemSamp / 10);
            }
            m_sqcount--;

            if (m_sq != 0 && m_mode == 0)
            {
                if (m_Limit != 0) { if ((m_SQLevel * 10.0) > m_avgdeff) b = 1; }
                else { if (m_SQLevel > m_avgdeff) b = 1; }
            }

            switch (m_mode)
            {
                case 0:
                    if (b == 0)
                    {
                        m_Count = m_ReCount / 2;
                        if (m_majority != 0) { m_mark = m_space = 0; m_mode = 256; }
                        else m_mode++;
                    }
                    else m_Count = m_ReCount;
                    break;
                case 1:
                    if (b != 0) m_mode = 0;
                    else if (m_Count == 0)
                    {
                        m_Count = m_ReCount;
                        m_Data = 0;
                        m_DataCount = m_BitLen;
                        m_SumParity = 0;
                        m_mode++;
                    }
                    break;
                case 2:
                    if (m_Count == 0)
                    {
                        m_Count = m_ReCount;
                        m_Data = (byte)(m_Data << 1);
                        m_Data |= (byte)b;
                        m_DataCount--;
                        if (m_DataCount == 0) m_mode += m_Parity != 0 ? 1 : 2;
                        if (m_Parity != 0) m_SumParity += b;
                    }
                    break;
                case 3:
                    if (m_Count == 0)
                    {
                        m_Count = m_ReCount;
                        m_mode++;
                        if (ParityFails(b)) m_mode = 0;
                        if (m_mode == 0) { m_Count = m_ReCount; m_mode = 7; }
                    }
                    break;
                case 4:
                    if (m_Count == 0)
                    {
                        if (b != 0 || m_ignoreFream != 0)
                        {
                            Put(m_Data);
                            switch (m_StopLen)
                            {
                                case 2: m_Count = (m_ReCount * 11 / 8); break;
                                case 1: m_Count = (m_ReCount * 7 / 8); break;
                                case 4: m_Count = (m_ReCount * 4 / 5); break;
                                default: m_Count = (m_ReCount * 3 / 8); break;
                            }
                            m_mode++;
                        }
                        if (b == 0) m_mode = 8;
                    }
                    break;
                case 5:
                    if (m_Count == 0) m_mode++;
                    break;
                case 6:
                    if (b != 0) m_Count = m_ReCount;
                    else { m_Count = m_ReCount / 2; m_mode = 0; }
                    break;
                case 7:
                    if (m_Count == 0) m_mode--;
                    break;
                case 8:
                    m_Count = m_ReCount;
                    if (b != 0) m_mode = 0;
                    break;
                case 256:
                    if (b != 0) m_mark++; else m_space++;
                    if (m_Count == 0)
                    {
                        b = (m_mark >= m_space) ? 1 : 0;
                        if (b != 0) m_mode = 0;
                        else { m_Count = m_ReCount / 2; m_mode++; }
                    }
                    break;
                case 257:
                    if (m_Count == 0)
                    {
                        m_Count = m_ReCount;
                        m_mark = m_space = 0;
                        m_Data = 0;
                        m_DataCount = m_BitLen;
                        m_SumParity = 0;
                        m_mode++;
                    }
                    break;
                case 258:
                    if (b != 0) m_mark++; else m_space++;
                    if (m_Count == 0)
                    {
                        b = (m_mark >= m_space) ? 1 : 0;
                        m_mark = m_space = 0;
                        m_Count = m_ReCount;
                        m_Data = (byte)(m_Data << 1);
                        m_Data |= (byte)b;
                        m_DataCount--;
                        if (m_DataCount == 0)
                        {
                            if (m_Parity != 0) m_mode++;
                            else
                            {
                                if (m_StopLen != 2 && m_StopLen != 1 && m_StopLen != 4) m_Count = m_ReCount * 7 / 8;
                                m_mode += 2;
                            }
                        }
                        if (m_Parity != 0) m_SumParity += b;
                    }
                    break;
                case 259:
                    if (b != 0) m_mark++; else m_space++;
                    if (m_Count == 0)
                    {
                        b = (m_mark >= m_space) ? 1 : 0;
                        m_mark = m_space = 0;
                        m_Count = m_ReCount;
                        m_mode++;
                        if (ParityFails(b)) m_mode = 0;
                        if (m_mode == 0) { m_Count = m_ReCount; m_mode = 7; }
                        else if (m_StopLen != 2 && m_StopLen != 1 && m_StopLen != 4) m_Count = m_ReCount * 7 / 8;
                    }
                    break;
                case 260:
                    if (b != 0) m_mark++; else m_space++;
                    if (m_Count == 0)
                    {
                        b = (m_mark >= m_space) ? 1 : 0;
                        m_mark = m_space = 0;
                        if (b != 0 || m_ignoreFream != 0)
                        {
                            Put(m_Data);
                            switch (m_StopLen)
                            {
                                case 2: m_Count = (m_ReCount * 7 / 8); break;
                                case 1: m_Count = (m_ReCount * 3 / 8); break;
                                case 4: m_Count = (m_ReCount * 2 / 5); break;
                                default: m_Count = m_ReCount; m_mode = -1; break;
                            }
                            m_mode++;
                        }
                        if (b == 0) m_mode = 8;
                    }
                    break;
                case 261:
                    if (m_Count == 0) m_mode++;
                    break;
                case 262:
                    if (b != 0) m_Count = m_ReCount;
                    else { m_Count = m_ReCount / 2; m_mode = 0; }
                    break;
            }
            m_Count--;
        }

        bool ParityFails(int b)
        {
            switch (m_Parity)
            {
                case 1: return (((m_SumParity & 1) == 0 ? 1 : 0) ^ b & 1) != 0;
                case 2: return (((m_SumParity & 1) ^ b) & 1) != 0;
                case 3: return b == 0;
                case 4: return b != 0;
                default: return false;
            }
        }

        public void Do(double d)
        {
            double ds = d;
            if (m_Limit != 0)
            {
                if (m_LimitAGC != 0)
                {
                    if (m_limitMax < d) m_limitMax = d;
                    if (m_limitMin > d) m_limitMin = d;
                    if ((d >= 0) && (m_d < 0))
                    {
                        m_limitagc = (m_limitMax - m_limitMin);
                        if (m_limitagc != 0)
                        {
                            m_limitagc = (64.0 * 16384.0) / m_limitagc;
                            if (m_limitagc >= 4096.0) m_limitagc = 4096;
                        }
                        else m_limitagc = 200;
                        m_limitMax = 1;
                        m_limitMin = -1;
                    }
                    m_d = d;
                    d *= m_limitagc;
                }
                else d *= m_LimitGain;
                if (d > 16384.0) d = 16384.0;
                if (d < -16384.0) d = -16384.0;
            }
            if ((Count & 1) != 0 || !R.DemOver)
            {
                if (R.DemOver) d = DECM2.Do(d, i2);

                switch (m_type)
                {
                    case 2:
                        {
                            d = m_pll.Do(d);
                            d = m_atcPLL.Do(d + 8192.0) - 8192.0;
                            if (d >= 0) { m_dMark = d; m_dSpace = 0; }
                            else { m_dMark = 0; m_dSpace = -d; }
                            Smooth();
                            if (m_atc != 0) { m_dMark = m_atcMark.Do(m_dMark); m_dSpace = m_atcSpace.Do(m_dSpace); }
                            break;
                        }
                    case 3:
                        {
                            m_Phase.DoFSK(ds);
                            m_dMark = Math.Abs(m_Phase.m_dm);
                            m_dSpace = Math.Abs(m_Phase.m_ds);
                            Smooth();
                            if (m_atc != 0) { m_dMark = m_atcMark.Do(m_dMark); m_dSpace = m_atcSpace.Do(m_dSpace); }
                            m_dMark /= (64 * 32768.0);
                            m_dSpace /= (64 * 32768.0);
                            break;
                        }
                    default:
                        {
                            if (m_type != 0)
                            {
                                m_dMark = MmFir.DoFIR(HMark, ZMark, d, m_Tap);
                                m_dSpace = MmFir.DoFIR(HSpace, ZSpace, d, m_Tap);
                            }
                            else
                            {
                                m_dMark = m_iirm.Do(d);
                                m_dSpace = m_iirs.Do(d);
                            }
                            m_dMark = Math.Abs(m_dMark);
                            m_dSpace = Math.Abs(m_dSpace);
                            Smooth();
                            if (m_atc != 0) { m_dMark = m_atcMark.Do(m_dMark); m_dSpace = m_atcSpace.Do(m_dSpace); }
                            break;
                        }
                }
                DoFSK();
            }
            else i2 = d;
            Count++;
        }

        void Smooth()
        {
            if (m_lpf != 0) { m_dMark = LpfMark.Do(m_dMark); m_dSpace = LpfSpace.Do(m_dSpace); }
            else { m_dMark = avgMark.Avg(m_dMark); m_dSpace = avgSpace.Avg(m_dSpace); }
        }

        public int GetData() { return m_Buff.Count > 0 ? m_Buff.Dequeue() : -1; }
    }

    /// <summary>MMTTY's Baudot to ASCII (CRTTY::ConvAscii), S-BELL code set.</summary>
    sealed class MmBaudot
    {
        static readonly char[] LTR = {
            '\0', 'T', '\r', 'O', ' ', 'H', 'N', 'M',
            '\n', 'L', 'R', 'G', 'I', 'P', 'C', 'V',
            'E', 'Z', 'D', 'B', 'S', 'Y', 'F', 'X',
            'A', 'W', 'J', '\0', 'U', 'Q', 'K', '\0' };
        static readonly char[] FIG = {
            '\0', '5', '\r', '9', ' ', 'h', ',', '.',
            '\n', ')', '4', '&', '8', '0', ':', ';',
            '3', '"', '$', '?', 's', '6', '!', '/',
            '-', '2', '\'', '\0', '7', '1', '(', '\0' };

        int m_fig;
        public int m_uos = 1;

        public char ConvAscii(int d)
        {
            char c = '\0';
            d &= 0x1f;
            if (d == 0x1b) m_fig = 1;
            else if (d == 0x1f) m_fig = 0;
            else if (m_fig != 0)
            {
                c = FIG[d];
                if (m_uos != 0 && c == ' ') m_fig = 0;
            }
            else c = LTR[d];
            return c;
        }
    }

    /// <summary>One MMTTY receiver: demodulator plus Baudot, fed 16-bit samples, giving text.</summary>
    sealed class MmttyReceiver
    {
        public readonly MmttyDem Dem;
        readonly MmBaudot _baudot = new MmBaudot();
        readonly StringBuilder _text = new StringBuilder();
        public readonly List<KeyValuePair<long, char>> Timed = new List<KeyValuePair<long, char>>();
        long _n;

        public static readonly string[] TypeNames = { "IIR", "FIR", "PLL", "FFT" };

        public MmttyReceiver(double sampFreq, int type, bool atc, double markHz, double shiftHz, double baud, bool squelch)
        {
            Dem = new MmttyDem(sampFreq, type);
            Dem.m_atc = atc ? 1 : 0;
            Dem.m_sq = squelch ? 1 : 0;
            Dem.SetBaudRate(baud);
            Dem.SetMarkFreq(markHz);
            Dem.SetSpaceFreq(markHz + shiftHz);
        }

        public void Feed(short[] samples, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Dem.Do(samples[i]);
                _n++;
                int d;
                while ((d = Dem.GetData()) >= 0)
                {
                    char c = _baudot.ConvAscii(d);
                    if (c != '\0') { _text.Append(c); Timed.Add(new KeyValuePair<long, char>(_n, c)); }
                }
            }
        }

        public string Text { get { return _text.ToString(); } }
    }
}
