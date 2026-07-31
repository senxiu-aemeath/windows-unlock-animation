using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WindowsUnlockAnimation.Resident
{
    internal static class Program
    {
        private const string MutexName = "Local\\WindowsUnlockAnimation";

        [STAThread]
        private static void Main()
        {
            if (HasArgument("--validate"))
            {
                Environment.ExitCode = ResidentContext.ValidateEnvironment();
                return;
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    return;
                }

                try
                {
                    using (ResidentContext context = new ResidentContext())
                    {
                        Application.Run(context);
                    }
                }
                catch (Exception exception)
                {
                    ResidentContext.WriteLog("Fatal resident error: " + exception);
                    Environment.ExitCode = 1;
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        private static bool HasArgument(string expected)
        {
            foreach (string argument in Environment.GetCommandLineArgs())
            {
                if (string.Equals(
                    argument,
                    expected,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal sealed class ResidentContext : ApplicationContext, IDisposable
    {
        private const int CurrentDisplaySettings = -1;
        private const int MaximumPlaybackSeconds = 15;
        private const int WtsSessionLock = 0x7;
        private const int WtsSessionUnlock = 0x8;
        private const int WtsSessionLogoff = 0x6;
        private const int WtsSessionDesktopReady = 0xF;

        private static readonly IntPtr HwndTopmost = new IntPtr(-1);
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpFrameChanged = 0x0020;
        private const uint SwpShowWindow = 0x0040;
        private const uint SwpNoOwnerZOrder = 0x0200;

        private readonly SessionMessageWindow sessionWindow;
        private readonly System.Windows.Forms.Timer dispatchTimer;
        private readonly System.Windows.Forms.Timer desktopReadyFallbackTimer;
        private readonly Dictionary<string, Form> curtains =
            new Dictionary<string, Form>(StringComparer.OrdinalIgnoreCase);

        private bool sessionLocked;
        private bool unlockPending;
        private bool playbackRequested;
        private bool playbackRunning;
        private bool disposed;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            public short SpecVersion;
            public short DriverVersion;
            public short Size;
            public short DriverExtra;
            public int Fields;
            public int PositionX;
            public int PositionY;
            public int DisplayOrientation;
            public int DisplayFixedOutput;
            public short Color;
            public short Duplex;
            public short YResolution;
            public short TTOption;
            public short Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string FormName;
            public short LogPixels;
            public int BitsPerPixel;
            public int PixelWidth;
            public int PixelHeight;
            public int DisplayFlags;
            public int DisplayFrequency;
            public int ICMMethod;
            public int ICMIntent;
            public int MediaType;
            public int DitherType;
            public int Reserved1;
            public int Reserved2;
            public int PanningWidth;
            public int PanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        private static extern bool EnumDisplaySettings(
            string deviceName,
            int modeNumber,
            ref DevMode mode);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        public ResidentContext()
        {
            sessionWindow = new SessionMessageWindow(OnSessionChange);

            dispatchTimer = new System.Windows.Forms.Timer();
            dispatchTimer.Interval = 20;
            dispatchTimer.Tick += OnDispatchTick;
            dispatchTimer.Start();

            desktopReadyFallbackTimer = new System.Windows.Forms.Timer();
            desktopReadyFallbackTimer.Interval = 750;
            desktopReadyFallbackTimer.Tick += OnDesktopReadyFallback;

            WriteLog(
                "Resident started in session " +
                Process.GetCurrentProcess().SessionId + ".");
        }

        public static int ValidateEnvironment()
        {
            try
            {
                string video = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "unlock.mp4");
                RequireFile(video);
                FindFfplay();

                Screen[] screens = Screen.AllScreens;
                if (screens.Length == 0)
                {
                    throw new InvalidOperationException(
                        "Windows did not report any active displays.");
                }

                foreach (Screen screen in screens)
                {
                    GetCurrentMode(screen.DeviceName);
                }

                WriteLog("Resident validation passed.");
                return 0;
            }
            catch (Exception exception)
            {
                WriteLog("Resident validation failed: " + exception);
                return 1;
            }
        }

        private void OnSessionChange(int reason, int sessionId)
        {
            if (sessionId != Process.GetCurrentProcess().SessionId)
            {
                return;
            }

            WriteLog("Session event " + reason + ".");

            if (reason == WtsSessionLock)
            {
                sessionLocked = true;
                unlockPending = false;
                playbackRequested = false;
                desktopReadyFallbackTimer.Stop();
                EnsureCurtains();
                WriteLog("Lock curtains prepared.");
            }
            else if (reason == WtsSessionUnlock)
            {
                sessionLocked = false;
                unlockPending = true;
                EnsureCurtains();
                desktopReadyFallbackTimer.Stop();
                desktopReadyFallbackTimer.Start();
            }
            else if (reason == WtsSessionDesktopReady && unlockPending)
            {
                desktopReadyFallbackTimer.Stop();
                playbackRequested = true;
                WriteLog("Desktop ready; playback requested.");
            }
            else if (reason == WtsSessionLogoff)
            {
                ExitThread();
            }
        }

        private void OnDesktopReadyFallback(object sender, EventArgs eventArgs)
        {
            desktopReadyFallbackTimer.Stop();
            if (unlockPending && !playbackRunning)
            {
                playbackRequested = true;
                WriteLog("Desktop-ready fallback timer requested playback.");
            }
        }

        private void OnDispatchTick(object sender, EventArgs eventArgs)
        {
            ReassertCurtains();

            if (playbackRequested && !playbackRunning)
            {
                playbackRequested = false;
                RunPlayback();
            }
        }

        private void RunPlayback()
        {
            playbackRunning = true;
            unlockPending = false;
            desktopReadyFallbackTimer.Stop();

            List<Process> players = new List<Process>();
            bool cursorHidden = false;

            try
            {
                EnsureCurtains();

                string video = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "unlock.mp4");
                RequireFile(video);

                Screen primaryScreen = Screen.PrimaryScreen;
                if (primaryScreen == null)
                {
                    throw new InvalidOperationException(
                        "Windows did not report a primary display.");
                }

                DevMode primaryMode = GetCurrentMode(primaryScreen.DeviceName);
                string ffplay = FindFfplay();

                Cursor.Hide();
                cursorHidden = true;

                Process player = StartPlayer(
                    ffplay,
                    video,
                    primaryScreen,
                    primaryMode);
                players.Add(player);

                Stopwatch stopwatch = Stopwatch.StartNew();
                WaitForPlayerWindow(player, stopwatch);
                PlacePlayerWindow(player, primaryScreen, primaryMode);
                Thread.Sleep(100);
                ClosePrimaryCurtain();
                WriteLog(
                    "Primary player ready after " +
                    stopwatch.ElapsedMilliseconds + " ms.");

                while (!player.HasExited)
                {
                    Application.DoEvents();
                    ReassertPlayer(player);
                    ReassertCurtains();

                    if (stopwatch.Elapsed.TotalSeconds >=
                        MaximumPlaybackSeconds)
                    {
                        throw new TimeoutException(
                            "Unlock playback exceeded " +
                            MaximumPlaybackSeconds + " seconds.");
                    }

                    Thread.Sleep(15);
                }

                if (player.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        "FFplay exited with code " + player.ExitCode + ".");
                }

                WriteLog("Playback completed successfully.");
            }
            catch (Exception exception)
            {
                WriteLog("Playback error: " + exception);
            }
            finally
            {
                foreach (Process player in players)
                {
                    try
                    {
                        if (!player.HasExited)
                        {
                            player.Kill();
                            player.WaitForExit(1000);
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        player.Dispose();
                    }
                }

                if (cursorHidden)
                {
                    Cursor.Show();
                }

                CloseAllCurtains();
                if (sessionLocked)
                {
                    EnsureCurtains();
                }

                playbackRunning = false;
            }
        }

        private void EnsureCurtains()
        {
            Screen[] screens = Screen.AllScreens;
            HashSet<string> activeDevices =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Screen screen in screens)
            {
                activeDevices.Add(screen.DeviceName);
                Form existing;
                if (curtains.TryGetValue(screen.DeviceName, out existing) &&
                    !existing.IsDisposed)
                {
                    continue;
                }

                Form curtain = new Form();
                curtain.AutoScaleMode = AutoScaleMode.None;
                curtain.BackColor = Color.Black;
                curtain.FormBorderStyle = FormBorderStyle.None;
                curtain.ShowInTaskbar = false;
                curtain.StartPosition = FormStartPosition.Manual;
                curtain.Bounds = screen.Bounds;
                curtain.TopMost = true;
                curtain.Show();
                curtain.BringToFront();
                curtains[screen.DeviceName] = curtain;
            }

            List<string> staleDevices = new List<string>();
            foreach (KeyValuePair<string, Form> pair in curtains)
            {
                if (!activeDevices.Contains(pair.Key))
                {
                    CloseCurtain(pair.Value);
                    staleDevices.Add(pair.Key);
                }
            }

            foreach (string device in staleDevices)
            {
                curtains.Remove(device);
            }

            Application.DoEvents();
            ReassertCurtains();
        }

        private void ClosePrimaryCurtain()
        {
            Screen primary = Screen.PrimaryScreen;
            if (primary == null)
            {
                return;
            }

            Form curtain;
            if (curtains.TryGetValue(primary.DeviceName, out curtain))
            {
                CloseCurtain(curtain);
                curtains.Remove(primary.DeviceName);
            }
        }

        private void CloseAllCurtains()
        {
            foreach (Form curtain in curtains.Values)
            {
                CloseCurtain(curtain);
            }
            curtains.Clear();
        }

        private static void CloseCurtain(Form curtain)
        {
            try
            {
                curtain.Close();
                curtain.Dispose();
            }
            catch
            {
            }
        }

        private void ReassertCurtains()
        {
            uint flags = SwpNoSize |
                SwpNoMove |
                SwpNoActivate |
                SwpShowWindow |
                SwpNoOwnerZOrder;

            foreach (Form curtain in curtains.Values)
            {
                if (!curtain.IsDisposed && curtain.IsHandleCreated)
                {
                    SetWindowPos(
                        curtain.Handle,
                        HwndTopmost,
                        0,
                        0,
                        0,
                        0,
                        flags);
                }
            }
        }

        private static void ReassertPlayer(Process player)
        {
            if (player.HasExited)
            {
                return;
            }

            player.Refresh();
            if (player.MainWindowHandle == IntPtr.Zero)
            {
                return;
            }

            SetWindowPos(
                player.MainWindowHandle,
                HwndTopmost,
                0,
                0,
                0,
                0,
                SwpNoSize |
                SwpNoMove |
                SwpNoActivate |
                SwpShowWindow |
                SwpNoOwnerZOrder);
        }

        private static Process StartPlayer(
            string ffplay,
            string video,
            Screen screen,
            DevMode mode)
        {
            string arguments = string.Join(
                " ",
                new string[]
                {
                    "-hide_banner",
                    "-loglevel quiet",
                    "-nostats",
                    "-an",
                    "-autoexit",
                    "-noborder",
                    "-alwaysontop",
                    "-left " + mode.PositionX,
                    "-top " + mode.PositionY,
                    "-x " + mode.PixelWidth,
                    "-y " + mode.PixelHeight,
                    "-window_title " + Quote(
                        "Windows Unlock Animation " + screen.DeviceName),
                    Quote(video)
                });

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = ffplay;
            startInfo.Arguments = arguments;
            startInfo.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.EnvironmentVariables[
                "SDL_HINT_VIDEO_MINIMIZE_ON_FOCUS_LOSS"] = "0";

            Process player = Process.Start(startInfo);
            if (player == null)
            {
                throw new InvalidOperationException("FFplay failed to start.");
            }
            return player;
        }

        private static void WaitForPlayerWindow(
            Process player,
            Stopwatch playbackStopwatch)
        {
            Stopwatch startupStopwatch = Stopwatch.StartNew();
            while (true)
            {
                if (player.HasExited)
                {
                    throw new InvalidOperationException(
                        "FFplay exited before creating its window (code " +
                        player.ExitCode + ").");
                }

                player.Refresh();
                if (player.MainWindowHandle != IntPtr.Zero)
                {
                    return;
                }

                if (startupStopwatch.ElapsedMilliseconds >= 3000)
                {
                    throw new TimeoutException(
                        "FFplay did not create its window within 3000 ms.");
                }

                if (playbackStopwatch.Elapsed.TotalSeconds >=
                    MaximumPlaybackSeconds)
                {
                    throw new TimeoutException(
                        "Unlock playback timed out during startup.");
                }

                Application.DoEvents();
                Thread.Sleep(10);
            }
        }

        private static void PlacePlayerWindow(
            Process player,
            Screen screen,
            DevMode mode)
        {
            bool placed = SetWindowPos(
                player.MainWindowHandle,
                HwndTopmost,
                mode.PositionX,
                mode.PositionY,
                mode.PixelWidth,
                mode.PixelHeight,
                SwpNoActivate |
                SwpFrameChanged |
                SwpShowWindow |
                SwpNoOwnerZOrder);

            if (!placed)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to place FFplay on " + screen.DeviceName + ".");
            }
        }

        private static DevMode GetCurrentMode(string deviceName)
        {
            DevMode mode = new DevMode();
            mode.Size = (short)Marshal.SizeOf(typeof(DevMode));
            if (!EnumDisplaySettings(
                deviceName,
                CurrentDisplaySettings,
                ref mode))
            {
                throw new InvalidOperationException(
                    "Unable to read the current mode for " + deviceName + ".");
            }
            return mode;
        }

        private static string FindFfplay()
        {
            string configuredFfplay =
                Environment.GetEnvironmentVariable("WINDOWS_UNLOCK_FFPLAY");
            if (!String.IsNullOrWhiteSpace(configuredFfplay))
            {
                configuredFfplay = Environment.ExpandEnvironmentVariables(
                    configuredFfplay.Trim().Trim('"'));
                if (File.Exists(configuredFfplay))
                {
                    return Path.GetFullPath(configuredFfplay);
                }

                throw new FileNotFoundException(
                    "WINDOWS_UNLOCK_FFPLAY does not point to an existing file.",
                    configuredFfplay);
            }

            string localFfplay = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "ffplay.exe");
            if (File.Exists(localFfplay))
            {
                return localFfplay;
            }

            string pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string rawDirectory in pathValue.Split(Path.PathSeparator))
            {
                string directory = rawDirectory.Trim().Trim('"');
                if (directory.Length == 0)
                {
                    continue;
                }

                try
                {
                    string candidate = Path.Combine(directory, "ffplay.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            throw new FileNotFoundException(
                "ffplay.exe was not found. Set WINDOWS_UNLOCK_FFPLAY, place " +
                "ffplay.exe beside the launcher, or add FFmpeg to PATH.");
        }

        private static void RequireFile(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Missing unlock video.", path);
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        public static void WriteLog(string message)
        {
            try
            {
                string logPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "unlock-session.log");
                File.AppendAllText(
                    logPath,
                    DateTime.Now.ToString("o") + " | " + message +
                    Environment.NewLine);
            }
            catch
            {
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposed)
            {
                disposed = true;
                if (disposing)
                {
                    desktopReadyFallbackTimer.Stop();
                    desktopReadyFallbackTimer.Dispose();
                    dispatchTimer.Stop();
                    dispatchTimer.Dispose();
                    sessionWindow.Dispose();
                    CloseAllCurtains();
                }
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class SessionMessageWindow : NativeWindow, IDisposable
    {
        private const int WmWtsSessionChange = 0x02B1;
        private const int NotifyForThisSession = 0;

        private readonly Action<int, int> callback;
        private bool registered;
        private bool disposed;

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSRegisterSessionNotification(
            IntPtr window,
            int flags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSUnRegisterSessionNotification(
            IntPtr window);

        public SessionMessageWindow(Action<int, int> callback)
        {
            this.callback = callback;

            CreateParams parameters = new CreateParams();
            parameters.Caption = "Windows Unlock Animation Session Listener";
            parameters.X = -32000;
            parameters.Y = -32000;
            parameters.Width = 1;
            parameters.Height = 1;
            CreateHandle(parameters);

            registered = WTSRegisterSessionNotification(
                Handle,
                NotifyForThisSession);
            if (!registered)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to register for WTS session notifications.");
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmWtsSessionChange)
            {
                callback(message.WParam.ToInt32(), message.LParam.ToInt32());
            }
            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (registered)
            {
                WTSUnRegisterSessionNotification(Handle);
                registered = false;
            }
            DestroyHandle();
        }
    }
}
