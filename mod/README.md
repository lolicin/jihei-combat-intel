# CombatInspector — 《机骸：第九行星》战斗详情读取 Mod

**能。而且这个游戏是我见过最适合做这件事的一类。** 下面先给结论和证据，再给已经写好并编译通过的实现。

---

## 1. 结论：为什么完全可行

从游戏目录逆向确认的事实：

| 项目 | 实测值 | 对 mod 的意义 |
|---|---|---|
| 引擎 | **Unity `2023.2.20f1c1`**（Unity 中国版，从 `globalgamemanagers` 头读出） | 现代 Unity，工具链齐全 |
| 脚本后端 | **Mono**（`MonoBleedingEdge\mono-2.0-bdwgc.dll` + `_Data\Managed\*.dll`） | ✅ **决定性优势**：不是 IL2CPP，可以直接用 C# 写插件、用 Harmony 打补丁，不需要 Il2CppDumper / 不需要还原类型 |
| 架构 | **DOTS / ECS**（`Unity.Entities`、`Unity.Collections`、`Unity.Mathematics`、`Unity.Transforms`、`Unity.Physics`、`Unity.Burst`） | ✅ **第二个决定性优势**：战斗状态不是散落在 MonoBehaviour 里的私有字段，而是结构化的 ECS 组件数据，可以整批查询 |
| 符号 | `Assembly-CSharp.pdb` **随游戏一起发布** | 反编译能拿到完整命名，连局部变量名都在 |
| 反作弊 | 无（只有 `steam_api64.dll`，纯 PvE 生存类） | 不会被踢 |
| 代码保护 | 无混淆、无加壳 | 直接可读 |

**关键点**：Mono + DOTS 的组合意味着——你要的"所有自身状态、敌人状态、位置"，本质上就是**遍历几个 EntityQuery 然后读组件字段**，不需要指针扫描、不需要猜内存偏移、不会因为游戏更新而失效（组件名一变编译就报错，而不是静默读错数据）。

游戏自己的代码就是这么干的。`DevTestTools.cs`（开发者作弊面板，随包发布）里那段就是标准答案：

```csharp
World world = World.DefaultGameObjectInjectionWorld;
EntityManager em = world.EntityManager;
using EntityQuery query = em.CreateEntityQuery(ComponentType.ReadOnly<PlayerTag>());
Entity player = query.GetSingletonEntity();
int levelBefore = em.GetComponentData<PlayerLevel>(player).Level;
```

本 mod 用的就是这个访问模式，所以它是**游戏自己验证过的路径**，不是我猜的。

---

## 2. 战斗数据字典

`Assembly-CSharp.dll` 反编译后有 **1692 个类型**：297 个 `IComponentData`、16 个 `IBufferElementData`、86 个 `ISystem`、113 个 `MonoBehaviour`、53 个枚举。全部类型清单在 `../component_inventory.txt`。

以下是战斗相关的核心部分（字段名均为反编译得到的**真实字段名**）。

### 2.1 阵营 / 身份（零大小 Tag）

| 组件 | 含义 |
|---|---|
| `PlayerTag` | 玩家机甲（本地是单例；联机时每个玩家机甲都带） |
| `EnemyTag` | 敌人 |
| `SquadAllyTag` | 小队友军 |
| `BossTag` | Boss，**带字段** `int StageIndex` |
| `NetworkMechSlot` | `byte Value`，联机槽位，用来区分"哪个是我" |
| `EliteEnemyTag` | 精英怪 |
| `MeleeEnemyTag` / `RangedEnemyTag` / `FlyingMeleeEnemyTag` / `DashMeleeEnemyTag` / `SuicideBomberEnemyTag` | AI 类型（对应枚举 `EnemyAIType`：Melee / Ranged / FlyingMelee / DashMelee / SuicideBomber / InterferenceFloater） |
| `DevInfiniteSurvivalTag` | **可启用组件**，开发者无限血甲精神（游戏自带 F2） |

### 2.2 血量 / 生存

| 组件 | 字段 |
|---|---|
| `CharacterCurrentHP` | `float Value` |
| `CharacterMaxHP` | `float Value`（基础）、`float FinalValue`（**生效上限**，UI 用的是这个） |
| `IronPlatingData` | `Value` / `MaxValue` / `MaxValueBase` — 重型机 Iron 的装甲 |
| `BeingSpiritData` | `Value` / `MaxValue` / `GrowthBase` 等 13 个字段 — 6 号机 Being 的精神力 |
| `PassiveRegenData` | `RegenRate` / `ActivationDelay` / `Timer` / `IsActive` — 脱战回血 |
| `InjuryMitigation` | `Value` / `ValueBase` — 减伤 |
| `MaxHPFactorData` / `ArmorFactorData` / `ExpRequiredFactorData` | 各 5 个 `bonus1..bonus5` 乘区，最终值 = 基础 × bonus1 × … × bonus5 |
| `DamageThisFrame` | **Buffer**，`float Value` 每元素一条 — 挂在**受击者**身上，本帧受到的每一笔伤害 |
| `BeDamagedThisFrame` | 可启用 Tag，本帧被打过 |
| `HitStunTimer` | `float Remaining` — 硬直 |
| `EnemyHitFlashTimer` | `float Remaining` — 受击闪白 |

> `DamageThisFrame` 的语义是从 `ProcessDamageJob.Execute(Entity entity, ref CharacterCurrentHP hp, DynamicBuffer<DamageThisFrame> damageBuffer, ...)` 确认的：它和 `CharacterCurrentHP` 在同一 query 的 RW 侧，所以是"该实体本帧承受的伤害列表"。做 DPS 统计 / 伤害溯源直接用它。

### 2.3 玩家成长 / 输出

| 组件 | 字段 |
|---|---|
| `PlayerLevel` | `int Level` / `float Exp` / `float MaxExp` |
| `PlayerAttackData` | `AttackPower` / `AttackPowerBase` / `CooldownTime` / `CooldownTimeBase` / `AttackMoveSpeed` / `DetectionRadius` / `ProjectileSpawnYOffset` / `CollisionFilter` / `AttackPrefab` |
| `PlayerSkill` | `Type`(枚举) / `CDType` / `IsActive` / `CanSkill` / `CDingSkill` / `CDtimer` / `CDtime` / `CDtimeBase` / `Damage` / `SkillTimes` / `MaxChargingTimes` |
| `PlayerPerk` | **Buffer**：`int PerkID` / `byte Level` |
| `PlayerKillCount` | `int Value` |
| `CharacterMoveSpeed` | `Value`（当前）/ `ValueBase`（基础） |
| `StormPassiveAttackSpeedData` | 16 个字段：`Stack` / `MaxStack` / `Timer` / `Duration` / `AttackSpeedPerStack` / `CritRatePerStack` / `AttackDamagePerStack` … 轻型机 Storm 的叠层 |
| `CritDamageData` / `AttackCritDamageMul` | 暴击伤害 |

`PlayerSkillType` 枚举（带中文 InspectorName）：`Sun`(中型机) / `Storm`(轻型机) / `Iron`(重型机) / `Being`(6号机) / `ReservedMech4` / `ReservedMech5`。

### 2.4 敌人 / Boss

| 组件 | 字段 |
|---|---|
| `EnemyDisplayName` | `FixedString64Bytes Value` — **敌人中文名** |
| `EnemyAttackData` | `int Damage` / `float CooldownTime` |
| `EnemyCurrentTarget` | `Entity Value` — 它正在打谁 |
| `LastHitBy` | `Entity Value` — 谁最后打了它（伤害溯源） |
| `EnemyMovementPaused` | 可启用 Tag |
| `BossBrainData` | `Mode` / `PendingCommand` / `DashSubPhase` / `Skill1SubPhase` / `Skill2SubPhase` / `Skill3SubPhase` / `SubTimer` / `LockedDirection` / `MoveSpeedMultiplier` / `DashSlashActive` — **Boss 当前 AI 状态机** |
| `BossRunState` | `StageIndex` / `BossSpawned` / `ActiveBossEntity` |
| `EnemySpawnState` | `SpawnTimer` / `SpawnCurrentCount` / `ReaperSpawnTimer` / `EliteHandledSegmentIndex` — 刷怪节奏 |
| `RangedEnemyData` / `SuicideBomberStateData` / `DashMeleeStateData` | 各 AI 的具体状态 |

### 2.5 位置 / 朝向

| 组件 | 字段 |
|---|---|
| `LocalTransform` | `Position`(float3) / `Rotation` / `Scale` |
| `LocalToWorld` | 矩阵，`.Position` 是真正的世界坐标 |
| `CharacterMoveDirection` | `float2 Value` |
| `FacingDirectionOverride` | `float Value` |
| `MouseTarget` | `float3 WorldPosition` — **瞄准点** |
| `AttackData` | `AttackType`(枚举) / `MoveSpeed` / `AttackDamage` — 弹幕 / AOE |
| `AttackOwner` | `Entity Value` — 这发弹幕是谁的 |
| `AttackOverTimer` | `float Value` — 剩余存活 |

> **坐标系注意**：游戏逻辑跑在 **XY 平面**，Z 只是渲染排序深度（游戏自己的 `EnemyAggroUtil.ResolveTargetXy`、`DepthSystem`、`VfxSpawnDepthLayer` 都印证这点）。所以本 mod 算距离用的是 **XY 平面距离**，不是 3D 距离。

### 2.6 Buff

`BuffState`（Buffer）字段是 `BuffType BuffType` / `float BuffLevel` / `float BuffTimer`，但 `BuffType` 枚举只有 `None` / `Slow` —— 也就是说**这个 buffer 只承载减速**。

真正的 buff 体系是按机甲分开的静态运行时类，通过 buffer 记录持有情况：

- `BeaconBuffOwned`（Buffer）+ `BeaconBuffKind` 枚举 + `BeingHudBuffId`
- `IronHudBuffId` / `StormHudBuffId` / `SunHudBuffId`
- `BeaconBuffRuntime.CollectHudEntries(em, player, dst)` / `.Owns(em, player, kind)` / `.GetAttackMul(em, player)` — **这些是 public static，mod 里可以直接调**

想拿完整 buff 列表，直接调 `BeaconBuffRuntime.CollectOwnedDescriptions(em, player, dst)` 就行，不用自己拼。

### 2.7 MonoBehaviour 单例（局外/全局上下文）

| 单例 | 有用成员 |
|---|---|
| `GameManager.Instance` | `isInGaming`、`CurrentStageIndex`、`CurrentMechSkillType` |
| `GamingUIManager.Instance` | `player`（`PlayerState`：HP/MaxHP/Exp/Level/SkillTimer/Plating/DroneCount…）— 现成的 HUD 快照，可当交叉校验 |
| `SteamManager.Instance` | `IsInLobby`、`SessionRole` |
| `SteamNetGameplay.Instance` | `LocalSlot`、`GetSlotSteamName(slot)`、`HudLevel`/`HudExp01`/`HudLocalHpPct` 等一整套 HUD 镜像值 |
| `LevelUPManager.Instance` / `RandomEventManager.Instance` | 升级选项、随机事件 |

联机是**手写的 ghost 同步**（`SteamNetGameplay` 132KB，配 `GhostNetScope`/`GhostPoseDriver`/`NetworkMechSlot`），**没有** `Unity.NetCode` 包。

---

## 3. 已经写好的实现

`CombatInspector` 是一个 BepInEx 插件，编译**已完成**（两个加载器变体都 0 警告 0 错误，产物 60KB 单文件、零依赖拷贝）。

### 3.1 它给你五种读取方式

**⓪ 网页实时仪表盘（推荐，打开即用）**

浏览器访问 `http://127.0.0.1:8790/` 即可。仪表盘**内嵌在 mod 程序集里**（`dashboard.html` 作为 EmbeddedResource 编译进 `CombatInspector.dll`），所以没有任何额外文件要部署，也不依赖任何 CDN / 外部库，断网也能用。

它每 250ms 轮询一次 `/state`，提供：

- **雷达**：XY 平面实时态势图。玩家居中（朝向按移动方向旋转），敌人按 AI 类型着色（Melee 红 / FlyingMelee 橙 / Ranged 紫 / DashMelee 粉 / 自爆 黄 / Boss 大红方块），精英加白环，血条画成外圈弧，出界的敌人压到边缘半透明显示。带距离环、瞄准线、索敌半径虚线圈、弹幕点、友军点。悬停出详情浮窗，范围可切 20/40/80/160/自动。
- **玩家面板**：HP / EXP / 装甲 / 精神力 / 技能CD 五条进度条，等级、击杀、攻击力（含基础值）、攻击冷却、移速、索敌半径、技能与充能、坐标、瞄准点、移动方向、Perk、Buff、全部 Tag。点过 "Deep dump" 后还会显示暴击率与减伤。
- **敌人表**：可按任意列排序（默认距离升序），HP 条、AI 标签、精英/硬直/停顿标记、坐标。点行钉住。
- **Boss 面板**：血条 + AI 状态机（mode / pendingCommand / subTimer / 硬直）。
- **弹幕 / AOE 表**：类型、归属实体、伤害、速度、剩余寿命、距玩家距离。
- **战斗事件流**：由帧间差分生成——你受到的伤害、每个敌人受到的伤害（含击杀判定）、实体消失（判定为死亡）、击杀数增长。
- 顶栏：连接状态、局内/关卡/机甲/场景/实体数/敌人数/轮询 fps；按钮：强制抓取、Deep dump、导出当前快照 JSON。
- 快捷键：`空格` 暂停刷新、`D` deep dump、`R` 强制抓取。

验证方式：用真实抓到的快照起一个 mock 服务，再用无头 Chrome 截图核对渲染（`mod/out/dashboard_shot.png` 为 mock 数据、`dashboard_live.png` 为真实游戏数据）。

**① 游戏内实时浮层（F9）**

IMGUI 面板，可拖动，显示：局内上下文 → 玩家 HP/EXP/装甲/精神力条 + 攻击力/冷却/技能/移速/位置/瞄准/击杀 → 小队友军 → Boss（血条 + AI 状态机 mode/command）→ 敌人表（名字 / 实体号 / HP / 距离 / 角度 / AI 类型 / 精英 / 硬直 / buff / 本帧伤害），按距离升序。

中文字体走 `Font.CreateDynamicFontFromOSFont(["Microsoft YaHei UI", "Microsoft YaHei", "SimHei", ...])`，所以敌人中文名能正常显示。

**② 本地 HTTP JSON 接口**

基于 `TcpListener` 手写的极简 HTTP/1.1（**故意不用 `HttpListener`**：绕开 Windows URL ACL 的管理员要求，也绕开 net472 与 Unity Mono 之间的 API 差异）。带 CORS，可以直接被浏览器面板 / Python / 你自己的 AI 工具消费。

```
GET /                           网页实时仪表盘（浏览器直接打开这个）
GET /health                     存活探测
GET /state                      最近一次主线程快照（零往返，最便宜）
GET /snapshot                   强制立刻重抓一次并返回
GET /deep?enemies=8&depth=4     反射式全组件全字段 dump
GET /dump                       把快照 + 深度 dump 落盘
GET /help                       纯文本接口说明
```

服务线程**永不触碰 ECS**。它要么回放主线程缓存好的 JSON，要么把请求排进队列、等 `Runner.Update()` 在 main thread 上完成后再返回（3 秒超时）。这是 ECS mod 最容易踩的坑，这里从设计上避开了。

**③ 落盘文件（F10 手动 / 每秒自动）**

写到 `<game>\BepInEx\plugins\CombatInspector\CombatInspectorOut\`：

- `latest.json` — 完整快照（缩进）
- `latest_deep.json` — 反射式全字段 dump
- `enemies.csv` — 敌人表格，可直接丢进 Excel / pandas

**④ 游戏内雷达窗（F11，默认右上角）**

独立于主浮层的第二个 IMGUI 窗口，标题栏可拖动，默认停在**屏幕右上角**。画的是 XY 平面态势：

| 目标 | 画法 |
|---|---|
| 普通敌人 | 小圆点，按 AI 类型着色（近战红 / 远程紫 / 飞行近战橙 / 突进近战粉 / 自爆黄 / 干扰浮空青） |
| **精英** | 稍大圆点 + **金色双环** |
| **首领** | **红色菱形 + 红环** |
| 自身 | 蓝色箭头，朝移动方向旋转 |
| 友军 | 绿点 |
| 弹幕 / AOE | 小黄点 |

另有：距离环 + 环上距离数字、十字线、瞄准线、索敌半径黄环、残血外圈血弧、出界目标压边半透明。
窗口内工具条可切范围（20/40/80/自动）与"名字"开关；下方显示最近一个敌人的名字/距离/血量，以及图例。

配置项（`[Radar]` 段）：`VisibleByDefault` / `Size`（160-700）/ `Range`（0=自动）/ `ShowNames`。
热键 `F11` 开关（`[Hotkeys] RadarKey`）。窗口位置每帧钳制在屏幕内，分辨率/DPI 变化不会跑丢。

> IMGUI 默认皮肤字体**不含中文字形**，直接用默认 GUIStyle 画中文会是方框。所有 IMGUI 文本（含窗口标题、工具栏、图例）统一走 `Cjk.cs` 里的动态 OS 字体（Microsoft YaHei UI → 雅黑 → 苹方 → 黑体 回退）。雷达第一版就是漏了这个才显示成乱码。

**⑤ 怪物头顶血条（F12）—— 含"下一击预计剩余"**

给**所有**敌人加条（游戏原生的 `EliteEnemyHudUI` 只覆盖精英怪）。做法是 `Camera.main.WorldToScreenPoint` 把实体世界坐标投到屏幕再画，不依赖任何游戏预制体。

每条同时给出：

- **当前血量**：绿=普通 / 金=精英 / 红=首领；判定"下一击可秒杀"时整条转红
- **橙色段 + 白色竖线**：下一击会被扣掉的部分，以及打完之后的预计血线
- 数字行：`45/45 →13 -32 2击`（当前/上限 → 预计剩余 −预计伤害 还需几击）；可秒杀时显示 `可秒杀(-32)`
- 小字行：`甲120(50%) 源:实测` —— 护甲值、护甲后伤害保留率、预测来源
- **引线**从条连到实体原点并点一个小点，所以即使头顶偏移不准也能看清条属于哪只怪

条宽按距离轻微缩放（远处更窄）避免糊屏；只画屏幕内、相机前方、距离阈值内的最近 N 个。
实测截图：`mod/out/healthbars_shot.png`。预测口径见 §7.1。

### 3.2 两层数据：curated + reflective

**第一层 `CombatScanner`** — 人工梳理的强类型快照，字段就是上面数据字典里那些。适合做 HUD / 面板。

**第二层 `DeepDumper`** — 这才是"获取**所有**状态"的答案。它不写死任何字段，而是：

```csharp
NativeArray<ComponentType> types = em.GetComponentTypes(entity, Allocator.Temp);
// 对每个组件按 IsBuffer / IsSharedComponent / IsZeroSized / IsChunkComponent 分派，
// 用 MakeGenericMethod 反射调用 GetComponentData<T> / GetBuffer<T> / GetSharedComponentData<T>，
// 再把 struct 的 public 字段递归摊平成 JSON 安全值
```

好处很实在：**游戏更新加了新组件，你不用改一行代码就能看到它**。

`SafeValue` 里做了大量防御，因为 ECS 组件里全是危险类型：

- `Entity` → `"E123:1"`（index:version）
- `FixedString32/64/512Bytes` → `.ToString()`
- `float2/3/4`、`quaternion` → 展开成字段字典
- `NativeArray` / `NativeList` / `BlobArray` / `BlobAssetReference` / 指针 → **一律不展开**，只记类型名（碰裸指针会直接崩游戏）
- `UnityEngine.Object` → 取 `name`
- `Unity.Mathematics.Random` → 只取 `state`
- 递归深度上限 + 循环引用保护 + 逐字段 try/catch（一个字段读炸不影响其它）

Tag（零大小组件）用**非泛型**的 `em.IsComponentEnabled(entity, componentType)` 读启用状态——比反射调泛型版更稳，且这个 API 在当前 Entities 版本里确实存在（已核对）。

托管组件（含引用类型字段的 `IComponentData`）读不了，因为 `EntityManager.GetComponentData<T>` 的约束是 `where T : unmanaged`（已核对签名）。这种情况不会抛个看不懂的泛型约束异常，而是输出 `<managed component: not readable ...>`。游戏里的组件几乎全是 blittable 的（要跑 Burst job），所以实际影响很小。

### 3.3 线程与时序安全

这是 ECS mod 最容易翻车的地方，说明一下为什么这里是安全的：

Unity Entities 的世界模拟跑在 PlayerLoop 的 **PreLateUpdate** 阶段，而 MonoBehaviour 的 `Update()` 在它**之前**。所以在 `Update()` 里读 ECS 时，上一帧的所有 Burst job 都已经完成，不会撞上 job safety system 的 "component has been declared as read/write in the job" 报错。

**这不是我的推断** —— 游戏自带的 `DevTestTools` MonoBehaviour 就是在 `Update()` 里读写 ECS 的（F1 升级、F2 无敌、F3 刷 Boss 全在 `Update` 里改组件），证明这个时序在本构建中是通的。

即便如此，所有读取仍然逐段 `try/catch`，失败只会在快照里留一条 `notes`，不会波及游戏。

### 3.4 附带的一个发现

代码里存在 `DevTestTools`：`enableDevShortcuts = true` 默认开启，绑定 **F1**（连升 5 级）/ **F2**（无限血甲精神）/ **F3**（强制刷 Boss + 切测试 AI）/ **F4**（Perk 自选面板）/ **F5**（随机事件面板）/ **小键盘 0-6**（Boss AI 手动指令：站立、向玩家移动、冲刺、回旋斩、挥砍剑气、朝天开炮）。

但它是个 MonoBehaviour，**是否被挂进了发布版的场景，光看 DLL 无法确定**（脚本引用是 GUID，没有工程的 `.meta` 就映射不回类名）。

所以本 mod 直接**在运行时探测**它，结果写进快照的 `devToolsPresent` / `devToolsDetail`，浮层上也会显示一行。如果它是活的，你等于白捡一整套作弊键——而且 F3 + 小键盘那套 Boss AI 手动指令，对做战斗 mod 的调试价值极高。

顺带说明两个**死路**，省得你再试：

- `Microsoft.CodeAnalysis.CSharp.dll`（Roslyn 4.12）确实在 `ScriptingAssemblies.json` 里会加载，但 `Assembly-CSharp` 对它**零引用**（grep 过 `Microsoft.CodeAnalysis|CSharpScript|Assembly.Load|Reflection.Emit`，无命中）。它是被别的包拖进来的，游戏没有自带运行时 C# 脚本能力。
- `MCPForUnity.Runtime.dll` 也在加载列表里，看着像现成的桥，但反编译后只有序列化 Converter（Vector3/Quaternion/Color…）和截图工具类。**MCP 服务端在 Unity Editor 里，玩家构建中没有任何桥接服务**。

---

## 4. 安装使用

### 前置

- .NET SDK（`dotnet`）——已验证 10.0.302 可用
- 游戏本体

### 步骤

```powershell
cd H:\hnworkspace\yxykgame\mod

# 0. 游戏路径已写在 gamepath.txt（UTF-8）。换机器就改这个文件，或用 -GameDir 传参。

# 1. 构建（会从游戏目录重新拷引用 DLL，并同时出两个加载器变体）
.\build.ps1

# 2. 安装（BepInEx 5 + 插件）
.\install.ps1

# 3. 启动游戏，进局内，按 F9
```

### 验证

```powershell
curl http://127.0.0.1:8790/health
curl http://127.0.0.1:8790/state
curl "http://127.0.0.1:8790/deep?enemies=5&depth=4" -o deep.json
```

日志：`<game>\BepInEx\LogOutput.log`
配置：`<game>\BepInEx\config\com.jhx9676.mechcore.combatinspector.cfg`（首次运行后生成）

### 卸载

```powershell
.\uninstall.ps1          # 只删插件，保留加载器
.\uninstall.ps1 -Full    # 连 BepInEx / winhttp.dll / doorstop 配置一起删，游戏目录回到原样
.\uninstall.ps1 -KeepOutput   # 保留抓到的 json/csv
```

安装是**纯增量**的：只往游戏目录添加 `winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`、`BepInEx\`。**不修改任何游戏资源和托管程序集**，所以游戏本体文件在 Steam 里始终是干净的。

---

## 5. 加载器选择

两个都已经 vendored 在 `mod\vendor\`，都能一键切换：

| 加载器 | 说明 | 命令 |
|---|---|---|
| **BepInEx 5.4.23.5**（默认，推荐） | LTS 分支，最新 release notes 里明确有 "Fix log writer errors for Unity 6"，说明在跟进新版 Unity | `.\install.ps1` |
| **BepInEx 6.0.0-pre.2 Unity.Mono** | 专为新版 Unity Mono 做的分支，备用 | `.\install.ps1 -Loader bepinex6 -ForceLoader` |

插件源码里**只有 `Plugin.cs` 一个文件碰加载器 API**，靠 `-p:Loader=bepinex6` + `#if BEPINEX6` 切换（BepInEx 6 的 `BaseUnityPlugin` 在 `BepInEx.Unity.Mono` 命名空间，已核实）。`CombatScanner` / `DeepDumper` / `Overlay` / `Runner` / `StateHttpServer` 全部与加载器无关，换 MelonLoader 也只需要重写那一个文件。

如果 `LogOutput.log` 里完全没有 CombatInspector 的字样，说明加载器本身没挂上（而不是插件有问题），换另一个变体重试即可。

---

## 6. 配置项

首次运行后生成 `BepInEx\config\com.jhx9676.mechcore.combatinspector.cfg`：

| 段 | 键 | 默认 | 说明 |
|---|---|---|---|
| Capture | `IntervalSeconds` | `0.25` | 主线程抓取间隔（每秒 4 次） |
| Capture | `FileWriteIntervalSeconds` | `1.0` | `latest.json` / `enemies.csv` 重写间隔 |
| Capture | `MaxEnemies` | `600` | 单次快照敌人上限 |
| Capture | `MaxProjectiles` | `400` | 单次快照弹幕上限 |
| Capture | `IncludeComponentNames` | `false` | 给每个实体附上完整组件类型名列表（很啰嗦） |
| Http | `Enabled` | `true` | 关掉本地 HTTP 端口 |
| Http | `Port` | `8790` | 端口 |
| Output | `Directory` | 插件目录下 | 输出路径 |
| Output | `WriteJson` / `WriteCsv` | `true` | 分别开关 |
| Hotkeys | `OverlayKey` | `F9` | 浮层开关 |
| Hotkeys | `DumpKey` | `F10` | 立即落盘 |
| Hotkeys | `RadarKey` | `F11` | 雷达窗开关 |
| Hotkeys | `OverlayVisibleByDefault` | `true` | 启动即显示浮层 |
| Radar | `VisibleByDefault` | `true` | 启动即显示雷达（默认右上角） |
| Radar | `Size` | `300` | 雷达尺寸（160-700 像素） |
| Radar | `Range` | `0` | 0=自动适配敌人；否则固定世界单位 |
| Radar | `ShowNames` | `false` | 点旁显示敌人名字 |
| Hotkeys | `BarsKey` | `F12` | 怪物血条开关 |
| Bars | `Enabled` | `true` | 给所有敌人头顶画血条 |
| Bars | `MaxCount` | `40` | 每帧最多画几条（按最近优先） |
| Bars | `MaxDistance` | `70` | 超过此世界距离的不画；0=不限 |
| Bars | `HeadOffsetY` | `0.7` | 条相对实体原点的世界上方偏移 |
| Bars | `ShowText` | `true` | 显示数值行（当前/预计剩余/需击数） |
| Bars | `ShowPrediction` | `true` | 显示橙色"将被扣掉"段 + 白色预计血线 |
| Bars | `ShowLeaderLine` | `true` | 条到实体原点的引线，避免归属歧义 |
| Hotkeys | `AdvisorKey` | `F8` | 战术建议开关 |
| Advisor | `Enabled` | `true` | 只读建议层（**不写任何游戏状态**） |
| Advisor | `MaxEngageDistance` | `40` | 超出此距离的目标被大幅降权 |
| Advisor | `ThreatRadius` | `9` | 进入此范围的敌人才影响走位建议 |
| Advisor | `KiteDistance` | `8` | 与焦点目标希望保持的距离 |

按键用 `UnityEngine.InputSystem` 的 `Key` 枚举名。**F9/F10 是刻意选的**：游戏自己占了 F1–F5 和小键盘 0–6，F6–F8 也没被用但留了余量，全代码库 grep 过 `f9Key|f10Key|f11Key|f12Key|f6Key|f7Key|f8Key`，无命中。

---

## 7. 从"读"扩展到"写"

读到之后，改数值就是同一套 API 的反向操作。游戏自己的 `DevTestTools` 就是范例：

```csharp
// 改血量
var hp = em.GetComponentData<CharacterCurrentHP>(player);
hp.Value = 99999f;
em.SetComponentData(player, hp);

// 加 tag（DevTestTools F2 的写法）
em.AddComponent<DevInfiniteSurvivalTag>(player);
em.SetComponentEnabled<DevInfiniteSurvivalTag>(player, true);

// 直接调游戏的 public static 运行时助手
BeaconBuffRuntime.GrantKindToAllCombatMechs(em, ecb, kind);
ChipMechanicRuntime.ApplyRadiusDamage(em, origin, radius, damage);
BossSpawnRuntime.TrySpawnBossImmediate(em, holder, stageIndex, playerPos, out var boss);
ItemPickupEffectHelpers.GrantLevelUps(player, count, em);
DevInfiniteSurvivalHelper.RefillSurvivalResources(em, player);
```

**批量改（比如一键清场）不要在主线程 `SetComponentData` 循环里做** —— 用 `EntityCommandBuffer`（ECB），在系统里 `Playback`。游戏里到处都是这个写法（`BossSkillRuntime`、`BeingDroneSystem` 等），照抄即可。

要拦截游戏逻辑（比如伤害计算），用 BepInEx 自带的 `0Harmony` 给 `ProcessDamageThisFrameSystem` / `AttackCheckSystem` / `DestroyEntitySystem` 打补丁。这些系统都是 `ISystem`（struct），Harmony 补丁要打在其 `OnUpdate` 或生成的静态 `__codegen__` 包装上，比 Mono 类麻烦一些——需要的话我可以接着做。

联机注意：这游戏是手写 ghost 同步 + Steam 大厅。**改数值只在本地生效，且很可能被 `SteamNetGameplay` 的同步逻辑覆盖或导致不同步**。单机 / 自建主机没问题，别拿去别人房里用。

### 7.1 伤害预测的口径（血条上那个"→剩余"是怎么算的）

先把游戏的真实结算链读清楚（全部来自反编译，非推测）：

```
ProcessDamageJob.Execute(entity, ref CharacterCurrentHP hp, DynamicBuffer<DamageThisFrame> buf, ...)
    hp.Value -= buf[i].Value;          // 只做扣血，伤害在上游算好
```

上游（玩家→敌人，`AttackCheckTriggerRunner.ComputePlayerDamage`）：

```csharp
damage = baseDamage * pow(0.5f, 目标护甲 / PerkConfig.ArmorPerHalfDamage)
// PerkConfig.ArmorPerHalfDamage = 100f  —— 护甲每满 100，伤害减半
```

`baseDamage` 取自弹体的 `AttackData.AttackDamage`，普攻路径就是 `PlayerAttackData.AttackPower`；
**暴击是在生成弹体那一刻 roll 的**（`CritRateData.Value` / `CritDamageData`），所以单次伤害无法事后精确复现。

因此采用**实测优先**：

| 情况 | 用什么 | 标注 |
|---|---|---|
| 该敌人区间内掉过血 | 相邻两次抓取之间 `CharacterCurrentHP` 的**下降量**滚动统计（窗口 8 次、6 秒过期、按 entity version 复位） | `源:实测·区间` |
| 还没掉过血 | 模型 `AttackPower × 期望暴击倍率 × pow(0.5, 护甲/100)` | `源:模型`（面板带"估"） |

> **这里返工过一次。** 最初的设计是读 `DamageThisFrame` buffer，理由是它是结算后真值。
> 代码本身没错，但**读取时机错了**：那个 buffer 每帧被 `ProcessDamageThisFrameSystem` 消费后清空，
> 而抓取跑在 MonoBehaviour `Update()`，早于本帧 ECS 模拟，于是永远读到"上一帧已清空"的空值。
> 证据是决策日志 **5427 个样本零命中**，且从血条上线到那次排查，`源` 从来没出现过"实测"。
> 换成 HP 差值后对时机完全免疫，拿到的仍是结算后真值（暴击、乘区、护甲减免都在里面）。
>
> 代价是语义变成**区间伤害**（4Hz 可能把相邻几次命中并进同一区间），所以标签明确写成
> `实测·区间`，不再简称"实测"。

`DamageThisFrame` 是结算后的真值，**已经含暴击、信标/芯片乘区、护甲减免**，所以实测路径比任何模型都准。

预测的已知漏算项（会误报"可秒杀"）：

- 无敌类 Tag 直接免伤：`StormLightningInvincibleTag`、`EventInvincibleTag`、`DevInfiniteSurvivalTag`(enabled)
- 芯片免死词条（`ChipMechanicState.AffixMask`）：bit 39 → 致命伤锁血 1 点；bit 45 → 复活到 20% 上限
- bit 41 是受击后自伤 10，会略微加大实测均值
- 弹道飞行时间、miss、AOE 多目标分摊均未建模，`需击数 / TTK` 是按玩家攻击间隔的粗估

### 7.2 自动战斗可行性

**结论：可行，而且这游戏属于特别好做的那类** —— 决策需要的一切都是结构化 ECS 数据（上面已经全读到了），执行只需要写少数几个组件。

| 环节 | 依据 |
|---|---|
| 感知 | 已完成：位置/血量/护甲/下一击预测/距离/角度/弹幕归属与威胁/技能CD |
| 目标选择 | 按 `predictedNextDamage`、`hitsToKill`、`willDieFromNextHit`、距离排序即可，数据现成 |
| 威胁评估 | `EnemyAttackData.Damage` × 自身护甲减免；`EnemyCurrentTarget` 能判断"它是不是在打我" |
| **瞄准执行** | `MouseTarget.WorldPosition`。证据：`PlayerAttackSystem` 自己就把准星写成 `pos.xy + dir * 2f`，而攻击系统每帧读它发起普攻 → **写好这个组件，普攻会自动进行** |
| 移动执行 | `CharacterMoveDirection.Value`（float2） |
| 技能执行 | `PlayerSkill`（`CanSkill`/`CDingSkill`/`IsActive`）；且游戏有 public static 施法入口可直接调：`PlayerAttackSystem.FireIronRailgun`、`IronPlatingRuntime.SpawnExplosionAoe`、`BeingDroneSystem` 等 |

**最关键的一个坑**：`MouseTarget` 每帧都会被游戏的输入/攻击系统重写。所以在 MonoBehaviour 的 `Update()` 里写它**会被下一帧覆盖**，必须二选一：

1. **正路** —— 注册自己的 ECS 系统，用 `[UpdateInGroup]` + `[UpdateAfter(typeof(PlayerAttackSystem))]`（或排在读取它的系统之前）来写；
2. 或用 Harmony 拦截输入解析（`MouseInputSystem` / `PlayerAimWorldResolver.Resolve`），把真实鼠标值换成 bot 算出的瞄准点。

建议的实现顺序，每步都可回退、便于验证决策质量：

1. **✅ 已完成 —— 只读建议层**（`TacticsAdvisor.cs`，热键 `F8`）：不接管任何操作，只把结论标出来，你拿自己的操作对比它判断得准不准。输出：
   - **集火目标**：实体号 / 名字 / 距离 / 血量 / "下一击可击杀"或"还需 N 击"，并附**打分理由**（可秒杀+1000、N击击杀+300/N、距离+prox、超出交战距离-400、正在打我+60、逼近中+40、精英+50、首领+120、自爆兵+150、远程+40、干扰+60…）
   - **候选排名**前 6 名带分数，直接能看出"它为什么选这个而不是那个"
   - **建议瞄准点**含**提前量**：解 `|rel + v·t| = 弹速·t` 的最小正根（弹速取 `PlayerAttackData.AttackMoveSpeed`，目标速度由 `CharacterMoveDirection` × `CharacterMoveSpeed` 得出），并给提前秒数
   - **建议走位**：威胁斥力（敌人伤害 × 我护甲减免 × 距离衰减）+ 与焦点目标保持风筝距离 + 垂直于来袭弹道闪避，归一化后给中文标签（后撤拉开距离 / 前压接近目标 / 侧向绕圈 / 规避）
   - **危险度** 0-100% + 档位（安全/注意/危险/危急）、近身威胁数、来袭弹数
   - 展示位置：主浮层「战术建议」块、雷达（青双环=推荐目标、青叉=瞄准点、青箭头=走位）、血条（推荐目标加绿方括号与"推荐"前缀）、仪表盘「战术建议」卡片与雷达叠加
2. **✅ 已完成并实机确认 —— 接管瞄准**（`AimOverrideSystem.cs` + `AimTakeoverPatch.cs`，热键 `F7`）。
   托管 `SystemBase` 在这个世界根本不被驱动（见 §7.4），改用 Harmony postfix 补 `MouseInputSystem` 后生效：
   `aimDiff` 从 8.9~11.7 降到 0.000，累计写入 2850 次。
3. **待做 —— 接管移动 + 技能**：全自动。

### 7.3 地图边界与障碍感知（走位必须知道哪里走不通）

> 联机同前：手写 ghost 同步，客户端写权威值会被覆盖或导致不同步，只在单机 / 自己当主机时安全。

只看敌人的走位建议是**危险的**：被围时它可能让你"后撤"，而身后就是墙角，直接把自己卡死。所以接入了三个真实数据源：

| 来源 | 内容 | 为什么可靠 |
|---|---|---|
| `IronPlatingRuntime.TryGetCameraPlayArea(out float2 min, max)` | 可玩区矩形 | 游戏**自己的 public static**，刷怪、画磁轨炮反射、生成随机事件都在用它 |
| `MapBoundaryWallData` | 每段墙的 `SegmentStart/SegmentEnd/InwardNormal/Side` | 能表达**斜墙**，比只用矩形精确 |
| `PhysicsWorldSingleton.OverlapAabb` | 16 方向 × 4 段外推的小方框探针，覆盖**所有**碰撞体 | 帐篷、集装箱、岩石等障碍物不需要预先知道名字和尺寸 |

**为什么用 `OverlapAabb` 而不是 `CastRay`（踩过的坑）**：`RaycastInput` 的 `Ray` 和 `QueryContext`
都是 **internal 字段**，用对象初始化器构造时 `QueryContext` 保持全零 → `CastRay` **永远返回 false**。
实测证据：725 次刷新、上万次探测，零命中且零异常，而 `PhysicsWorldSingleton` 确实取到了 ——
排除了"取不到单例"和"job 依赖抛异常"两个猜测后才定位到这里。改用游戏自己在 `BeingFieldSystem`
里用出效果的 `OverlapAabb`：沿每个方向外推一个小方框（半边长 ≈ 身体半径 0.3）。顺带这比射线更贴合
"身体能不能挤过去"——射线能从缝里穿过去，身体不行。

命中的 `Entity` 直接可取，所以能区分**"被生物挡住"和"被地形挡住"**（躲怪和撞墙是两回事），
并且按实体身份跳过自身，不需要靠距离阈值猜。

换成 `OverlapAabb` 后实机验证通过（人站在障碍物堆里）：

```
navInfo: 可玩区=(-31.7,4.2)~(3.8,24.2) 墙段=5 受阻方向=15/16 其中生物=0
         有通路=True 障碍命中=12779/946次刷新 物理单例=已取到 探测异常=0
advice : 墙距=9.61 通畅=3.20 避墙修正=True 被困=False
```

15 个方向被地形挡住、只有 1 条缝，建议层准确挑出了那条缝并给出满通畅度 —— 这正是被围时最需要的能力。

走位决策流程：

```
战术期望方向（敌人斥力 + 风筝距离 + 垂直来袭弹道闪避）
      ↓  叠加墙体/边界推离（WallMargin=2.2 格内开始推，斜墙按线段最近点）
      ↓  16 方向射线探测：标记 blocked / clearance / 是否生物
      ↓  ChooseSafeDirection：以期望方向为基准，按角度偏移 0,±1,±2,… 找第一个走得通的方向
      ↓  全被挡 → trapped=true，标签变"四面受阻·找缝"，退而选最通畅的方向
```

诊断与可视化：

- `advice.tacticalX/Y`（修正前）vs `moveX/Y`（修正后）、`moveAdjustedForWalls`、`trapped`、`wallDistance`、`pathClearance`、`navAvailable`
- `/state` 的 `navInfo`：可玩区矩形、墙段数、受阻方向数/16、其中生物数、是否有通路
- **雷达上直接画出来**：蓝色细框=可玩区、橙色粗线=墙段、从玩家发散的 16 条探针（**绿=走得通、红=被地形挡、黄=被生物挡**，长度=通畅距离）
- 浮层显示"原本想走 (x,y)，因墙/障碍改为 (x,y)"

> 这一步对自动战斗是**必需**的，不是锦上添花：没有路径感知的全自动 bot 会在墙角把自己卡死，而"看起来在动但其实动不了"比不动更糟。

### 7.4 接管瞄准为什么没生效（一次完整的排错记录）

`AimOverrideSystem` 是个托管 `SystemBase`，代码、注册、开关全部正常，但 `OnUpdate` **一次都没执行过**。

定位靠的是自己埋的计数器（不是猜的）：

```
autoAimInfo: enabled=True  状态=未运行（OnUpdate 从未执行）  本帧写入=0  累计=0
```

`F7` 日志切了 12 次、`Active` 确实是 true，但 `累计写入` 恒为 0 → 系统没跑，而不是"跑了但没写"。

试过三种注册/排序方式，全部一样：

| 做法 | 结果 |
|---|---|
| `world.AddSystemManaged(new T())` + `[UpdateAfter(MouseInputSystem)]` | 日志"注册成功"，OnUpdate 不跑 |
| `world.GetOrCreateSystemManaged<T>()`（走类型管理路径，理论上才应用排序属性） | 世界系统数 65，OnUpdate 仍不跑 |
| 去掉两个跨类型锚点，只留 `[UpdateInGroup(SimulationSystemGroup, OrderLast=true)]` | 仍不跑 |

结论：**问题不在注册路径也不在排序锚点，而是托管 `SystemBase` 根本没进这个世界的更新循环**
（该世界由 unmanaged `ISystem` 驱动）。

> 教训两条。① `AddSystemManaged` 是**静默失败**的典型：注册返回成功、日志漂亮、实际什么都不干。
> 没有 `累计写入` 这种可量化计数器，就会去瞎改瞄准逻辑而永远发现不了系统压根没跑。
> ② 状态字符串不要和字段初始值撞车——最初 `LastStatus` 默认值就是"未启用"，
> 和 OnUpdate 里写的"未启用"完全一样，导致"没跑"和"跑了但关着"分不开；改成
> "未运行（OnUpdate 从未执行）"后一眼定案。

**已改用 Harmony postfix**（`AimTakeoverPatch.cs`）：补
`MouseInputSystem.__codegen__OnUpdate(IntPtr self, IntPtr state)` —— Entities 为 unmanaged ISystem
生成的入口，是个 `internal static` 方法，所以 postfix 不涉及 struct 实例装箱。时机天然位于
"输入解析之后、`PlayerAttackSystem` 读取之前"，不用跟世界循环较劲。

安全性有依据而非侥幸：`MouseInputSystem` 在自己的 OnUpdate 里已经调过
`CompleteDependencyBeforeRW<MouseTarget>()`（`MouseInputSystem.cs:271`），所以 postfix 执行时
没有未完成 job 持有 `MouseTarget`，用 `EntityManager.SetComponentData` 直接写是安全的。

挂载用**手动 `harmony.Patch()` 而不是 `PatchAll`**：目标类型/方法找不到时要**明确写日志报出来**，
绝不再留静默失败（这是本节最大的教训）。判据也换成可量化计数器：

```
autoAimInfo: enabled=True 状态=接管中（补丁写入） 补丁执行=N 本帧写入=1 累计写入=M
             机甲=E164v… 目标=E277v… 上次瞄准=(x,y) 提前=0.477s 弹速=20.0
```

- `补丁执行` 恒为 0 → postfix 根本没被调用（补丁没挂上 / 方法名不对），看启动日志的 `aimpatch:` 行
- `补丁执行` 增长但 `aimDiff` 不收敛 → 写入被后面的系统覆盖，改去补更靠后的时机
- `状态=写入异常` + `异常=…` → 撞了 job safety，退化成用 `state` 指针走 unsafe 直接写 chunk 内存

### 7.5 待办

- ~~**接管瞄准**~~ ✅ **已实机确认生效**（Harmony postfix）：`补丁执行=3139 累计写入=2850 aimDiff=0.000`
  （接管前 aimDiff 是 8.9~11.7）。`补丁执行 > 累计写入` 的差值是目标超距/死亡/切换的帧，属正常。
- 接管移动（写 `CharacterMoveDirection`）——有了路径感知才敢做
- 接管技能释放
- 芯片免死/无敌 Tag 纳入预测，消除"可秒杀"误报

---

## 8. 已知边界

- **`DynamicBuffer<T>` 的非泛型枚举是个坑**。在这个 Entities 构建里，`DynamicBuffer<T>` 把**非泛型** `IEnumerable.GetEnumerator()` 显式实现成 `throw new NotImplementedException()`（只有泛型 `IEnumerable<T>` 版本可用，已反编译核实，`Unity.Entities.decompiled.cs:44698`）。所以反射路径**绝不能** `as IEnumerable` 后 foreach，必须走 `Length` + `this[int]` 索引器。第一版就踩中了，`PlayerPerk` / `BeaconBuffOwned` 全部报 `NotImplementedException`，修复后正常（`PlayerPerk [Buffer]` → `{"PerkID":25,"Level":1}`）。
- **游戏更新会删依赖**。某次更新把 `Newtonsoft.Json.dll` 从 Managed 目录整个删掉（连 `ScriptingAssemblies.json` 也一并没了）。mod 原先用它做 JSON 序列化，更新后每次序列化都会抛 `FileNotFoundException`。现已改为自带的 `MiniJson`（`MiniJson.cs`，零外部依赖，输出经 node 校验为合法 JSON，非有限浮点输出 `null`、循环引用输出 `<circular>`、深度封顶 12）。**教训：不要依赖游戏可能增删的程序集。**
- **IMGUI 默认皮肤字体不含中文字形**。任何用默认 GUIStyle 画的中文都是方框，窗口标题也一样。所有 IMGUI 文本统一走 `Cjk.cs` 的动态 OS 字体。
- **编译目标是 `net472`**：游戏 Mono profile 的 `mscorlib` 是 `4.6.57.0`，匹配。
- **Entities 版本没有元数据可查**（Unity 包一律 `AssemblyVersion("0.0.0.0")`）。从 API 形态看是 **1.x 早期**：有 `SystemHandle`，但 `GetComponentTypes` 还返回 `NativeArray<ComponentType>`（`DynamicComponentTypeArray` 是 1.2+ 才有），`ComponentType` 上是 `IsSharedComponent` 而非 `IsSharedComponentData`。**这不影响 mod**——工程直接引用游戏自带的 `Unity.Entities.dll`，不存在版本猜测。踩到差异时编译器会直接告诉你（本次就靠这个发现并修正了 3 处 API 差异）。
- **游戏更新后需要重新 `build.ps1`**：引用 DLL 会从游戏目录重拷，组件字段改名会立刻变成编译错误而不是静默读错——这是特性，不是缺陷。
- `deep` dump 里 `NativeArray` / Blob / 指针类字段刻意不展开。要真读它们得走 unsafe 指针，风险和收益不成比例。
- 浮层是 IMGUI，性能开销可忽略，但截图/录屏时会一起被拍进去。

---

## 10. 实测验证记录

以下是在真实游戏上跑通的结果（Unity `2023.2.20f1c1`，BepInEx 5.4.23.5）：

```
[Info   :   BepInEx] BepInEx 5.4.23.5 - 机骸：第九行星
[Info   :   BepInEx] Running under Unity v2023.2.20.8921061
[Info   :   BepInEx] CLR runtime version: 4.0.30319.42000
[Info   :   BepInEx] System platform: Bits64, Windows
[Info   :   BepInEx] Loading [CombatInspector 1.0.0]
[Info   :CombatInspector] HTTP endpoint listening on http://127.0.0.1:8790/
[Info   :CombatInspector] CombatInspector installed successfully.
```

`GET /state` 实测返回（局内）：

```
worldReady=True  isInGame=True  scene=MainScene  mech=Sun  stage=0
totalEntities=1659..1727   players=1   enemies=19..25   bosses=0
devToolsPresent=True  ->  "DevTestTools is LIVE on 'Canvas' -
                           F1/F2/F3/F4/F5 and numpad 0-6 dev cheats are active in this build"
```

玩家实例（真实抓取）：`E163`，HP 80/80，Lv2，EXP 5/13，ATK 30（基础 30），攻击冷却 1.0s，
移速 3.0，索敌 10，技能 Sun/Single，击杀 35，坐标 (-18.03, 17.17, -0.83)，瞄准 (-18.53, 13.01)，
Perk `#28 Lv1`，Tag 10 个（含 `PlayerTag`、`MechCombatTag`、`BeDamagedThisFrame`…）。

敌人实例：`NRT1_Enemy无光小飞鸟白色` / `…白色 大`，HP 20/25，AI `Melee+FlyingMelee`，
伤害 10 / 冷却 1.0s，移速 1.4，`targetEntityIndex=163`（= 玩家），距离 6.6–30，角度齐全。

`GET /deep?enemies=1&depth=4` 实测：玩家实体 **78 个组件**全字段展开，含
`CritRateData{Value:0.05}`、`CritDamageData{Value:1,ChipValue:1}`、`BeaconAttackMulData{KillMul,LowHpMul}`、
`ChipRuntimeAttackMul/AttackSpeedMul/MoveMul`、`MetaSkillCooldownMul{Multiplier}`、`MetaPickupRangeBonus`、
`BurnDamageFactorData` 等 5 个燃烧乘区、`BurningAttackData{AttackPrefab:E800:1, DamageInterval, CollisionFilter{...}}`、
`Unity.Physics.PhysicsVelocity/PhysicsMass/PhysicsDamping/PhysicsGravityFactor`、
`Unity.Transforms.LocalTransform/LocalToWorld/Child[Buffer]/PostTransformMatrix`。
Buffer 修复后：`PlayerPerk[Buffer] -> [{"PerkID":25,"Level":1}]`、`Unity.Transforms.Child[Buffer] -> [{"Value":"E169:7"}]`。

仪表盘渲染已用无头 Chrome 截图核对：`mod/out/dashboard_shot.png`（mock 数据）、
`mod/out/dashboard_live.png`（真实游戏数据）。

### 血条与预测的实测验证

局内抓取（`/state` 诊断字段）：

```
barsInfo  = enabled=True drawn=2 cam=ok frame=2731/2732
radarInfo = visible=True rect=(3500,14 326x396) posInit=True lastDrawFrame=2731/2732 err=-
player: atkPower=30 critRate=0 critMult=1 estDmg=30 atkCd=1
enemy : hp=20/20 armor=0 armorMul=1.000 pred=30.0 src=模型 after=0.0 die=true hits2kill=1
```

要点：

- `cam=ok` → `Camera.main` 可用，血条投影成立；`drawn=2` 是因为大部分敌人当时不在屏幕内（正常剔除）
- `radarInfo` 顺带确认 Unity 逻辑分辨率是 **3840**（Windows 150% 缩放），所以 IMGUI 坐标空间是 3840×2160，不是物理 2560
- 当时没人攻击 → `tracker=0`，预测走模型：`30 × 1 × 1 = 30`，敌人 20 血 → 预计归零、`可秒杀`、`1 击必杀`，符合预期
- 桌面截图 `mod/out/healthbars_shot.png` 里能看到实际渲染的一条：`45/45 →13 -32 2击`（绿+橙分段）

### 游戏更新后的复验（buildid 25191507）

一次游戏更新删除了 `Newtonsoft.Json.dll`。处理与复验过程：

1. `build.ps1` 刷新引用 DLL 后重编译 **0 错误** —— 说明战斗组件名在该更新中未变；
2. 发现 Newtonsoft 被删 → 换成自带 `MiniJson`，去掉对它的编译期与运行期依赖；
3. 重新部署后 `/state`、`/deep` 输出经 node 校验为合法 JSON，仍返回 20 个敌人、同名组件与坐标；
4. 桌面截图 `mod/out/game_overlay.png` 确认全中文 IMGUI 浮层在游戏内正常渲染
   （对局/自身/血量/经验/敌人表/近战+飞行近战 标签均为中文）。

雷达窗第一版漏设中文字体导致乱码，修复方式见 3.1 ④ 的说明；修复版已部署
（`mod/out/_deploy_watch.txt` 记录：deployed=True，字节数与构建产物一致）。

---

## 11. 工程结构

```
H:\hnworkspace\yxykgame\
├── component_inventory.txt           全部组件/系统/枚举 + 字段清单
├── types_all.txt                     类型名总表
├── decompiled\AssemblyCSharp\        （本地研究用，不随仓库发布：游戏反编译源码 773 个 .cs）
└── mod\
    ├── gamepath.txt                  游戏安装路径（UTF-8，一行）
    ├── _common.ps1                   路径解析助手（脚本全 ASCII，避开 PS 5.1 的编码坑）
    ├── build.ps1                     刷新引用 DLL + 构建两个加载器变体
    ├── install.ps1                   装加载器 + 插件
    ├── uninstall.ps1                 卸载（-Full 回到原样）
    ├── vendor\                       BepInEx 5.4.23.5 / 6.0.0-pre.2 离线包
    ├── libs\  libs5\  libs6\         编译期引用（build.ps1 自动填充）
    ├── dist\bepinex5|6\              构建产物
    ├── out\                          验证产物：样本 JSON + 仪表盘截图 + mock 服务
    └── CombatInspector\
        ├── CombatInspector.csproj
        ├── Plugin.cs                 BepInEx 入口（唯一碰加载器 API 的文件）
        ├── Runner.cs                 MonoBehaviour：抓取循环 / 热键 / 落盘 / 处理 HTTP 队列
        ├── CombatScanner.cs          第一层：强类型战斗快照
        ├── DeepDumper.cs             第二层：反射式全组件全字段 dump
        ├── Overlay.cs                IMGUI 主浮层（全中文）
        ├── Radar.cs                  IMGUI 雷达窗（右上角，普通/精英/首领区分）
        ├── HealthBars.cs             怪物头顶血条（世界→屏幕投影，含下一击预测）
        ├── DamageTracker.cs          实测优先的伤害预测（滚动统计 + 模型兜底）
        ├── TacticsAdvisor.cs         只读战术建议（集火目标 / 提前量瞄准 / 走位 / 危险度）
        ├── Navigation.cs             地图边界 + 障碍感知（可玩区/墙段/16方向射线）
        ├── AimOverrideSystem.cs      接管瞄准的状态与执行逻辑（静态类；曾是 SystemBase，见 §7.4）
        ├── AimTakeoverPatch.cs       Harmony postfix 补 MouseInputSystem，驱动上面的逻辑
        ├── Cjk.cs                    IMGUI 中文字体与样式（默认皮肤字体不含中文字形）
        ├── MiniJson.cs               零依赖 JSON 序列化（游戏更新删掉了 Newtonsoft）
        ├── StateHttpServer.cs        TcpListener 极简 HTTP + 主线程请求队列
        ├── dashboard.html            网页实时仪表盘（编译为 EmbeddedResource）
        └── Models.cs                 DTO
```
