using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace GameHook
{
    /// <summary>
    /// The policy trained by analysis/train.py: a small MLP over the last few states.
    /// Features() here and frame_features() in train.py must compute the same numbers.
    /// </summary>
    internal sealed class MlpAgent : IAgent
    {
        public const string FileName = "GameHookPolicy.txt";

        // scales that bring features to roughly -1..1. Same as train.py
        private const float PositionScale = 1000;
        private const float VelocityScale = 10;
        private const float WingScale = 100;
        private const int Servants = 3;
        public const int FrameSize = 13 + Servants * 5;

        private int _history;      // states per input
        private int _spacing;      // ticks between them
        private string[] _buttons; // trigger names of the button outputs
        private int _actions;      // past steps of buttons per input
        private int _aims;         // aim directions after the buttons
        private List<float[,]> _weights; // [out, in] per layer
        private List<float[]> _biases;

        private readonly List<float[]> _frames = new List<float[]>(); // features at each step of this episode
        private readonly List<ulong> _held = new List<ulong>();       // buttons it held at each step
        private Random _rng;                                           // samples the buttons
        private readonly NPC[] _nearest = new NPC[Servants];
        private readonly float[] _nearestDist = new float[Servants];

        public string Name { get { return "mlp"; } }

        /// <summary>
        /// Reads the weights file written by train.py: whitespace separated tokens, see export() there.
        /// Runs the test input in the file and logs how far the output is from what PyTorch computed.
        /// </summary>
        public static MlpAgent Load(string path)
        {
            string[] tokens = File.ReadAllText(path).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            int pos = 0;
            Func<string> next = () => tokens[pos++];
            Func<int> nextInt = () => int.Parse(next(), CultureInfo.InvariantCulture);
            Func<float> nextFloat = () => float.Parse(next(), CultureInfo.InvariantCulture);
            Action<string> expect = word =>
            {
                if (next() != word)
                    throw new FormatException(path + ": expected " + word);
            };

            MlpAgent agent = new MlpAgent();
            expect("history");
            agent._history = nextInt();
            agent._spacing = nextInt();
            expect("buttons");
            agent._buttons = new string[nextInt()];
            for (int i = 0; i < agent._buttons.Length; i++)
                agent._buttons[i] = next();
            expect("actions");
            agent._actions = nextInt();
            expect("aims");
            agent._aims = nextInt();

            expect("layers");
            int layers = nextInt();
            agent._weights = new List<float[,]>();
            agent._biases = new List<float[]>();
            for (int l = 0; l < layers; l++)
            {
                int rows = nextInt();
                int cols = nextInt();
                float[,] w = new float[rows, cols];
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                        w[r, c] = nextFloat();
                float[] b = new float[rows];
                for (int r = 0; r < rows; r++)
                    b[r] = nextFloat();
                agent._weights.Add(w);
                agent._biases.Add(b);
            }

            expect("test");
            float[] input = new float[nextInt()];
            for (int i = 0; i < input.Length; i++)
                input[i] = nextFloat();
            float[] output = agent.Forward(input);
            float maxDiff = 0;
            for (int i = 0; i < output.Length; i++)
                maxDiff = Math.Max(maxDiff, Math.Abs(output[i] - nextFloat()));
            Hook.WriteEvent("{\"ev\":\"policy\",\"testMaxDiff\":" + maxDiff.ToString("R", CultureInfo.InvariantCulture) + "}");
            return agent;
        }

        public void Begin(int seed)
        {
            _frames.Clear();
            _held.Clear();
            _rng = new Random(seed);
        }

        public Controls Act(Player player, NPC boss)
        {
            // Act at step i sees the state after step i-1, which is what log row i-1 holds
            int i = _frames.Count;
            _frames.Add(Features(player, boss));

            float[] input = new float[_history * FrameSize + _actions * _buttons.Length];
            for (int h = 0; h < _history; h++)
            {
                // train.py clamps to row 0, the state after step 0, which is frame 1 here
                int k = Math.Max(i - h * _spacing, Math.Min(i, 1));
                Array.Copy(_frames[k], 0, input, h * FrameSize, FrameSize);
            }
            // then the buttons of steps i-1 to i-_actions, nothing pressed before the episode
            int at = _history * FrameSize;
            for (int k = 1; k <= _actions; k++)
            {
                ulong held = i - k >= 0 ? _held[i - k] : 0;
                for (int b = 0; b < _buttons.Length; b++)
                    input[at++] = (held & Agents.Bit(_buttons[b])) != 0 ? 1 : 0;
            }
            float[] output = Forward(input);

            // sampled rather than pressed when above 0.5: starting or releasing a press has a small
            // chance on any one tick, which a threshold would never act on, so it would never start
            // moving from rest, and never let go once moving
            Controls c = new Controls();
            for (int b = 0; b < _buttons.Length; b++)
            {
                double p = 1 / (1 + Math.Exp(-output[b]));
                if (_rng.NextDouble() < p)
                    c.Held |= Agents.Bit(_buttons[b]);
            }
            _held.Add(c.Held);
            int best = 0;
            for (int a = 1; a < _aims; a++)
            {
                if (output[_buttons.Length + a] > output[_buttons.Length + best])
                    best = a;
            }
            Agents.Aim(ref c, best);
            return c;
        }

        /// <summary>Linear layers with ReLU between them, none after the last.</summary>
        private float[] Forward(float[] x)
        {
            for (int l = 0; l < _weights.Count; l++)
            {
                float[,] w = _weights[l];
                float[] y = new float[w.GetLength(0)];
                for (int r = 0; r < y.Length; r++)
                {
                    float sum = _biases[l][r];
                    for (int c = 0; c < x.Length; c++)
                        sum += w[r, c] * x[c];
                    y[r] = l < _weights.Count - 1 ? Math.Max(sum, 0) : sum;
                }
                x = y;
            }
            return x;
        }

        /// <summary>One state: the player relative to the arena, the boss and servants relative to the player.</summary>
        private float[] Features(Player player, NPC boss)
        {
            float[] f = new float[FrameSize];
            Vector2 p = player.Center;
            f[0] = (p.X - Env.ArenaMiddleX) / PositionScale;
            f[1] = (p.Y - Env.ArenaFloorY) / PositionScale;
            f[2] = player.velocity.X / VelocityScale;
            f[3] = player.velocity.Y / VelocityScale;
            f[4] = (float)player.statLife / player.statLifeMax2;
            f[5] = player.wingTime / WingScale;

            if (boss.active && boss.type == NPCID.EyeofCthulhu)
            {
                Vector2 b = boss.Center;
                f[6] = 1;
                f[7] = (b.X - p.X) / PositionScale;
                f[8] = (b.Y - p.Y) / PositionScale;
                f[9] = boss.velocity.X / VelocityScale;
                f[10] = boss.velocity.Y / VelocityScale;
                f[11] = (float)boss.life / boss.lifeMax;
                f[12] = boss.ai[0] / 3;
            }

            int kept;
            Hook.FindServants(p, _nearest, _nearestDist, out kept);
            for (int k = 0; k < kept; k++)
            {
                NPC s = _nearest[k];
                int at = 13 + k * 5;
                f[at] = 1;
                f[at + 1] = (s.Center.X - p.X) / PositionScale;
                f[at + 2] = (s.Center.Y - p.Y) / PositionScale;
                f[at + 3] = s.velocity.X / VelocityScale;
                f[at + 4] = s.velocity.Y / VelocityScale;
                _nearest[k] = null;
            }
            return f;
        }
    }
}
