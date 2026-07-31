# windows-unlock-animation

Windows 解锁后的多屏全屏过场：主屏播放动画，侧屏保持纯黑，并尽量避免桌面与任务栏提前闪现。

A multi-monitor animation for Windows unlock: play it on the primary display, keep secondary displays black, and minimize desktop flash.

> 这是身份验证完成后的桌面过场，不会替换 Windows 登录界面。
>
> This is a post-authentication desktop transition; it does not replace the Windows sign-in screen.

## 工作方式 / How it works

常驻程序随用户登录启动。锁屏时预置黑色遮罩，解锁后在主屏播放视频，结束后恢复全部显示器。一次性解锁任务作为后备。

The resident starts at user logon. It pre-stages black covers when Windows locks, plays a video on the primary display after unlock, then restores every display. A one-shot unlock task acts as fallback.

## 要求 / Requirements

- Windows 10/11 x64
- Windows PowerShell 5.1
- .NET Framework 4.x
- FFmpeg with `ffplay.exe`
- 自备 MP4 视频 / Your own MP4 video

## 准备视频 / Prepare the video

视频素材不包含在仓库中。将视频放到项目根目录并命名为：

Media is not included. Place your video in the project root as:

```text
unlock.mp4
```

### 分辨率与画面比例 / Resolution and aspect ratio

视频不必与显示器使用相同分辨率。启动器会让 FFplay 覆盖主屏，FFplay 在运行时保持视频比例并缩放到最大，空余区域显示为黑色；原视频文件不会被修改。

The video does not need to match the display resolution. The launcher makes FFplay cover the primary display; FFplay preserves the video aspect ratio, fits it at runtime, and fills unused space with black. The source file is not modified.

| 输入视频 / Input | 在 3840×2160 主屏上的结果 / Result on a 3840×2160 display |
| --- | --- |
| `3840×2160` | 原尺寸铺满 / Fills the display at native size |
| `1920×1080` | 等比放大并铺满 / Upscales proportionally to fill |
| `3440×3440` | 居中显示为 `2160×2160`，左右各 `840 px` 黑边 / Centers at `2160×2160` with `840 px` black bars on each side |
| 竖屏视频 / Portrait video | 按高度适配，左右补黑 / Fits by height with black side bars |

指定分辨率不是硬性要求。为获得最佳清晰度，建议使用与主屏相同的分辨率和比例。若视频背景不是纯黑，运行时黑边可能存在色差，此时可预先补边。动画最好以黑帧开场，并在结尾保留约 0.5 秒静止画面。

An exact resolution is not required. For the sharpest result, use the primary display's resolution and aspect ratio. If the video background is not pure black, runtime bars may differ slightly; pre-padding avoids that. Starting on black and holding the final frame for about 0.5 seconds is recommended.

## 安装 / Install

确认 FFplay 可用 / Confirm FFplay is available:

```powershell
ffplay -version
```

然后运行 / Then run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build-launcher.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install-unlock-task.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install-resident-task.ps1
```

常驻程序会立即启动，并在以后每次用户登录时自动运行。按 `Win+L` 后重新解锁即可测试。

The resident starts immediately and automatically at every future user logon. Press `Win+L`, then unlock to test it.

如果 FFplay 不在 `PATH`，可将 `ffplay.exe` 放在启动器旁边，或把完整路径写入用户环境变量 `WINDOWS_UNLOCK_FFPLAY`。

If FFplay is not on `PATH`, place `ffplay.exe` beside the launcher or set the `WINDOWS_UNLOCK_FFPLAY` user environment variable to its full path.

## 验证 / Validate

以下命令只检查显示器、视频和 FFplay，不播放动画：

These commands check the displays, video, and FFplay without playing the animation:

```powershell
.\UnlockAnimationLauncher.exe --validate
.\UnlockAnimationResident.exe --validate
```

会话记录保存在 `unlock-session.log`，播放错误保存在 `unlock-error.log`。日志、编译产物和媒体文件均被 Git 忽略。

Session events are written to `unlock-session.log`; playback errors go to `unlock-error.log`. Logs, build outputs, and media files are ignored by Git.

## 卸载 / Uninstall

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install-resident-task.ps1 -Remove
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install-unlock-task.ps1 -Remove
```

以上命令只移除计划任务，不删除项目文件。

These commands remove only the scheduled tasks, not the project files.
