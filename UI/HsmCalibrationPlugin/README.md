# HSM Calibration Plugin

## English

`HsmCalibration` is a LabAPI test plugin for checking HintServiceMeow screen coordinates. By default it creates a static rectangular outline with labeled tags on all four screen edges. It can also run the original animated orbit mode for motion stress tests.

Commands:

- `hsmcal start` - show the orbiting markers for the command sender, or all ready players from server console.
- `hsmcal start all` - show the markers for every ready player.
- `hsmcal stop` / `hsmcal stop all` - clear markers.
- `hsmcal status` - show current settings.
- `hsmcal set mode rectangle` - use the static rectangular outline.
- `hsmcal set mode orbit` - use the animated orbiting markers.
- `hsmcal set <horizontal|vertical|leftX|rightX|topY|bottomY|insetX|insetY|labels|markers|radiusX|radiusY|centerX|centerY|orbit|interval|size|coords> <value>` - replace a setting and restart active sessions.
- `hsmcal add <field> <delta>` - nudge a numeric setting.

Rectangle mode defaults to short `T/B/L/R` edge tags with coordinates hidden to avoid overlap. Its default X bounds are `-1780..1780`, matching the observed 1080p centered-position display width. Use `hsmcal set coords 1` when you need coordinate readouts.

Install `HintServiceMeow.dll` first, then place `HsmCalibration.dll` in `LabAPI/plugins/<port>`.

## 中文

`HsmCalibration` 是用于校准 HintServiceMeow 坐标的 LabAPI 测试插件。默认会创建静态矩形边框，并在屏幕四条边上显示带标签的标记。也可以切换到原来的环绕动画模式，用来测试动态刷新。

命令：

- `hsmcal start` - 给命令发送者显示移动标记；如果从服务器控制台执行，则给所有已就绪玩家显示。
- `hsmcal start all` - 给所有已就绪玩家显示标记。
- `hsmcal stop` / `hsmcal stop all` - 清除标记。
- `hsmcal status` - 查看当前设置。
- `hsmcal set mode rectangle` - 使用静态矩形边框。
- `hsmcal set mode orbit` - 使用动态环绕标记。
- `hsmcal set <horizontal|vertical|leftX|rightX|topY|bottomY|insetX|insetY|labels|markers|radiusX|radiusY|centerX|centerY|orbit|interval|size|coords> <value>` - 修改设置并重启当前校准显示。
- `hsmcal add <field> <delta>` - 按增量微调数值设置。

矩形模式默认使用简短的 `T/B/L/R` 边缘标记，并隐藏坐标以避免重叠。默认 X 边界为 `-1780..1780`，与实测 1080p 居中坐标显示宽度一致。需要坐标读数时可执行 `hsmcal set coords 1`。

请先安装 `HintServiceMeow.dll`，再把 `HsmCalibration.dll` 放入 `LabAPI/plugins/<port>`。
