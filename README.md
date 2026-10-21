# RgvSimulatorGui

四向穿梭车(RGV)、提升机、输送线的 WPF 模拟上位机。内置 OPC UA 服务器与 Modbus TCP 从站,用于在没有真实设备/PLC 的情况下与 RCS 联调。

## 功能

- 四向车、提升机、输送线的运行模拟
- 内嵌 OPC UA 服务器:端点 `opc.tcp://127.0.0.1:<端口>`,节点 `ns=3;s={连接名}.{标签}`,值以 `byte[10]` 报文承载
- Modbus TCP 从站:监听地址可选 `0.0.0.0`(全部网卡)或 `127.0.0.1`(仅本机)
- 按当前参数生成 RCS `appsettings` 中 `Connections:OpcUa` 的连接条目并复制到剪贴板

## 技术栈

- .NET 8 + WPF
- WPF-UI 4.3.0(Fluent 风格界面)
- OPCFoundation.NetStandard.Opc.Ua 1.5.374.126

## 运行

需要 Windows 与 .NET 8 SDK。

```powershell
dotnet run --project RCS.Tools.RgvSimulatorGui.csproj
```

发布:

```powershell
dotnet publish RCS.Tools.RgvSimulatorGui.csproj -c Release -r win-x64
```

依赖说明:WPF-UI 默认从 nuget.org 还原;`nuget.config` 中的 `wpf-ui-offline` 源指向仓库外的离线包目录,离线环境下可自行调整或删除该源。

## 目录

- `Simulator/` - Modbus TCP 从站与 RGV 轨道模拟
- `Devices/` - 输送线、提升机、OPC UA 设备模拟
- `Assets/` - 图标等资源
- `MainWindow.*` - 主界面

## 许可

MIT,详见 [LICENSE](LICENSE)。
