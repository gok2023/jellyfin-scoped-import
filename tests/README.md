# 测试说明

测试仅覆盖当前 ScopedImport 0.2.0.1，不依赖旧 Python 助手、ReadOnlyPlanner 或 AppendProbe 工程。

## 离线检查

从项目根目录执行：

```powershell
pwsh -NoProfile -File scripts/test.ps1
```

Node 页面测试使用 VM 和假的 XMLHttpRequest/ApiClient，不发送网络请求。命名测试只调用纯解析逻辑，不创建媒体文件。ImportTests 使用当前 Importer/RepositoryGuard 源码、真实 Jellyfin 模型和内存接口替身，在忽略的 `artifacts/import-fixtures-*` 下创建合成 NFO、一个字节的假视频和目录联接；不运行编码器、宿主或数据库。21 组覆盖 NFO 内容降级/DTD 多编码/大小/访问/重解析限制、剧集编号歧义、元数据输入与合并、提供器结果、整体取消、任务锁、重复入库和操作不存在/正常响应的控制器契约；其中一项实际等待 30 秒的合作取消。ProviderStub 对完整刷新、远程图片及其他未预期服务调用均抛错。

锁文件的 `testRuntimeReferences` 固定一个 Jellyfin.Database.Implementations.dll（BaseItem 构造所需），bootstrap 从同一已校验官方归档提取，test.ps1 先核摘要。该 DLL 不作为插件引用，不进入插件 ZIP。两套页面增加 18 个 VM 场景，覆盖根/自定义基础路径、三种终态、模式切换、失败重查、操作恢复、已有项指引和准确 ItemId 详情链接。Python 门禁测试验证默认拒绝、生产端口/外部地址拒绝、错误目录、伪标记、路径逃逸、凭据脱敏及禁止扫描/关闭服务请求。

`test-operation-recovery.cjs` 的 33 个 VM 场景先在补修前复现两页旧编号 404 无法恢复（0 通过、2 失败），再验证修复：专用错误码与对应编号、重复/重叠查询、刷新恢复、显式结束跟踪、就绪拒绝、网络与空超时拒绝、401/403、代理 404/错误编号或错误码、服务故障、不自动重发、local/library 新模式及新编号、服务器/基础路径隔离和旧键显式迁移。配置页替身覆盖 ApiClient 1.11.0 的 Fetch Response.status/json() 拒绝，并兼容 responseJSON/responseText 测试形状。控制器测试直接调用 action，未启动 ASP.NET/Jellyfin，因此不是实际 HTTP 中间件或反向代理验收。

## 集成测试：尚未验证，宿主引导流程尚未实现

`integration/test_scoped_import.py`：真实定向入库、普通用户/匿名权限、库根和逃逸拒绝、观看状态、已有条目和仓储写入审计。会新增测试用户、媒体、条目、播放状态。

`integration/test_v02.py`：剧集目录、季集布局、提供器顺序/禁用/失败、写入保护。会新增样片和条目，临时修改库配置并尝试恢复。

`integration/test_remote_metadata.py`：真实 TMDb 元数据；需要互联网访问，会新增条目及样片，临时修改库配置并尝试恢复。可能产生外部网络请求，不能作为默认测试。

`integration/MetadataFixtures/` 是仅供隔离测试的提供器插件；绝不装入生产或放入 ScopedImport 发布 ZIP。`JELLYFIN_TARGETED_LAB` 必须指向下述宿主根，否则 fixture 拒绝运行。该测试插件会主动尝试违反元数据阶段仓储写入禁令，以确认写入被拦截；如果防护失效，可能留下额外测试条目。

公共工具 `integration/support/lab.py` 提取了旧脚本的 HTTP、认证、样片、快照与哈希能力，移除了宿主启动、关闭、基线扫描、实验探针、旧 planner 业务。导入模块不联网、不写文件。

未完成隔离宿主引导与基线复现，因此本轮不可宣称集成测试可复现通过，也不要创建标记绕过检查。未来独立任务应实现可审查的宿主引导，建立全新测试身份及基线，然后才能执行这些脚本；不要复用旧实验实例凭据、数据库、身份或媒体。

拟采用的隔离协议：

- 根固定为项目 `.local/integration-host/`，端点固定 `http://127.0.0.1:18096`；明确设置 `SCOPEDIMPORT_INTEGRATION=DISPOSABLE-HOST-ONLY` 后仍需完成所有验证。
- 根目录包含 server/data/config/cache/logs/media/evidence；任何目录联接或符号链接拒绝。
- `isolation.json` 是私有引导产物，不提交：kind 为 ScopedImport-disposable-host-v1，version 为 10.11.11，server_id 是全新宿主的 32 位小写十六进制 Id，pid 是该进程 ID。标记本身不能授权联网。
- Windows CIM 必须证明监听 PID、可执行文件及完整参数对应这个宿主。参数严格顺序为 `--datadir <root>/data --configdir <root>/config --cachedir <root>/cache --logdir <root>/logs --nowebclient --nonetchange --ffmpeg <root>/server/ffmpeg.exe`；参数路径请加双引号。
- network.xml 仅允许 127.0.0.1:18096 HTTP 监听；不支持 HTTPS 测试监听。
- 先核实公开 API 的版本、Id 和 ServerName（SCOPEDIMPORT-DISPOSABLE-TEST），才允许发送宿主自己的私有 `test-credentials.json`；随后核实管理员身份、认证后的数据/cache/log目录及所有库路径。
- 仅允许 Lab TV、Lab Movies、Lab Other 三个 fixture 库，路径全在 media 下，库路径分别严格对应 media/tv、media/movies、media/other；禁止实时监听、本地元数据保存、MetadataSavers、章节及 trickplay 图片抽取。
- 宿主应独立创建基线并禁用全部计划任务触发器，任务均须 Idle；认证后预检会核实。基线不由这些测试工具自动扫描。公共工具禁止 Library/Refresh、System/Shutdown/Restart、Startup、旧实验 API。
- 每次 API 请求重验本机进程和监听。任何条件不能证明则失败停止，错误不输出凭据或响应正文。

人工验收隔离协议与创建基线后，以 `python -B tests/integration/test_scoped_import.py` 等入口显式执行；不要使用生产凭据。失败可能留下 fixture 用户/条目/样片，恢复方式是丢弃整个已证明隔离的宿主并重新建立，不对生产作任何操作。当前工具不自动清理或删除宿主。

进程、监听和路径检查只能降低误连风险，不构成针对恶意本机用户或验证期间目录/进程竞争替换的安全边界。测试期间宿主目录应由单一受信任操作者控制。
