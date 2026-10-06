using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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
        // flush once a second at 60 ticks/s
        private const int FlushEveryTicks = 60;

        // No field initializers since they could throw outside our try/catch
        private static StreamWriter _writer;
        private static bool _initialized;
        private static bool _disabled;
        private static uint _lastTick;
        private static int _ticksSinceFlush;

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

            _writer = new StreamWriter(path, false, new UTF8Encoding(false), 64 * 1024);
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Disable();

            _writer.Write("{\"ev\":\"start\",\"freq\":");
            _writer.Write(Stopwatch.Frequency);
            _writer.Write("}\n");

            _initialized = true;
        }

        private static void WriteTick(long timestamp, uint tick)
        {
            _writer.Write("{\"ev\":\"tick\",\"ts\":");
            _writer.Write(timestamp);
            _writer.Write(",\"tick\":");
            _writer.Write(tick);
            _writer.Write("}\n");
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
