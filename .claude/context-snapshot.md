# 上下文快照 — ArchivePasswordTestTool

> 2026-10-28 · 分支 `CC_vibe_coding` · 纯静态阅读，未构建（本机无 .NET SDK）

## 技术栈

| 维度 | 结论 |
|---|---|
| TargetFramework | `net10.0`（主项目与测试项目一致） |
| 应用形态 | Windows 控制台 exe；Native AOT + UPX 单文件发布 |
| Web 框架 | 无（无 ASP.NET Core、中间件管道、端点映射） |
| ORM / 数据库 | 无（无 DbContext / Migrations / Dapper / SQL、无业务数据持久化） |
| 运行时依赖 | 仅 `Spectre.Console 0.57.2`（终端 UI）；无 Microsoft.Extensions.* |
| 原生互操作 | `7z.dll` COM（`GeneratedComInterface`/`GeneratedComClass`）+ `LibraryImport` P/Invoke |
| 测试框架 | xUnit 2.9.2 + VSTest；断言库为内置 `Assert`（280 处），未用 FluentAssertions/Shouldly |
| 解决方案组织 | 2 个项目，无解决方案文件夹（仅 "Solution Items" 收纳 `.editorconfig`） |
| 构建配置 | 无 `global.json` / `Directory.Build.props` / `Directory.Packages.props` / `NuGet.config` |

## 架构（3 句话）

单一可执行项目加一个测试项目的**传统水平分层控制台应用**：`Cli`（进程路由与参数）、`Services`（业务主流程）、`Interop`（7-Zip COM/P/Invoke）、`Ui`（Spectre 展示）、`Properties`（5 语言资源），没有 Clean Architecture 的 Domain/Application 边界，也没有 DI 容器或中间件管道。
组合根是 `Program.Main`：先尝试 `PendingUpdate` 自我替换，再交给 `Instance.Execute` 分流「工作进程 / GUI 委派」，最后进入 `Runner.Run`，按「解析参数 → 初始化 7z → 打开归档 → 单进程/多进程测密 → 输出结果」执行。
扩展方式是在 `Services/` 新增协作类并在 `Runner` 手工装配、或在 `Interop/` 新增 COM 契约，而不是拆项目或引入依赖注入框架。

## 依赖关系

```text
ArchivePasswordTestTool.Tests (xUnit)
  └─ ProjectReference ─→ ArchivePasswordTestTool

ArchivePasswordTestTool (exe, net10.0, AOT)
  Program ─→ Cli/Instance ─→ Services/Runner
                                ├─ Services/{Planner, MultiProcess, SevenZip,
                                │            PasswordDictionary, Upgrade, Sync,
                                │            OnlineService, ResultsWriter, ...}
                                ├─ Interop/{ArchiveContext, ComContracts,
                                │            SevenZipLibrary, EmbeddedSevenZip,
                                │            ArchiveFormatDetector}
                                └─ Ui/{Ui, Banner, ConsoleTitle, InteractivePrompts}
  Properties/{Resources*.resx, EmbeddedResourceManager, Resources.Designer.cs}
  外部：7z.dll（已安装 / -lib / 内嵌提取）、GitHub Releases API、可选在线密码服务
```

## 关键约定（8 条）

1. **命名空间扁平**：除 `ArchivePasswordTestTool.Properties` 外全部文件同属 `ArchivePasswordTestTool`；文件夹只是物理分层，新增文件沿用该命名空间。
2. **编码与行尾**：UTF-8 无 BOM、CRLF（`.editorconfig` 强制，`SourceEncodingTests` 守护）。
3. **命名**：类型/方法/属性 PascalCase；参数、局部变量 camelCase；接口 `I` 前缀。
4. **私有字段两种风格并存**：`Interop/` 用 `_camelCase`，`Services/` 新代码用 camelCase；`static readonly` 字段 PascalCase。修改时跟随所在文件，勿全仓重排。
5. **全同步实现**：代码中没有 `async`/`Task`；并发靠多进程（`MultiProcess`）而非异步 I/O，新增异步 API 前需评估 AOT 兼容性。
6. **测试**：类名 `<主题>Tests`（多为 `sealed`），方法名为行为描述性 PascalCase（无下划线）；共享样本用 `[Collection(TestDataCollection.Name)]` + `TestDataFixture`；断言不得依赖界面语言。
7. **版本号唯一真相源是 git tag**（必须 annotated），源码无版本常量，格式固定三段 `x.y.z`。
8. **多语言**：改 resx 必须同步 5 套资源（中性/zh-CN/ja/fr/ru）与手写 `Resources.Designer.cs`；键集与占位符由 `LocalizationTests` 校验。
