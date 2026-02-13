#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using static Soapimane.Other.LogManager;

namespace Soapimane.AntiDetection

{
    /// <summary>
    /// Provides process hiding, anti-debugging, and anti-VM capabilities
    /// to protect the application from detection and analysis.
    /// </summary>
    public static class ProcessHider
    {
        #region P/Invoke Declarations

        [DllImport("kernel32.dll")]
        private static extern bool IsDebuggerPresent();

        [DllImport("kernel32.dll")]
        private static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess, ref bool isDebuggerPresent);

        [DllImport("kernel32.dll")]
        private static extern void OutputDebugString(string lpOutputString);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref IntPtr processInformation, int processInformationLength, ref int returnLength);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, ref int returnLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessPriorityClass(IntPtr hProcess, uint dwPriorityClass);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowText(IntPtr hWnd, string lpString);

        [DllImport("kernel32.dll")]
        private static extern bool GetProcessAffinityMask(IntPtr hProcess, out IntPtr lpProcessAffinityMask, out IntPtr lpSystemAffinityMask);

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessAffinityMask(IntPtr hProcess, IntPtr dwProcessAffinityMask);

        [DllImport("kernel32.dll")]
        private static extern int GetTickCount();

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceFrequency(out long lpFrequency);

        [DllImport("ntdll.dll")]
        private static extern int NtSetInformationProcess(IntPtr processHandle, int processInformationClass, ref int processInformation, int processInformationLength);

        // Constants
        private const uint REALTIME_PRIORITY_CLASS = 0x00000100;
        private const uint HIGH_PRIORITY_CLASS = 0x00000080;
        private const int ProcessBreakOnTermination = 29;
        private const int ProcessDebugPort = 7;
        private const int ProcessBasicInformation = 0;

        #endregion

        #region Private Fields

        private static readonly Random _random = new Random();
        private static string _originalProcessName = string.Empty;
        private static string _spoofedWindowTitle = string.Empty;
        private static bool _antiDebuggingEnabled = false;
        private static Thread? _debuggerDetectionThread;

        private static volatile bool _shouldStopDetection = false;

        #endregion

        #region Process Name Randomization

        /// <summary>
        /// List of innocuous process names to spoof as
        /// </summary>
        private static readonly string[] InnocuousProcessNames = new[]
        {
            "svchost",
            "RuntimeBroker",
            "dllhost",
            "SearchIndexer",
            "WmiPrvSE",
            "fontdrvhost",
            "backgroundTaskHost",
            "WindowsInternal.ComposableShell.Experiences.TextInput.InputApp"
        };

        /// <summary>
        /// List of innocuous window titles to use
        /// </summary>
        private static readonly string[] InnocuousWindowTitles = new[]
        {
            "Settings",
            "System Settings",
            "Windows Explorer",
            "Task Manager",
            "Services",
            "Event Viewer",
            "Device Manager",
            "Network Connections"
        };

        /// <summary>
        /// Randomizes the process name by creating a suspended copy with a different name
        /// Note: This is a simulation - true process name changing requires more invasive techniques
        /// </summary>
        public static void RandomizeProcessName()
        {
            try
            {
                _originalProcessName = Process.GetCurrentProcess().ProcessName;
                string newName = InnocuousProcessNames[_random.Next(InnocuousProcessNames.Length)];
                
                // Log the change (in production, this would be more sophisticated)
                Log(LogLevel.Info, $"Process name spoofing initialized: {_originalProcessName} -> {newName}");

                
                // Process hollowing technique
                try
                {
                    string currentPath = Process.GetCurrentProcess().MainModule?.FileName 
                        ?? throw new InvalidOperationException("Cannot get current process path");
                    
                    // Create suspended process with target name
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = currentPath,
                        Arguments = $"--spoofed-name={newName}",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    
                    // Use process hollowing to create suspended process
                    IntPtr hProcess = IntPtr.Zero;
                    IntPtr hThread = IntPtr.Zero;
                    
                    // Create process in suspended state
                    if (CreateProcessSuspended(currentPath, newName, out hProcess, out hThread))
                    {
                        // Unmap original executable memory
                        if (NtUnmapViewOfSection(hProcess, GetImageBase(hProcess)) == 0)
                        {
                            // Allocate new memory for our code
                            IntPtr newImageBase = VirtualAllocEx(hProcess, GetImageBase(Process.GetCurrentProcess().Handle), 
                                (uint)GetCurrentImageSize(), AllocationType.Commit | AllocationType.Reserve, 
                                MemoryProtection.ExecuteReadWrite);
                            
                            if (newImageBase != IntPtr.Zero)
                            {
                                // Write our process memory to new process
                                WriteProcessMemory(hProcess, newImageBase, GetCurrentProcessImage(), 
                                    (uint)GetCurrentImageSize(), out _);
                                
                                // Update PEB with new image base
                                SetImageBase(hProcess, newImageBase);
                                
                                // Resume the hollowed process
                                ResumeThread(hThread);
                                
                                // Exit current process
                                Environment.Exit(0);
                            }
                        }
                        
                        CloseHandle(hProcess);
                        CloseHandle(hThread);
                    }
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Warning, $"Process hollowing failed: {ex.Message}. Falling back to name storage.");
                }
                
                // Fallback: just store the intended spoof
                _originalProcessName = newName;

            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Failed to randomize process name: {ex.Message}");
            }

        }

        /// <summary>
        /// Spoofs the window title of the main window
        /// </summary>
        public static void SpoofWindowTitle(Window window)
        {
            try
            {
                if (window == null) return;
                
                _spoofedWindowTitle = InnocuousWindowTitles[_random.Next(InnocuousWindowTitles.Length)];
                
                IntPtr hWnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (hWnd != IntPtr.Zero)
                {
                    SetWindowText(hWnd, _spoofedWindowTitle);
                    window.Title = _spoofedWindowTitle;
                }
                
                Log(LogLevel.Info, $"Window title spoofed to: {_spoofedWindowTitle}");
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Failed to spoof window title: {ex.Message}");
            }

        }

        /// <summary>
        /// Gets a random innocuous process name
        /// </summary>
        public static string GetRandomProcessName()
        {
            return InnocuousProcessNames[_random.Next(InnocuousProcessNames.Length)];
        }

        #endregion

        #region Anti-Debugging

        /// <summary>
        /// Enables all anti-debugging protections
        /// </summary>
        public static void EnableAntiDebugging()
        {
            if (_antiDebuggingEnabled) return;
            
            _antiDebuggingEnabled = true;
            _shouldStopDetection = false;
            
            // Start debugger detection thread
            _debuggerDetectionThread = new Thread(DebuggerDetectionLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.Lowest
            };
            _debuggerDetectionThread.Start();
            
            // Set process as critical (Windows will BSOD if process is killed)
            // Only enable in release builds as it can interfere with debugging
            #if !DEBUG
            SetProcessAsCritical(true);
            #endif
            
            Log(LogLevel.Info, "Anti-debugging protections enabled");


        }


        /// <summary>
        /// Disables anti-debugging protections
        /// </summary>
        public static void DisableAntiDebugging()
        {
            _shouldStopDetection = true;
            _antiDebuggingEnabled = false;
            
            #if !DEBUG
            SetProcessAsCritical(false);
            #endif
            
            _debuggerDetectionThread?.Join(1000);


            Log(LogLevel.Info, "Anti-debugging protections disabled");
        }


        /// <summary>
        /// Main debugger detection loop
        /// </summary>
        private static void DebuggerDetectionLoop()
        {
            int detectionCount = 0;
            const int MAX_DETECTIONS = 3;
            
            while (!_shouldStopDetection)
            {
                try
                {
                    bool debuggerDetected = false;
                    
                    // Method 1: IsDebuggerPresent API
                    if (IsDebuggerPresent())
                    {
                        debuggerDetected = true;
                        Log(LogLevel.Warning, "Debugger detected via IsDebuggerPresent");
                    }
                    
                    // Method 2: CheckRemoteDebuggerPresent
                    bool remoteDebugger = false;
                    CheckRemoteDebuggerPresent(GetCurrentProcess(), ref remoteDebugger);
                    if (remoteDebugger)
                    {
                        debuggerDetected = true;
                        Log(LogLevel.Warning, "Debugger detected via CheckRemoteDebuggerPresent");
                    }
                    
                    // Method 3: NtQueryInformationProcess (DebugPort)
                    IntPtr debugPort = IntPtr.Zero;
                    int returnLength = 0;
                    int status = NtQueryInformationProcess(GetCurrentProcess(), ProcessDebugPort, ref debugPort, IntPtr.Size, ref returnLength);
                    if (status == 0 && debugPort != IntPtr.Zero)
                    {
                        debuggerDetected = true;
                        Log(LogLevel.Warning, "Debugger detected via NtQueryInformationProcess");
                    }
                    
                    // Method 4: Timing check (debugger slows execution)
                    if (DetectDebuggerViaTiming())
                    {
                        debuggerDetected = true;
                        Log(LogLevel.Warning, "Debugger detected via timing analysis");
                    }
                    
                    // Method 5: OutputDebugString trick
                    if (DetectDebuggerViaOutputDebugString())
                    {
                        debuggerDetected = true;
                        Log(LogLevel.Warning, "Debugger detected via OutputDebugString");
                    }
                    
                    // Method 6: Hardware breakpoints
                    if (DetectHardwareBreakpoints())
                    {
                        debuggerDetected = true;
                        Log(LogLevel.Warning, "Debugger detected via hardware breakpoints");
                    }

                    
                    if (debuggerDetected)
                    {
                        detectionCount++;
                        if (detectionCount >= MAX_DETECTIONS)
                        {
                            // Take action: crash, exit, or enter decoy mode
                            HandleDebuggerDetected();
                            return;
                        }
                    }
                    else
                    {
                        detectionCount = Math.Max(0, detectionCount - 1);
                    }
                    
                    Thread.Sleep(1000 + _random.Next(500)); // Randomize check interval
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Error, $"Debugger detection error: {ex.Message}");
                }

            }
        }

        /// <summary>
        /// Detects debugger via timing analysis
        /// </summary>
        private static bool DetectDebuggerViaTiming()
        {
            const int ITERATIONS = 100;
            long totalTicks = 0;
            long minTicks = long.MaxValue;
            long maxTicks = long.MinValue;
            
            for (int i = 0; i < ITERATIONS; i++)
            {
                long start = Stopwatch.GetTimestamp();
                
                // Simple operation
                int x = i * i;
                x = x ^ (x >> 16);
                
                long end = Stopwatch.GetTimestamp();
                long ticks = end - start;
                
                totalTicks += ticks;
                minTicks = Math.Min(minTicks, ticks);
                maxTicks = Math.Max(maxTicks, ticks);
            }
            
            double average = (double)totalTicks / ITERATIONS;
            double variance = maxTicks - minTicks;
            
            // High variance or very slow average indicates debugger
            return variance > average * 10 || average > 1000;
        }

        /// <summary>
        /// Detects debugger via OutputDebugString behavior
        /// </summary>
        private static bool DetectDebuggerViaOutputDebugString()
        {
            int lastError = Marshal.GetLastWin32Error();
            OutputDebugString("Test string for debugger detection");
            int newError = Marshal.GetLastWin32Error();
            
            // If error code changed, a debugger likely processed the string
            return lastError != newError;
        }

        /// <summary>
        /// Detects hardware breakpoints via debug registers using GetThreadContext
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadContext(IntPtr hThread, ref CONTEXT lpContext);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flNewProtect, out uint lpflOldProtect);

        private const uint PAGE_EXECUTE_READWRITE = 0x40;

        private static bool DetectHardwareBreakpoints()
        {
            try
            {
                CONTEXT ctx = new CONTEXT();
                ctx.ContextFlags = CONTEXT_DEBUG_REGISTERS;
                
                // Get current thread context
                IntPtr hThread = GetCurrentThread();
                
                if (GetThreadContext(hThread, ref ctx))
                {
                    // Check debug registers for hardware breakpoints
                    // Dr0-Dr3 contain breakpoint addresses
                    // Dr6 contains debug status (which breakpoints were hit)
                    // Dr7 contains debug control (which breakpoints are enabled)
                    
                    bool hasHardwareBreakpoints = (ctx.Dr0 != 0) || (ctx.Dr1 != 0) || 
                                                  (ctx.Dr2 != 0) || (ctx.Dr3 != 0);
                    
                    // Check if any breakpoints are enabled in Dr7
                    // Bits 0, 2, 4, 6 enable local breakpoints for Dr0-Dr3
                    bool breakpointsEnabled = (ctx.Dr7 & 0x01) != 0 ||  // Local Dr0
                                              (ctx.Dr7 & 0x04) != 0 ||  // Local Dr1
                                              (ctx.Dr7 & 0x10) != 0 ||  // Local Dr2
                                              (ctx.Dr7 & 0x40) != 0;    // Local Dr3
                    
                    return hasHardwareBreakpoints && breakpointsEnabled;
                }
                
                return false;
            }
            catch
            {
                return false;
            }
        }

        // Process hollowing helper P/Invoke declarations
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(string? lpApplicationName, string lpCommandLine, 
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, 
            uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, 
            ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int NtUnmapViewOfSection(IntPtr ProcessHandle, IntPtr BaseAddress);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, 
            uint dwSize, AllocationType flAllocationType, MemoryProtection flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, 
            byte[] lpBuffer, uint nSize, out int lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, 
            [Out] byte[] lpBuffer, uint dwSize, out int lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr Process, out PROCESS_MEMORY_COUNTERS ppsmemCounters, uint cb);

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public uint cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public uint dwX;
            public uint dwY;
            public uint dwXSize;
            public uint dwYSize;
            public uint dwXCountChars;
            public uint dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb;
            public uint PageFaultCount;
            public uint PeakWorkingSetSize;
            public uint WorkingSetSize;
            public uint QuotaPeakPagedPoolUsage;
            public uint QuotaPagedPoolUsage;
            public uint QuotaPeakNonPagedPoolUsage;
            public uint QuotaNonPagedPoolUsage;
            public uint PagefileUsage;
            public uint PeakPagefileUsage;
        }

        private enum AllocationType : uint
        {
            Commit = 0x1000,
            Reserve = 0x2000,
            Decommit = 0x4000,
            Release = 0x8000,
            Free = 0x10000,
            Private = 0x20000,
            Mapped = 0x40000,
            Reset = 0x80000,
            TopDown = 0x100000,
            WriteWatch = 0x200000,
            Physical = 0x400000,
            Rotate = 0x800000,
            LargePages = 0x20000000
        }

        private enum MemoryProtection : uint
        {
            Execute = 0x10,
            ExecuteRead = 0x20,
            ExecuteReadWrite = 0x40,
            ExecuteWriteCopy = 0x80,
            NoAccess = 0x01,
            ReadOnly = 0x02,
            ReadWrite = 0x04,
            WriteCopy = 0x08,
            GuardModifierflag = 0x100,
            NoCacheModifierflag = 0x200,
            WriteCombineModifierflag = 0x400
        }

        private const uint CREATE_SUSPENDED = 0x00000004;

        private static bool CreateProcessSuspended(string path, string commandLine, out IntPtr hProcess, out IntPtr hThread)
        {
            hProcess = IntPtr.Zero;
            hThread = IntPtr.Zero;
            
            try
            {
                var si = new STARTUPINFO();
                si.cb = (uint)Marshal.SizeOf(typeof(STARTUPINFO));
                
                var pi = new PROCESS_INFORMATION();
                
                bool result = CreateProcess(null, $"\"{path}\" {commandLine}", IntPtr.Zero, IntPtr.Zero, 
                    false, CREATE_SUSPENDED, IntPtr.Zero, null, ref si, out pi);
                
                if (result)
                {
                    hProcess = pi.hProcess;
                    hThread = pi.hThread;
                    return true;
                }
                
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static IntPtr GetImageBase(IntPtr hProcess)
        {
            try
            {
                // Read PEB to get image base address
                // This is a simplified implementation
                byte[] buffer = new byte[IntPtr.Size];
                IntPtr pebAddress = GetProcessPebAddress(hProcess);
                
                if (pebAddress != IntPtr.Zero)
                {
                    // Image base is at offset 0x10 in PEB (x64) or 0x08 (x86)
                    int imageBaseOffset = IntPtr.Size == 8 ? 0x10 : 0x08;
                    
                    if (ReadProcessMemory(hProcess, pebAddress + imageBaseOffset, buffer, (uint)IntPtr.Size, out _))
                    {
                        return IntPtr.Size == 8 ? 
                            (IntPtr)BitConverter.ToInt64(buffer, 0) : 
                            (IntPtr)BitConverter.ToInt32(buffer, 0);
                    }
                }
            }
            catch { }
            
            return IntPtr.Zero;
        }

        private static bool SetImageBase(IntPtr hProcess, IntPtr newImageBase)
        {
            try
            {
                IntPtr pebAddress = GetProcessPebAddress(hProcess);
                
                if (pebAddress != IntPtr.Zero)
                {
                    int imageBaseOffset = IntPtr.Size == 8 ? 0x10 : 0x08;
                    byte[] newBaseBytes = IntPtr.Size == 8 ? 
                        BitConverter.GetBytes(newImageBase.ToInt64()) : 
                        BitConverter.GetBytes(newImageBase.ToInt32());
                    
                    int bytesWritten;
                    return WriteProcessMemory(hProcess, pebAddress + imageBaseOffset, newBaseBytes, 
                        (uint)newBaseBytes.Length, out bytesWritten);
                }
            }
            catch { }
            
            return false;
        }

        private static IntPtr GetProcessPebAddress(IntPtr hProcess)
        {
            try
            {
                // Query process basic information to get PEB address
                PROCESS_BASIC_INFORMATION pbi = new PROCESS_BASIC_INFORMATION();
                int returnLength = 0;
                
                int status = NtQueryInformationProcess(hProcess, ProcessBasicInformation, ref pbi, 
                    Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION)), ref returnLength);
                
                if (status == 0) // STATUS_SUCCESS
                {
                    return pbi.PebBaseAddress;
                }
            }
            catch { }
            
            return IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2;
            public IntPtr Reserved3;
            public IntPtr UniqueProcessId;
            public IntPtr Reserved4;
        }

        private static int GetCurrentImageSize()
        {
            try
            {
                var currentProcess = Process.GetCurrentProcess();
                var mainModule = currentProcess.MainModule;
                
                if (mainModule != null)
                {
                    return mainModule.ModuleMemorySize;
                }
            }
            catch { }
            
            // Default fallback size (4MB)
            return 4 * 1024 * 1024;
        }

        private static byte[] GetCurrentProcessImage()
        {
            try
            {
                string? currentPath = Process.GetCurrentProcess().MainModule?.FileName;
                
                if (!string.IsNullOrEmpty(currentPath) && File.Exists(currentPath))
                {
                    return File.ReadAllBytes(currentPath);
                }
            }
            catch { }
            
            return Array.Empty<byte>();
        }


        /// <summary>
        /// Sets or removes critical process status
        /// </summary>
        private static void SetProcessAsCritical(bool critical)
        {
            try
            {
                int isCritical = critical ? 1 : 0;
                NtSetInformationProcess(GetCurrentProcess(), ProcessBreakOnTermination, ref isCritical, sizeof(int));
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Failed to set critical process: {ex.Message}");
            }

        }

        /// <summary>
        /// Handles detected debugger
        /// </summary>
        private static void HandleDebuggerDetected()
        {
            Log(LogLevel.Error, "DEBUGGER DETECTED - Taking protective action");

            
            // Strategy: Enter decoy mode or crash
            // For now, we just exit cleanly
            #if !DEBUG
            Environment.Exit(0xDEAD);
            #endif
        }

        #endregion

        #region Process Protection

        /// <summary>
        /// Sets high priority for the process
        /// </summary>
        public static void SetHighPriority()
        {
            try
            {
                SetProcessPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS);
                Log(LogLevel.Info, "Process priority set to HIGH");
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Failed to set high priority: {ex.Message}");
            }
        }


        /// <summary>
        /// Sets real-time priority (use with caution)
        /// </summary>
        public static void SetRealtimePriority()
        {
            try
            {
                SetProcessPriorityClass(GetCurrentProcess(), REALTIME_PRIORITY_CLASS);
                Log(LogLevel.Info, "Process priority set to REALTIME");
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Failed to set realtime priority: {ex.Message}");
            }
        }


        /// <summary>
        /// Restricts process to specific CPU cores (anti-analysis)
        /// </summary>
        public static void RestrictToSingleCore()
        {
            try
            {
                GetProcessAffinityMask(GetCurrentProcess(), out IntPtr processMask, out IntPtr systemMask);
                
                // Restrict to first core only
                IntPtr newMask = new IntPtr(1);
                SetProcessAffinityMask(GetCurrentProcess(), newMask);
                
                Log(LogLevel.Info, "Process restricted to single CPU core");
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Failed to restrict affinity: {ex.Message}");
            }
        }


        #endregion

        #region Context Structure (for hardware breakpoint detection)

        [StructLayout(LayoutKind.Sequential)]
        private struct CONTEXT
        {
            public uint ContextFlags;
            public uint Dr0;
            public uint Dr1;
            public uint Dr2;
            public uint Dr3;
            public uint Dr6;
            public uint Dr7;
            // ... other fields omitted for brevity
        }

        private const uint CONTEXT_DEBUG_REGISTERS = 0x00010000;

        #endregion
    }
}
