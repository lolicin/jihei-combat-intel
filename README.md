# 机骸：第九行星 — 战斗情报层 (jihei-combat-intel)

一个 BepInEx mod，把《机骸：第九行星》的 **ECS 战斗世界完整读出来**，并在此基础上做
**伤害预测、怪物血条、战术建议、地图障碍感知**。

这是"给这游戏做自动战斗"的**第一步**：先把战场读清楚、把决策逻辑暴露出来供人工验证，
而不是一上来就黑盒接管操作。

> 游戏是 Unity **2023.2.20f1c1** + **Entities 1.x**（纯 ECS，战斗数据不在 GameObject 上），
> 且有手写 Steam ghost 同步的网络层。这决定了读法和写法都必须走 ECS，不能走 `GameObject.Find`。

---

## 状态一览（哪些是实测过的，哪些不是）

| 能力 | 状态 | 依据 |
|---|---|---|
| 读取全量战斗快照（玩家/敌人/Boss/友军/弹幕） | ✅ 实测 | 局内 40+ 敌人实时抓取 |
| 反射式深度转储（每个组件每个字段） | ✅ 实测 | `PlayerPerk [Buffer]` → `{"PerkID":25,"Level":1}` |
| HTTP/JSON/CSV 输出 + 网页实时仪表盘 | ✅ 实测 | `http://127.0.0.1:8790/` |
| IMGUI 游戏内浮层（全中文） | ✅ 实测 | 见 `docs/images/` |
| 雷达窗（普通/精英/首领区分） | ✅ 实测 | F11 |
| **怪物头顶血条 + 下一击预计剩余** | ✅ 实测 | F12，`45/45 →13 -32 2击` |
| **只读战术建议**（集火目标/提前量瞄准/走位/危险度） | ✅ 实测 | F8，含打分理由与候选排名 |
| **地图边界 + 障碍感知** | ✅ 实测 | 障碍命中 12779 次，能挑出 15 挡 1 通里的那条缝 |
| **接管瞄准**（写 `MouseTarget`） | ✅ 实测生效 | F7；`aimDiff` 从 8.9~11.7 降到 **0.000**，累计写入 2850 次 |
| 接管移动 / 技能 | ⛔ 未开始 | 依赖上一条打通 |

**已知失败是诚实记录的**，不是没做完就删掉——§7.4 完整留着那次排错（三种注册方式都"成功"、日志漂亮、
实际什么都不干），因为"静默失败"这个坑本身值得记下来。失败的 `SystemBase` 方案已换成静态类 + Harmony 补丁，
但结论保留在文档里。

---

## 这游戏怎么被打伤害的（核心公式，反编译确证）

```csharp
// ProcessDamageJob：只做扣血，伤害在上游算好塞进受击者 buffer
hp.Value -= DamageThisFrame[i].Value;

// AttackCheckTriggerRunner.ComputePlayerDamage：玩家 → 敌人
damage = baseDamage * pow(0.5f, 目标护甲 / PerkConfig.ArmorPerHalfDamage);  // 常量 = 100
// baseDamage = 弹体 AttackData.AttackDamage；普攻即 PlayerAttackData.AttackPower
// 暴击在生成弹体那一刻 roll（CritRateData / CritDamageData）→ 单次伤害无法事后精确复现
```

所以预测策略是**实测优先**：`DamageThisFrame` 是结算后的真值（已含暴击、乘区、护甲减免），
命中过一次就用它的滚动统计预测；没命中过才退回模型，并在界面上明确标 `源:模型`，不假装精确。

---

## 快速开始

```powershell
cd mod
# 游戏路径写进 gamepath.txt（一行，UTF-8）；可从 gamepath.example.txt 复制改
.\build.ps1          # 刷新引用 DLL + 构建 BepInEx 5/6 两个变体
.\install.ps1        # 装加载器 + 插件
# 启动游戏，进局内：F7 接管瞄准 / F8 建议层 / F9 浮层 / F10 落盘 / F11 雷达 / F12 血条
```

游戏内**自带开发工具**（`DevTestTools is LIVE on 'Canvas'`，F1-F5 + 小键盘 0-6 作弊），
所以这个 mod 完全不需要碰作弊层就能拿到数据。

详细文档、完整数据字典、配置项、排错记录都在 → **[mod/README.md](mod/README.md)**

---

## 仓库内容

```
component_inventory.txt   全部 ECS 组件 / 系统 / 枚举 + 字段清单（从游戏程序集提取）
types_all.txt             类型名总表
docs/images/              仪表盘渲染截图（mock 数据）
mod/
  CombatInspector/        mod 源码（17 个 .cs + dashboard.html）
  *.ps1                   构建 / 安装 / 卸载 / 部署哨兵 / 实机探针
  README.md               完整技术文档
```

### 有意**不**发布的东西

- `decompiled/` —— 游戏的反编译源码（773 个 .cs）。那是开发商的专有代码，且这是 Playtest 版本。
  本仓库所有结论都**引用了具体类型/方法名**，你可以自行复现，但源码本身不该由我分发。
- `mod/libs*/`、`mod/vendor/` —— 游戏 DLL 与 BepInEx 发行包，请自备。
- `mod/out/` —— 实机验证产物。其中若干张是**桌面截图**，会拍到无关窗口，不适合公开。

---

## 值得看的几个文件

| 文件 | 为什么 |
|---|---|
| `CombatScanner.cs` | 纯 ECS 读取层：`ResolvedObjectTree` → `World` → `EntityManager`，以及各类组件的读法 |
| `DeepDumper.cs` | 反射式全组件 dump，含 `DynamicBuffer` 的正确遍历方式（见下的坑） |
| `DamageTracker.cs` | 实测优先的伤害预测 |
| `TacticsAdvisor.cs` | 集火打分（带可解释理由）、拦截提前量解算、走位合成 |
| `Navigation.cs` | 可玩区 / 墙段 / 障碍探测，含 `CastRay` 陷阱的完整说明 |
| `AimOverrideSystem.cs` | 接管瞄准的**正确思路** + 为什么在这个版本没生效 |
| `MiniJson.cs` | 零依赖 JSON（游戏某次更新把 `Newtonsoft.Json.dll` 整个删了） |
| `Cjk.cs` | IMGUI 画中文（默认皮肤字体不含中文字形） |

---

## 踩过的坑（都写进了文档，这里只列名字）

1. **`DynamicBuffer<T>` 的非泛型 `GetEnumerator()` 是 `throw NotImplementedException`** —— 反射路径必须走 `Length` + 索引器。
2. **`RaycastInput` 的 `QueryContext` 是 internal 字段** —— 对象初始化器不会设置它，导致 `CastRay` **永远返回 false**（零命中零异常，极难察觉）。改用游戏自己用出效果的 `OverlapAabb`。
3. **`AddSystemManaged(instance)` 不应用 `[UpdateInGroup]`/`[UpdateAfter]` 属性** —— 注册"成功"但系统不进更新循环。
4. **托管 `SystemBase` 在这个 unmanaged 驱动的世界里不被驱动** —— 三种注册方式全试过，`OnUpdate` 从未执行。
5. **游戏更新会删依赖程序集**（`Newtonsoft.Json.dll` 就这么没了）—— 不要依赖游戏可能增删的 DLL。
6. **IMGUI 默认皮肤无中文字形** —— 中文全是方框，需动态取 OS 字体。
7. **状态字符串别和字段初始值撞车** —— 最初 `LastStatus` 默认值就是"未启用"，导致"没跑"和"跑了但关着"完全分不开。

贯穿 2/3/4/7 的方法论：**每个可疑环节都埋一个可量化计数器**（`障碍命中/刷新次数`、`累计写入`）。
没有它们，就会去瞎改逻辑而永远发现不了"代码根本没在执行"。

---

## 合法性与使用边界

- 纯**本地只读**为主：抓取、可视化、给建议。不修改游戏内存、不影响他人（联机下写权威值会被覆盖）。
- 接管类功能默认**全部关闭**，且只写本地玩家实体的输入组件。
- 反编译仅用于**理解数据接口**以做兼容性；本仓库不重新分发游戏代码。
- 这是 **Playtest** 版本，游戏一更新字段就可能改名 —— `build.ps1` 会重拷引用 DLL，改名立刻变成编译错误而不是静默读错（这是特性）。

---

## License

研究/学习用途。mod 源码本身随本仓库发布；**游戏资产与反编译源码不在此列**。
