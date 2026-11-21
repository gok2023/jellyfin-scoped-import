# 当前技术状态

更新：2026-11-21。源码版本 **0.2.0.3**；Windows x64 保留原版 Jellyfin **10.11.11 / ABI 10.11.11.0** 构建，新增 **12.4 / ABI 12.4.0.0** 构建。稳定插件 GUID：347d2c09-78a1-4697-9c3d-e23566122144。正式发布标记为 v0.2.0.3；12.4 生产插件已按用户授权安装，用户已确认实际使用通过。

## Jellyfin 12.4 兼容构建（2026-11-21）

以 `5b667b8` 的 0.2.0.3 定制源码为基础，新增独立的 **12.4 / ABI 12.4.0.0 / .NET 10** 构建；插件版本、GUID、嵌入页面及原中文操作界面保留。10.11.11 默认构建和已验证旧安装包继续保留。本轮已获授权维护 main 并发布 v0.2.0.3；10.11 与 12.4 包独立标记。

12.4 的查询仓储通过工厂共享 `BaseItemRepository`，写入独立为 `IItemPersistenceService`。服务注册复用原查询实例，在新持久化接口上复用原 RepositoryGuard 的追加、只读和新对象身份限制；不识别的仓储/装饰器保持未就绪。`SaveImagesAsync` 按新对象身份核验，`ReattachUserDataAsync` 仍不转发新对象的历史数据重挂接。保留电影、Series、Season、Episode 新对象的原单项元数据/图片刷新顺序；不开放任意已有记录修改，不使用全库扫描兜底。

新增目录接口以固定、已核验清单回答查询；`Invalidate` 不扩展清单，`Move` 明确拒绝移动目录。独立管理页使用带 Token 的标准 Authorization，不使用 X-Emby-Token，也不启用 Jellyfin 旧认证。

依赖与许可锁见 `scripts/dependencies-jf12.lock.json`。引用从此前已核验的官方 Windows 12.4 ZIP 提取，与正式程序相应文件逐项匹配；SDK 为官方 10.0.404。初次准备使用 `scripts/bootstrap-jf12.ps1`，不会启动宿主；已有校验材料可通过参数复用。构建入口：

```powershell
pwsh -NoProfile -File scripts/build-jf12.ps1
# 可用 -DotnetPath 指向另一份已验证的 10.0.404 SDK，避免重复解压。
```

本轮结果：12.4 插件、ArtifactCheck 和纯内存 PersistenceTests 编译零警告零错误；追加/既有项拒绝、只读、身份变更拒绝、异步图片、用户数据重挂接抑制、删除审计及目录禁止移动回归通过；8 项登录与配置页回归通过。没有创建测试媒体，没有启动隔离宿主或运行真实入库。10.11 默认构建及产物检查通过，使用 `-SkipTests`，不计作本轮完整旧版集成回归。

当前 12.4 安装候选为 `artifacts/build-jf12-174b33d9d1564684946ebc09912a19f6`，DLL、目标 ABI 和源文件摘要记录于 build-manifest.json。用户授权后正常停服安装到实际插件目录，重启为 Active；`GET /ScopedImport/Libraries` 管理员响应 Ready=true，未认证为 401，`Audit.Sequence=0`。配置哈希未改变，Playback Reporting 历史逐行保持；个人观看基线不公开。没有提交 Import、PickFolder 或刷新操作。

用户现已确认 Scoped Import 真实入库和刮削通过实际使用验收；此前 Active/Ready 与内存回归分别作为自动化证据保留。原有《流人》季关联问题保持独立，未清理已有媒体或修复生产数据库。Lllllvlv 源码市场保存两个 ABI 的独立包及摘要；实际可用构建以其 catalog.json 为准。长期入口见 `D:\TOOL\freebo\source\docs\jellyfin-maintenance.md`。

## 当前功能与流程

1. 管理员选库、选择服务器可访问的目录或视频文件；验证物理根、路径边界、重解析点、枚举预算及任务锁。
2. 使用 Jellyfin ResolvePaths、Emby.Naming 与受限目录服务识别 Movie、Series、Season、Episode；支持无编号季集。EpisodeLayout 保留供既有回归使用，不是当前导入主解析入口。
3. 查询仓储与宿主对象，跳过已有项。多个同编号季中，唯一精确路径且身份一致的目标可复用；其它季保留并警告，剧集归属重叠或不明确时拒绝。无路径非虚拟季可核验复用；虚拟季转换未开放。
4. 新对象读取本地 NFO、按模式获取文字元数据，探测新视频，按父子顺序追加；媒体流保存限新 ID。坏 NFO 内容可降级，DTD/大小/路径/访问安全错误仍停止。
5. 联网模式保存完成后，依次等待新 Series、新 Season、新 Episode 的原生单项图片刷新；电影只刷新新 Movie。已有剧新增季只处理新季集，已有季新增集只处理新集，重复导入不重刮已有项。
6. 操作返回新增、跳过、文字提供器及 NativeRefresh 结果。HTTP 202 仅表示排队；图片失败保留新增结果，返回调用与主图登记状态，不能据此宣称完整刮削成功。操作状态保存在内存，页面支持编号跟踪与显式失效恢复。

## 元数据与保护边界

- 联网：按库启用的 GetMetadataProviders/GetMetadata 获取并合并文字字段；随后调用 IProviderManager.RefreshSingleItem。MetadataRefreshMode=None 避免重复文字请求，ImageRefreshMode=FullRefresh 请求缺失图片；ReplaceAllMetadata、ReplaceAllImages、RemoveOldMetadata、ForceSave、IsAutomated 均 false。已接入原生图片流程，不是只取文本，也不等同完整原生文字/人物关联刷新。
- 离线：只读取本地 NFO 和基础媒体信息，不调用远程元数据或原生远程图片流程；无 NFO 允许基础入库。
- 创建阶段 AppendScope 只允许新 ID，核对路径、父级及季集编号。元数据阶段 NewItemMetadataScope 按本次新对象的 ID、类型、路径、父级、SeriesId/SeasonId 与编号限定 SaveItems/SaveImages；不允许任意已有项更新或删除。SaveImages 只替换获准新条目的图片关联信息。
- 原生 Series 服务可能处理季集，因此刷新前核对关联子项，发现既有或虚拟子项时跳过。首次图片刷新中的 ReattachUserDataAsync 对核验通过的新对象不转发，避免重新分配历史播放数据；人物服务的额外写入未开放。
- 新季集创建时核对持久化 Series 展示键，Series 原生刷新后仅同步本次新建子项；已有异常键不自动修复。保护异常记录方法、ID、原因和调用栈。
- 不调用全库扫描、父级递归刷新或 RefreshFullItem 兜底，不手工写 SQLite。保护不是系统沙箱，不能约束其它插件绕过接口、宿主后台任务或全部文件 I/O；超时是软限制。

## 历史验证与用户反馈（截至 2026-11-13）

以下为 10.11.11 的历史证据；本轮 12.4 执行结果以上方兼容构建一节为准。

- 已修复 `RepositoryGuard` 在原生删除返回后将 GUID 列表误转为单个 GUID 的异常。使用本地 Jellyfin 10.11.11 引用编译通过（零警告、零错误），`ImportTests --repository-delete` 用内存替身验证两个 ID 按顺序转发一次并完整写入审计。用户报告手动替换 Debug DLL 后测试通过，并确认该 DLL 为当前本地安装基线，版本仍为 0.2.0.3。
- 用户于本轮明确确认：已把最新插件安装到实际 Jellyfin 10.11.11，实际使用后整体功能满足当前需求。此为用户反馈，未附逐场景证据或安装 DLL 与源码的逐字节对照，不扩大为所有场景的正式集成验收。
- 维护前最新构建记录：build-bc128b44e5134cdb82df0fc2657402d7，0.2.0.3，36 项 ImportTests、11 项隔离门禁及页面/命名回归通过。历史中间产物将在最终构建通过后清理，不再作为可用文件路径引用。
- 本轮执行 scripts/build.ps1 通过：零警告零错误，124 个不同粒度离线用例/检查组通过（14 命名、8 登录、2 配置、18 导入 UI、35 恢复 UI、36 ImportTests、11 隔离门禁）；ArtifactCheck 的版本/ABI/资源与七文件白名单通过，git diff --check 通过。最终产物位于 artifacts/build-628c22ef54ba477086b5eebd38212815；本验收补记晚于打包，业务源码和测试未再修改。测试使用合成 NFO/假视频、接口替身、真实模型方法和 Node VM；Series.GetSeasons 配合内存查询通过，不代表真实 HTTP 季列表。
- 真实隔离宿主引导尚未完成：实际图片下载及落盘、真实数据库/播放记录快照、HTTP 用户筛选、重启、反向代理、全进程 I/O 与第三方组合仍未逐项验收。MetadataFixtures 仅编译，不加载。不得用用户整体反馈或 Mock 替代这些项目。

## 已知问题与下一步

**《流人》部分已有季仍未显示，尚未彻底解决。** 已发现 Season/Episode 展示关联键失配及当前无可见目录的重复 Season；没有自动修复或清理。事实、来源不确定性和后续授权边界见 [诊断摘要](SEASON_VISIBILITY.md)。本次工程收尾不继续修改入库核心逻辑。

后续独立研究该问题；准备可证明隔离的宿主完成图片与持久化验收，补充历史贡献来源，再决定是否单独发布 0.2.0.3。正常维护保留数据库保护、路径隔离、媒体识别及操作状态回归。

## 工程与发布维护

正式目录为 src/ScopedImport、scripts、tests、tools、docs 及根构建/许可文件。10.11 流程的 SDK 9.0.318 与 Jellyfin 依赖锁固定；12.4 使用独立入口和锁（见上文）；bootstrap 只获取引用，不启动宿主。build.ps1 执行构建、ArtifactCheck、离线测试及七文件安装包白名单；build-manifest.json 记录 DLL/ZIP、源码和依赖摘要。

artifacts 与 .local 不提交。已清理 17 个旧 build-* 和 15 个合成 import-fixtures-*（含最终回归产生的 fixture），保留最终构建、全部正式测试、依赖、许可及维护文件；删除前核实 artifacts 范围，联接仅移除节点且不跟随，未删除 tests 或依赖；不触碰生产文件、个人备份或用途未确认的 .local 历史材料。Git 正常提交/推送，不改历史、不强推、不改旧 Release。历史工程/发布细节保存在 Git 历史，不再堆入当前状态文档。

许可证为 GPL-3.0-only；第三方声明与历史来源不确定性见 [THIRD_PARTY_NOTICES](../THIRD_PARTY_NOTICES.md)。安装与升级见 [README](../README.md)，长期规则见 [AGENTS](../AGENTS.md)，测试操作见 [tests/README](../tests/README.md)。
