[CmdletBinding()]
param(
    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$createdNew = $false
$mutex = [System.Threading.Mutex]::new(
    $true,
    'Local\WindowsUnlockAnimation',
    [ref]$createdNew)
if (-not $createdNew) {
    $mutex.Dispose()
    exit 0
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$players = New-Object System.Collections.Generic.List[System.Diagnostics.Process]
$curtains = New-Object System.Collections.Generic.List[System.Windows.Forms.Form]
$primaryCurtains = New-Object System.Collections.Generic.List[System.Windows.Forms.Form]
$cursorHidden = $false
$maximumPlaybackSeconds = 15

try {
    if (-not ('WindowsUnlockAnimation.DisplayModeReader' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace WindowsUnlockAnimation
{
    public static class DisplayModeReader
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct DevMode
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

        public static DevMode GetCurrent(string deviceName)
        {
            DevMode mode = new DevMode();
            mode.Size = (short)Marshal.SizeOf(typeof(DevMode));
            if (!EnumDisplaySettings(deviceName, -1, ref mode))
            {
                throw new InvalidOperationException(
                    "Unable to read the current mode for " + deviceName + ".");
            }

            return mode;
        }

        public static void PlaceWindow(
            IntPtr window,
            int x,
            int y,
            int width,
            int height)
        {
            const uint flags = 0x0010 | 0x0020 | 0x0040 | 0x0200;
            if (!SetWindowPos(
                window,
                new IntPtr(-1),
                x,
                y,
                width,
                height,
                flags))
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to place an FFplay window.");
            }
        }
    }
}
'@
    }

    $ffplayCommand = Get-Command ffplay.exe -ErrorAction Stop
    $ffplay = $ffplayCommand.Source

    $centerVideo = Join-Path $PSScriptRoot 'unlock.mp4'
    $leftVideo = Join-Path $PSScriptRoot 'unlock-left.mp4'
    $rightVideo = Join-Path $PSScriptRoot 'unlock-right.mp4'

    foreach ($video in @($centerVideo)) {
        if (-not (Test-Path -LiteralPath $video -PathType Leaf)) {
            throw "Missing unlock video: $video"
        }
    }

    $screens = @([System.Windows.Forms.Screen]::AllScreens)
    if ($screens.Count -eq 0) {
        throw 'Windows did not report any active displays.'
    }

    $displayModes = foreach ($screen in $screens) {
        $mode = [WindowsUnlockAnimation.DisplayModeReader]::GetCurrent(
            $screen.DeviceName)
        [pscustomobject]@{
            DeviceName = $screen.DeviceName
            IsPrimary  = $screen.Primary
            Logical    = $screen.Bounds
            X          = $mode.PositionX
            Y          = $mode.PositionY
            Width      = $mode.PixelWidth
            Height     = $mode.PixelHeight
        }
    }

    $primary = @($displayModes | Where-Object IsPrimary)[0]
    if ($null -eq $primary) {
        throw 'Windows did not report a primary display.'
    }

    $targets = foreach ($display in $displayModes) {
        $video = if ($display.IsPrimary) {
            $centerVideo
        }
        elseif ($display.X -lt $primary.X) {
            $leftVideo
        }
        else {
            $rightVideo
        }

        [pscustomobject]@{
            Display = $display
            Video   = $video
        }
    }

    if ($ValidateOnly) {
        $targets | ForEach-Object {
            [pscustomobject]@{
                Device     = $_.Display.DeviceName
                Primary    = $_.Display.IsPrimary
                Position   = '{0},{1}' -f $_.Display.X, $_.Display.Y
                Resolution = '{0}x{1}' -f $_.Display.Width, $_.Display.Height
                Video      = Split-Path -Leaf $_.Video
                Player     = $ffplay
            }
        }
        return
    }

    $targets = @($targets | Where-Object { $_.Display.IsPrimary })

    # Put a black, borderless, topmost curtain over every display first. This
    # hides Explorer and the taskbar while the three player windows initialise.
    foreach ($screen in $screens) {
        $form = New-Object System.Windows.Forms.Form
        $form.AutoScaleMode = [System.Windows.Forms.AutoScaleMode]::None
        $form.BackColor = [System.Drawing.Color]::Black
        $form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
        $form.ShowInTaskbar = $false
        $form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
        $form.Bounds = $screen.Bounds
        $form.TopMost = $true
        $form.Show()
        $form.BringToFront()
        if ($screen.Primary) {
            $primaryCurtains.Add($form)
        }
        else {
            $curtains.Add($form)
        }
    }

    [System.Windows.Forms.Cursor]::Hide()
    $cursorHidden = $true
    [System.Windows.Forms.Application]::DoEvents()

    # Launch side displays first and the primary display last. All source clips
    # begin with black frames, concealing the tiny process-start timing offset.
    $launchOrder = @($targets | Sort-Object { $_.Display.IsPrimary })
    foreach ($target in $launchOrder) {
        $display = $target.Display
        $quotedVideo = '"' + $target.Video.Replace('"', '\"') + '"'
        $windowTitle = 'Windows Unlock Animation ' + $display.DeviceName
        $quotedTitle = '"' + $windowTitle.Replace('"', '\"') + '"'

        $arguments = @(
            '-hide_banner'
            '-loglevel quiet'
            '-nostats'
            '-an'
            '-autoexit'
            '-noborder'
            '-alwaysontop'
            "-left $($display.X)"
            "-top $($display.Y)"
            "-x $($display.Width)"
            "-y $($display.Height)"
            "-window_title $quotedTitle"
            $quotedVideo
        ) -join ' '

        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = $ffplay
        $startInfo.Arguments = $arguments
        $startInfo.WorkingDirectory = $PSScriptRoot
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
        $startInfo.EnvironmentVariables['SDL_HINT_VIDEO_MINIMIZE_ON_FOCUS_LOSS'] = '0'

        $player = [System.Diagnostics.Process]::Start($startInfo)
        if ($null -eq $player) {
            throw "FFplay failed to start for $($display.DeviceName)."
        }

        $players.Add($player)
    }

    $playbackDeadline = [DateTime]::UtcNow.AddSeconds($maximumPlaybackSeconds)
    $windowDeadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        $allWindowsReady = $true
        foreach ($player in $players) {
            if ($player.HasExited) {
                throw "FFplay exited before creating its window (code $($player.ExitCode))."
            }

            $player.Refresh()
            if ($player.MainWindowHandle -eq [IntPtr]::Zero) {
                $allWindowsReady = $false
            }
        }

        [System.Windows.Forms.Application]::DoEvents()
        if (-not $allWindowsReady) {
            if ([DateTime]::UtcNow -ge $windowDeadline) {
                throw 'FFplay did not create all display windows within 3 seconds.'
            }
            [System.Threading.Thread]::Sleep(10)
        }
    } while (-not $allWindowsReady)

    for ($index = 0; $index -lt $players.Count; $index++) {
        $display = $launchOrder[$index].Display
        [WindowsUnlockAnimation.DisplayModeReader]::PlaceWindow(
            $players[$index].MainWindowHandle,
            $display.X,
            $display.Y,
            $display.Width,
            $display.Height)
    }

    [System.Threading.Thread]::Sleep(100)
    foreach ($form in $primaryCurtains) {
        $form.Close()
        $form.Dispose()
    }
    $primaryCurtains.Clear()

    do {
        $stillPlaying = $false
        foreach ($player in $players) {
            if (-not $player.HasExited) {
                $stillPlaying = $true
                break
            }
        }

        [System.Windows.Forms.Application]::DoEvents()
        if ($stillPlaying) {
            if ([DateTime]::UtcNow -ge $playbackDeadline) {
                throw "Unlock playback exceeded $maximumPlaybackSeconds seconds."
            }
            [System.Threading.Thread]::Sleep(15)
        }
    } while ($stillPlaying)

    foreach ($player in $players) {
        if ($player.ExitCode -ne 0) {
            throw "FFplay exited with code $($player.ExitCode)."
        }
    }
}
catch {
    $errorRecord = '{0:o} | {1}' -f [DateTime]::Now, $_.Exception.Message
    Add-Content -LiteralPath (Join-Path $PSScriptRoot 'unlock-error.log') -Value $errorRecord
    throw
}
finally {
    foreach ($player in $players) {
        if ($null -ne $player) {
            if (-not $player.HasExited) {
                $player.Kill()
                $player.WaitForExit(1000) | Out-Null
            }
            $player.Dispose()
        }
    }

    foreach ($form in $curtains) {
        $form.Close()
        $form.Dispose()
    }

    foreach ($form in $primaryCurtains) {
        $form.Close()
        $form.Dispose()
    }

    if ($cursorHidden) {
        [System.Windows.Forms.Cursor]::Show()
    }

    if ($createdNew) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}
