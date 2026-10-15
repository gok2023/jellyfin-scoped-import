# 当前技术状态

更新：2026-10-16。当前版本 **0.2.0.1**，本轮按用户明确授权完成正式版本构建和 GitHub 发布流程；发布及清理结果在本页的正式产物节记录。仓库：[gok2023/jellyfin-scoped-import](https://github.com/gok2023/jellyfin-scoped-import)，Public，main。起始 HEAD 为 a3d0e470f119f1c5784d6e1cf928ab86cb48b7b6，先前未提交修复均属本次范围。生产服务、数据库、凭据和媒体未访问，不重写历史、不强推。

## 当前版本、身份和许可

- 插件 0.2.0.1，稳定 GUID `347d2c09-78a1-4697-9c3d-e23566122144`，Windows x64 / 原版 Jellyfin 10.11.11（ABI 10.11.11.0）及 BaseItemRepository。
- csproj 的 AssemblyVersion/FileVersion/Version、Libraries API、客户端/页面、meta.json 与发布文件名均为 0.2.0.1；历史 0.2.0.0 标签/Release/附件保留。
- GPL-3.0-only，见 LICENSE 和 THIRD_PARTY_NOTICES；Jellyfin 参考/测试组件保留其许可。历史贡献/摘录来源不完整，未发现明确冲突，来源补充仍是待办，不声明全部材料为原创。
- 维护 src/ScopedImport、scripts、tests、tools、docs 和根构建说明；.local、artifacts、旧生产备份不入 Git。AGENTS 已审阅规则以独立文档提交纳入，不改变其他长期规则。

## 当前行为与安全边界

ScopedImport 在 Jellyfin 内运行，由管理员提交具体路径，后台单任务枚举目标范围并只追加新条目。两套页面分别是独立 App 与管理配置页，使用 OperationId 查询；202 只表示已排队。FetchMetadata 优先，保留 FetchTmdb 兼容。

- 仅已初始化的本地 Windows movies/tvshows 库；拒绝库根、UNC、越界、重解析点和歧义布局。电影为独立单片目录一个主视频；剧集支持 NFO、SxxEyy、E01 子目录，无季号且无既有其他季歧义时按第一季。
- 现有条目按路径/类型/父级核对并跳过；不修补其元数据。创建前复查目标和仓储冲突；共享保存锁和追加上下文限制写入新 ID，不直接写数据库，不用全库扫描、祖先刷新或完整刷新兜底。
- 枚举深度 8、10000 项、120 秒，任务 15 分钟、探测 60 秒、提供器 30 秒为合作式软时限。第三方绕过仓储或不响应取消不受完整约束，代理不是系统沙箱。
- 媒体探测由宿主编码器驱动，可能启动 ffprobe；按库模式可能访问外部提供器。不支持 ISO/STRM/光盘目录、快捷方式、多版本和歧义季集，不承诺整个 Jellyfin 进程只访问输入目录。

## 0.2.0.1 修复与能力边界

源码确认的原问题：两种模式都先读取 NFO，格式异常直接中断；切换模式仍会碰同一坏文件。原电影目录名未解析年份，远程合并还会重复读取 NFO；GetMetadata 仅合并部分字段，不等同完整刮削。Start 原已有 finally 释放锁；没有证据证明用户原现场永久锁死。配置页查询失败会重新允许提交，与独立页不同；之后又在两页复现旧编号 404 持续 busy，已补修。

- NFO 按操作读一次，终态清理 XML 缓存。无 NFO 可基础入库；非 XML、空/损坏文件、不支持根类型给中文警告后降级。合法 XML 注释/CDATA 可正常读取。打开流受 2,000,000 字节限制；DTD 禁止、XmlResolver=null、字符上限保留，UTF-8/16/32 回归覆盖。路径/访问/大小/取消/预算及仓储保护异常不降级，原文件不改，不联网解析 NFO URL。
- 电影名称/年份用现有 Emby.Naming 纯解析；NFO 标题/年份/ID 优先，提供器返回的 ID 转交下一个提供器，空简介可补齐。选择和顺序委托固定版本的 [ProviderManager](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Providers/Manager/ProviderManager.cs)，不强制启用禁用提供器。支持字段随新对象 CreateItem 保存。
- Movies 状态区分 Created/ExistingSkipped 及 LocalOnly/FetchedFields/NoMatch/ProvidersUnavailable/ProviderFailed/ProviderTimeout；逐提供器记录 Metadata/NoMetadata/Error/Timeout。FetchedFields 只代表获取支持字段，不等于完整刮削、海报或演员流程。仓储保护异常专门分类并终止任务。
- 两页按准确 ItemId 打开电影详情，兼容服务器基础路径且不携带令牌；用户正常登录后仍需点击更多菜单“刮削元数据”，输入名称、年份或 ID，自行选择结果。已有电影重新提交仍跳过写入，并给出同样指引，不宣称已检查其完整性。路由/菜单依据 [Web appRouter](https://github.com/jellyfin/jellyfin-web/blob/v10.11.11/src/components/router/appRouter.js) 和 [itemContextMenu](https://github.com/jellyfin/jellyfin-web/blob/v10.11.11/src/components/itemContextMenu.js)，未猜测识别表单深链。
- 专用操作不存在响应为 HTTP 404 + Code=ScopedImport.OperationNotFound + 对应 OperationId，并声明 NoStore。两页按结构化状态区分网络/超时、401/403、代理或未确认 404、5xx、格式错误。确认失效仍提示原结果无法确认，不代表失败/没有写入；仅用户显式结束跟踪后重新检查 Libraries，就绪才开放新任务，不自动重发 POST。普通网络错误不能直接解锁。
- 新存储键按服务器地址（含基础路径）和身份隔离，两页共享当前作用域。旧全局键不自动认领/查询，用户确认归属才迁移，或显式结束旧跟踪。清除只影响跟踪，不取消服务端任务，不绕过任务锁和既有项检查。状态在内存，重启/淘汰后无法恢复原结果；POST 响应丢失仍需人工确认服务端。

## 验证依据与用户反馈

**用户反馈**：本轮用户报告“已实际测试修复版，通过、没有发现问题”。对应上一轮交付目录的 0.2.0.1 DLL，SHA256=`2a11e9d8b576ac77b9c0fd9ef627a2709660bd29eca23a3280c0f62d3df0e709`；原 ZIP SHA256=`6d52c580e180c295b764b286ad0800a6a9aedd5e65234e70ff722d0ee8eba15b`。开始时实际 DLL/ZIP、meta、当前 src 与依赖锁逐项匹配原清单。用户没有逐项提供测试条件、场景、日志或 I/O 证据，反馈不能被扩写成全部场景、反向代理、完整自动刮削或全进程 I/O 通过。

**历史离线证据**：修复前 VM 先复现旧编号问题（两页 0 通过/2 失败）；之后 107 项离线检查通过。此前 0.2.0.0/旧工程真实入库结果不作为 0.2.0.1 重新验收证据。本轮发布重新执行正式构建和离线测试，不借用旧 manifest。

**本轮构建**：将执行 scripts/build.ps1；最终结果、摘要及用户测试版差异见正式产物节。测试为合成 NFO/一字节假视频、内存替身和 Node VM，不启动 Jellyfin/编码器/数据库。14 命名、8 登录、2 配置选择、18 导入 UI、33 恢复 UI、21 ImportTests、11 隔离门禁，共 107 个不同粒度用例/检查组。MetadataFixtures 仅编译不装载；测试说明见 tests/README.md。

**未验证**：可证明隔离的一次性宿主引导尚未完成；本轮不运行真实集成/重启、反向代理、全进程 I/O 或第三方组合。控制器契约直接调用 action，非 ASP.NET 中间件集成；VM 不等同真实浏览器。软时限不能强制终止不合作提供器。旧 DLL 裸 404 或丢弃 JSON 的代理无法提供不存在确认。Linux/Docker/其他 ABI 未验证。

## 工程、依赖与正式产物

- global.json 锁 SDK 9.0.318；dependencies.lock.json 固定官方 Jellyfin 归档 SHA256/上游 MD5、六参考 DLL 和一个离线模型测试 DLL 的摘要/ABI/许可。归档 SHA256 为项目观察，不是官方签名认证。无 PackageReference，不读旧工程 DLL。
- bootstrap 只下载/提取固定引用，不启动宿主；本轮复用已核验依赖，不重新引导。build 使用唯一输出目录，ArtifactCheck 校验版本、ABI、资源、调试路径，再离线测试和白名单打包。
- ZIP 七文件白名单：ScopedImport_0.2.0.1/ScopedImport.dll、同目录 meta.json、README、CHANGELOG、THIRD_PARTY_NOTICES、LICENSE、SHA256SUMS.txt。不含 SDK/宿主/数据库/凭据/日志/媒体或 fixture DLL。
- 正式目录固定为 artifacts/ScopedImport-0.2.0.1，保留 ZIP、外部 SHA256、可安装子目录、build-manifest.json 和必要脱敏验证摘要。仅在远程正式附件核验完成后清理其他 artifacts 子项；不跟随重解析点，不触及 .local、tests、Git 或仓库外备份。
- 发布按正常快进推送、v0.2.0.1 标签、Draft 上传/下载摘要核验后转正式 Latest；不强推、不覆盖不同身份版本，不改变可见性/许可/无关设置，保留 v0.2.0.0 标签和附件。当前执行结果将在本节收尾记录。

## 下一步

继续补充历史贡献来源；独立完成严格隔离宿主引导后，验收真实提供器、持久化/重启、Web 登录/代理/基础路径及无关既有项快照，再补系统 I/O 和第三方组合证据。完整自动图片/演员刮削须另行审查 10.11.11 刷新调用链、人物/父级/关联写入、图片文件范围与预算，不能因 RefreshSingleItem 名称而放宽边界。本轮不新增此业务，不访问生产。

## 历史记录说明

以下保留截至 0.2.0.0 的初始化、公开及退役事实；其中“本轮”指当时阶段，旧 artifacts 路径只是历史证据来源，不覆盖上文 0.2.0.1 当前状态。发布收尾将提取必要脱敏摘要，再清理旧临时产物。

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
