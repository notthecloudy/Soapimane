﻿using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media;
using Soapimane.Other;
using static Soapimane.Other.LogManager;




namespace Soapimane.Other
{
    /// <summary>
    /// Manages StreamGuard protection for application windows and controls
    /// Prevents screen capture of protected content by setting window display affinity
    /// Also manages system tray icons for user feedback
    /// </summary>
    public static class StreamGuardManager
    {
        #region Constants
        const uint WDA_NONE = 0;
        const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_APPWINDOW = 0x00040000;
        const uint GA_ROOT = 2;
        #endregion

        #region Private Fields
        private static bool _isEnabled = false;
        private static HashSet<nint> _protectedWindows = new();
        private static bool _eventsAttached = false;
        private static System.Windows.Threading.DispatcherTimer? _popupMonitorTimer;
        private static bool _trayIconCreated = false;
        private static nint _mainApplicationHandle = nint.Zero;
        
        // Anti-detection: Screen recorder detection
        private static System.Windows.Threading.DispatcherTimer? _screenRecorderTimer;

        private static bool _screenRecorderDetected = false;
        
        private static readonly string[] ScreenRecorderProcesses = new[]
        {
            "obs64", "obs32", "obs",
            "gamecapture", "ge_force_experience",
            "recentralize", "xsplit", "streamlabs",
            "wirecast", "vmix64", "vmix",
            "action", "bandicam", "fraps",
            "camtasia", "camstudio"
        };
        #endregion

        #region P/Invoke Declarations
        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(nint hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        private static extern nint GetParent(nint hWnd);
        [DllImport("user32.dll")]
        private static extern nint GetAncestor(nint hWnd, uint gaFlags);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, nint lParam);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")]
        private static extern int GetClassName(nint hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(nint hWnd);
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(nint hIcon);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);
        
        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_DELETE = 0x00000002;
        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;
        private const uint WM_USER = 0x0400;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_RBUTTONDOWN = 0x0204;
        
        private delegate bool EnumWindowsProc(nint hWnd, nint lParam);
        
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public nint hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public nint hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public nint hBalloonIcon;
        }
        #endregion

        #region Screen Recorder Detection
        private static bool IsScreenRecorderRunning()
        {
            try
            {
                foreach (var process in System.Diagnostics.Process.GetProcesses())
                {
                    try
                    {
                        string processName = process.ProcessName;
                        if (string.IsNullOrEmpty(processName)) continue;
                        
                        string processNameLower = processName.ToLower();
                        
                        foreach (var recorder in ScreenRecorderProcesses)
                        {
                            if (processNameLower.Contains(recorder.ToLower()))
                            {
                                Log(LogLevel.Warning, $"Screen recorder detected: {processName}");
                                return true;
                            }
                        }
                    }
                    catch { }
                }


            }
            catch { }
            return false;
        }

        private static void OnScreenRecorderDetected()
        {
            if (_screenRecorderDetected) return;
            _screenRecorderDetected = true;
            Log(LogLevel.Warning, "Screen recording software detected - auto-hiding overlays", true, 5000);
            Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (Window window in Application.Current.Windows)
                {
                    if (window.GetType().Name == "FOV" || window.GetType().Name.Contains("DetectedPlayerWindow") || window.Title.Contains("FOV"))
                    {
                        window.Hide();
                    }
                }
            });
        }


        private static void StartScreenRecorderMonitoring()
        {
            if (_screenRecorderTimer != null) return;
            _screenRecorderTimer = new System.Windows.Threading.DispatcherTimer();
            _screenRecorderTimer.Interval = TimeSpan.FromSeconds(5);
            _screenRecorderTimer.Tick += (s, e) =>
            {
                if (IsScreenRecorderRunning())
                {
                    OnScreenRecorderDetected();
                }
                else if (_screenRecorderDetected)
                {
                    _screenRecorderDetected = false;
                }
            };
            _screenRecorderTimer.Start();
        }


        private static void StopScreenRecorderMonitoring()
        {
            if (_screenRecorderTimer != null)
            {
                _screenRecorderTimer.Stop();
                _screenRecorderTimer = null;
            }
        }
        #endregion

        #region Window Protection
        private static void ApplyToWindow(Window window, bool enable)
        {
            if (window == null) return;
            var hWnd = new WindowInteropHelper(window).Handle;
            if (hWnd == nint.Zero)
            {
                if (enable && _isEnabled)
                {
                    window.SourceInitialized += (s, e) => ApplyToWindow(window, true);
                }
                return;
            }
            if (enable)
            {
                if (_protectedWindows.Contains(hWnd)) return;
                _protectedWindows.Add(hWnd);
            }
            else
            {
                _protectedWindows.Remove(hWnd);
            }
            SetWindowDisplayAffinity(hWnd, enable ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
            window.ShowInTaskbar = !enable;
            var extendedStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            if (enable)
                SetWindowLong(hWnd, GWL_EXSTYLE, (extendedStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
            else
                SetWindowLong(hWnd, GWL_EXSTYLE, (extendedStyle | WS_EX_APPWINDOW) & ~WS_EX_TOOLWINDOW);
            if (enable)
            {
                AddTrayIcon(window, hWnd);
            }
            else
            {
                RemoveTrayIcon(hWnd);
            }
        }

        private static Window? FindParentWindow(UserControl userControl)
        {
            Window? parentWindow = Window.GetWindow(userControl);
            if (parentWindow != null) return parentWindow;
            DependencyObject? parent = userControl;
            while (parent != null && !(parent is Window))
            {
                parent = VisualTreeHelper.GetParent(parent) ?? LogicalTreeHelper.GetParent(parent);
            }
            if (parent is Window window) return window;
            return null;
        }


        private static void ApplyToUserControl(UserControl userControl, bool enable)
        {
            if (userControl == null) return;
            Window? parentWindow = FindParentWindow(userControl);
            if (parentWindow != null)
            {
                ApplyToWindow(parentWindow, enable);
            }
            else if (enable)
            {
                userControl.Loaded += (s, e) => {
                    Window? delayedWindow = FindParentWindow(userControl);
                    if (delayedWindow != null)
                    {
                        ApplyToWindow(delayedWindow, enable);
                    }
                };
            }
        }

        #endregion

        #region Popup Window Protection
        private static void ProtectAllProcessWindows()
        {
            uint currentProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            EnumWindows((hWnd, lParam) =>
            {
                try
                {
                    if (IsWindowVisible(hWnd))
                    {
                        GetWindowThreadProcessId(hWnd, out uint windowProcessId);
                        if (windowProcessId == currentProcessId)
                        {
                            var className = new System.Text.StringBuilder(256);
                            GetClassName(hWnd, className, className.Capacity);
                            string classNameStr = className.ToString();
                            if (classNameStr.Contains("ComboBox") || classNameStr.Contains("Popup") ||
                                classNameStr.Equals("HwndWrapper[DefaultDomain") || classNameStr.Contains("HwndWrapper") ||
                                classNameStr.Contains("DropDown") || classNameStr.Contains("MenuDropAlignment"))
                            {
                                if (_isEnabled && !_protectedWindows.Contains(hWnd))
                                {
                                    SetWindowDisplayAffinity(hWnd, WDA_EXCLUDEFROMCAPTURE);
                                    _protectedWindows.Add(hWnd);
                                    var extendedStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                                    SetWindowLong(hWnd, GWL_EXSTYLE, (extendedStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
                                }
                                else if (!_isEnabled && _protectedWindows.Contains(hWnd))
                                {
                                    SetWindowDisplayAffinity(hWnd, WDA_NONE);
                                    _protectedWindows.Remove(hWnd);
                                    var extendedStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                                    SetWindowLong(hWnd, GWL_EXSTYLE, (extendedStyle | WS_EX_APPWINDOW) & ~WS_EX_TOOLWINDOW);
                                }
                            }
                        }
                    }
                }
                catch { }
                return true;
            }, nint.Zero);
        }
        #endregion

        #region Event Monitoring
        private static void AttachEvents()
        {
            if (_eventsAttached) return;
            Application.Current.Activated -= OnAppActivated;
            Application.Current.Activated += OnAppActivated;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
            EventManager.RegisterClassHandler(typeof(UserControl), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnUserControlLoaded));
            StartPopupMonitoring();
            _eventsAttached = true;
        }

        private static void DetachEvents()
        {
            if (!_eventsAttached) return;
            Application.Current.Activated -= OnAppActivated;
            StopPopupMonitoring();
            _eventsAttached = false;
        }

        private static void StartPopupMonitoring()
        {
            if (_popupMonitorTimer != null) return;
            _popupMonitorTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _popupMonitorTimer.Tick += (s, e) => ProtectAllProcessWindows();
            _popupMonitorTimer.Start();
        }

        private static void StopPopupMonitoring()
        {
            _popupMonitorTimer?.Stop();
            _popupMonitorTimer = null;
        }


        private static void OnAppActivated(object? sender, EventArgs e)
        {
            if (!_isEnabled) return;
            CheckAndProtectNewWindows();
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (!_isEnabled) return;
            if (sender is Window window) ApplyToWindow(window, true);
        }

        private static void OnUserControlLoaded(object sender, RoutedEventArgs e)
        {
            if (!_isEnabled) return;
            if (sender is UserControl userControl) ApplyToUserControl(userControl, true);
        }
        #endregion

        #region Helper Methods
        private static void CheckAllUserControls()
        {
            foreach (Window window in Application.Current.Windows)
            {
                CheckUserControlsInWindow(window);
            }
        }

        private static void CheckUserControlsInWindow(DependencyObject? parent)
        {
            if (parent == null) return;
            if (parent is UserControl userControl) ApplyToUserControl(userControl, true);
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                CheckUserControlsInWindow(child);
            }
        }


        private static void CheckAndProtectNewWindows()
        {
            foreach (Window window in Application.Current.Windows)
            {
                var hWnd = new WindowInteropHelper(window).Handle;
                if (hWnd != nint.Zero && !_protectedWindows.Contains(hWnd))
                {
                    ApplyToWindow(window, true);
                }
            }
            CheckAllUserControls();
            ProtectAllProcessWindows();
        }
        #endregion

        #region Public API
        public static void ApplyStreamGuardToAllWindows(bool enable)
        {
            _isEnabled = enable;
            foreach (Window window in Application.Current.Windows)
                ApplyToWindow(window, enable);
            if (enable)
            {
                CheckAllUserControls();
                ProtectAllProcessWindows();
                AttachEvents();
                StartScreenRecorderMonitoring();
            }
            else
            {
                ProtectAllProcessWindows();
                DetachEvents();
                StopScreenRecorderMonitoring();
                foreach (var hWnd in _protectedWindows.ToArray())
                {
                    RemoveTrayIcon(hWnd);
                }
                _protectedWindows.Clear();
            }
        }

        public static void ForceProtectAllContent()
        {
            if (!_isEnabled) return;
            foreach (Window window in Application.Current.Windows)
            {
                ApplyToWindow(window, true);
                CheckUserControlsInWindow(window);
            }
            ProtectAllProcessWindows();
        }

        public static void ProtectComboBoxPopups()
        {
            if (!_isEnabled) return;
            ProtectAllProcessWindows();
        }

        public static void ProtectWindow(Window window)
        {
            if (_isEnabled) ApplyToWindow(window, true);
        }

        public static void ProtectUserControl(UserControl userControl)
        {
            if (_isEnabled) ApplyToUserControl(userControl, true);
        }

        public static void ProtectOverlayWindows()
        {
            if (!_isEnabled) return;
            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (Window window in Application.Current.Windows)
                {
                    string windowType = window.GetType().Name;
                    if (windowType == "FOV" || windowType.Contains("DetectedPlayerWindow") ||
                        window.Title.Contains("FOV") || window.Title.Contains("Overlay"))
                    {
                        ApplyToWindow(window, true);
                    }
                }
            });
        }
        #endregion

        #region System Tray Management
        private static void AddTrayIcon(Window window, nint hWnd)
        {
            try
            {
                if (_trayIconCreated && _mainApplicationHandle != nint.Zero)
                {
                    if (hWnd != _mainApplicationHandle) return;
                }
                else
                {
                    _mainApplicationHandle = hWnd;
                }
                NOTIFYICONDATA iconData = new NOTIFYICONDATA();
                iconData.cbSize = (uint)Marshal.SizeOf(iconData);
                iconData.hWnd = _mainApplicationHandle;
                iconData.uID = 1;
                iconData.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
                iconData.uCallbackMessage = WM_USER + 1;
                iconData.hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512);
                iconData.szTip = "CouldBeSoapimane";
                Shell_NotifyIcon(_trayIconCreated ? NIM_MODIFY : NIM_ADD, ref iconData);
                _trayIconCreated = true;
                if (!_eventsAttached)
                {
                    HwndSource source = HwndSource.FromHwnd(_mainApplicationHandle);
                    if (source != null)
                    {
                        source.AddHook(WndProc);
                        _eventsAttached = true;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to add tray icon: {ex.Message}");
            }
        }

        private static void RemoveTrayIcon(nint hWnd)
        {
            try
            {
                if (_trayIconCreated && _mainApplicationHandle == hWnd)
                {
                    NOTIFYICONDATA iconData = new NOTIFYICONDATA();
                    iconData.cbSize = (uint)Marshal.SizeOf(iconData);
                    iconData.hWnd = _mainApplicationHandle;
                    iconData.uID = 1;
                    iconData.uFlags = 0;
                    Shell_NotifyIcon(NIM_DELETE, ref iconData);
                    _trayIconCreated = false;
                    _mainApplicationHandle = nint.Zero;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to remove tray icon: {ex.Message}");
            }
        }

        private static nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
        {
            if (hwnd != _mainApplicationHandle) return nint.Zero;
            if (msg == (WM_USER + 1))
            {
                switch ((uint)lParam)
                {
                    case WM_LBUTTONDOWN:
                        RestoreAllWindowsFromTray();
                        handled = true;
                        break;
                    case WM_RBUTTONDOWN:
                        ShowTrayContextMenu(hwnd);
                        handled = true;
                        break;
                }
            }
            return nint.Zero;
        }

        private static void RestoreAllWindowsFromTray()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (Window window in Application.Current.Windows)
                {
                    if (window.GetType().Name == "FOV" || window.GetType().Name.Contains("DetectedPlayerWindow") ||
                        window.Title.Contains("FOV") || window.Title.Contains("DetectedPlayerWindow") || window.Title.Contains("Overlay"))
                    {
                        continue;
                    }
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    window.Show();
                    window.Activate();
                    window.Topmost = true;
                    window.Topmost = false;
                }
            });
        }

        private static void ShowTrayContextMenu(nint hWnd)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var menu = new ContextMenu
                {
                    Background = System.Windows.Media.Brushes.Black,
                    BorderBrush = System.Windows.Media.Brushes.Gray,
                    BorderThickness = new System.Windows.Thickness(1),
                    Padding = new System.Windows.Thickness(0),
                    MinWidth = 100,
                    StaysOpen = false
                };
                var titleItem = new System.Windows.Controls.MenuItem
                {
                    Header = "StreamGuard",
                    Foreground = System.Windows.Media.Brushes.LightGray,
                    Background = System.Windows.Media.Brushes.Transparent,
                    FontSize = 10,
                    FontWeight = System.Windows.FontWeights.SemiBold,
                    Height = 24,
                    Padding = new System.Windows.Thickness(8, 4, 8, 4),
                    BorderThickness = new System.Windows.Thickness(0),
                    IsEnabled = false
                };
                menu.Items.Add(titleItem);
                var titleSeparator = new System.Windows.Controls.Separator { Height = 1, Background = System.Windows.Media.Brushes.Gray, Margin = new System.Windows.Thickness(0) };
                menu.Items.Add(titleSeparator);
                var reopenItem = new System.Windows.Controls.MenuItem
                {
                    Header = "↻ Reopen",
                    Foreground = System.Windows.Media.Brushes.White,
                    Background = System.Windows.Media.Brushes.Transparent,
                    FontSize = 11,
                    Height = 28,
                    Padding = new System.Windows.Thickness(8, 0, 8, 0),
                    BorderThickness = new System.Windows.Thickness(0)
                };
                var exitItem = new System.Windows.Controls.MenuItem
                {
                    Header = "✕ Exit",
                    Foreground = System.Windows.Media.Brushes.White,
                    Background = System.Windows.Media.Brushes.Transparent,
                    FontSize = 11,
                    Height = 28,
                    Padding = new System.Windows.Thickness(8, 0, 8, 0),
                    BorderThickness = new System.Windows.Thickness(0)
                };
                reopenItem.Click += (s, e) => { menu.IsOpen = false; RestoreAllWindowsFromTray(); };
                exitItem.Click += (s, e) => { menu.IsOpen = false; Application.Current.Dispatcher.BeginInvoke((Action)(() => Application.Current.Shutdown()), System.Windows.Threading.DispatcherPriority.Background); };
                menu.Items.Add(reopenItem);
                var separator = new System.Windows.Controls.Separator { Height = 1, Background = System.Windows.Media.Brushes.Gray, Margin = new System.Windows.Thickness(0, 1, 0, 1) };
                menu.Items.Add(separator);
                menu.Items.Add(exitItem);
                Window? mainWindow = null;
                foreach (Window window in Application.Current.Windows)
                {
                    var windowHandle = new WindowInteropHelper(window).Handle;
                    if (windowHandle == _mainApplicationHandle) { mainWindow = window; break; }
                }
                if (mainWindow != null)
                {
                    menu.PlacementTarget = mainWindow;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                    menu.IsOpen = true;
                }
                else
                {
                    // Fallback: close menu if no main window found
                    menu.IsOpen = false;
                }

            });
        }

        #endregion
    }
}
