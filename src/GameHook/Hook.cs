using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.Testing;

namespace GameHook
{
    /// <summary>
    /// Entry point called by the patched game. The patcher inserts
    /// <c>call GameHook.Hook::OnTick()</c> as the first instruction of
    /// <c>Terraria.Main.DoUpdate</c>.
    /// </summary>
    public static class Hook
    {
        // flush every tick so LiveLog can follow the file live. one ~60 byte WriteFile per tick is negligible next to a 16 ms frame
        private const int FlushEveryTicks = 1;

        // one bit per trigger in a ulong
        private const int MaxTriggers = 64;

        // nearest servants written per tick, a fixed count so every row has the same columns
        private const int MaxServants = 3;

        // No field initializers since they could throw outside our try/catch
        private static StreamWriter _writer;
        private static string[] _triggerNames;
        private static bool _initialized;
        private static bool _disabled;
        private static uint _lastTick;
        private static int _ticksSinceFlush;
        private static NPC[] _nearest;
        private static float[] _nearestDist;

        /// <summary>
        /// Called once per XNA Update, which can be more often than once per
        /// game tick, but logs only when the game's tick counter has advanced, so
        /// each row is the state after the most recent completed tick.
        /// </summary>
        public static void OnTick()
        {
            if (_disabled)
                return;

            try
            {
                uint tick = DetailedFPS.NonRepeatedFrameCount;
                if (_initialized && tick == _lastTick)
                    return;

                if (!_initialized)
                    Initialize();

                _lastTick = tick;
                WriteTick(Stopwatch.GetTimestamp(), tick);

                if (++_ticksSinceFlush >= FlushEveryTicks)
                {
                    _writer.Flush();
                    _ticksSinceFlush = 0;
                }
            }
            catch
            {
                // never throw into the game. logging bug should cost data but not crash player's session
                Disable();
            }
        }

        private static void Initialize()
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameHookLogs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "gamehook-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".jsonl");

            _nearest = new NPC[MaxServants];
            _nearestDist = new float[MaxServants];

            _writer = new StreamWriter(path, false, new UTF8Encoding(false), 64 * 1024);
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Disable();

            _writer.Write("{\"ev\":\"start\",\"freq\":");
            _writer.Write(Stopwatch.Frequency);
            _writer.Write("}\n");

            // Take the names from the running game instead of hardcoding them bit i of "held" in every tick row means _triggerNames[i] is held
            List<string> known = PlayerInput.KnownTriggers;
            _triggerNames = known.GetRange(0, Math.Min(known.Count, MaxTriggers)).ToArray();

            _writer.Write("{\"ev\":\"triggers\",\"names\":[");
            for (int i = 0; i < _triggerNames.Length; i++)
            {
                if (i > 0)
                    _writer.Write(',');
                _writer.Write('"');
                _writer.Write(_triggerNames[i]);
                _writer.Write('"');
            }
            _writer.Write("],\"dropped\":");
            _writer.Write(known.Count - _triggerNames.Length);
            _writer.Write("}\n");

            StartLiveLog();
            _initialized = true;
        }
        
        private static void StartLiveLog()
        {
            try
            {
                string gameDir = AppDomain.CurrentDomain.BaseDirectory;
                string pathFile = Path.Combine(gameDir, "GameHook.livelog");
                if (!File.Exists(pathFile))
                    return;
                string exe = File.ReadAllText(pathFile).Trim();
                if (File.Exists(exe))
                    Process.Start(exe, "\"" + gameDir.TrimEnd('\\') + "\"");
            }
            catch
            {
                // own try so a bad path does not disable all logging
            }
        }

        /// <summary>
        /// Packs Triggers.Current into a bitmask in _triggerNames order.
        /// Current is rebuilt in UpdateInput on every tick, so this is what was held on the
        /// most recent tick. Pressed/released edges are left to the analysis.
        /// </summary>
        private static ulong ReadHeldTriggers()
        {
            Dictionary<string, bool> status = PlayerInput.Triggers.Current.KeyStatus;
            ulong held = 0;
            for (int i = 0; i < _triggerNames.Length; i++)
            {
                // KeyStatus is filled from KnownTriggers in SetupKeys, but TryGetValue just in case so a missing key should not cost the session
                bool down;
                if (status.TryGetValue(_triggerNames[i], out down) && down)
                    held |= 1UL << i;
            }
            return held;
        }

        /// <summary>
        /// One "tick" line: the action read this tick, then the state after it was applied.
        /// Field meanings are in docs/schema.md.
        /// </summary>
        private static void WriteTick(long timestamp, uint tick)
        {
            _writer.Write("{\"ev\":\"tick\"");
            WriteField("ts", timestamp);
            WriteField("tick", tick);
            WriteField("held", ReadHeldTriggers());
            WriteField("menu", Main.gameMenu ? 1 : 0);

            // player fields are left out rather than faked when there is no local player
            Player player = Main.LocalPlayer;
            if (player != null)
            {
                WriteAction(player);
                WritePlayer(player);
                WriteNpcs(player);
            }
            _writer.Write("}\n");
        }

        /// <summary>
        /// Cursor position relative to the local player's center, in world pixels, and the
        /// inventory index of the item in hand
        /// Main.mouseX is rescaled for zoom and UI scale at various points in the frame
        /// This function redoes SetZoom_MouseInWorld from scratch
        /// </summary>
        private static void WriteAction(Player player)
        {
            Vector2 mouse = new Vector2(PlayerInput.MouseX, PlayerInput.MouseY);
            Vector2 screenCenter = new Vector2(PlayerInput.RealScreenWidth, PlayerInput.RealScreenHeight) / 2f;
            float zoom = Main.GameViewMatrix.RenderZoom.X;
            Vector2 world = Main.screenPosition + screenCenter + (mouse - screenCenter) / zoom;

            // relative to the player so camera movement between update and draw mostly cancels out
            Vector2 aim = world - player.Center;
            WriteField("ax", (long)Math.Round(aim.X));
            WriteField("ay", (long)Math.Round(aim.Y));
            WriteField("slot", player.selectedItem);
        }

        /// <summary>
        /// local player state after this tick's update
        /// </summary>
        private static void WritePlayer(Player player)
        {
            Vector2 center = player.Center; // position is written as center, same as aim origin
            WriteField("px", center.X);
            WriteField("py", center.Y);
            WriteField("vx", player.velocity.X);
            WriteField("vy", player.velocity.Y);
            WriteField("hp", player.statLife);
            WriteField("hpMax", player.statLifeMax2);
            WriteField("wing", player.wingTime);
            WriteField("rocket", player.rocketTime);
            WriteField("dead", player.dead ? 1 : 0);
        }

        /// <summary>
        /// The Eye of Cthulhu, if one is active, and the servants of cthulhu nearest to the player
        /// </summary>
        private static void WriteNpcs(Player player)
        {
            Vector2 center = player.Center;
            NPC boss = null;
            int servants = 0;
            int kept = 0;

            // same for loop the game uses for searching NPCs
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                NPC npc = Main.npc[i];
                if (npc == null || !npc.active)
                    continue;

                if (npc.type == NPCID.EyeofCthulhu)
                {
                    if (boss == null)
                        boss = npc;
                }
                else if (npc.type == NPCID.ServantofCthulhu)
                {
                    servants++;
                    // insertion into a short sorted list, nearest first
                    float dist = Vector2.DistanceSquared(npc.Center, center);
                    int at = kept < MaxServants ? kept++ : MaxServants;
                    while (at > 0 && _nearestDist[at - 1] > dist)
                    {
                        if (at < MaxServants)
                        {
                            _nearest[at] = _nearest[at - 1];
                            _nearestDist[at] = _nearestDist[at - 1];
                        }
                        at--;
                    }
                    if (at < MaxServants)
                    {
                        _nearest[at] = npc;
                        _nearestDist[at] = dist;
                    }
                }
            }

            if (boss != null)
            {
                Vector2 bossCenter = boss.Center;
                WriteField("bx", bossCenter.X);
                WriteField("by", bossCenter.Y);
                WriteField("bvx", boss.velocity.X);
                WriteField("bvy", boss.velocity.Y);
                WriteField("bhp", boss.life);
                WriteField("bhpMax", boss.lifeMax);
                WriteField("bphase", boss.ai[0]);
            }

            WriteField("sn", servants);
            for (int k = 0; k < kept; k++)
            {
                NPC npc = _nearest[k];
                Vector2 c = npc.Center;
                WriteServantField(k, "x", c.X);
                WriteServantField(k, "y", c.Y);
                WriteServantField(k, "vx", npc.velocity.X);
                WriteServantField(k, "vy", npc.velocity.Y);
                _nearest[k] = null; // NPC references sjouldnt persist between ticks
            }
        }

        private static void WriteServantField(int index, string suffix, float value)
        {
            WriteField("s" + index + suffix, value);
        }

        private static void WriteField(string name, long value)
        {
            _writer.Write(",\"");
            _writer.Write(name);
            _writer.Write("\":");
            _writer.Write(value);
        }

        private static void WriteField(string name, ulong value)
        {
            _writer.Write(",\"");
            _writer.Write(name);
            _writer.Write("\":");
            _writer.Write(value);
        }

        private static void WriteField(string name, float value)
        {
            _writer.Write(",\"");
            _writer.Write(name);
            _writer.Write("\":");
            if (float.IsNaN(value) || float.IsInfinity(value))
                _writer.Write("null");
            else
                _writer.Write(value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        private static void Disable()
        {
            _disabled = true;
            try
            {
                if (_writer != null)
                    _writer.Dispose();
            }
            catch
            {
            }
            _writer = null;
        }
    }
}
