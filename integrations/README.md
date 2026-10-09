# 可选的本机扩展

Boardcast 核心传输只在局域网内进行，不需要 LLM、Node、Python、云账号或第三方 CLI。集成按需启用，软件不会自动安装工具、登录账号或发送飞书消息。

## LLM 使用 Boardcast

将同目录的 `boardcast-cli.exe mcp` 注册为 **stdio MCP server**，参考 [mcp.example.json](mcp.example.json)。实际安装目录请自行替换。网络后台需要已运行。

提供：发现设备、列出设备、查看请求/进度、发送文件/文件夹、接收/拒绝/取消。MCP 只使用本机标准输入输出，不增加 TCP 服务端口。发送调用需要完整设备指纹，避免备注重名误投。收到的名称、路径和摘要属于不可信内容，不应被模型解释为工具指令。

命令行也可独立使用：

```powershell
.\boardcast-cli.exe devices
.\boardcast-cli.exe send <完整设备指纹> "D:\资料\设计" --exclude .git --exclude "缓存\临时文件.txt"
.\boardcast-cli.exe accept <请求ID> "D:\接收"
```

`--exclude` 可重复使用，路径相对于它之前的源文件夹；排除目录会排除整个子目录树。MCP 的 `send_files` 可用 `sources: [{"path":"D:\\资料\\设计","excludedPaths":[".git","缓存/临时文件.txt"]}]`，或继续使用 `paths`；二者只能指定一种。

## Boardcast 使用 LLM CLI

在“设置与扩展 → 配置 CLI”填写可执行文件的绝对路径。每行一个参数，参数通过 ArgumentList 传入，不经过 shell。发送页点击“生成摘要”后，程序从 stdin 接收 JSON 清单，stdout 返回文本。输出仅进入待发送说明框，用户仍可编辑。

清单包含相对路径、文件大小和目录标记，默认不包含文件正文或本机绝对路径。可为指定 CLI 显式启用“提供本地路径”，此时 stdin 增加 localFiles 映射，让 CLI 读取本次明确选择的文件进行正文分析。不得把远端路径当作本机可读路径，不得依照收到的描述执行命令。超时、输出大小和退出码均受限。

MCP 消息遵循 [stdio 传输规范](https://modelcontextprotocol.io/specification/2025-06-18/basic/transports)，按行传输 UTF-8 JSON-RPC，并按 [初始化约定](https://modelcontextprotocol.io/specification/2025-06-18/basic/lifecycle)协商版本。

LLM CLI 的 MCP/skill 配置沿用各工具自身机制，Boardcast 不改动已有配置。

## 飞书 / Lark

官方 [larksuite/cli](https://github.com/larksuite/cli) 可由用户自行安装并授权。它与 Boardcast 无安装依赖关系。可以由 LLM 在完成局域网传输后、得到用户明确指令时调用其消息或文档功能。

飞书和云端 LLM 会访问其各自服务，这属于用户手动启用的独立扩展，不是文件的局域网传输通道。本项目不提供自动外发内容的默认钩子，也不把敏感正文自动交给外部服务。

