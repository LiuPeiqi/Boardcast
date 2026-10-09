# 文件图标选型

调研日期：2026-10-08。比较以下五个项目的官方仓库、文件/文件夹图标及许可。

| 项目 | 视觉与覆盖 | 许可 | 本项目取舍 |
| --- | --- | --- | --- |
| [Material Icon Theme](https://github.com/material-extensions/vscode-material-icon-theme) | 彩色扁平矢量，后缀及文件名关联丰富，文件夹轮廓清楚 | [MIT](https://github.com/material-extensions/vscode-material-icon-theme/blob/main/LICENSE) | 采用少量图形，统一 24×24 画布 |
| [vscode-icons](https://github.com/vscode-icons/vscode-icons) | 类型覆盖广，含品牌标识，视觉差异较大 | 代码 MIT；图标 CC BY-SA；品牌图标另有版权 | 不混入另一套资产与许可体系 |
| [Fluent System Icons](https://github.com/microsoft/fluentui-system-icons) | 适合 Windows，提供 regular、filled、color；通用文档图形一致 | [MIT](https://github.com/microsoft/fluentui-system-icons/blob/main/LICENSE) | 适合应用操作图标；文件后缀品牌辨识需另补映射 |
| [Lucide](https://github.com/lucide-icons/lucide) | 圆角线性、笔画统一，通用类型完整 | [ISC / 部分 Feather 图标 MIT](https://github.com/lucide-icons/lucide/blob/main/LICENSE) | 清爽，但彩色文件类型辨识弱于专门文件主题 |
| [Tabler Icons](https://github.com/tabler/tabler-icons) | 大量 24×24 图标，含 file-type 系列，线性和 filled 风格 | [MIT](https://github.com/tabler/tabler-icons/blob/main/LICENSE) | 类型图标丰富，但本项目不需要整套通用图标库 |

## 已接入的资源

`FileItemIcon.Get(path, isDirectory)` 返回冻结、缓存的 WPF `DrawingImage`。仅根据文件名/后缀分类，不读取磁盘，发送列表与接收请求共用同一接口；未知后缀回落为通用文件。所有文件夹使用统一的暖黄色文件夹轮廓。支持文档、PDF、表格、演示、图片、音视频、压缩包、配置、终端、数据库、字体及常见代码类型。

图形来自 [Material Icon Theme 固定版本](https://github.com/material-extensions/vscode-material-icon-theme/tree/9d513c781fb5bd0619ca21bd519fd1df5f19f84a) 的 `icons/*.svg`，只保留实际映射的 32 个小图形。目录采用 `folder-base.svg` 的文件夹轮廓，去除模板徽标并改为暖黄色；默认文件采用 `document.svg` 并改为中性灰。其余保留原始路径与颜色。SVG 路径转换为 WPF 矢量，并保留非零填充规则和原始 viewBox；无 SVG 渲染框架、图标字体、网络请求或额外 NuGet 依赖。

完整上游许可保存在 [Material-Icon-Theme.LICENSE](third-party/Material-Icon-Theme.LICENSE)，现有发布脚本会连同 `docs` 目录带入发行包。

验证：全部 32 个图形已在 WPF 实际渲染，确认画布边界与冻结缓存一致；已验证大写后缀、未知类型、特殊文件名、不同路径分隔符，以及不存在的远端路径。验证程序为临时文件，未增加常驻依赖或项目。
