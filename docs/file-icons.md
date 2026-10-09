# 文件图标选型

调研日期：2026-10-08。比较以下五个项目的官方仓库、文件/文件夹图标及许可。

| 项目 | 视觉与覆盖 | 许可 | 本项目取舍 |
| --- | --- | --- | --- |
| [Material Icon Theme](https://github.com/material-extensions/vscode-material-icon-theme) | 彩色扁平矢量，后缀及文件名关联丰富，文件夹轮廓清楚 | [MIT](https://github.com/material-extensions/vscode-material-icon-theme/blob/main/LICENSE) | 采用少量图形，统一 24×24 画布 |
| [vscode-icons](https://github.com/vscode-icons/vscode-icons) | 类型覆盖广，含品牌标识，视觉差异较大 | 代码 MIT；图标 CC BY-SA；品牌图标另有版权 | 不混入另一套资产与许可体系 |
| [Fluent System Icons](https://github.com/microsoft/fluentui-system-icons) | 适合 Windows，提供 regular、filled、color；通用文档图形一致 | [MIT](https://github.com/microsoft/fluentui-system-icons/blob/main/LICENSE) | 适合应用操作图标；文件后缀品牌辨识需另补映射 |
| [Lucide](https://github.com/lucide-icons/lucide) | 圆角线性、笔画统一，通用类型完整 | [ISC / 部分 Feather 图标 MIT](https://github.com/lucide-icons/lucide/blob/main/LICENSE) | 清爽，但彩色文件类型辨识弱于专门文件主题 |
| [Tabler Icons](https://github.com/tabler/tabler-icons) | 大量 24×24 图标，含 file-type 系列，线性和 filled 风格 | [MIT](https://github.com/tabler/tabler-icons/blob/main/LICENSE) | 类型图标丰富，但本项目不需要整套通用图标库 |

## Windows 已接入的资源

`FileItemIcon.Get(path, isDirectory)` 返回冻结、缓存的 WPF `DrawingImage`。仅根据文件名/后缀分类，不读取磁盘，发送列表与接收请求共用同一接口；未知后缀回落为通用文件。Windows 文件夹使用统一的暖黄色文件夹轮廓。支持文档、PDF、表格、演示、图片、音视频、压缩包、配置、终端、数据库、字体及常见代码类型。

图形来自 [Material Icon Theme 固定版本](https://github.com/material-extensions/vscode-material-icon-theme/tree/9d513c781fb5bd0619ca21bd519fd1df5f19f84a) 的 `icons/*.svg`，只保留实际映射的 32 个小图形。目录采用 `folder-base.svg` 的文件夹轮廓，去除模板徽标并改为暖黄色；默认文件采用 `document.svg` 并改为中性灰。其余保留原始路径与颜色。SVG 路径转换为 WPF 矢量，并保留非零填充规则和原始 viewBox；无 SVG 渲染框架、图标字体、网络请求或额外 NuGet 依赖。

完整上游许可保存在 [Material-Icon-Theme.LICENSE](third-party/Material-Icon-Theme.LICENSE)，现有发布脚本会连同 `docs` 目录带入发行包。

验证：全部 32 个图形已在 WPF 实际渲染，确认画布边界与冻结缓存一致；已验证大写后缀、未知类型、特殊文件名、不同路径分隔符，以及不存在的远端路径。验证程序为临时文件，未增加常驻依赖或项目。

## 跨端一致性与 Mac 文件夹例外

2026-10-09 按 Windows 0.2.7 的实际图形补齐设计资产：[图标目录与映射](assets/icons/catalog.json)、[浅深背景总览](assets/icons/contact-sheet.png)。32 个现有文件/文件夹类型、一个 Mac 蓝色文件夹代表及 Windows/Mac/其他平台三种标记，共 36 个 112×112 透明 PNG。目录记录源文件 SHA-256、扩展名、特殊名称、平台映射、各 PNG 摘要及派生规则，并附 [MIT 许可](assets/icons/Material-Icon-Theme.LICENSE)。

文件类型保持既有几何与原色，包括 nonzero 填充、原始 viewBox 和多层颜色；特殊名称优先、目录优先、最后后缀、忽略大小写及未知文件 fallback 与当前软件一致。Mac 后续 classifier 可以读取 catalog 或生成静态映射，不为不同系统另选一批近似图标。平台继续使用 Windows 四格、Mac 笔记本和其他平台显示器，按当前 secondary/Muted 颜色渲染，不换成 Apple 品牌标志。

用户允许文件夹颜色与风格跟随平台。Mac 生产实现仅对已授权、实际存在的普通目录查询 NSWorkspace 路径图标，对远端或失效路径使用当前系统通用 .folder 图标；保留系统原色与内置阴影。folder-macos.png 沿用软件的 folder-base 几何，采用蓝色作为 Windows 环境的设计预览代表，未冒充真实 Mac 图标。包目录单独保留标记及传输限制。文件类型和系统文件夹以 original 显示，平台与动作可以 template 显示；不能将所有内容图标统一 tint。

### 资产维护与后续接入

[Export-DesignIcons.ps1](../scripts/Export-DesignIcons.ps1)在 Windows、PowerShell 和 .NET 10 SDK 下从原 C# 图形导出；它在独立 artifacts/mac-icon-design-export 目录编译，未增加产品依赖。不要在 Mac 构建命令中假设 WPF 导出工具可运行。

~~~powershell
./scripts/Export-DesignIcons.ps1
~~~

导出过程校验原类型数量、173 项分类样本、非空绘制、透明像素、尺寸、源颜色与摘要。源几何、名称映射或类型数量变化时先复核导出器，再重建资产；导出与预览不访问示例文件路径，也不请求网络。

当前 PNG、catalog 和导出脚本只补齐设计与实现交接资料，Swift 尚未接入。Mac P1 需要把经核对的类型资源、catalog、完整版权与 MIT 许可纳入 SwiftPM 资源及最终 app，验证 Bundle.module 或资源加载器、Retina、浅深外观和移动 app 后的查找。Mac 系统文件夹运行时查询，不能将某台 Mac 的截图固定成所有系统版本的生产资源；更换显示色彩不能改动真实发送路径、分类或信任逻辑。
