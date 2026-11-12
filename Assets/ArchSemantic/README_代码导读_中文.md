# CMER 项目代码导读（中文）

> 目标：帮助你以“技术架构师视角”快速读懂 `Assets/ArchSemantic/Scripts` 下全部脚本。
> 
> 说明：`Scripts` 总体代码量较大（尤其 `LiveGeometryKernel.cs`、`PilotSession.cs`），直接在源码中做“逐行中文注释”会显著增加维护成本和合并冲突风险。建议优先使用本导读建立整体模型，再按模块分批做源码注释（我可以继续按你的优先级逐文件落地）。

---

## 1. 项目整体功能（一句话）

这是一个用于建筑语义实验（CMER）的 Unity XR 交互系统：
- 通过手势/桌面输入修改 3 个语义参数（P1/P2/P3）；
- 实时重建并验证建筑几何；
- 支持模型视图与 1:1 身体尺度切换；
- 按实验流程记录事件、检查点、回溯链路。

---

## 2. 你在项目里能“操作”的所有能力

### 2.1 实验流程层（`StudyFlow`）
- Setup（准备）
- Practice（练习）
- InitialWalkthrough（初始走查）
- Revision（修改）
- FinalWalkthrough（最终走查）
- Questionnaire（问卷）
- Washout（洗脱）
- Recall（回溯）
- Complete（结束）

### 2.2 输入与交互层
- **手势输入**（Quest Hands）
  - 选择参数 P1/P2/P3
  - 双手展开/收拢（P1/P2）
  - 主手沿路径推动（P3）
  - 松手触发提交
- **桌面模式输入**（Desktop Rehearsal）
  - 视口控制与导航
- **工作台 UI（侧边面板）**
  - 空间跳转（F/R/G/C）
  - 参数选择与锁定
  - 组别预览（研究者模式）
  - 隐藏/显示 UI

### 2.3 几何与导航层
- 参数变化 -> 几何重建（异步）
- 几何合法性验证（体量、净高、通路）
- 模型视角与 1:1 视角切换
- 地面跟随、可站立校验、传送

### 2.4 数据与可追溯层
- 事件流：`events.jsonl` + `events.csv`
- 检查点：`states/*.json`
- 会话封存：`seal.json`
- 回溯链：`recall_history.json`

---

## 3. `Scripts` 下每个脚本的职责

> 按目录分组说明。

## A. `Geometry/`

### 3.1 `GeometryDomain.cs`
**定位**：几何参数域与约束规则核心。  
**你重点看**：
- `SpatialState`：P1/P2/P3 状态容器；
- `Clamp/Ease/Dimensions`：把参数映射为尺寸；
- `IsLegal/LegalDimensions`：合法性判定；
- `SolveActive`：活动轴求解（保持其它轴不变）。

**价值**：这是“语义参数 -> 数学几何约束”的第一性原理文件。

### 3.2 `GeometryPrimitives.cs`
**定位**：轻量几何基元（点、向量、曲线、实体、三角化）。  
**你重点看**：
- `Point3d/Vector3d/Transform`；
- `Solid.Prism`（棱柱构建）；
- `Solid.Triangles`（面三角化）。

**价值**：该项目避免 CAD 运行时依赖，几何数据结构在这里自包含。

### 3.3 `GeometryValidation.cs`
**定位**：几何结果度量与验证（体积、房间、路径）。  
**你重点看**：
- `GeometryMetrics`、`RoomMetrics`；
- `Measure`：几何哈希、体量误差、最小门洞；
- `FloorNavigation`：地面查找/支撑检测；
- `RouteValidation`：通路宽度与连通保守验证。

**价值**：这是实验“可用性与契约正确性”的守门员。

### 3.4 `LiveGeometryKernel.cs`
**定位**：几何生成内核（最大文件）。  
**你重点看**：
- `Evaluate`：输入 `Scenario + P1/P2/P3` 输出完整语义几何；
- 命名规则 `A__CLS__OWNER__ROLE__NN`；
- 墙、板、门窗、上层空间等构造逻辑。

**价值**：这是“把抽象语义落成建筑实体”的主引擎。

### 3.5 `LiveGeometryDriver.cs`
**定位**：Unity 驱动层（任务调度 + 网格应用）。  
**你重点看**：
- `RequestState` / `Prepare` / `Apply`；
- 异步任务、票据(ticket)防串；
- Mesh 与 Collider 更新。

**价值**：把内核结果安全地投到 Unity 场景并保持交互流畅。

---

## B. `Interaction/`

### 3.6 `NoHandleSemanticGestureInteractor.cs`
**定位**：手势解释器（核心交互入口）。  
**你重点看**：
- 手部追踪可用性检测；
- 命令姿态（history/scale/workspace/context/reset/point）；
- 激活-预览-提交事务闭环；
- `engine.SetAxis` 
- / `engine.Commit` 调用时机。

**价值**：这是“人手动作 -> 参数事务”的桥。

### 3.7 `SemanticGestureZone.cs`
**定位**：每个参数的手势标定容器。  
**你重点看**：
- `axis`、`semantic`、`travelMeters`。

**价值**：将抽象参数与空间交互标定解耦。

### 3.8 `HandSkeletonView.cs`
**定位**：XR Hands 可视化。  
**你重点看**：
- 官方手部 prefab 加载；
- 跟踪时显示/丢失时隐藏。

**价值**：纯表现层，不改业务状态。

---

## C. `Study/`

### 3.9 `PilotSession.cs`
**定位**：会话总控（应用入口级核心）。  
**你重点看**：
- 生命周期 `Start/Update` 附近逻辑；
- 权限门控 `CanEdit/CanNavigate/CanToggleWorkspace`；
- 预览/提交事务（`SetAxis` -> `Commit` -> `Prepared`）；
- 历史（Undo/Redo）、检查点、故障处理。

**价值**：几乎所有系统都通过它协同。

### 3.10 `StudyCore.cs`
**定位**：协议与流程核心模型。  
**你重点看**：
- 枚举：Condition/Scale/Phase/InputMode；
- `StudySettings.Validate`；
- `StudyFlow` 的状态机推进；
- `CheckpointValidation`。

**价值**：实验规则的“法典”。

### 3.11 `StudyView.cs`
**定位**：导航与视图编排（`StudyNavigation`）。  
**你重点看**：
- `ShowModel` / `ShowBody`；
- `GoToSpace/WalkthroughStop/TryTeleport`；
- `FollowFloor`、`AlignFloor`。

**价值**：保证“看哪里、站哪里、怎么转场”一致正确。

### 3.12 `StudyWorkspace.cs`
**定位**：侧边工作台 UI（表现与交互入口）。  
**你重点看**：
- 面板构建与按钮命中；
- pointer 与 pinch 消费策略；
- `RequestWorkspaceAction` 触发。

**价值**：实验者实际可见可点的 UI 中枢。

### 3.13 `StudyRecorder.cs`
**定位**：数据记录与封存、回溯谱系。  
**你重点看**：
- `Append` 事件落盘；
- `SaveCheckpoint`；
- `Seal` 完整性清单；
- `RecallArchive.Attach/Read`。

**价值**：研究可追溯与数据可信的关键。

---

## 4. 关键调用链（建议先背下来）

## 4.1 参数编辑主链（最重要）
1. `NoHandleSemanticGestureInteractor` 识别手势并得到参数值；
2. 调用 `PilotSession.SetAxis(axis, value)` 开始预览事务；
3. `PilotSession` 调 `LiveGeometryDriver.RequestState(... commit=false)`；
4. `LiveGeometryDriver.Prepare` 内部调用：
   - `GeometryDomain.SolveActive`（必要时）
   - `LiveGeometryKernel.Evaluate`
   - `GeometryValidation.Measure`
5. `PilotSession.Prepared(data)` 收到预览结果并 `geometry.Apply(data, false)`；
6. 松手后 `PilotSession.Commit(axis)`；
7. 再走一次 `RequestState(... commit=true)`，`Prepared` 后写历史/日志/检查点。

## 4.2 导航主链
1. 工作台按钮或流程触发导航请求；
2. `PilotSession` 校验阶段与权限；
3. `StudyNavigation` 执行 `ShowModel/ShowBody/TryTeleport/GoToSpace`；
4. `FloorNavigation` 与碰撞体保证可站立与地面合法。

## 4.3 记录主链
1. `PilotSession` 在关键事件创建 `StudyEvent`；
2. `StudyRecorder.Append` 同步写 JSONL + CSV；
3. 阶段性 `SaveCheckpoint`；
4. 结束时 `Seal` 生成文件哈希清单。

---

## 5. 推荐读码顺序（架构师版本）

> 建议分 4 轮，每轮都有“输入/输出/约束”。

### 第 1 轮：建立系统地图（30~60 分钟）
- `START_HERE.txt`
- `PilotSession.cs`（只看字段、状态和公开方法）
- `StudyCore.cs`（看枚举和 `StudyFlow`）

你要回答：
- 会话状态有哪些？
- 谁有权限触发编辑？
- 参数事务边界在哪里？

### 第 2 轮：看交互如何触发事务（60~90 分钟）
- `NoHandleSemanticGestureInteractor.cs`
- `StudyWorkspace.cs`

你要回答：
- 手势如何变成 `SetAxis/Commit`？
- 哪些动作会被 UI 消费掉？
- 如何避免误触与重复触发？

### 第 3 轮：看几何如何生成与校验（半天）
- `GeometryDomain.cs`
- `LiveGeometryKernel.cs`
- `GeometryValidation.cs`
- `LiveGeometryDriver.cs`

你要回答：
- 参数如何映射到几何尺寸？
- 非法输入如何被限制？
- 预览与提交在几何层有何不同？

### 第 4 轮：看导航与可追溯（60~120 分钟）
- `StudyView.cs`
- `StudyRecorder.cs`

你要回答：
- 模型视图与身体视图如何切换？
- 数据能否完整回放与验真？

---

## 6. 你可以按这条“理解主线”快速掌控项目

1. **先把 `PilotSession` 当作总线**：其他模块几乎都在围绕它。  
2. **把编辑当事务**：Begin(预览) -> Validate -> Commit(落地) -> Record。  
3. **把几何当纯函数**：`Scenario + P1/P2/P3 -> Geometry Result + Metrics`。  
4. **把 UI 当门禁**：任何操作都要过 `CanEdit/CanNavigate/InputReady`。  
5. **把数据当证据链**：事件 + 检查点 + seal + recall lineage。

---

## 7. 关于“逐行中文注释”的建议执行方式（实战）

如果你确认要落地到源码，我建议按下面节奏做，避免一次性改太大：

- **批次 1（最值得注释）**：
  - `PilotSession.cs`
  - `NoHandleSemanticGestureInteractor.cs`
  - `LiveGeometryDriver.cs`

- **批次 2（规则层）**：
  - `StudyCore.cs`
  - `GeometryDomain.cs`

- **批次 3（重计算层）**：
  - `LiveGeometryKernel.cs`
  - `GeometryValidation.cs`

- **批次 4（展示与记录）**：
  - `StudyView.cs`
  - `StudyWorkspace.cs`
  - `StudyRecorder.cs`
  - 其余小文件

每批完成后做一次编译与回归。

---

## 8. 下一步我可以直接帮你做的事

你只要回复一个序号：
- `1`：我先给你做 **批次1** 的源码中文注释（高价值核心）。
- `2`：我先给你输出 **“参数编辑事务”时序图 + 状态图**（便于架构评审）。
- `3`：我先给你做 **“几何内核变量词典”**（LiveGeometryKernel 关键变量逐项释义）。

我建议从 `1` 开始。