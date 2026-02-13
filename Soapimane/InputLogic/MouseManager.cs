using Soapimane.Class;
using Soapimane.MouseMovementLibraries.GHubSupport;
using Class;
using MouseMovementLibraries.ddxoftSupport;
using MouseMovementLibraries.RazerSupport;
using MouseMovementLibraries.SendInputSupport;
using Soapimane.AntiDetection;
using System.Drawing;
using System.Runtime.InteropServices;

namespace InputLogic
{
    internal class MouseManager
    {
        private static readonly double ScreenWidth = WinAPICaller.ScreenWidth;
        private static readonly double ScreenHeight = WinAPICaller.ScreenHeight;

        private static DateTime LastClickTime = DateTime.MinValue;
        private static bool isSpraying = false;

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private static double previousX = 0;
        private static double previousY = 0;
        public static double smoothingFactor = 0.5;
        public static bool IsEMASmoothingEnabled = false;

        // Anti-detection: Syscall support
        private static bool _syscallsInitialized = false;
        private static readonly Random _antiDetectionRandom = new();

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);

        private static Random MouseRandom = new();

        /// <summary>
        /// Initializes syscall support for anti-detection
        /// </summary>
        public static void Initialize()
        {
            _syscallsInitialized = SyscallInvoker.InitializeSyscalls();
        }


        private static double EmaSmoothing(double previousValue, double currentValue, double smoothingFactor) => (currentValue * smoothingFactor) + (previousValue * (1 - smoothingFactor));

        // Cleanup
        private static (Action down, Action up) GetMouseActions()
        {
            string mouseMovementMethod = Dictionary.dropdownState["Mouse Movement Method"];
            Action mouseDownAction;
            Action mouseUpAction;

            switch (mouseMovementMethod)
            {
                case "SendInput":
                    // Anti-detection: Use syscalls when available
                    if (_syscallsInitialized)
                    {
                        mouseDownAction = () => SyscallInvoker.SafeSendMouseInput(0, 0, SyscallInvoker.MOUSEEVENTF_LEFTDOWN);
                        mouseUpAction = () => SyscallInvoker.SafeSendMouseInput(0, 0, SyscallInvoker.MOUSEEVENTF_LEFTUP);
                    }
                    else
                    {
                        mouseDownAction = () => SendInputMouse.SendMouseCommand(MOUSEEVENTF_LEFTDOWN);
                        mouseUpAction = () => SendInputMouse.SendMouseCommand(MOUSEEVENTF_LEFTUP);
                    }
                    break;
                case "LG HUB":
                    mouseDownAction = () => LGMouse.Move(1, 0, 0, 0);
                    mouseUpAction = () => LGMouse.Move(0, 0, 0, 0);
                    break;
                case "Razer Synapse (Require Razer Peripheral)":
                    mouseDownAction = () => RZMouse.mouse_click(1);
                    mouseUpAction = () => RZMouse.mouse_click(0);
                    break;
                case "ddxoft Virtual Input Driver":
                    mouseDownAction = () => DdxoftMain.ddxoftInstance.btn?.Invoke(1);
                    mouseUpAction = () => DdxoftMain.ddxoftInstance.btn?.Invoke(2);
                    break;


                default:
                    // Anti-detection: Use syscalls when available
                    if (_syscallsInitialized)
                    {
                        mouseDownAction = () => SyscallInvoker.SafeSendMouseInput(0, 0, SyscallInvoker.MOUSEEVENTF_LEFTDOWN);
                        mouseUpAction = () => SyscallInvoker.SafeSendMouseInput(0, 0, SyscallInvoker.MOUSEEVENTF_LEFTUP);
                    }
                    else
                    {
                        mouseDownAction = () => mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                        mouseUpAction = () => mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                    }
                    break;
            }

            return (mouseDownAction, mouseUpAction);
        }


        public static async Task DoTriggerClick(RectangleF? detectionBox = null)
        {
            // there was a toggle for this, but i realized if it was off, it would never stop spraying. - T
            if (!(InputBindingManager.IsHoldingBinding("Aim Keybind") || InputBindingManager.IsHoldingBinding("Second Aim Keybind")))
            {
                ResetSprayState();
                return;
            }


            if (Dictionary.toggleState["Spray Mode"])
            {
                if (Dictionary.toggleState["Cursor Check"])
                {
                    Point mousePos = WinAPICaller.GetCursorPosition();

                    if (detectionBox.HasValue && !detectionBox.Value.Contains(mousePos.X, mousePos.Y))
                    {
                        if (isSpraying) ReleaseMouseButton();
                        return;
                    }
                    else if (!detectionBox.HasValue)
                    {
                        // No detection box available, can't perform cursor check
                        if (isSpraying) ReleaseMouseButton();
                        return;
                    }
                }


                if (!isSpraying) HoldMouseButton();
                return;
            }

            // Single click logic if spray mode off
            int timeSinceLastClick = (int)(DateTime.UtcNow - LastClickTime).TotalMilliseconds;
            int triggerDelayMilliseconds = (int)(Dictionary.sliderSettings["Auto Trigger Delay"] * 1000);
            const int clickDelayMilliseconds = 20;

            if (timeSinceLastClick < triggerDelayMilliseconds && LastClickTime != DateTime.MinValue)
            {
                return;
            }

            var (mouseDown, mouseUp) = GetMouseActions();

            mouseDown.Invoke();
            await Task.Delay(clickDelayMilliseconds);
            mouseUp.Invoke();

            LastClickTime = DateTime.UtcNow;
        }

        #region Spray Mode Methods
        public static void HoldMouseButton()
        {
            if (isSpraying) return;

            var (mouseDown, _) = GetMouseActions();
            mouseDown.Invoke();
            isSpraying = true;
        }

        public static void ReleaseMouseButton()
        {
            if (!isSpraying) return;

            var (_, mouseUp) = GetMouseActions();
            mouseUp.Invoke();
            isSpraying = false;
        }

        public static void ResetSprayState()
        {
            if (isSpraying)
            {
                ReleaseMouseButton();
            }
        }
        #endregion

        /// <summary>
        /// Applies anti-detection randomization to detection box position
        /// </summary>
        private static (int x, int y) ApplyDetectionBoxRandomization(int x, int y)
        {
            // Add subtle randomization to detection box position (±2 pixels)
            // This prevents consistent detection patterns
            int randomOffsetX = _antiDetectionRandom.Next(-2, 3);
            int randomOffsetY = _antiDetectionRandom.Next(-2, 3);
            return (x + randomOffsetX, y + randomOffsetY);
        }

        public static void MoveCrosshair(int detectedX, int detectedY)
        {
            // Anti-detection: Apply detection box randomization
            var (randomizedX, randomizedY) = ApplyDetectionBoxRandomization(detectedX, detectedY);

            int halfScreenWidth = (int)ScreenWidth / 2;
            int halfScreenHeight = (int)ScreenHeight / 2;

            int targetX = randomizedX - halfScreenWidth;
            int targetY = randomizedY - halfScreenHeight;

            double aspectRatioCorrection = ScreenWidth / ScreenHeight;

            int MouseJitter = (int)Dictionary.sliderSettings["Mouse Jitter"];
            int jitterX = MouseRandom.Next(-MouseJitter, MouseJitter);
            int jitterY = MouseRandom.Next(-MouseJitter, MouseJitter);

            Point start = new(0, 0);
            Point end = new(targetX, targetY);
            Point newPosition = new Point(0, 0);

            switch (Dictionary.dropdownState["Movement Path"])
            {
                case "Cubic Bezier":
                    Point control1 = new Point(start.X + (end.X - start.X) / 3, start.Y + (end.Y - start.Y) / 3);
                    Point control2 = new Point(start.X + 2 * (end.X - start.X) / 3, start.Y + 2 * (end.Y - start.Y) / 3);
                    newPosition = MovementPaths.CubicBezier(start, end, control1, control2, 1 - Dictionary.sliderSettings["Mouse Sensitivity (+/-)"]);
                    break;
                case "Linear":
                    newPosition = MovementPaths.Lerp(start, end, 1 - Dictionary.sliderSettings["Mouse Sensitivity (+/-)"]);
                    break;
                case "Exponential":
                    newPosition = MovementPaths.Exponential(start, end, 1 - (Dictionary.sliderSettings["Mouse Sensitivity (+/-)"] - 0.2), 3.0);
                    break;
                case "Adaptive":
                    newPosition = MovementPaths.Adaptive(start, end, 1 - Dictionary.sliderSettings["Mouse Sensitivity (+/-)"]);
                    break;
                case "Perlin Noise":
                    newPosition = MovementPaths.PerlinNoise(start, end, 1 - Dictionary.sliderSettings["Mouse Sensitivity (+/-)"], 20, 0.5);
                    break;
                default:
                    newPosition = MovementPaths.Lerp(start, end, 1 - Dictionary.sliderSettings["Mouse Sensitivity (+/-)"]);
                    break;
            }

            if (IsEMASmoothingEnabled)
            {
                newPosition.X = (int)EmaSmoothing(previousX, newPosition.X, smoothingFactor);
                newPosition.Y = (int)EmaSmoothing(previousY, newPosition.Y, smoothingFactor);
            }

            newPosition.X = Math.Clamp(newPosition.X, -150, 150);
            newPosition.Y = Math.Clamp(newPosition.Y, -150, 150);

            newPosition.Y = (int)(newPosition.Y / aspectRatioCorrection);

            newPosition.X += jitterX;
            newPosition.Y += jitterY;

            string mouseMethod = Dictionary.dropdownState["Mouse Movement Method"];
            
            // Anti-detection: Use syscalls for SendInput and default methods when available
            if (_syscallsInitialized && (mouseMethod == "SendInput" || mouseMethod == "Mouse Event"))
            {
                SyscallInvoker.SafeSendMouseInput(newPosition.X, newPosition.Y, SyscallInvoker.MOUSEEVENTF_MOVE);
            }
            else
            {
                switch (mouseMethod)
                {
                    case "SendInput":
                        SendInputMouse.SendMouseCommand(MOUSEEVENTF_MOVE, newPosition.X, newPosition.Y);
                        break;

                    case "LG HUB":
                        LGMouse.Move(0, newPosition.X, newPosition.Y, 0);
                        break;

                    case "Razer Synapse (Require Razer Peripheral)":
                        RZMouse.mouse_move(newPosition.X, newPosition.Y, true);
                        break;

                    case "ddxoft Virtual Input Driver":
                        DdxoftMain.ddxoftInstance.movR?.Invoke(newPosition.X, newPosition.Y);
                        break;



                    default:
                        mouse_event(MOUSEEVENTF_MOVE, (uint)newPosition.X, (uint)newPosition.Y, 0, 0);
                        break;
                }
            }

            previousX = newPosition.X;
            previousY = newPosition.Y;

            if (!Dictionary.toggleState["Auto Trigger"])
            {
                ResetSprayState();
            }
        }

    }
}
