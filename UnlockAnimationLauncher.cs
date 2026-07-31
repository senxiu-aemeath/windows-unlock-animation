using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WindowsUnlockAnimation
{
    internal static class Program
    {
        private const int CurrentDisplaySettings = -1;
        private const int MaximumPlaybackSeconds = 15;
        private const string MutexName = "Local\\WindowsUnlockAnimation";
        private static readonly IntPtr HwndTopmost = new IntPtr(-1);
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpFrameChanged = 0x0020;
        private const uint SwpShowWindow = 0x0040;
        private const uint SwpNoOwnerZOrder = 0x0200;

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

        private sealed class DisplayTarget
        {
            public Screen Screen;
            public DevMode Mode;
            public string Video;
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

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            int exitCode = 0;
            bool validateOnly = HasCommandLineArgument("--validate");

            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    return;
                }

                List<Process> players = new List<Process>();
                List<DisplayTarget> launchedTargets =
                    new List<DisplayTarget>();
                List<Form> primaryCurtains = new List<Form>();
                List<Form> sideCurtains = new List<Form>();
                bool cursorHidden = false;

                try
                {
                    string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    string centerVideo = Path.Combine(
                        baseDirectory,
                        "unlock.mp4");
                    string leftVideo = Path.Combine(
                        baseDirectory,
                        "unlock-left.mp4");
                    string rightVideo = Path.Combine(
                        baseDirectory,
                        "unlock-right.mp4");

                    RequireFile(centerVideo);
                    string ffplay = FindFfplay();
                    Screen[] screens = Screen.AllScreens;
                    if (screens.Length == 0)
                    {
                        throw new InvalidOperationException(
                            "Windows did not report any active displays.");
                    }

                    List<DisplayTarget> targets = BuildTargets(
                        screens,
                        centerVideo,
                        leftVideo,
                        rightVideo);

                    // Only the primary display plays video. Side displays stay
                    // under their black topmost curtains for the same duration.
                    targets.RemoveAll(delegate(DisplayTarget target)
                    {
                        return !target.Screen.Primary;
                    });

                    if (validateOnly)
                    {
                        return;
                    }

                    foreach (Screen screen in screens)
                    {
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
                        if (screen.Primary)
                        {
                            primaryCurtains.Add(curtain);
                        }
                        else
                        {
                            sideCurtains.Add(curtain);
                        }
                    }

                    Cursor.Hide();
                    cursorHidden = true;
                    Application.DoEvents();

                    // False sorts before true: side displays start first and the
                    // primary display starts last. The clips begin with black.
                    targets.Sort(delegate(DisplayTarget first, DisplayTarget second)
                    {
                        return first.Screen.Primary.CompareTo(second.Screen.Primary);
                    });

                    foreach (DisplayTarget target in targets)
                    {
                        players.Add(StartPlayer(ffplay, target));
                        launchedTargets.Add(target);
                    }

                    Stopwatch stopwatch = Stopwatch.StartNew();
                    WaitForPlayerWindows(players, stopwatch);
                    PlacePlayerWindows(players, launchedTargets);

                    // The curtains only mask player startup. Once every FFplay
                    // window exists, close them so they cannot sit above the
                    // equally topmost playback windows in the z-order.
                    Thread.Sleep(100);
                    CloseCurtains(primaryCurtains);

                    bool stillPlaying;
                    do
                    {
                        stillPlaying = false;
                        foreach (Process player in players)
                        {
                            if (!player.HasExited)
                            {
                                stillPlaying = true;
                                break;
                            }
                        }

                        Application.DoEvents();
                        if (stillPlaying)
                        {
                            ReassertTopmost(players, sideCurtains);

                            if (stopwatch.Elapsed.TotalSeconds >=
                                MaximumPlaybackSeconds)
                            {
                                throw new TimeoutException(
                                    "Unlock playback exceeded " +
                                    MaximumPlaybackSeconds + " seconds.");
                            }

                            Thread.Sleep(15);
                        }
                    }
                    while (stillPlaying);

                    foreach (Process player in players)
                    {
                        if (player.ExitCode != 0)
                        {
                            throw new InvalidOperationException(
                                "FFplay exited with code " + player.ExitCode + ".");
                        }
                    }
                }
                catch (Exception exception)
                {
                    exitCode = 1;
                    WriteErrorLog(exception);
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

                    CloseCurtains(primaryCurtains);
                    CloseCurtains(sideCurtains);

                    if (cursorHidden)
                    {
                        Cursor.Show();
                    }

                    mutex.ReleaseMutex();
                }
            }

            Environment.ExitCode = exitCode;
        }

        private static void WaitForPlayerWindows(
            List<Process> players,
            Stopwatch playbackStopwatch)
        {
            const int windowStartupTimeoutMilliseconds = 3000;
            Stopwatch startupStopwatch = Stopwatch.StartNew();

            while (true)
            {
                bool allWindowsReady = true;
                foreach (Process player in players)
                {
                    if (player.HasExited)
                    {
                        throw new InvalidOperationException(
                            "FFplay exited before creating its window (code " +
                            player.ExitCode + ").");
                    }

                    player.Refresh();
                    if (player.MainWindowHandle == IntPtr.Zero)
                    {
                        allWindowsReady = false;
                    }
                }

                if (allWindowsReady)
                {
                    return;
                }

                if (startupStopwatch.ElapsedMilliseconds >=
                    windowStartupTimeoutMilliseconds)
                {
                    throw new TimeoutException(
                        "FFplay did not create all display windows within " +
                        windowStartupTimeoutMilliseconds + " ms.");
                }

                if (playbackStopwatch.Elapsed.TotalSeconds >=
                    MaximumPlaybackSeconds)
                {
                    throw new TimeoutException(
                        "Unlock playback exceeded " +
                        MaximumPlaybackSeconds + " seconds during startup.");
                }

                Application.DoEvents();
                Thread.Sleep(10);
            }
        }

        private static void PlacePlayerWindows(
            List<Process> players,
            List<DisplayTarget> targets)
        {
            if (players.Count != targets.Count)
            {
                throw new InvalidOperationException(
                    "Player and display target counts do not match.");
            }

            for (int index = 0; index < players.Count; index++)
            {
                Process player = players[index];
                DisplayTarget target = targets[index];
                player.Refresh();

                bool placed = SetWindowPos(
                    player.MainWindowHandle,
                    HwndTopmost,
                    target.Mode.PositionX,
                    target.Mode.PositionY,
                    target.Mode.PixelWidth,
                    target.Mode.PixelHeight,
                    SwpNoActivate |
                    SwpFrameChanged |
                    SwpShowWindow |
                    SwpNoOwnerZOrder);

                if (!placed)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Unable to place FFplay on " +
                        target.Screen.DeviceName + ".");
                }
            }

            Application.DoEvents();
        }

        private static void ReassertTopmost(
            List<Process> players,
            List<Form> sideCurtains)
        {
            uint flags = SwpNoSize |
                SwpNoMove |
                SwpNoActivate |
                SwpShowWindow |
                SwpNoOwnerZOrder;

            foreach (Process player in players)
            {
                if (player.HasExited)
                {
                    continue;
                }

                player.Refresh();
                IntPtr handle = player.MainWindowHandle;
                if (handle != IntPtr.Zero)
                {
                    SetWindowPos(
                        handle,
                        HwndTopmost,
                        0,
                        0,
                        0,
                        0,
                        flags);
                }
            }

            foreach (Form curtain in sideCurtains)
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

        private static void CloseCurtains(List<Form> curtains)
        {
            foreach (Form curtain in curtains)
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

            curtains.Clear();
        }

        private static bool HasCommandLineArgument(string expected)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            foreach (string argument in arguments)
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

        private static List<DisplayTarget> BuildTargets(
            Screen[] screens,
            string centerVideo,
            string leftVideo,
            string rightVideo)
        {
            List<DisplayTarget> targets = new List<DisplayTarget>();
            DevMode primaryMode = new DevMode();
            bool foundPrimary = false;

            foreach (Screen screen in screens)
            {
                if (screen.Primary)
                {
                    primaryMode = GetCurrentMode(screen.DeviceName);
                    foundPrimary = true;
                    break;
                }
            }

            if (!foundPrimary)
            {
                throw new InvalidOperationException(
                    "Windows did not report a primary display.");
            }

            foreach (Screen screen in screens)
            {
                DevMode mode = GetCurrentMode(screen.DeviceName);
                string video;
                if (screen.Primary)
                {
                    video = centerVideo;
                }
                else if (mode.PositionX < primaryMode.PositionX)
                {
                    video = leftVideo;
                }
                else
                {
                    video = rightVideo;
                }

                DisplayTarget target = new DisplayTarget();
                target.Screen = screen;
                target.Mode = mode;
                target.Video = video;
                targets.Add(target);
            }

            return targets;
        }

        private static DevMode GetCurrentMode(string deviceName)
        {
            DevMode mode = new DevMode();
            mode.Size = (short)Marshal.SizeOf(typeof(DevMode));
            if (!EnumDisplaySettings(deviceName, CurrentDisplaySettings, ref mode))
            {
                throw new InvalidOperationException(
                    "Unable to read the current mode for " + deviceName + ".");
            }

            return mode;
        }

        private static Process StartPlayer(string ffplay, DisplayTarget target)
        {
            string title = "Windows Unlock Animation " +
                target.Screen.DeviceName;
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
                    "-left " + target.Mode.PositionX,
                    "-top " + target.Mode.PositionY,
                    "-x " + target.Mode.PixelWidth,
                    "-y " + target.Mode.PixelHeight,
                    "-window_title " + Quote(title),
                    Quote(target.Video)
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
                throw new InvalidOperationException(
                    "FFplay failed to start for " + target.Screen.DeviceName + ".");
            }

            return player;
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

        private static void WriteErrorLog(Exception exception)
        {
            try
            {
                string logPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "unlock-error.log");
                File.AppendAllText(
                    logPath,
                    DateTime.Now.ToString("o") + " | " + exception +
                    Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
