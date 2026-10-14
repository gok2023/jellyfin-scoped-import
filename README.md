# ScopedImport：Jellyfin 定向入库插件

把指定路径下缺失的电影或剧集追加到已有 Jellyfin 媒体库，适合新增少量媒体时由管理员手动入库。插件在 Jellyfin 内运行，无需 Python 常驻助手。

当前插件版本 **0.2.0.0**，仅针对 **Windows x64 + 原版 Jellyfin 10.11.11（ABI 10.11.11.0）**。这是第三方插件，不是 Jellyfin 官方内置插件，也未加入官方插件目录。新工程通过安全离线检查，真实集成、重启及系统 I/O 验证仍未完成，使用前请备份。

## 主要功能与工作原理

- 按电影目录、剧目目录、季目录或单集文件追加缺失条目，核对并跳过已有条目。
- 支持单片目录（一个主视频）、NFO、SxxEyy 和 E01 单集子目录；拒绝存在季集歧义的布局。
- 新条目可按媒体库启用的提供器及顺序补充元数据，也可选择只读取本地 NFO。
- 管理员权限、路径边界和追加保护检查；页面展示操作结果及插件记录的目录/文件访问。

插件检查输入路径与媒体库的对应关系，枚举目标范围，识别缺失条目，再调用 Jellyfin 服务保存。视频探测可能启动宿主的 ffprobe；远程元数据模式可能访问外部提供器。

## 支持条件与普通扫描的区别

只支持已初始化物理根的电影/电视剧库（movies/tvshows）和服务器可访问的本地 Windows 路径。当前内部接口与原版 BaseItemRepository 绑定；其他版本或替换仓储服务的插件可能导致保护无法启用。**没有验证 Linux、Docker 或未来 Jellyfin 版本兼容性。**

普通媒体库扫描用于发现和更新库内容；ScopedImport 用于按指定路径追加缺失条目，不负责同步整个库、删除失效条目或修复已有条目的元数据。插件不会主动调用普通全库扫描、父级刷新或完整元数据刷新作为失败兜底，但 Jellyfin 后台任务、其他插件及媒体探测仍可能产生其他 I/O，不能据此保证整个进程只访问输入目录。

## 从 GitHub Releases 手动安装

### 1. 选择正确的安装包

打开 [Releases 页面](https://github.com/gok2023/jellyfin-scoped-import/releases)，核对版本与测试限制。当前已有 [v0.2.0.0 Release](https://github.com/gok2023/jellyfin-scoped-import/releases/tag/v0.2.0.0)，最初发布于私有仓库；仓库公开不会补齐尚未完成的验证。

在该版本的 **Assets** 下载：

- `ScopedImport-0.2.0.0-jf10.11.11.zip`：可安装插件。
- `ScopedImport-0.2.0.0-jf10.11.11.zip.sha256`：ZIP 的 SHA256 校验文件。

GitHub 自动生成的 **Source code (zip)** / **Source code (tar.gz)** 是源码归档，不含可直接安装的插件 DLL。不要将它们解压到插件目录。

在下载目录用 PowerShell 计算摘要，与 `.sha256` 文件中的摘要逐字核对：

```powershell
Get-FileHash -LiteralPath .\ScopedImport-0.2.0.0-jf10.11.11.zip -Algorithm SHA256
Get-Content -LiteralPath .\ScopedImport-0.2.0.0-jf10.11.11.zip.sha256
```

当前 ZIP 实际包含七个文件：

```text
ScopedImport_0.2.0.0/
  ScopedImport.dll
  meta.json
README.md
CHANGELOG.md
THIRD_PARTY_NOTICES.md
LICENSE
SHA256SUMS.txt
```

ZIP 内的 `SHA256SUMS.txt` 校验插件 DLL；外部 `.zip.sha256` 校验整个 ZIP。包不含 Jellyfin 服务端、参考 DLL、SDK 或测试插件。既有 ZIP 内文档保留发布时的 Private 状态说明；当前安装说明与仓库状态以本页和 [PROJECT](docs/PROJECT.md) 为准，附件本轮未替换。

### 2. 确认该实例的真实数据目录

不要根据 `jellyfin.exe` 所在目录猜测。先查看该实例管理面板的路径信息或启动日志中的数据目录，再核对实际启动参数、环境变量以及服务/托盘配置。自定义 `--datadir`（便携启动也可能写作 `-d`）和 `JELLYFIN_DATA_DIR` 会影响位置；Windows 安装还可能配置注册表 `DataFolder`。多实例必须确认选中的是正确实例。

常见默认插件位置如下；服务运行账户、自定义服务参数及便携启动可能改变位置：

| 启动方式 | 常见插件目录 |
|---|---|
| Windows 托盘应用 | `%ProgramData%\Jellyfin\Server\plugins` |
| 直接运行服务端 | `%LocalAppData%\jellyfin\plugins`（运行账户） |
| 便携版 / 自定义 Windows 服务 | `<实际数据目录>\plugins` |

依据：[官方插件安装说明](https://jellyfin.org/docs/general/server/plugins/)、[官方数据目录规则](https://jellyfin.org/docs/general/administration/configuration/)、[便携 Windows 安装](https://jellyfin.org/docs/general/installation/advanced/manual/)。默认路径只作为定位线索。

### 3. 停止、备份并复制

1. 等待正在进行的入库操作结束，再完全停止该 Jellyfin 实例（Windows 服务停止对应服务；托盘版停止/退出服务器），确认服务器进程已退出。
2. 备份该实例的数据目录、配置目录及旧插件目录，保留可恢复的数据库备份。配置目录若单独设置也要备份；不要在进程写入时只复制单个数据库文件。
3. 将 ZIP 解压到临时目录，只把其中 `ScopedImport_0.2.0.0` 文件夹复制到 `<实际数据目录>\plugins`。根部的说明和许可文件保留供阅读，无需放入插件目录。
4. 升级时先将所有旧 ScopedImport 目录或散放的旧 `ScopedImport.dll` 移到插件目录之外的备份位置，避免同时加载多个版本。只处理此插件，不覆盖其他插件。

安装后的层级必须是：

```text
<实际数据目录>\plugins\ScopedImport_0.2.0.0\ScopedImport.dll
<实际数据目录>\plugins\ScopedImport_0.2.0.0\meta.json
```

DLL 和 `meta.json` 在同一层。不要多套一层 ZIP 名称目录，也不要将 DLL 放进 `plugins\configurations`。确保 Jellyfin 运行账户有读取权限。

### 4. 重启并开始使用

1. 启动原来的 Jellyfin 实例，在管理面板的插件列表检查“定向入库 Scoped Import”及版本；检查启动日志是否成功加载 `ScopedImport.dll`，是否出现 ABI、重复加载或权限错误。
2. 在管理菜单打开“定向入库”，或在该服务器地址（含自定义基础路径）后追加 `/ScopedImport/App`。不要直接打开本地 HTML 文件。
3. 用 Jellyfin 管理员账户登录，确认页面提示“追加保护已启用”。无法启用时停止操作，不修改保护条件强行运行。
4. 输入服务器可访问、属于已初始化媒体库的完整目录/视频路径，选择元数据模式，提交一次并等待结果。首次建议在可恢复的测试库尝试一个小目标。
5. HTTP 202 只表示排队；查看完成、拒绝或部分完成结果。网络中断或超时时先重新查询操作，不在状态未知时重复提交。操作状态在内存中，重启后不能继续查询原任务。

## 升级、卸载、回退与加载排查

升级前核对新版本支持的 Jellyfin ABI 和发布说明；停止服务器、备份、移出旧插件、安装新文件夹，再重启检查。此包关闭自动更新，按手动流程处理。

卸载时等待任务结束、停止服务器，将 ScopedImport 目录及残留的同名 DLL 移出 `plugins`，再重启确认不再加载。先保留配置备份；不用删除整个 `plugins\configurations`，也不要手工删除数据库记录或媒体文件。

回退插件只需在停机时移出新版本并恢复与当前宿主兼容的旧插件文件。**回退或卸载不会撤销已经创建的媒体条目，也不会恢复先前数据库状态。** 若要恢复库状态，应停止服务器，恢复同一时间点的数据及配置备份，并核对对应 Jellyfin 版本；不要合并或手工编辑数据库。没有可验证的备份时，不承诺可无损回退。

插件未出现或加载失败时，依次检查：实际实例与数据目录、DLL/meta 层级、运行账户权限、旧版本残留、Jellyfin 是否确为原版 10.11.11，以及启动日志错误。页面 401 检查登录，403 检查管理员权限；404 检查插件加载与基础路径；“追加保护未启用”检查宿主版本和仓储替换。分享日志前删除令牌、账号、媒体名称、私人路径等信息。

## 限制、风险与测试状态

- 不支持 ISO、STRM、光盘目录、复杂多版本或歧义季集布局；拒绝库根、UNC、越界和重解析路径。
- 只为新条目处理元数据；部分完成不自动回滚，重试不会修复已有条目缺失的元数据。
- 插件通过宿主持久化服务写入媒体库，仍有数据库和库状态风险；管理员权限和仓储代理不是系统沙箱，不能约束其他插件绕过服务的行为。超时为软限制。
- 新工程独立构建与 **35 项安全离线检查**通过：14 命名/布局、8 登录、2 配置页面、11 隔离门禁；MetadataFixtures 仅编译，未加载。
- 历史版本有实际入库/隔离验证记录及用户反馈，不能作为当前正式工程已重新验证的证据。当前真实集成、远程元数据、重启、全进程 I/O 和第三方组合验证尚未完成。

先备份数据与配置，并验证恢复方法；建议在隔离测试实例验收后再决定使用。完整证据和剩余条件见 [当前技术状态](docs/PROJECT.md)。

## 从源码构建

需要 Windows、PowerShell 7、Node.js（HTML mock）、Python 3.10+（离线门禁，无第三方包）。.NET SDK **9.0.318** 由 bootstrap 下载到忽略目录，无需全局安装。

在项目根执行：

```powershell
pwsh -NoProfile -File scripts/bootstrap.ps1
pwsh -NoProfile -File scripts/build.ps1
```

bootstrap 校验固定官方 SDK 和 Jellyfin 分发包，只提取参考 DLL，不启动服务。首次下载约 445 MiB；来源、摘要和 ABI 在 [依赖锁文件](scripts/dependencies.lock.json)。build 使用独立输出目录，校验程序集版本、ABI、GUID 与嵌入页面，执行离线测试后打包到 `artifacts/build-*/`。不要从旧 bin/dist 选取 DLL。

单独离线测试：`pwsh -NoProfile -File scripts/test.ps1`。`build.ps1 -SkipTests` 只供显式诊断，不能算完整验收。集成测试需要已证明隔离的一次性宿主，运行条件和副作用见 [测试说明](tests/README.md)。本轮不发布新的 Stable Release 或 Pre-release；后续二进制发布须独立验收。

## 许可证与免责声明

采用 **GPL-3.0-only**，见 [LICENSE](LICENSE)；第三方版权、来源和历史记录不完整的事实见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。软件按许可证提供，不作适用性或无损运行保证。分发二进制时须保留许可与通知，并提供对应源码及构建说明。

开发者入口：[AGENTS.md](AGENTS.md)、[PROJECT](docs/PROJECT.md)、[CHANGELOG](CHANGELOG.md)。历史资料仅保留为仓库外私有备份，不是构建依赖。
