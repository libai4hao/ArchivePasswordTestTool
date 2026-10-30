# 构建与打包教程

面向需要从源码构建、发布单文件 exe 并放入 GPU 工具的维护者。
日常使用见 [使用手册](USER_GUIDE.md)，架构与取舍见 [开发文档](DEVELOPMENT.md)。

## 1. 前置条件

| 项目 | 要求 |
|------|------|
| 操作系统 | Windows 10 / 11 x64（项目依赖 COM 互操作与 Win32 API） |
| .NET SDK | 10.0.x（实测 10.0.403） |
| C++ 链接器 | 仅 Native AOT 需要：Visual Studio「使用 C++ 的桌面开发」+ Windows SDK |
| 7-Zip | 仅生成测试样本需要；程序运行自带内嵌 `7z.dll` |
| 网络（可选） | 首次构建自动下载 `7z.dll`、resvg、UPX；离线可用缓存或参数关闭 |

```text
dotnet --info
```

## 2. 快速构建

```text
dotnet build -c Release
```

产物：`ArchivePasswordTestTool\bin\Release\net10.0\ArchivePasswordTestTool.exe`。
这是普通 apphost，依赖本机 .NET 运行时；对外分发请用下面的发布形态。

## 3. 发布形态

### 3.1 Native AOT（对外发布，约 4.2 MB）

```text
dotnet publish ArchivePasswordTestTool\ArchivePasswordTestTool.csproj -c Release `
  -p:PublishProfile=Properties\PublishProfiles\win-x64-aot.pubxml -o dist\aot
```

- 必须显式写 csproj：`PublishProfile` 路径相对**项目目录**解析。
- 首次构建会下载并缓存 `tools\7zip\7z.dll`、`tools\resvg\resvg.exe`、`tools\upx\upx.exe`。
- 产物只有一个 `dist\aot\ArchivePasswordTestTool.exe`，免安装 .NET。

### 3.2 单文件 + 裁剪（兼容形态）

```text
dotnet publish ArchivePasswordTestTool\ArchivePasswordTestTool.csproj -c Release `
  -p:PublishProfile=Properties\PublishProfiles\win-x64.pubxml -o dist\single
```

单文件形态不能用 UPX（bundle 布局会被破坏），体积约 13 MB。

### 3.3 AOT 常见错误：找不到 vswhere.exe

```text
vswhere.exe 不是内部或外部命令
error MSB3073: ... link.exe
```

ILCompiler 通过 `vswhere` 定位 MSVC。若 VS 安装器目录不在 PATH，先在当前终端补上再发布：

```text
set "PATH=%PATH%;C:\Program Files (x86)\Microsoft Visual Studio\Installer"
dotnet publish ... -p:PublishProfile=Properties\PublishProfiles\win-x64-aot.pubxml -o dist\aot
```

Git Bash 等价写法：

```bash
export PATH="/c/Program Files (x86)/Microsoft Visual Studio/Installer:$PATH"
```

仍失败时确认已安装「使用 C++ 的桌面开发」与 Windows SDK（`C:\Program Files (x86)\Windows Kits\10\Lib\...`）。

## 4. 放入 GPU 工具（可选）

GPU 加速依赖外部的 **hashcat 8.x（推荐）** 与 **7z2hashcat**。
把工具放在 exe 同目录，或整个解压成一级子目录（例如 `hashcat-8.0.0\`）：

```text
ArchivePasswordTestTool.exe
hashcat.exe                  ← hashcat 8.x
OpenCL\
modules\
bridges\                     ← hashcat 8.0.0 需要
hashcat.hcstat2
7z2hashcat64-2.0.exe         ← 仅 7z 加速需要
```

或：

```text
ArchivePasswordTestTool.exe
hashcat-8.0.0\
  hashcat.exe
  OpenCL\ modules\ bridges\ hashcat.hcstat2
  7z2hashcat64-2.0.exe
```

程序只查程序目录及其一级子目录，不搜索 PATH/注册表。
6.x / 7.x / 8.x 均可；程序会自动适配 8.0.0 的参数变化
（8.0 移除 `--stdin`、`--quiet` 不再抑制状态屏）。

> 推荐 hashcat 8.0.0：6.2.6 / 7.1.2 在 11600（7-Zip）大字典下存在
> `module_hash_encode` 崩溃/挂起问题；8.0.0 + 程序的 `--status-timer 3600`
> + `--potfile-disable` 已实测跑完 14,459,679 行。
> 旧版本目录（如 `hashcat-6.2.6\`）建议删除，避免混淆；根目录工具优先级更高。

## 5. 测试

```text
dotnet test ArchivePasswordTestTool.Tests\ArchivePasswordTestTool.Tests.csproj -c Debug
```

- 样本由 `TestDataFixture` 幂等生成到 `TestData/`（不入库）；需要 7-Zip CLI 生成样本。
- RAR 用例需要 WinRAR，未安装时跳过。
- GPU E2E 需要主程序目录有 `hashcat.exe` 与 `7z2hashcat64-2.0.exe`，否则自动跳过。
- 当前基线：**213 通过 / 0 失败**。

## 6. 发布检查清单

1. `dotnet test` 全绿。
2. AOT 产物存在且只有一个 exe：
   ```text
   dir dist\aot
   ```
3. 冒烟：用已知样本跑一次
   ```text
   dist\aot\ArchivePasswordTestTool.exe -D TestData\DictOK.txt -F TestData\SecretHeader.7z
   ```
4. GPU 冒烟（放入工具后）：
   ```text
   set APTT_GPU_MIN=0
   dist\aot\ArchivePasswordTestTool.exe -D TestData\DictOK.txt -F TestData\SecretHeader.7z
   ```
   面板应显示 `GPU · <显卡> · hashcat <版本>`，并找到密码。
5. 确认发布包只包含一个 exe（自更新按「第一个 exe 资产」选择）；GPU 工具不随包分发。

## 7. 常见问题

| 现象 | 原因 / 处理 |
|------|-------------|
| AOT 发布报 vswhere/link 错误 | 见 3.3；确认 MSVC + Windows SDK |
| `dotnet publish` 提示 NETSDK1198 | 未指定 csproj，`PublishProfile` 在测试项目不存在 |
| 产物没有图标 / 构建失败 | resvg 未缓存且无法下载；离线时把 `resvg.exe` 放到 `tools\resvg\` |
| 没有 UPX 压缩 | `tools\upx\upx.exe` 缺失；按需放入，或忽略（产物约 11 MB） |
| GPU 面板显示 CPU | 检查工具目录/版本/字典阈值；见 [使用手册](USER_GUIDE.md#gpu-加速) |
| GPU 跑一会回退 CPU | 看程序输出的原因；hashcat 版本过旧或工具缺失，建议 8.0.0 |
