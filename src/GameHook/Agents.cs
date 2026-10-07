using System;
using Microsoft.Xna.Framework;
using Terraria;

namespace GameHook
{
    /// <summary>One tick of input: held triggers (bit i is Hook.TriggerNames[i]) and aim relative to the player.</summary>
    internal struct Controls
    {
        public ulong Held;
        public int Ax;
        public int Ay;
    }

    /// <summary>Something that plays an episode: one Controls per world update.</summary>
    internal interface IAgent
    {
        string Name { get; }

        void Begin(int seed);

        Controls Act(Player player, NPC boss);
    }

    internal static class Agents
    {
        // the discretized aim: this many directions around the player, at this distance
        public const int AimDirections = 16;
        public const int AimRadius = 200;

        public static ulong Bit(string trigger)
        {
            int i = Array.IndexOf(Hook.TriggerNames, trigger);
            return i < 0 ? 0 : 1UL << i;
        }

        public static void Aim(ref Controls c, int direction)
        {
            double angle = 2 * Math.PI * direction / AimDirections;
            c.Ax = (int)Math.Round(AimRadius * Math.Cos(angle));
            c.Ay = (int)Math.Round(AimRadius * Math.Sin(angle));
        }
    }

    /// <summary>
    /// Shoots at the boss all the time and runs away from it when it gets close horizontally,
    /// turning back toward the middle near the arena's edges. Flies up when the boss is very close.
    /// </summary>
    internal sealed class KiteAgent : IAgent
    {
        private const float KeepDistance = 320;
        private const float FlyDistance = 200;
        private const float EdgeDistance = 1300; // from the arena's middle, of 1600 to its edge

        public string Name { get { return "kite"; } }

        public void Begin(int seed) { }

        public Controls Act(Player player, NPC boss)
        {
            Vector2 toBoss = boss.Center - player.Center;
            Controls c = new Controls();
            c.Held = Agents.Bit("MouseLeft");
            c.Ax = (int)Math.Round(toBoss.X);
            c.Ay = (int)Math.Round(toBoss.Y);

            float fromMiddle = player.Center.X - Env.ArenaMiddleX;
            if (Math.Abs(fromMiddle) > EdgeDistance)
                c.Held |= Agents.Bit(fromMiddle > 0 ? "Left" : "Right");
            else if (Math.Abs(toBoss.X) < KeepDistance)
                c.Held |= Agents.Bit(toBoss.X > 0 ? "Left" : "Right");

            if (toBoss.Length() < FlyDistance)
                c.Held |= Agents.Bit("Jump");
            return c;
        }
    }
}
