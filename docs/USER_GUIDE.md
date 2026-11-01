# 使用手册

ArchivePasswordTestTool 用 7-Zip 引擎对加密压缩包做密码字典测试，
支持多字典、多压缩包/文件夹、交互式下一轮，以及可选的 hashcat GPU 加速(已适配AMD显卡)。
构建与打包见 [构建教程](BUILD.md)，架构与取舍见 [开发文档](DEVELOPMENT.md)。

## 1. 快速开始

```text
ArchivePasswordTestTool.exe -D PasswordDictionary.txt -F secret.7z
```

- `-D` 密码字典（每行一个密码），可重复；默认 `PasswordDictionary.txt`
- `-F` 压缩包或文件夹，可重复；分卷包传第一个分卷（`.001` / `.part1.rar`）

无参数双击启动会进入交互引导：可一次拖入多个字典与压缩包/文件夹，
每个列表直接按 Enter（空输入）结束；处理完后可继续下一轮。

## 2. 参数

| 参数 | 说明 |
|------|------|
| `-D` | 密码字典文件，可重复；按出现顺序合并并全局去重 |
| `-F` | 压缩包或文件夹，可重复；文件夹递归扫描 `.7z/.zip/.rar/.exe/.001` |
| `-lib` | 指定 `7z.dll` 路径（默认：已安装 7-Zip → 程序目录/内嵌版） |
| `-Api` | 覆盖在线密码共享服务地址 |
| `-NoQuery` | 关闭「测试前按哈希查询在线共享密码」 |
| `-NoReport` | 关闭「找到密码后自动回传」 |
| `-NoGpu` | 禁用 GPU/hashcat 加速（等同 `APTT_GPU=0`） |

显式传入的文件不做扩展名白名单过滤；文件夹展开时只收白名单扩展名并做签名确认。
非首卷（`.002` 及以后）会被跳过。

## 3. 支持格式

| 格式 | 支持 |
|------|------|
| 7z | ✅（含 header 加密 `-mhe=on`） |
| Zip | ✅（含 WinZip AES-128/192/256） |
| RAR4 / RAR5 | ✅（含 `-hp` header 加密） |
| 分卷 | ✅ `.7z.001` / `.zip.001` / `.part1.rar`，传第一卷 |
| SFX 自解压 | ✅（自动在 PE stub 后定位压缩数据） |

## 4. GPU 加速

GPU 加速通过外部 **hashcat（推荐 8.0.0）** 完成，程序本身不内置密码学内核。
把 hashcat 发行包放到**程序目录**：直接放 exe 同目录，或整个解压成一级子目录
（例如 `hashcat-8.0.0\`）。使用 7z 加速时，再把 `7z2hashcat64-2.0.exe` 放到同一位置。

```text
ArchivePasswordTestTool.exe
hashcat.exe                  ← 8.x 推荐
OpenCL\  modules\  bridges\  hashcat.hcstat2
7z2hashcat64-2.0.exe         ← 仅 7z 需要
```

程序只查程序目录及其一级子目录，不搜索 PATH/注册表。
6.x / 7.x / 8.x 均可，程序会自动适配 8.0.0 的参数变化。

### 支持范围

| 格式 | GPU | 说明 |
|------|-----|------|
| header 加密 7z（`-mhe=on`） | ✅ | 头部压缩方式 0/1/2/7（原始/LZMA1/LZMA2/DEFLATE）均支持 |
| WinZip AES ZIP | ✅ | AES-128/192/256 |
| 普通 7z（仅文件数据加密） | ❌ | 需要解压验证，走 CPU |
| ZipCrypto ZIP | ❌ | 无 KDF，GPU 收益有限 |
| RAR4 / RAR5 | ❌ | 格式专有，走 CPU |
| 分卷 7z | ❌ | 7z2hashcat 对分卷 seek 失败，走 CPU |

### 启用条件与阈值

- hashcat 可用 + GPU 设备可用 + 字典规模达到阈值；
- 默认阈值：**header 7z ≥ 25,000 行**、**ZIP AES ≥ 500,000 行**；
- `APTT_GPU_MIN` 可统一覆盖；设为 `0` 可强制小字典也走 GPU（调试用）；
- 字典低于阈值时程序会打印一行提示，而不是静默显示 CPU。

hashcat 首次启动需要初始化 OpenCL 并编译内核，通常有 **8–15 秒**固定开销，
所以小字典走 CPU 往往更快。实测（RX 6650 XT、header 加密 7z）：

| 模式 | 速度 |
|------|------|
| CPU（16 线程） | 约 512 个/秒 |
| GPU（hashcat 8.0.0） | 约 3,900 个/秒（14,459,679 行约 61 分钟） |

### 结果确认与回退

- hashcat 给出的候选仍逐条交给 **7z.dll 实际验证**，不会直接采信；
- hashcat 失败、工具缺失、GPU 不可用时会打印原因并回退 CPU；
- GPU 会话有 **120 秒无进展看门狗**：hashcat 卡死时终止并回退，不会无限挂起；
- `-NoGpu` / `APTT_GPU=0` 可强制 CPU。

### 示例

```text
# 直接使用（面板会显示加速方式）
ArchivePasswordTestTool.exe -D dict.txt -F secret-header.7z

# 强制 CPU
ArchivePasswordTestTool.exe -D dict.txt -F secret-header.7z -NoGpu

# 小字典也启用 GPU（小字典通常更慢，仅调试/验证用）
set APTT_GPU_MIN=0
ArchivePasswordTestTool.exe -D small.txt -F secret-header.7z
```

> 建议使用 hashcat 8.0.0。6.2.6 / 7.1.2 在 11600（7-Zip）大字典下存在
> `module_hash_encode` 崩溃/挂起问题；程序已用 `--potfile-disable`、
> `--status-timer 3600` 和看门狗规避，但升级到 8.x 更稳。

## 5. 多字典与批量

```text
# 多个字典按顺序合并、跨文件去重
ArchivePasswordTestTool.exe -D common.txt -D company.txt -F a.7z

# 多个压缩包 / 文件夹
ArchivePasswordTestTool.exe -D dict.txt -F a.7z -F D:\archives -F b.zip
```

- 文件夹递归扫描，只收白名单扩展名并确认签名；
- 分卷只取 `.001`；跳过隐藏/系统文件与无法访问的目录；
- 开始前统一询问一次是否保存结果，每包写 `<压缩包名>[results].txt`；
- 批量结束输出汇总表；只要有一个包失败，退出码为 1。

## 6. 交互模式

无参数启动（例如双击 exe）：

1. 按提示拖入一个或多个密码字典，空行 Enter 结束；
2. 拖入压缩包或文件夹，空行 Enter 结束；
3. 处理完成后询问是否继续下一轮（沿用上一轮字典，只重新选择压缩包）；
4. 直接 Enter 结束会话。

双击启动出现错误时（如未安装 7-Zip 且内嵌 dll 不可用），窗口会等待按键确认；
从终端启动则直接退出。

## 7. 在线密码共享

默认开启：计算压缩包哈希后向服务查询共享密码（命中则优先测试），
找到密码后自动回传。网络失败只提示、不阻断。`-NoQuery` / `-NoReport` 可分别关闭。
未配置服务地址时自动跳过。

## 8. 环境变量

| 变量 | 说明 |
|------|------|
| `APTT_PARALLELISM` | 并行度上限（线程/工作进程共用）；`1` = 单线程 |
| `APTT_WORKERS` | 7z 测密的工作进程数（默认 = 逻辑核心数；`1` 退回线程模式） |
| `APTT_TRACE` | `1` 时输出 COM 回调跟踪日志到 stderr |
| `APTT_FORCE_EMBEDDED_7Z` | `1` 时跳过系统 7-Zip，强制用内嵌 `7z.dll` |
| `APTT_GPU` | `0` 禁用 GPU（等同 `-NoGpu`） |
| `APTT_GPU_MIN` | 统一覆盖 GPU 字典阈值；`0` 强制小字典也走 GPU |
| `APTT_NO_UPGRADE_CHECK` | `1` 跳过启动时后台更新检查 |

## 9. 常见问题

| 现象 | 处理 |
|------|------|
| 面板一直显示 CPU | ① hashcat/7z2hashcat 是否在程序目录或一级子目录；② hashcat 是否 ≥6（推荐 8.0.0）；③ 字典是否达到阈值（看提示，或 `APTT_GPU_MIN=0`） |
| 提示「GPU 工具未找到」 | 把 hashcat 发行包（含 `OpenCL\ modules\ hashcat.hcstat2`）和 `7z2hashcat64-2.0.exe` 放到 exe 同目录 |
| 提示「格式不支持 GPU」 | 普通 7z、ZipCrypto、RAR、分卷 7z 按设计走 CPU；header 加密 7z 与 AES ZIP 才支持 |
| GPU 跑一会回退 CPU | 看输出原因；hashcat 版本过旧或工具异常，升级到 8.0.0 |
| 小字典 GPU 更慢 | 正常：hashcat 固定启动开销 8–15 秒；大字典才划算 |
| 找不到密码但字典里明明有 | 字典编码自动识别 UTF-8/UTF-16/GBK；确认密码行没有额外空格；分卷请传第一卷 |
| 没有安装 7-Zip | 程序自带内嵌 `7z.dll`；也可 `-lib` 指定自己的 dll |

## 10. 结果文件

需要保存时生成 `<压缩包名>[results].txt`，包含压缩包信息、测试结果与密码行
（未加密 / 找到 / 未找到三态）。批量模式开始前统一询问一次。
