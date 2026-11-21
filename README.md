# Seraph A Living Survival Simulation

炽天使：生存模拟 **1.0.1**，适用于 Vintage Story 1.22.x。客户端与服务端均需安装主包；可选客户端调试包需要 VSImGui 1.2.7。

## 功能

- 营养与消化：热量、六项营养储备、电解质及可搜索的食物营养手册。
- 饮水与水质：水分吸收、污染传播与消退、过滤、煮沸、蒸馏和井水。
- 体能与休息：耐力、劳动疲劳、肌肉训练、睡眠与营养增益。
- 体温与环境：日晒遮阳、衣物、风雨、浸水换热、失温与冻伤。
- 伤病与急救：伤口、出血、绷带、按压止血、免疫与疾病。
- 卫生与污染：排泄、洗浴、整组果蔬清洗、腐败物污染及 Real Smoke 气体联动。
- 生存设施：井、浴桶、导流锅、过滤锅、烟囱、肥皂、堆肥与围石篝火。
- 状态界面：原生角色面板、人体部位图、状态提示和中英文游戏内手册。

## 操作与指令

按 `C` 查看角色，按 `H` 阅读手册；文明核心菜单的肉图标打开营养手册。对水源按住 `Shift + 右键` 约 3 秒饮水，手持果蔬时清洗整组。按住 `P` 按压止血，`K` 闭眼休息，`Shift + V` 排泄；快捷键可在设置中修改。

| 指令 | 用途 |
| --- | --- |
| `/vsslssmod`、`/vsslssmod check` | 查看自身状态、指标与行动限制 |
| `/vsslssmod cancel` | 取消护理动作 |
| `/vsslssmod drink`、`/vsslssmod pressure`、`/vsslssmod clean` | 查看饮水、止血和清洗操作提示 |
| `/vsslssmod mode survival\|shadow\|observe` | 管理员切换模拟模式 |
| `/vsslssmod compat`、`/vsslssmod probe` | 管理员检查兼容与运行状态 |
| `/vsslssmod speed <倍率>` | 查看游戏日历调速提示，实际调速使用原版 `/time speed` |
| `.vsslssmodhud` | 开关人体部位图 |
| `.slssdebug`、`.slsspollution` | 开关活动调试、污染范围显示 |
| `.slssperf start\|stop\|status` | 性能采样，默认采样 60 秒 |
| `.vsslssmod debug` | 打开可选调试窗口，也可按 `Ctrl + Shift + V`；修改状态需要管理员权限 |

## 配置

以下 `ModConfig` 路径相对于游戏数据目录。手动修改后重启服务端；无效配置会拒绝加载并保留原文件，旧测试版存档和配置不自动迁移。

| 文件 | 用途 |
| --- | --- |
| `ModConfig/vsslssmod.Server.json` | 首次运行生成的服务端配置 |
| `ModConfig/vsslssmod.Foods.json` | 可选食物数据覆盖；以主包内 `assets/vsslssmod/config/foods.json` 为模板 |
| `assets/vsslssmod/config/compatibility.json`（包内） | 内置兼容目录，用于诊断 |

常用服务端配置（完整字段以生成文件为准）：

| 字段 | 默认值 | 作用 |
| --- | --- | --- |
| `Mode` | `survival` | `survival` 应用生理效果；`shadow` 仅模拟；`observe` 不推进模拟 |
| `EnableClinicalEffects` / `EnableMovementEffects` | `true` | 生理状态与移动影响 |
| `EnableSymptomVisuals` | `true` | 症状视觉效果 |
| `EnableGroundWaterDrinking` / `EnableMedicalActions` / `EnableDefecation` | `true` | 水源饮水、护理、排泄 |
| `DrinkServingL` / `DrinkCooldownRealSeconds` | `0.25` / `3` | 每次饮水量（升）与冷却（现实秒） |
| `DigestionGPerGameHour` | `1667` | 每游戏小时食物消化量（克） |
| `Digestion.WaterMlPerHour` / `Digestion.WaterHalfLifeHours` | `2000` / `0.3333333333333333` | 每游戏小时吸水上限（毫升）与胃水吸收半衰期（小时） |
| `SolarPowerWAtFullExposure` | `500` | 完全暴露时的日光热量（瓦） |
| `ClothingCloByCode` / `HeadgearShadeByCode` | `{}` | 衣物保暖值与头饰遮阳比例（0–1），按物品代码指定 |
| `SurfaceWater` / `DiseaseExposure` / `Sanitation` | 配置对象 | 天然水、疾病暴露与卫生污染参数 |
| `Exertion` / `ThermalAdaptation` / `Pain` / `Circulation` | 配置对象 | 劳动、热适应、疼痛与循环参数 |
| `TelemetryEnabled` / `CheckpointSeconds` | `false` / `30` | 遥测开关与存档检查点间隔（现实秒） |

## 作者与授权

作者：HansJack、ch4Ver、buzidao9、Zhiketangjiang2、KitamuraEri。

项目代码采用 [MIT](LICENSE)。模型、图标、音效、游戏衍生素材和数据来源见 [素材声明](ASSET-CREDITS.md) 与 [第三方许可](THIRD-PARTY-NOTICES)。第三方素材遵循各自授权；发行包保留版权声明，不包含此 README。
