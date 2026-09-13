using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;

namespace AutoExile.Systems
{
    // These are synchronous Win32 calls, not ExileCore click helpers. SendInput
    // acceptance is not a game acknowledgement; mouse hooks can still delay it.
    internal static class NativeMouseInput
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MouseData
        {
            public int X, Y;
            public uint Data, Flags, Time;
            public UIntPtr ExtraInfo;
        }
        [StructLayout(LayoutKind.Explicit)]
        private struct InputData { [FieldOffset(0)] public MouseData Mouse; }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeInput { public uint Type; public InputData Data; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Point { public int X, Y; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, ref NativeInput input, int size);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        private static long _retryMoveAfter;

        public static Vector2 Position
        {
            get
            {
                using var trace = InputLatencyDiagnostics.Begin("GetCursorPos");
                var ok = GetCursorPos(out var point);
                var error = Marshal.GetLastWin32Error();
                if (!ok)
                {
                    trace.Failed = true;
                    trace.Result = $"failed win32={error}";
                    throw new Win32Exception(error, "GetCursorPos failed");
                }
                return new(point.X, point.Y);
            }
        }
        public static void Move(Vector2 position)
        {
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
                throw new ArgumentException("Invalid cursor position");
            using var trace = InputLatencyDiagnostics.Begin("SetCursorPos", detail: $"target={position}");
            var ok = SetCursorPos((int)MathF.Round(position.X), (int)MathF.Round(position.Y));
            var error = Marshal.GetLastWin32Error();
            trace.Result = $"ok={ok} win32={(ok ? 0 : error)}";
            trace.Failed = !ok;
            if (!ok) throw new Win32Exception(error, "SetCursorPos failed");
        }
        public static bool TryMove(Vector2 position)
        {
            // Desktop/focus transitions must not throw out of the host's Tick, or
            // press a movement/skill key with a cursor we failed to position.
            if (Environment.TickCount64 < Volatile.Read(ref _retryMoveAfter)) return false;
            try { Move(position); return true; }
            catch (Win32Exception)
            {
                Volatile.Write(ref _retryMoveAfter, Environment.TickCount64 + 250);
                return false;
            }
        }
        public static void SendButton(uint flags)
        {
            if (InputLatencyDiagnostics.SequenceId != 0 && (flags & (0x2 | 0x8 | 0x20)) != 0)
                InputLatencyDiagnostics.Record($"event=button-target flags=0x{flags:X} desktop=[{DescribeDesktop()}]");
            var input = new NativeInput { Data = new InputData { Mouse = new MouseData { Flags = flags } } };
            using var trace = InputLatencyDiagnostics.Begin("SendInput.mouse",
                always: InputLatencyDiagnostics.SequenceId != 0, detail: $"flags=0x{flags:X}");
            var inserted = SendInput(1, ref input, Marshal.SizeOf<NativeInput>());
            var error = Marshal.GetLastWin32Error();
            trace.Result = $"inserted={inserted}/1 win32={(inserted == 1 ? 0 : error)}";
            trace.Failed = inserted != 1;
            if (inserted != 1) throw new Win32Exception(error, "SendInput rejected mouse event");
        }

        public static string DescribeDesktop()
        {
            using var trace = InputLatencyDiagnostics.Begin("desktop-snapshot");
            // No hooks, SendMessage, cross-process window text, or game memory reads.
            try
            {
                var position = Position;
                var point = new Point { X = (int)position.X, Y = (int)position.Y };
                var foreground = GetForegroundWindow();
                var hit = WindowFromPoint(point);
                var root = GetAncestor(hit, 2);
                var foregroundThread = GetWindowThreadProcessId(foreground, out var foregroundPid);
                GetWindowThreadProcessId(hit, out var hitPid);
                GetWindowThreadProcessId(root, out var rootPid);
                bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
                return $"cursor={position} fg=0x{foreground.ToInt64():X}/pid={foregroundPid}/tid={foregroundThread} " +
                    $"hit=0x{hit.ToInt64():X}/pid={hitPid} root=0x{root.ToInt64():X}/pid={rootPid} " +
                    $"overlayHit={hitPid == Environment.ProcessId} selfPid={Environment.ProcessId} " +
                    $"osButtons=L:{Down(1)},R:{Down(2)} modifiers=ctrl:{Down(17)},shift:{Down(16)},alt:{Down(18)}";
            }
            catch (Exception ex) { return $"unavailable={ex.GetType().Name}:{ex.Message}"; }
        }
    }
}
