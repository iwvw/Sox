# Sox {{VERSION}}

{{CHANGES}}

---

## 下载

两种版本，按机器是否已装运行时选择。

| 文件 | 大小 | 说明 |
|---|---:|---|
| [`Sox-{{VERSION}}-x64-merged-setup.exe`](https://github.com/iwvw/Sox/releases/download/v{{VERSION}}/Sox-{{VERSION}}-x64-merged-setup.exe) | {{SIZE_X64_MERGED_SETUP}} | 合并版安装包，推荐。运行时随包，开箱即用 |
| [`Sox-{{VERSION}}-x64-merged-portable.zip`](https://github.com/iwvw/Sox/releases/download/v{{VERSION}}/Sox-{{VERSION}}-x64-merged-portable.zip) | {{SIZE_X64_MERGED_PORTABLE}} | 合并版便携包，解压即用 |
| [`Sox-{{VERSION}}-x64-split-setup.exe`](https://github.com/iwvw/Sox/releases/download/v{{VERSION}}/Sox-{{VERSION}}-x64-split-setup.exe) | {{SIZE_X64_SPLIT_SETUP}} | 分离版安装包，体积小，安装时自动补齐运行时 |
| [`Sox-{{VERSION}}-x64-split-portable.zip`](https://github.com/iwvw/Sox/releases/download/v{{VERSION}}/Sox-{{VERSION}}-x64-split-portable.zip) | {{SIZE_X64_SPLIT_PORTABLE}} | 分离版便携包，需机器已装运行时 |
| [`Sox-{{VERSION}}-arm64-merged-portable.zip`](https://github.com/iwvw/Sox/releases/download/v{{VERSION}}/Sox-{{VERSION}}-arm64-merged-portable.zip) | {{SIZE_ARM64_MERGED_PORTABLE}} | ARM64 合并版便携包 |
| [`Sox-{{VERSION}}-arm64-split-portable.zip`](https://github.com/iwvw/Sox/releases/download/v{{VERSION}}/Sox-{{VERSION}}-arm64-split-portable.zip) | {{SIZE_ARM64_SPLIT_PORTABLE}} | ARM64 分离版便携包 |

### 选哪个

| 场景 | 建议 |
|---|---|
| 图省事、磁盘充足 | 合并版（运行时随包） |
| 已装 .NET 10 与 Windows App SDK 运行时 | 分离版（体积小很多） |
| 用安装包 | 分离版也行，安装时会自动检测并补齐运行时 |
| ARM 设备（骁龙本等） | arm64 便携包 |

## 注意事项

- 首次启动会请求一次管理员权限，用于注册后台索引服务，之后不再需要。
- 分离版要求 .NET 10 Desktop Runtime 与 Windows App SDK 2.x Runtime；安装包会自动下载安装，便携包需自行预装。
- 安装包未做代码签名，Windows SmartScreen 可能提示未知发布者，选择「仍要运行」即可。
- 便携版数据保存在程序目录下的 `Data\`，安装版数据在 `%ProgramData%\Sox` 与 `%LocalAppData%\Sox`。
