# Sox {{VERSION}}

{{CHANGES}}

---

## 下载

| 文件 | 大小 | 说明 |
|---|---:|---|
| [`Sox-{{VERSION}}-x64-setup.exe`](https://github.com/iwvw/Sox/releases/download/v{{VERSION}}/Sox-{{VERSION}}-x64-setup.exe) | {{SIZE_X64_SETUP}} | 安装版，推荐 |
| [`Sox-{{VERSION}}-x64-portable.zip`](https://github.com/iwvw/Sox/releases/download/v{{VERSION}}/Sox-{{VERSION}}-x64-portable.zip) | {{SIZE_X64_PORTABLE}} | 便携版，解压即用 |
| [`Sox-{{VERSION}}-arm64-portable.zip`](https://github.com/iwvw/Sox/releases/download/v{{VERSION}}/Sox-{{VERSION}}-arm64-portable.zip) | {{SIZE_ARM64_PORTABLE}} | ARM64 便携版 |

不确定选哪个：绝大多数 Windows 电脑选 x64，ARM 设备（骁龙本等）选 arm64。

## 注意事项

- 首次启动会请求一次管理员权限，用于注册后台索引服务，之后不再需要。
- 安装包未做代码签名，Windows SmartScreen 可能提示未知发布者，选择「仍要运行」即可。
- 便携版数据保存在程序目录下的 `Data\`，安装版数据在 `%ProgramData%\Sox` 与 `%LocalAppData%\Sox`。
