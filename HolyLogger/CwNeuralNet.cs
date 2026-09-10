using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HolyLogger
{
    // THE NEURAL NETWORK THAT READS MORSE, run here in plain C# with no library at all.
    //
    // It is somebody else's network - f4exb's MorseAngel, MIT licence - and it is theirs because
    // they did the part that cannot be written by hand: they showed it enormous amounts of Morse
    // until it learned to hear it. What travels with HolyLogger is only what it learned: 44,827
    // numbers in Data\CwNeural.bin, 180 KB.
    //
    // NO ONNX RUNTIME, NO NATIVE DLL, NOTHING EXTRA IN THE INSTALLER. That was the first plan, and
    // it was the wrong one: the network turned out to be tiny - two small layers - and running it is
    // a few hundred multiplications per step, which C# does perfectly well. A whole runtime library
    // and its native binaries would have been carried along to do the work of the one file below.
    //
    // Shape: two stacked LSTM layers, 60 hidden values each, fed one number at a time (the loudness
    // of the note, see CwNeuralFrontEnd), then a linear layer down to seven answers:
    //     0  end of character
    //     1  end of word
    //     2..6  the first, second ... fifth element of the character being sent
    //
    // RUN AS A STREAM, one input at a time, carrying the memory forward. The original runs it over a
    // sliding window of 208 inputs and keeps only the last answer, which is the same arithmetic done
    // 208 times over - fine on a graphics card, hopeless on a logging computer. An LSTM carries its
    // own memory from step to step, which is the whole point of it, so one step per new input gives
    // the same answers for a two-hundredth of the work.
    public class CwNeuralNet
    {
        public const int HiddenSize = 60;
        public const int Outputs = 7;
        public const int Layers = 2;

        // Where the weights are expected, beside the program. Named for what it is rather than for
        // where it came from, because the file is ours to ship.
        public const string WeightsFileName = "CwNeural.bin";

        // ----- the learned numbers -----

        // PyTorch keeps the four gates in one block, in this order: input, forget, candidate,
        // output. Everything below follows that order, because the numbers were learned in it.
        readonly float[][] _weightIh = new float[Layers][];   // [layer][4*hidden x inputSize]
        readonly float[][] _weightHh = new float[Layers][];   // [layer][4*hidden x hidden]
        readonly float[][] _biasIh = new float[Layers][];
        readonly float[][] _biasHh = new float[Layers][];
        readonly int[] _inputSize = new int[Layers];
        float[] _linearWeight;                                // [Outputs x hidden]
        float[] _linearBias;

        // ----- what it remembers between steps -----

        readonly float[][] _h = new float[Layers][];
        readonly float[][] _c = new float[Layers][];
        readonly float[] _gates = new float[4 * HiddenSize];
        readonly float[] _answers = new float[Outputs];

        public bool Loaded { get; private set; }

        public CwNeuralNet()
        {
            for (int l = 0; l < Layers; l++)
            {
                _h[l] = new float[HiddenSize];
                _c[l] = new float[HiddenSize];
            }
        }

        /// <summary>
        /// Reads the weights from the file beside the program. Returns false with a plain reason
        /// rather than throwing: a missing weights file must leave the ordinary decoder working.
        /// </summary>
        public bool Load(string path, out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(path)) { error = "The CW network file is missing."; return false; }

                var tensors = ReadTensors(path);

                for (int l = 0; l < Layers; l++)
                {
                    _weightIh[l] = Need(tensors, "lstm.weight_ih_l" + l);
                    _weightHh[l] = Need(tensors, "lstm.weight_hh_l" + l);
                    _biasIh[l] = Need(tensors, "lstm.bias_ih_l" + l);
                    _biasHh[l] = Need(tensors, "lstm.bias_hh_l" + l);

                    // Rows are the four gates stacked; the width is what the layer is fed.
                    _inputSize[l] = _weightIh[l].Length / (4 * HiddenSize);

                    if (_weightHh[l].Length != 4 * HiddenSize * HiddenSize ||
                        _biasIh[l].Length != 4 * HiddenSize ||
                        _biasHh[l].Length != 4 * HiddenSize)
                    {
                        error = "The CW network file is not the shape this program expects.";
                        return false;
                    }
                }

                _linearWeight = Need(tensors, "linear.weight");
                _linearBias = Need(tensors, "linear.bias");

                if (_linearWeight.Length != Outputs * HiddenSize || _linearBias.Length != Outputs)
                {
                    error = "The CW network file is not the shape this program expects.";
                    return false;
                }

                Forget();
                Loaded = true;
                return true;
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                error = "The CW network file could not be read.";
                Loaded = false;
                return false;
            }
        }

        static float[] Need(Dictionary<string, float[]> tensors, string name)
        {
            float[] v;
            if (!tensors.TryGetValue(name, out v))
                throw new InvalidDataException("missing " + name);
            return v;
        }

        // The file: "MORSENN1", how many tensors, then for each - name length, name, rank, the
        // dimensions, and the numbers as little-endian float32.
        static Dictionary<string, float[]> ReadTensors(string path)
        {
            var result = new Dictionary<string, float[]>(StringComparer.Ordinal);

            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream, Encoding.ASCII))
            {
                var magic = reader.ReadBytes(8);
                if (Encoding.ASCII.GetString(magic) != "MORSENN1")
                    throw new InvalidDataException("not a CW network file");

                int count = reader.ReadInt32();
                if (count < 1 || count > 64) throw new InvalidDataException("bad tensor count");

                for (int i = 0; i < count; i++)
                {
                    int nameLength = reader.ReadInt32();
                    if (nameLength < 1 || nameLength > 256) throw new InvalidDataException("bad name");
                    string name = Encoding.ASCII.GetString(reader.ReadBytes(nameLength));

                    int rank = reader.ReadInt32();
                    if (rank < 1 || rank > 4) throw new InvalidDataException("bad rank");

                    long numbers = 1;
                    for (int d = 0; d < rank; d++) numbers *= reader.ReadInt32();
                    if (numbers < 1 || numbers > 1000000) throw new InvalidDataException("bad size");

                    var values = new float[numbers];
                    for (long n = 0; n < numbers; n++) values[n] = reader.ReadSingle();

                    result[name] = values;
                }
            }

            return result;
        }

        /// <summary>Empties the memory - a different station, or a gap long enough to start again.</summary>
        public void Forget()
        {
            for (int l = 0; l < Layers; l++)
            {
                Array.Clear(_h[l], 0, HiddenSize);
                Array.Clear(_c[l], 0, HiddenSize);
            }
        }

        /// <summary>
        /// One step: hand it the loudness of the note now (0 to 1) and get the seven answers back.
        /// The returned array is REUSED on the next call - copy it if it must be kept.
        /// </summary>
        public float[] Step(float loudness)
        {
            float[] layerInput = null;
            float single = loudness;

            for (int l = 0; l < Layers; l++)
            {
                var wIh = _weightIh[l];
                var wHh = _weightHh[l];
                var bIh = _biasIh[l];
                var bHh = _biasHh[l];
                var h = _h[l];
                var c = _c[l];
                int inputSize = _inputSize[l];

                // gates = W_ih . x + b_ih + W_hh . h + b_hh
                for (int g = 0; g < 4 * HiddenSize; g++)
                {
                    float sum = bIh[g] + bHh[g];

                    int row = g * inputSize;
                    if (inputSize == 1) sum += wIh[row] * single;
                    else for (int k = 0; k < inputSize; k++) sum += wIh[row + k] * layerInput[k];

                    int hrow = g * HiddenSize;
                    for (int k = 0; k < HiddenSize; k++) sum += wHh[hrow + k] * h[k];

                    _gates[g] = sum;
                }

                for (int k = 0; k < HiddenSize; k++)
                {
                    float i = Sigmoid(_gates[k]);
                    float f = Sigmoid(_gates[HiddenSize + k]);
                    float g2 = Tanh(_gates[2 * HiddenSize + k]);
                    float o = Sigmoid(_gates[3 * HiddenSize + k]);

                    float cell = f * c[k] + i * g2;
                    c[k] = cell;
                    h[k] = o * Tanh(cell);
                }

                layerInput = h;
            }

            // The linear layer: seven answers from the sixty values the top layer holds.
            var top = _h[Layers - 1];
            for (int o = 0; o < Outputs; o++)
            {
                float sum = _linearBias[o];
                int row = o * HiddenSize;
                for (int k = 0; k < HiddenSize; k++) sum += _linearWeight[row + k] * top[k];
                _answers[o] = sum;
            }

            return _answers;
        }

        static float Sigmoid(float x)
        {
            if (x >= 0)
            {
                float z = (float)Math.Exp(-x);
                return 1.0f / (1.0f + z);
            }
            else
            {
                // Written this way round for large negative x, where Exp(-x) overflows.
                float z = (float)Math.Exp(x);
                return z / (1.0f + z);
            }
        }

        static float Tanh(float x)
        {
            return (float)Math.Tanh(x);
        }
    }
}
