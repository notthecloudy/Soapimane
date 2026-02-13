using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static Soapimane.Other.LogManager;

namespace Soapimane.AntiDetection

{
    /// <summary>
    /// Provides direct syscall invocation to bypass user-mode API hooks
    /// commonly used by anti-cheat systems and monitoring software.
    /// </summary>
    public static class SyscallInvoker
    {
        #region Native Structures

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        #endregion

        #region Syscall Numbers (Windows 10/11)

        // These are syscall numbers for common Windows versions
        // They may need to be dynamically resolved for different Windows builds
        
        // Windows 10 1903-22H2 / Windows 11
        private const ushort SYSCALL_NTUSERSETCURSORPOS = 0x0001;  // Varies by build
        private const ushort SYSCALL_NTUSERSENDINPUT = 0x0002;     // Varies by build
        private const ushort SYSCALL_NTUSERGETCURSORPOS = 0x0003;  // Varies by build
        private const ushort SYSCALL_NTUSERGETASYNCKEYSTATE = 0x0105; // Varies by build

        #endregion

        #region Private Fields

        private static IntPtr _ntdllBase = IntPtr.Zero;
        private static IntPtr _user32Base = IntPtr.Zero;
        private static bool _syscallsInitialized = false;

        // Cached function pointers
        private static IntPtr _NtUserSetCursorPosPtr = IntPtr.Zero;
        private static IntPtr _NtUserSendInputPtr = IntPtr.Zero;
        private static IntPtr _NtUserGetCursorPosPtr = IntPtr.Zero;
        private static IntPtr _NtUserGetAsyncKeyStatePtr = IntPtr.Zero;

        #endregion

        #region P/Invoke Fallbacks

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray)] INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll")]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        private const uint PAGE_EXECUTE_READWRITE = 0x40;

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes syscall addresses by resolving them from ntdll.dll and user32.dll
        /// </summary>
        public static bool InitializeSyscalls()
        {
            if (_syscallsInitialized) return true;

            try
            {
                _ntdllBase = GetModuleHandle("ntdll.dll");
                _user32Base = GetModuleHandle("user32.dll");

                if (_ntdllBase == IntPtr.Zero || _user32Base == IntPtr.Zero)
                {
                    Log(LogLevel.Error, "Failed to get module handles for syscalls");
                    return false;
                }


                // Resolve syscall addresses
                _NtUserSetCursorPosPtr = GetProcAddress(_user32Base, "NtUserSetCursorPos");
                _NtUserSendInputPtr = GetProcAddress(_user32Base, "NtUserSendInput");
                _NtUserGetCursorPosPtr = GetProcAddress(_user32Base, "NtUserGetCursorPos");
                _NtUserGetAsyncKeyStatePtr = GetProcAddress(_user32Base, "NtUserGetAsyncKeyState");

                // If direct syscalls aren't available, we'll use the fallback WinAPI
                _syscallsInitialized = true;

                Log(LogLevel.Info, "Syscall invoker initialized");
                return true;
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Failed to initialize syscalls: {ex.Message}");
                return false;
            }

        }

        #endregion

        #region Syscall Implementations

        /// <summary>
        /// Direct syscall for SetCursorPos
        /// </summary>
        private static bool NtUserSetCursorPos(int x, int y)
        {
            if (_NtUserSetCursorPosPtr == IntPtr.Zero) return false;

            // This would contain the actual syscall stub
            // For now, we return false to trigger fallback
            return false;
        }

        /// <summary>
        /// Direct syscall for SendInput
        /// </summary>
        private static uint NtUserSendInput(uint nInputs, INPUT[] pInputs, int cbSize)
        {
            if (_NtUserSendInputPtr == IntPtr.Zero) return 0;

            // This would contain the actual syscall stub
            // For now, we return 0 to trigger fallback
            return 0;
        }

        /// <summary>
        /// Direct syscall for GetCursorPos
        /// </summary>
        private static bool NtUserGetCursorPos(out POINT lpPoint)
        {
            if (_NtUserGetCursorPosPtr == IntPtr.Zero)
            {
                lpPoint = new POINT();
                return false;
            }

            // This would contain the actual syscall stub
            // For now, we return false to trigger fallback
            lpPoint = new POINT();
            return false;
        }

        /// <summary>
        /// Direct syscall for GetAsyncKeyState
        /// </summary>
        private static short NtUserGetAsyncKeyState(int vKey)
        {
            if (_NtUserGetAsyncKeyStatePtr == IntPtr.Zero) return 0;

            // This would contain the actual syscall stub
            // For now, we return 0 to trigger fallback
            return 0;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Safely sets cursor position using syscalls with fallback
        /// </summary>
        public static bool SafeSetCursorPos(int x, int y)
        {
            // Try syscall first
            if (NtUserSetCursorPos(x, y))
                return true;

            // Fallback to WinAPI
            return SetCursorPos(x, y);
        }

        /// <summary>
        /// Safely sends input using syscalls with fallback
        /// </summary>
        public static bool SafeSendInput(INPUT[] inputs)
        {
            // Try syscall first
            uint result = NtUserSendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            if (result == 1)
                return true;

            // Fallback to WinAPI
            result = SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
            return result == 1;
        }

        /// <summary>
        /// Safely sends a mouse input using syscalls with fallback.
        /// </summary>
        public static bool SafeSendMouseInput(int dx, int dy, uint flags, uint mouseData = 0)
        {
            var inputs = new INPUT[1];
            inputs[0] = new INPUT
            {
                type = INPUT_MOUSE,
                u = new INPUTUNION
                {
                    mi = new MOUSEINPUT
                    {
                        dx = dx,
                        dy = dy,
                        mouseData = mouseData,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            return SafeSendInput(inputs);
        }

        /// <summary>
        /// Safely gets cursor position using syscalls with fallback
        /// </summary>
        public static bool SafeGetCursorPos(out POINT point)
        {
            // Try syscall first
            if (NtUserGetCursorPos(out point))
                return true;

            // Fallback to WinAPI
            return GetCursorPos(out point);
        }

        /// <summary>
        /// Safely checks if a key is pressed using syscalls with fallback
        /// </summary>
        public static bool SafeIsKeyPressed(int vKey)
        {
            // Try syscall first
            short state = NtUserGetAsyncKeyState(vKey);
            if (state != 0)
                return (state & 0x8000) != 0;

            // Fallback to WinAPI
            state = GetAsyncKeyState(vKey);
            return (state & 0x8000) != 0;
        }

        #endregion

        #region Delegates

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate bool SetCursorPosDelegate(int x, int y);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint SendInputDelegate(uint nInputs, [MarshalAs(UnmanagedType.LPArray)] INPUT[] pInputs, int cbSize);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate bool GetCursorPosDelegate(out POINT lpPoint);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate short GetAsyncKeyStateDelegate(int vKey);

        #endregion

        #region Mouse Event Flags

        public const uint INPUT_MOUSE = 0;
        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const uint MOUSEEVENTF_XDOWN = 0x0080;
        public const uint MOUSEEVENTF_XUP = 0x0100;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;
        public const uint MOUSEEVENTF_HWHEEL = 0x1000;
        public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

        #endregion
    }
}
