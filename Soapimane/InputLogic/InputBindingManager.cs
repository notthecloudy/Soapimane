using Gma.System.MouseKeyHook;
using Soapimane.Other;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static Soapimane.Other.LogManager;




namespace InputLogic
{
    internal class InputBindingManager
    {
        private IKeyboardMouseEvents? _mEvents;
        private readonly Dictionary<string, string> bindings = [];
        private static readonly Dictionary<string, bool> isHolding = [];
        private string? settingBindingId = null;

        // Performance: Hook evasion and raw input
        private bool _useRawInput = false;
        private bool _hooksCompromised = false;
        private readonly Random _inputJitter = new();
        private double _accumulatedJitterX = 0;
        private double _accumulatedJitterY = 0;
        private DateTime _lastHookCheck = DateTime.MinValue;
        private const int HOOK_CHECK_INTERVAL_MS = 5000;

        public event Action<string, string>? OnBindingSet;

        public event Action<string>? OnBindingPressed;

        public event Action<string>? OnBindingReleased;

        public static bool IsHoldingBinding(string bindingId) => isHolding.TryGetValue(bindingId, out bool holding) && holding;

        public void SetupDefault(string bindingId, string keyCode)
        {
            bindings[bindingId] = keyCode;
            isHolding[bindingId] = false;
            OnBindingSet?.Invoke(bindingId, keyCode);
            EnsureHookEvents();
        }

        public void StartListeningForBinding(string bindingId)
        {
            settingBindingId = bindingId;
            EnsureHookEvents();
        }

        private void EnsureHookEvents()
        {
            if (_mEvents == null && !_useRawInput)
            {
                _mEvents = Hook.GlobalEvents();
                _mEvents.KeyDown += GlobalHookKeyDown!;
                _mEvents.MouseDown += GlobalHookMouseDown!;
                _mEvents.KeyUp += GlobalHookKeyUp!;
                _mEvents.MouseUp += GlobalHookMouseUp!;
                
                // Start hook monitoring
                Task.Run(MonitorHookIntegrity);
            }
        }
        
        /// <summary>
        /// Monitors hook integrity and switches to raw input if compromised
        /// </summary>
        private async Task MonitorHookIntegrity()
        {
            while (!_hooksCompromised)
            {
                await Task.Delay(HOOK_CHECK_INTERVAL_MS);
                
                if (DetectHookCompromise())
                {
                    Log(LogLevel.Warning, "Input hooks compromised, switching to raw input");
                    _hooksCompromised = true;
                    SwitchToRawInput();
                }

            }
        }
        
        /// <summary>
        /// Detects if hooks are being monitored or tampered with
        /// </summary>
        private bool DetectHookCompromise()
        {
            // Check for debugger presence
            if (IsDebuggerPresent())
            {
                return true;
            }
            
            // Check for known anti-cheat processes
            var suspiciousProcesses = new[] { "EasyAntiCheat", "BattlEye", "Vanguard", "FaceIT" };
            var runningProcesses = System.Diagnostics.Process.GetProcesses()
                .Select(p => p.ProcessName.ToLower())
                .ToList();
                
            foreach (var suspicious in suspiciousProcesses)
            {
                if (runningProcesses.Any(p => p.Contains(suspicious.ToLower())))
                {
                    return true;
                }
            }
            
            return false;
        }
        
        [DllImport("kernel32.dll")]
        private static extern bool IsDebuggerPresent();
        
        /// <summary>
        /// Switches to raw input for anti-detection
        /// </summary>
        private void SwitchToRawInput()
        {
            try
            {
                // Dispose existing hooks
                StopListening();
                
                // Register for raw input
                var rawDevices = new RawInputDevice[]
                {
                    new RawInputDevice
                    {
                        UsagePage = 0x01, // Generic Desktop
                        Usage = 0x06,     // Keyboard
                        Flags = RawInputDeviceFlags.InputSink | RawInputDeviceFlags.NoLegacy,
                        TargetWindow = IntPtr.Zero
                    },
                    new RawInputDevice
                    {
                        UsagePage = 0x01, // Generic Desktop
                        Usage = 0x02,     // Mouse
                        Flags = RawInputDeviceFlags.InputSink | RawInputDeviceFlags.NoLegacy,
                        TargetWindow = IntPtr.Zero
                    }
                };
                
                if (RegisterRawInputDevices(rawDevices, (uint)rawDevices.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
                {
                    _useRawInput = true;
                    Log(LogLevel.Info, "Successfully switched to raw input");
                }
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Failed to switch to raw input: {ex.Message}");

            }
        }
        
        [DllImport("user32.dll")]
        private static extern bool RegisterRawInputDevices(RawInputDevice[] pRawInputDevices, uint uiNumDevices, uint cbSize);
        
        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputDevice
        {
            public ushort UsagePage;
            public ushort Usage;
            public RawInputDeviceFlags Flags;
            public IntPtr TargetWindow;
        }
        
        [Flags]
        private enum RawInputDeviceFlags : uint
        {
            Remove = 0x00000001,
            Exclude = 0x00000010,
            PageOnly = 0x00000020,
            NoLegacy = 0x00000030,
            InputSink = 0x00000100,
            CaptureMouse = 0x00000200,
            NoHotKeys = 0x00000200,
            AppKeys = 0x00000400,
            ExInputSink = 0x00001000,
            DevNotify = 0x00002000
        }
        
        /// <summary>
        /// Applies sub-pixel jitter to input for anti-detection
        /// </summary>
        public void ApplyInputJitter(ref int x, ref int y)
        {
            // Add sub-pixel jitter to avoid detection patterns
            double jitterX = (_inputJitter.NextDouble() - 0.5) * 0.5;
            double jitterY = (_inputJitter.NextDouble() - 0.5) * 0.5;
            
            // Store jitter for accumulation
            _accumulatedJitterX += jitterX;
            _accumulatedJitterY += jitterY;
            
            // Apply integer portion
            int applyX = (int)_accumulatedJitterX;
            int applyY = (int)_accumulatedJitterY;
            
            x += applyX;
            y += applyY;
            
            _accumulatedJitterX -= applyX;
            _accumulatedJitterY -= applyY;
        }


        private void GlobalHookKeyDown(object sender, KeyEventArgs e)
        {
            if (settingBindingId != null)
            {
                bindings[settingBindingId] = e.KeyCode.ToString();
                OnBindingSet?.Invoke(settingBindingId, e.KeyCode.ToString());
                settingBindingId = null;
            }
            else
            {
                foreach (var binding in bindings)
                {
                    if (binding.Value == e.KeyCode.ToString())
                    {
                        isHolding[binding.Key] = true;
                        OnBindingPressed?.Invoke(binding.Key);
                    }
                }
            }
        }

        private void GlobalHookMouseDown(object sender, MouseEventArgs e)
        {
            if (settingBindingId != null)
            {
                bindings[settingBindingId] = e.Button.ToString();
                OnBindingSet?.Invoke(settingBindingId, e.Button.ToString());
                settingBindingId = null;
            }
            else
            {
                foreach (var binding in bindings)
                {
                    if (binding.Value == e.Button.ToString())
                    {
                        isHolding[binding.Key] = true;
                        OnBindingPressed?.Invoke(binding.Key);
                    }
                }
            }
        }

        private void GlobalHookKeyUp(object sender, KeyEventArgs e)
        {
            foreach (var binding in bindings)
            {
                if (binding.Value == e.KeyCode.ToString())
                {
                    isHolding[binding.Key] = false;
                    OnBindingReleased?.Invoke(binding.Key);
                }
            }
        }

        private void GlobalHookMouseUp(object sender, MouseEventArgs e)
        {
            foreach (var binding in bindings)
            {
                if (binding.Value == e.Button.ToString())
                {
                    isHolding[binding.Key] = false;
                    OnBindingReleased?.Invoke(binding.Key);
                }
            }
        }

        public void StopListening()
        {
            if (_mEvents != null)
            {
                _mEvents.KeyDown -= GlobalHookKeyDown!;
                _mEvents.MouseDown -= GlobalHookMouseDown!;
                _mEvents.KeyUp -= GlobalHookKeyUp!;
                _mEvents.MouseUp -= GlobalHookMouseUp!;
                _mEvents.Dispose();
                _mEvents = null;
            }
        }
        
        /// <summary>
        /// Gets whether raw input mode is active
        /// </summary>
        public bool IsUsingRawInput => _useRawInput;
        
        /// <summary>
        /// Gets whether hooks were detected as compromised
        /// </summary>
        public bool AreHooksCompromised => _hooksCompromised;
    }
}
