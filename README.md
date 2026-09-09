# IndustrialCommunication

配置驱动的一体化工业通信库(.NET 8):一个 JSON 配置文件描述所有设备,统一 API 读写各类 PLC 与现场设备。

**核心能力**

- **16 种协议接入**:Modbus 全家族、西门子/三菱/欧姆龙/松下/基恩士/AB/GE/LS 八大品牌 PLC、OPC UA
- **轮询引擎 + 订阅**:`pollGroups` 配置即数据采集,值变化回调
- **后台连接监控**:断线自动重连 + 心跳探活半开连接
- **MQTT / Sparkplug B 云桥接**:轮询数据发布上云,云端命令下发写入
- **DI 集成**:`services.AddIndustrialCommunication(...)` 一步接入 Generic Host
- **统一编程模型**:同一套 `Read/Write/ReadGroup` API 覆盖全部协议,运行期错误返回 Result 而非异常

## 协议支持

| 协议 | protocol 名称 | 底层实现 | 传输 |
|---|---|---|---|
| Modbus TCP | `ModbusTcp` | [NModbus](https://github.com/NModbus/NModbus) 3.0.83 | TCP 502 |
| Modbus UDP | `ModbusUdp` | NModbus | UDP 502 |
| Modbus RTU | `ModbusRtu` | NModbus + NModbus.Serial | 串口 RS232/485 |
| Modbus ASCII | `ModbusAscii` | NModbus + NModbus.Serial | 串口 RS232/485 |
| 西门子 S7(200 SMART/300/400/1200/1500) | `S7` | [S7netplus](https://github.com/S7NetPlus/s7netplus) 0.20.0 | ISO-on-TCP 102 |
| 三菱 MC(3E/4E 帧) | `MitsubishiMc` | 库内手写报文 | TCP 5007 |
| 三菱 MC | `MitsubishiMcUdp` | 同上 | UDP 5007 |
| 欧姆龙 FINS | `OmronFins` | 库内手写报文 | FINS/TCP 9600 |
| 欧姆龙 FINS | `OmronFinsUdp` | 同上 | FINS/UDP 9600 |
| 松下 MEWTOCOL-COM | `PanasonicMewtocol` | 库内手写报文 | TCP 9094 |
| 基恩士上位链路 | `KeyenceUpperLink` | 库内手写报文 | TCP 8501 |
| 基恩士上位链路 | `KeyenceUpperLinkUdp` | 同上 | UDP 8501 |
| AB EtherNet/IP(ControlLogix/CompactLogix) | `RockwellEtherNetIp` | 库内手写 CIP | TCP 44818 |
| GE SRTP(90-30/90-70/RX3i) | `GeSrtp` | 库内手写(逆向工程规范) | TCP 18245 |
| LS 产电 XGT 专用协议 | `LsFEnet` | 库内手写报文 | TCP 2004 |
| OPC UA | `OpcUa` | [OPCFoundation.NetStandard.Opc.Ua.Client](https://github.com/OPCFoundation/UA-.NETStandard) 1.5.378 | opc.tcp 4840 |
| MQTT / Sparkplug B | `mqtt` 配置节 | [MQTTnet](https://github.com/dotnet/MQTTnet) 4.3 | 云桥接(见下文) |

**加一个协议 = 新建一个驱动包 + 一个 `Add...()` 注册,Core 永不动。**

### 国产设备接入指引(经调研核实,全部走现有 Modbus 驱动)

| 设备 | 接入方式 | 注意事项 |
|---|---|---|
| 汇川 H1U/H2U 小型 PLC | **Modbus RTU**(串口从站,站号 D8121、格式 D8120) | 本体无网口,上以太网需串口服务器;M/D 从 0 起,X/Y 八进制 |
| 汇川 H3U / H5U / Easy | **Modbus TCP**(默认 502) | M0-7999→线圈 0x0000、D0-7999→保持 0x0000、B/R→0x3000、S→0xE000、X→0xF800、Y→0xFC00(八进制);32 位量注意 CDAB |
| 汇川 AM/AC 中型(CODESYS) | **Modbus TCP**(InoProShop 勾选 Modbus Slave) | %MW→保持 0x0000 直映、%QX→线圈、%IX→离散输入 |
| 汇川变频器/伺服 | **Modbus RTU** | MD200-500 命令字 3000H;IS620P/SV660P 走 FC03/06/10;EtherCAT/CAN 版本不走 Modbus |
| 台达 DVP ES/SS/EH | **Modbus ASCII/RTU**(COM2,常默认 ASCII) | **地址偏移**:S=0x0000、X=0x0400、Y=0x0500、T=0x0600、C=0x0700、M=0x0800、**D=0x1000**、T 当前值=0x1100、C 当前值=0x1200;X/Y 八进制;十进制地址=Hex+1 |
| 台达 AS300 | **Modbus TCP**(502)+ EtherNet/IP | D 区沿用 0x1000 偏移风格 |
| 台达 MS300/VFD-M 变频器 | **Modbus RTU** | **2000H=运行命令/频率**(与汇川 3000H 不同,勿混模板);VFD-M 需 P92 选 Modbus 模式 |

汇川/台达均**无原生三菱 MC 从站**,MC 驱动不适用;若必须走 MC 只能加协议网关(硬件方案)。

## 快速开始

```bash
dotnet run --project samples/IndustrialCommunication.Demo   # 无需硬件:内置 Modbus 从站 + MQTT broker
```

```csharp
using IndustrialCommunication;

using var host = CommunicationHost.Load("comm.json", r => r
    .AddModbusTcp().AddModbusUdp().AddModbusRtu().AddModbusAscii()
    .AddSiemensS7().AddMitsubishiMc().AddMitsubishiMcUdp()
    .AddOmronFins().AddOmronFinsUdp()
    .AddPanasonicMewtocol().AddKeyenceUpperLink().AddKeyenceUpperLinkUdp()
    .AddRockwellEtherNetIp().AddGeSrtp().AddLsFEnet().AddOpcUa());

var plc = host.GetClient("s7-main");          // 按名称取设备(惰性构建 + 缓存)
await host.ConnectAllAsync();

var t = await plc.ReadAsync<float>("DB1.DBD10");   // Result 模式,失败不抛异常
if (t.Success) Console.WriteLine(t.Value);

await plc.WriteAsync<short>("MW100", -4242);       // 类型化写入

var group = await plc.ReadGroupAsync([             // 组读,单点失败不拖垮整组
    new DevicePoint { Name = "speed", Address = "DB1.DBD10", ValueType = PlcValueType.Float32 },
    new DevicePoint { Name = "run",   Address = "M50.4",     ValueType = PlcValueType.Bit }]);
```

**ASP.NET Core / Generic Host**(IndustrialCommunication.DependencyInjection 包):

```csharp
services.AddIndustrialCommunication("comm.json", r => r.AddModbusTcp().AddSiemensS7());
// 注入 IPlcClientFactory,用 factory.GetClient("s7-main") 取客户端;容器负责生命周期
```

## 配置文件

```jsonc
{
  "defaults": {                                  // 全部设备的运行参数
    "timeoutMs": 3000,                           //   单次读写超时,超时后强制断连(半开保护)
    "connectTimeoutMs": 5000,                    //   连接超时
    "connectRetries": 2,                         //   ConnectAsync 内的重试次数
    "retryIntervalMs": 2000,
    "autoReconnect": true,                       //   断线后下次操作前透明重连
    "connectionMonitor": {                       //   后台连接监控(可选)
      "enabled": true,                           //     断线自动重连 + 在线心跳探活
      "intervalMs": 5000
    }
  },
  "devices": [
    { "name": "s7-main", "protocol": "S7",
      "description": "产线主控", "enabled": true,
      "connection": { "ip": "192.168.1.10", "cpuType": "S71200", "rack": 0, "slot": 0 } },
    { "name": "inverter", "protocol": "ModbusTcp",
      "connection": { "ip": "192.168.1.20", "port": 502, "unitId": 1, "dataLayout": "CDAB" } },
    { "name": "rtu-scale", "protocol": "ModbusRtu",
      "connection": { "portName": "COM3", "baudRate": 9600, "parity": "None", "stopBits": "One", "unitId": 2 } },
    { "name": "mc-q06", "protocol": "MitsubishiMc",
      "connection": { "ip": "192.168.1.30", "port": 5007, "frame": "4E", "monitorTimerMs": 3000 } },
    { "name": "fins-nj", "protocol": "OmronFins",
      "connection": { "ip": "192.168.1.40", "sourceNode": 25 } },
    { "name": "fp0r", "protocol": "PanasonicMewtocol",
      "connection": { "ip": "192.168.1.50", "station": 1 } },
    { "name": "kv", "protocol": "KeyenceUpperLink",
      "connection": { "ip": "192.168.1.60 } },
    { "name": "logix", "protocol": "RockwellEtherNetIp",
      "connection": { "ip": "192.168.1.70", "slot": 0 } },
    { "name": "ge-9030", "protocol": "GeSrtp",
      "connection": { "ip": "192.168.1.90" } },
    { "name": "ls-xgb", "protocol": "LsFEnet",
      "connection": { "ip": "192.168.1.95", "cpuInfo": 176 } },
    { "name": "ua", "protocol": "OpcUa",
      "connection": { "endpointUrl": "opc.tcp://192.168.1.80:4840" } }
  ],
  "pollGroups": [                                // 轮询组(可选)→ PollingEngine
    { "name": "line1", "device": "s7-main", "intervalMs": 1000,
      "points": [
        { "name": "speed", "address": "DB1.DBD10", "valueType": "Float32" },
        { "name": "run",   "address": "M50.4",     "valueType": "Bit" }
      ] }
  ],
  "mqtt": {                                      // MQTT 桥(可选,IndustrialCommunication.Mqtt 包)
    "host": "broker.example.com", "port": 1883,
    "username": "u", "password": "p", "clientId": "gateway-1", "topicPrefix": "plant1"
  }
}
```

要点:

- `connection` 节按 protocol 反序列化为强类型 Options,未知字段、非法值在启动期统一报错;支持注释与尾逗号;枚举一律字符串
- 任意驱动的 `connection.encoding` 可配置字符串编码(默认 UTF-8;gb2312/shift-jis 需应用先注册 `CodePagesEncodingProvider`)
- 设备名、轮询组名大小写不敏感且不可重复;`enabled: false` 的设备不参与
- 完整真机模板见 `samples/IndustrialCommunication.Demo/configs/hardware.example.json`

## 轮询引擎与订阅

```csharp
await using var engine = host.CreatePollingEngine();

engine.PollCompleted += (_, e) =>                 // 每轮结束都触发(含失败状态)
    Console.WriteLine($"{e.GroupName}: changed {e.Changed.Count}");

using var sub = engine.Subscribe("speed", v =>    // 单点订阅:只在值变化时回调
    Console.WriteLine($"speed = {v}"));

await engine.StartAsync();                        // 每组一个循环;首轮全量,之后只报变化
```

- 变化检测含数组元素逐项比较;失败周期保留最后一次好值、循环不中断;Stop/Dispose 干净收尾
- 事件回调在线程池上执行,不要阻塞

## MQTT 云桥接(IndustrialCommunication.Mqtt 包)

`mqtt` 配置节 + `Attach(engine)` 即可把轮询变化发布到 broker:

```csharp
var mqttOptions = host.MqttNode is { } node
    ? MqttBridgeOptions.FromJsonElement(node)                          // 从配置文件读
    : new MqttBridgeOptions { Host = "127.0.0.1", Port = 1883 };       // 或手工构造

await using var bridge = new MqttBridge(host, mqttOptions);
bridge.Attach(engine);           // 轮询变化 → {prefix}/groups/{group} JSON
await bridge.StartAsync();
```

- **发布**:`{prefix}/groups/{group}`,payload 含 `group / timestamp / changed(仅变化点) / ok`
- **命令**:向 `{prefix}/commands/write` 发布 `{"device":"plc1","address":"HR10","value":123,"type":"Int16"}`,执行结果回 `{prefix}/commands/result`
- **TLS**:`"tls": true`(典型端口 8883)

### Sparkplug B 模式(同一包)

```csharp
var sparkplug = new SparkplugBridgeOptions { Group = "plant", EdgeNode = "edge01", Devices = ["plc1", "inverter1"] };
await using var spBridge = new SparkplugBridge(host, mqttOptions, sparkplug);
spBridge.Attach(engine);      // 轮询变化 → spBv1.0/plant/DDATA/edge01/{device}(protobuf)
await spBridge.StartAsync();  // NDEATH 遗嘱 → NBIRTH → DBIRTH;NCMD rebirth / DCMD 写命令自动处理
```

- protobuf 编解码为库内手写,字段号对照官方 sparkplug_b.proto 逐字段核实,不引入第三方 Sparkplug 库
- metric 名即点位地址;DCMD 写入按 metric 类型路由到 `WriteAsync` / `WriteStringAsync`

## OPC UA 原生订阅(服务器推送,免轮询)

```csharp
var uaClient = (OpcUaPlcClient)host.GetClient("ua");
await uaClient.ConnectAsync();

await using var subscription = await uaClient.CreateSubscriptionAsync(
    (_, e) => Console.WriteLine($"{e.NodeId} = {e.Value} @{e.Timestamp}"),
    publishingIntervalMs: 500);

subscription.Subscribe("ns=2;s=PumpSpeed");   // 每个节点一个 MonitoredItem
subscription.Subscribe("i=2258");             // 服务器当前时间,常用于链路观察
await subscription.ApplyChangesAsync();       // 之后值变化由服务器主动推送
```

回调参数 `OpcUaValueChangedEventArgs` 含 `NodeId / Value / Timestamp / GoodQuality`。

## 地址格式

| 驱动 | 字/位示例 | 说明 |
|---|---|---|
| Modbus | `HR100` `IR0` `C10` `DR3`;经典式 `40001` | 命名区 = **0 基协议地址**(HR=保持/IR=输入/C=线圈/DR=离散);经典人读地址自动减一 |
| S7 | `DB1.DBX10.0` `DB1.DBW10` `M100.5` `MW100` `I0.1` `QW20`;`T5` `C10`(定时器/计数器,每点 2 字节) | X=位,B/W/D=字节宽度;裸 `M100` 有歧义须写全 |
| 三菱 MC | `D100` `W1F` `R500` `M100` `X0A` `Y1F` `B0` | X/Y/B/W/SB/SW 十六进制;另支持 L/F/V/S/ZR/SM/SD |
| 欧姆龙 | `CIO100` `W100` `H50` `DM100`;位 `CIO100.5` | 字区 + word.bit(0..15) |
| 松下 | `DT100`(别名 D100)`FL500` `LD200`;位 `R0101` 或 `R10.1` | 位编号 = 3 位十进制字号 + 1 位十六进制位号 |
| 基恩士 | `DM100` `EM/FM/ZF/W/TM`;位 `R100` `MR/LR/CR` `B`(十六进制) | R 类为"通道+位"十进制;W/B/VB 十六进制 |
| AB | `MyDint` `MyArray[7]` `PID1.Setpoint` `MyDint.3`(位) | Logix Tag 名,多级点分、多维索引、位后缀;大数组与 UDT 自动分片读取 |
| GE | `R100` `AI10` `AQ20`(字);`I10` `Q20` `M100`(位) | 地址 1 基;`%` 前缀可选 |
| LS | `D100`(= %DW100)`M10` `ZR500`;位 `M100.3`(= %MX1603)或 `MX1603` | 字号×16+位号=绝对位号;变量名上限 16 字符 |
| OPC UA | `ns=2;s=PumpSpeed` 或 `i=2259` | 标准 NodeId 字符串 |

**字节序 (dataLayout)**:统一模型按逻辑大端 ABCD 描述多字类型,每设备可覆盖:

| 值 | 字节序 | 默认于 |
|---|---|---|
| `ABCD` | 大端 | Modbus、S7、欧姆龙、OPC UA |
| `CDAB` | 字交换(低字在前) | 三菱、松下、基恩士、AB、GE、LS(小端体系) |
| `BADC` / `DCBA` | 字节交换 | 少数设备,按需覆盖 |

## 错误模型与重连

`CommErrorKind`:`NotConnected / Timeout / ConnectionLost / ProtocolError / DeviceRejected(带协议原始错误码) / InvalidAddress / InvalidArgument / Cancelled / Disposed`

- 运行期所有读写返回 `CommResult` / `CommResult<T>`(`Success / Kind / ErrorCode / Message`,`EnsureSuccess()` 可转异常);异常只用于启动期错误(配置错、未注册协议、重名设备)
- 设备拒绝(MC end code / FINS 响应码 / Modbus exception / CIP status / SRTP status / Mewtocol `!码` / Keyence `E码` / FEnet error)→ `DeviceRejected`,`ErrorCode` 为协议原始码
- 超时 → 连接被主动关闭(半开保护),下次操作前自动重连;`connectionMonitor.enabled` 后另有后台心跳主动探活
- 每客户端内部串行化请求(信号量),RTU 总线与 TCP 一致性都安全

## 测试

```bash
dotnet test          # 306 单测 + 69 集成测试(2 个真机用例默认跳过)
```

- **单测**:各驱动地址解析、MC/FINS/Mewtocol/Keyence/EIP/SRTP/FEnet 黄金字节帧构造(对照官方手册、逆向工程论文与开源实现交叉验证)、字节序矩阵、配置加载、protobuf 编解码
- **集成测试**:进程内 NModbus 从站(Modbus TCP/UDP 真协议栈)、脚本化假服务器(MC/FINS/Mewtocol/Keyence/EIP/SRTP/FEnet 按文档帧回包,含分片/多帧/错误码/超时路径)、MQTTnet 进程内 broker(MQTT 与 Sparkplug 全链路)、轮询引擎与连接监控行为
- **真机用例**(默认跳过):设 `INDUSTRIALCOMM_S7_TEST_IP` 启用 S7;设 `INDUSTRIALCOMM_OPCUA_TEST_URL` 启用 OPC UA 订阅;Modbus RTU 建议 com0com 虚拟串口对

## 项目结构

```
src/
  IndustrialCommunication.Core/                  统一接口、Result、字节序编解码、轮询引擎、连接监控、配置、工厂
  IndustrialCommunication.DependencyInjection/    AddIndustrialCommunication + IPlcClientFactory
  IndustrialCommunication.Mqtt/                  MQTT 桥 + Sparkplug B(手写 protobuf)
  IndustrialCommunication.Drivers.Modbus/        TCP / UDP / RTU / ASCII(共享命令层)
  IndustrialCommunication.Drivers.Siemens/       S7(S7netplus 封装)
  IndustrialCommunication.Drivers.Mitsubishi/    MC 3E/4E(手写,TCP + UDP)
  IndustrialCommunication.Drivers.Omron/         FINS(手写,TCP + UDP)
  IndustrialCommunication.Drivers.Panasonic/     MEWTOCOL-COM(手写,多帧续传)
  IndustrialCommunication.Drivers.Keyence/       上位链路(手写,TCP + UDP)
  IndustrialCommunication.Drivers.Rockwell/      EtherNet/IP CIP(手写,分片读写 + UDT)
  IndustrialCommunication.Drivers.GeSrtp/        GE SRTP(手写,逆向工程规范)
  IndustrialCommunication.Drivers.LsFEnet/       LS XGT 专用协议(手写)
  IndustrialCommunication.Drivers.OpcUa/         OPC UA(官方栈封装,原生订阅)
samples/IndustrialCommunication.Demo/            开箱即跑的演示(内置 Modbus 从站 + MQTT broker)
tests/IndustrialCommunication.Tests/             单元测试
tests/IndustrialCommunication.IntegrationTests/  集成测试
```

## 已知边界

- **S7 优化块**:S7-1200/1500 的优化访问块无法通过标准 S7 PutGet 协议寻址(行业事实,snap7/S7netplus 同样不支持)。两条路:TIA 中 DB 属性取消"优化的块访问"(标准做法),或走 PLC 内置 OPC UA 服务(本库 OpcUa 驱动)
- EIP:UDT 按原始结构字节读取(成员偏移按 Studio 5000 布局);tag 模板自动发现(0x55 tag list + template)未实现
- MC 未支持 1E 帧/随机读写/串口;FINS 未支持 EM 区;Mewtocol 位读写单点(RCS/WCS)、读 ≤509 字;Keyence 位/字读写 ≤256 点;AB 位写仅单 BOOL(word.bit 写需读改写);LS 位走个别模式 ≤16 点/请求、BYTE/DWORD 尺寸后缀未支持
- OPC UA 默认 SecurityPolicy.None(加密端点需证书管理,未实现)
- Sparkplug B:数据/DDATA、命令/NCMD/DCMD、遗嘱已支持;模板(Template metric)、数据集、alias 压缩未实现
- 真机兼容性:手写协议的字节格式全部对照官方手册/逆向工程论文/proto 定义逐字节验证,但不同机型固件差异需现场确认(抓包即可对照帧构造单测定位)
