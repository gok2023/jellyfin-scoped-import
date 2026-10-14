# 当前技术状态

更新：2026-10-14。阶段：正式工程为唯一活跃开发项目；当前仓库为 **Public**：本轮按用户明确授权，在安全、权属及文档门禁通过后经已认证GitHub CLI直接公开。目标：[gok2023/jellyfin-scoped-import](https://github.com/gok2023/jellyfin-scoped-import)。已有私有阶段发布的 v0.2.0.0 Release；本轮不创建新Release或替换附件，仅同步既有Release说明的当前可见性，不部署或访问生产。公开及匿名访问已验证，结果见本文末尾。

## 版本、许可与维护范围

正式源码在 src/ScopedImport；插件程序集0.2.0.0，GUID 347d2c09-78a1-4697-9c3d-e23566122144。仅支持Windows / 原版Jellyfin10.11.11.0 ABI及BaseItemRepository。

作者决定采用 **GPL-3.0-only**，见LICENSE及THIRD_PARTY_NOTICES。不得把未知来源素材声明为作者原创；历史贡献/摘录记录不完整，未发现具体授权冲突，来源确认仍为待办。如发现实质冲突暂停相应分发。第三方版权和各自许可保留，不被本项目许可替换。

维护源码、脚本、测试、工具、文档及根构建配置；当前版本不是永久上限，未来升级按AGENTS验证并同步版本、依赖、文档。旧工程的历史源码、产物和实验状态已转存为仓库外私有备份，不是构建依赖；原旧根仅保留生产备份，不再作为开发或安装入口。

## 架构

```mermaid
flowchart TD
  UI[app.html / import.html] --> API[Plugin.cs：管理员API]
  API --> Job[Importer：后台单任务]
  Job --> Plan[校验路径/库映射并枚举目标子树]
  Plan --> Layout[EpisodeLayout：季集解析]
  Layout --> New[跳过已有项，准备缺失条目]
  New --> Meta[NFO / 按库远程元数据 / 媒体探测]
  Meta --> Save[共享锁 + ILibraryManager.CreateItem]
  Save --> Guard[RepositoryGuard：DI仓储追加代理]
  Guard --> Persist[Jellyfin持久化服务]
  Save --> Streams[新视频媒体流保存]
  Job --> Status[OperationId状态查询]
  Status --> UI
```

插件运行在Jellyfin内，无Python常驻助手。Python/Node仅作测试工具；媒体探测由宿主编码器驱动，可能启动ffprobe。App允许匿名取页面，入库/库信息/状态/审计API要求管理员。FetchMetadata优先，保留FetchTmdb兼容；202只代表排队，操作状态在内存中，重启丢失。

## 安全边界和技术决策

- 已初始化movies/tvshows库；完整本地Windows路径。拒绝库根、UNC、越界、链接/重解析点及歧义布局。
- 单片电影目录一个主视频；剧集支持NFO、SxxEyy、E01子目录。无季号按第一季，但已有其他季的歧义会拒绝。ISO/STRM/光盘/复杂多版不支持。
- 只处理新对象的元数据，不走完整刷新、远程海报或演员创建。既有项按路径/类型/父级核对并跳过，部分成功不自动回滚；重试不自动修补已创建项。
- 枚举深度8、10000项、120秒；任务15分钟、探测60秒、提供器30秒为软时限。
- 标准Updated通知可能回溯父级，完整刷新会改旧项；“先查不存在再CreateItem”仍有upsert竞争。当前共享仓储锁/追加上下文复查替代盲目创建；不直接写数据库。
- 代理不是系统沙箱或数据库唯一约束；第三方绕过仓储/替换服务、合法后台扫描和不响应取消的I/O不受完整约束。无全进程I/O证明。
- 客户端0.1.1和某条0.1错误提示为保留的历史标识，不是程序集版本。App静态提示现已区分历史隔离验证与新工程离线验证；没有改变页面脚本或入库逻辑。

## 工程与构建

- 仅迁移七项正式文件和当前有效测试。四业务C#未变；HTML仅占位路径脱敏和必要验证说明；csproj仅工程引用/版本/许可/仓库元数据调整。
- 排除旧助手、规划/探针工程、旧安装器、生产备份/数据库/日志、研究快照。必要公共测试工具抽为tests/integration/support/lab.py。
- global.json锁SDK9.0.318；dependencies.lock.json锁官方归档、项目SHA256、上游MD5及六参考DLL摘要/ABI。SDK使用官方SHA512。Jellyfin归档SHA256是项目记录，不是上游签名认证。
- Directory.Build.props仅用本项目.local参考路径，PathMap脱敏PE调试路径。NuGet.Config清空包源（无PackageReference），不读取旧工程DLL。
- bootstrap只校验/提取依赖，不启动宿主；build用唯一输出目录、ArtifactCheck验证版本/资源/ABI/调试路径，再离线测试和白名单打包。
- 当前ZIP白名单：正式DLL、meta.json、README、CHANGELOG、THIRD_PARTY_NOTICES、LICENSE、SHA256SUMS；没有SDK/宿主/凭据/数据库/测试插件；历史发布采用该白名单；本轮只生成离线候选包，不上传。
- 构建清单记录许可、来源待办、源/依赖/包哈希及测试范围，位于忽略的artifacts目录。

## 已完成验证与未完成项

工程迁移和首次提交前终审分别完成35项安全离线检查：14命名、8登录、2配置选择、11隔离门禁。编译0警告/0错误；MetadataFixtures仅编译未装载。原0.2历史记录的隔离入库/远程TMDb/重启结果及0.1实盘用户反馈不是当前新工程重新执行的证据。

终审实际Git枚举37项原源码候选，12项忽略探针均通过；未发现真实凭据、私人路径或私有产物。新许可证加入后，已复核38项暂存清单和远程源码树，远程38个blob哈希与本地首次提交逐项一致。本轮许可/静态文案/打包变更后再次通过35项离线检查，编译0警告0错误；候选ZIP仅含7项正式插件/文档/许可内容。为使克隆源码与验收工作树一致，文本统一LF，不改业务逻辑。

真实基础/v02/远程/重启集成尚未运行：缺独立宿主bootstrap、FFmpeg和可证明隔离的前态。全进程I/O及实际第三方组合尚未验收。正式生产加载版本未知且本轮不查。

## GitHub初始化记录（历史Private阶段）

- 仓库：https://github.com/gok2023/jellyfin-scoped-import；本轮确认目标不存在后新建，Visibility为Private，默认分支main。
- 本地分支：main；Git提交身份已配置（不记录姓名/邮箱）。
- CLI：官方便携版2.102.0，位于.local，归档SHA256核验通过；账号gok2023已认证。CLI确认目标不存在后创建空Private仓库，仓库名称/Private/空状态已核实。
- 暂存：38项明确源码白名单，敏感路径扫描0命中，12项忽略探针全部通过；origin已设置为指定HTTPS地址。首次Commit/Push前已重新通过CLI账号、空Private仓库及origin预检。
- 首次提交标题：chore: 初始化 ScopedImport 正式插件工程。
- 首次提交SHA：4b6037828f0e1b65e05c7f0f3133745753ec52be；对应插件0.2.0.0，含用户指定的标题和八项提交说明。本状态记录以单独文档提交同步，不自引用其自身SHA。
- 首次Push验收：Private、main、首次本地/远程SHA一致，38项远程文件清单/哈希匹配；README、LICENSE、AGENTS和PROJECT存在，无私有数据或外部依赖。状态记录同步后已核验main一致及工作树清洁，最终提交SHA通过Git查询，避免文档自引用哈希。初始化阶段未创建Release；仓库保持Private、禁止生产部署，后续Release授权与结果见发布记录。

## 旧工程退役与验证（历史阶段）

2026-10-14按用户授权完成：补存实验宿主配置、数据库、凭据、日志、fixture、两份旧助手EXE及历史API调查原件；补充归档276文件，连同既有历史112文件共388文件，逐文件大小/SHA256通过。生产备份另有72份副本；原件的路径、内容摘要、大小、创建/修改时间及属性均未改变。归档和详细清单仅保存在仓库之外的私有目录，不进入GitHub。字节一致不等于已验证数据库恢复能力；仍建议离机备份。

旧SDK与独立新SDK逐文件一致；旧服务端与锁定官方包、上游源码与固定v10.11.11 Git blob逐项核对。三个包外安装文件另行保留哈希归档。旧工程所有非生产备份文件已有归档或固定重取证据，删除8,523文件，旧根只剩production-trial-backups的72原件。未删除旧根，未运行旧安装器、服务或数据库。

新工程仅删除.local/downloads中SDK9.0.318 ZIP缓存，官方固定URL可用、SHA512与锁文件一致；现有SDK、六参考DLL、CLI、Git历史和候选包均保留。ZIP仅bootstrap使用，当前build/test/package不读取它；再次执行bootstrap会按锁文件重新下载SDK归档。

清理之后独立build.ps1再次通过，0警告/0错误，35项安全离线检查通过（14命名、8登录、2配置、11隔离门禁）。MetadataFixtures只编译；真实集成/重启仍未验证。退役验收当时的候选目录为artifacts/build-ab46b3376cc9487fbc03a705ea5d17df（发布收尾后已由下方最终目录替代），ZIP通过版本/资源/ABI和7项白名单检查。业务代码未改；退役执行阶段没有提交或推送Git，本次收尾仅提交本文。

退役验收时，旧工程文件分配空间从1,587.13MiB降至463.18MiB，回收1,123.95MiB；新工程SDK ZIP缓存回收284.38MiB。扣除本轮构建与文档增量，两处D盘工程净减少1,476,271,000字节（约1.3749GiB），新工程剩余958.49MiB。前一低风险清理阶段另回收61.55MiB，不重复计入本次退役量。上述为NTFS文件分配空间，不含目录/MFT元数据；仓库外私有备份另占空间，复制本身不释放源位置空间。

未来完全删除旧根的唯一剩余文件范围是生产备份；必须获得单独明确批准，先评估恢复验证和离机副本。本轮到此停止。

## v0.2.0.0 最终工程版发布（历史Private阶段）

历史阶段用户授权发布Release及清理旧构建产物；当时仓库保持Private，不部署生产。发布标签v0.2.0.0，对应当前发布文档提交及相同七项正式源码；具体提交SHA以Git标签查询为准，不自引用。本轮只调整README、CHANGELOG、第三方分发说明和当前状态，不修改业务代码/版本/GUID。

Release：https://github.com/gok2023/jellyfin-scoped-import/releases/tag/v0.2.0.0。资产仅ScopedImport-0.2.0.0-jf10.11.11.zip及同名.sha256；GitHub自动提供该标签源码归档，发布时接收者须具备该Private仓库及标签源码权限；公开后该标签随仓库开放。GPL-3.0-only、版权声明和历史来源不确定性继续保留，未发现具体授权冲突。

发布前重新独立构建并执行35项安全离线检查（14命名、8登录、2配置、11隔离门禁），0警告/0错误；包白名单7项，校验版本/GUID/资源/ABI及对应源码摘要。MetadataFixtures仅编译；真实集成、重启和全进程I/O仍未验证，不因创建正式Release而提升验证结论。

本地仅保留artifacts/ScopedImport-0.2.0.0：安装ZIP、外部SHA256文件、构建清单，以及可直接复制的ScopedImport_0.2.0.0目录（DLL和meta）。其他build-*输出在最终包核验及远程附件下载SHA256复核通过后清理。SDK、六参考DLL、CLI、源码/Git、私有历史归档和生产备份均保留。远程Release从Draft完成附件核验后发布为非Prerelease，并设为Latest；不覆盖既有版本。

## 下一步与文档维护

首次私有源码初始化、旧工程退役、离线验收及Private Release已完成。本轮完善公开用户说明并检查所有可访问Git历史和附件，通过门禁后公开源码仓库；不自动创建新Release或替换既有附件。继续补历史贡献来源、实现严格隔离宿主引导和真实集成/重启验证，再补系统I/O证据；不得把离线通过写成完整集成通过。

README服务用户；AGENTS规范开发；本文是唯一技术状态入口；CHANGELOG只记版本变化，不新增重复STATUS/TODO/SESSION。下一任务先读README和本文，按具体模块读取源码。

## 本轮正式公开与验收（2026-10-14）

私有历史归档现为仓库外私有备份，位置不在公开文档中记录。本轮不读取、迁移或上传其内容，不修改旧工程、生产数据或媒体目录。历史Private初始化/Release事实保留，公开不会提升插件测试等级。

本轮README补齐功能、适用条件、与普通扫描区别、实际七文件ZIP结构、数据目录定位、停机备份、手动安装、升级卸载、回退和加载排查；依据官方插件/数据目录说明及实际附件。本轮未修改业务源码、版本、GUID或构建逻辑。既有ZIP文档属于私有发布时快照，保留原附件，当前状态以main文档为准。

正式发布前仍需：补充历史贡献来源；建立可证明隔离的一次性宿主，执行基础/v02/远程元数据/重启集成与第三方组合测试，再提供系统I/O证据；独立验收对应源码与候选包后决定后续版本发布。

公开前门禁结果：基线main为b87ccb81cae47df1a57db8b56575eff41595ff68；本地非浅克隆，远程完整引用与本地一致（main及v0.2.0.0注释标签）。检查全部5个可达提交、45个唯一历史blob、38个历史文件路径、提交/标签说明及本地3个悬空blob；未发现实际凭据、个人绝对路径、生产配置、数据库、日志、媒体或私有实验产物。候选命中为mock值、测试哨兵、API路由和插件GUID，逐项复核排除；未发现明确权属/许可冲突，历史来源不完整仍需补充。不执行历史重写或强制推送。

远程仅main和一个标签；无PR、Actions附件、Wiki、Pages或Fork。既有Release为非Draft、非Prerelease的v0.2.0.0，附件仅安装ZIP及.sha256；API下载ZIP的SHA256与GitHub附件摘要和本地旧包一致，实际七项内容已核查。源码38项实际跟踪清单通过，19项忽略探针全部通过；本地Markdown链接和公开上游链接有效，Private仓库链接已认证核实，公开后另行匿名复验。

本轮文档修改后的build.ps1完整通过：编译0警告/0错误，35项安全离线检查通过，ArtifactCheck及ABI、版本/GUID/嵌入资源/调试路径与七项包白名单通过。候选包在artifacts/build-80fc2472c32c4ffe94b3af74a686c52a/，未上传；MetadataFixtures仅编译。git diff --check通过，修改仅README、PROJECT、CHANGELOG及第三方分发说明，无业务源码变更。没有访问生产服务、数据库、媒体挂载或私有归档。

公开完成：GitHub CLI确认gok2023/jellyfin-scoped-import为Public；GPL-3.0-only保持不变。本轮公开文档提交SHA为af6e6b46f2be2e5fb82a94ac84f154b33e94e266，已与远程main逐项核实。普通Git HTTPS推送受连接重置阻塞，改用已认证CLI Git Data API同步四个blob、tree及commit，所得SHA与本地完全相同，非强制更新main；没有历史重写。最终状态记录另作中文文档提交，其SHA不在本文自引用。

未携带认证信息的公开HTTP访问：首页、README、LICENSE、Plugin.cs、PROJECT、Releases列表及v0.2.0.0页面全部200；三个raw文档字节与本地一致，公开API确认private=false，远程完整38个blob哈希与本地HEAD一致。所有Markdown本地路径及外部文档/许可链接均有效。

既有正式Release仍为v0.2.0.0（非Draft/非Prerelease），仅原安装ZIP和.sha256两个附件，ID和SHA256均未改变。本轮只更新既有Release说明，将Private明确作为历史并链接当前README；没有新建Stable/Pre-release、改标签或上传文件。历史ZIP内说明不替换，当前状态已在main及Release页面解释。

真实集成、远程元数据、重启、全进程I/O及第三方组合验证仍未完成；下一步先完成隔离宿主与独立验收、补历史来源，再决定后续正式发布计划。本轮源码公开与用户文档完善到此完成，不部署生产。
