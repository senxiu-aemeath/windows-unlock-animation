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

建议按主屏分辨率等比缩放并补黑边，以黑帧开场，并在结尾保留约 0.5 秒静止画面。

Scale to the primary display without stretching, pad with black, begin on a black frame, and hold the final frame for about 0.5 seconds.

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
