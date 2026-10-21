using Opc.Ua;
using Opc.Ua.Server;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Kean.Tools.RgvSimulatorGui
{
    /// <summary>
    /// 一台输送线：两个 OPC 节点（读=PLC→WCS、写=WCS→PLC），值都是 10 字节报文。
    /// 节点名就是 RCS 代码里的写法：ns=3;s={server}.{tag}，例如 ns=3;s=S7 connection_20.DB2.440,b,10。
    /// </summary>
    internal sealed class ConveyorDevice
    {
        public int DeviceId;
        public string Server = "S7 connection_20";
        public string ReadTag = "DB2.0,b,10";
        public string WriteTag = "DB1.0,b,10";

        // PLC→WCS 报文字段（ConveyorAbstractDriver.DecodeInput 的取法：data[1] 状态、[2..3] 指令号、[4] 开关位、[6] 方向）
        public byte State;          // 0 空闲 1 运行 2 完成 ≥30 故障
        public ushort Instruction;  // 最近一次命令的指令号
        public byte Action;         // 运行方向：1 正转 2 反转 0 停
        public int Writes;
        public string LastWrite = "-";
        /// <summary>WCS 最近写进写区的那帧原文（界面上要看 DB 区实时值）</summary>
        public byte[] LastWrittenBytes;
        public int BusyTicks;       // 剩余"运行中"周期数；为 0 且 state=1 表示在等下游空出来
        public int HoldTicks;       // 完成后保持"完成"的周期数

        // 模拟出来的"货"：它在哪一节设备上，由输送任务带着往下游走
        public bool HasCargo;                    // 光电有货（反馈 bit0）
        public int Destination;                  // 最近一次命令的目标设备号；0 = 未指定（直接送到终点）
        public int Next;                         // 下一节设备号：货往哪一节走；0 = 没有下一节
        public bool WaitingDownstream;           // 运行到位、下游没空，等它空出来再交货
        public bool CargoDirty;                  // 界面改了有货位，下一轮推给订阅方
        public string Note;                      // 停在端头等待 / 和提升机交接时的阶段说明（界面状态列显示），null = 无
        public bool Reserved;                    // 被提升机交接预约为接货端：不接别的货、不接 WCS 指令、不允许放货
        public bool InTransfer;                  // 作为起点在和提升机交接（端头停着等提升机、向提升机送货）：不接 WCS 指令、不允许取货

        public string ReadNodeId => $"{Server}.{ReadTag}";
        public string WriteNodeId => $"{Server}.{WriteTag}";

        /// <summary>反馈帧长度：跟标签里要的字节数一致（DB2.750,b,10 → 10；扫描器 30；安全门 11），解析不出来就按 10</summary>
        public int FrameLength => BytesOf(ReadTag);

        /// <summary>标签里要的字节数（DB2.750,b,10 → 10），解析不出来按 10</summary>
        public static int BytesOf(string tag)
        {
            var parts = (tag ?? "").Split(',');
            return parts.Length > 0 && int.TryParse(parts[^1].Trim(), out var n) && n > 0 ? n : 10;
        }

        /// <summary>
        /// 反馈报文的开关位（data[4]），按 RCS 发送段读的位定义：
        /// bit0 有货（RCS 用 data[4] &amp; loadSensor 判断载货）；bit1 允许取货（有货、没在运行、没被预约，车可以来取）；
        /// bit3 允许放货（无货、空闲、没被预约，车可以来放）。扫描器、安全门等非 10 字节报文只给 bit0。
        /// </summary>
        public byte Sensor
        {
            get
            {
                int bits = HasCargo ? 1 : 0;
                if (FrameLength == 10)
                {
                    if (HasCargo && State != 1 && State < 30 && !Reserved && !InTransfer && !WaitingDownstream)
                    {
                        bits |= 1 << 1;
                    }
                    if (!HasCargo && State == 0 && !Reserved && !InTransfer)
                    {
                        bits |= 1 << 3;
                    }
                }
                return (byte)bits;
            }
        }

        public byte[] BuildFrame()
        {
            // 长度按标签走：RCS 那边是按 readTag 的长度读的，短了后面的驱动解析会错位
            var frame = new byte[Math.Max(10, FrameLength)];
            frame[0] = 0;                       // 标志位
            frame[1] = State;                   // 设备状态
            // 指令号按 RCS 的取法写（ConveyorAbstractDriver.DecodeInput：data[3] | data[2] << 8，即 data[2] 是高字节）
            frame[2] = (byte)(Instruction >> 8);
            frame[3] = (byte)(Instruction & 0xFF);
            frame[4] = Sensor;                  // 开关状态（bit0 有货、bit1 允许取货、bit3 允许放货）
            frame[6] = Action;                  // 运行方向
            return frame;
        }
    }

    /// <summary>把设备表变成 OPC 节点；命名空间固定落在 ns=3（RCS 里 OPC_NAMESPACE_INDEX 硬编码为 3）</summary>
    internal sealed class ConveyorNodeManager : CustomNodeManager2
    {
        public const string PadNamespace = "urn:kean:sim:pad";            // 占位，保证输送线落到 3
        public const string NamespaceUri = "urn:kean:sim:conveyor";

        private readonly List<ConveyorDevice> _devices;
        private readonly Dictionary<string, BaseDataVariableState> _readNodes = new();
        private readonly Dictionary<string, BaseDataVariableState> _writeNodes = new();
        private readonly Dictionary<string, List<ConveyorDevice>> _devicesByWriteNode = new();

        public ConveyorNodeManager(IServerInternal server, ApplicationConfiguration configuration, List<ConveyorDevice> devices)
            : base(server, configuration, NamespaceUri)
        {
            _devices = devices;
            var table = server.NamespaceUris;
            var index = table.GetIndex(NamespaceUri);
            ConveyorNamespaceIndex = (ushort)(index >= 0 ? index : NamespaceIndexes[0]);
        }

        /// <summary>输送线命名空间的实际索引（目标 3）</summary>
        public ushort ConveyorNamespaceIndex { get; private set; }

        /// <summary>把命名空间表打出来（排查 ns 索引用）</summary>
        public string DescribeNamespaces()
        {
            var table = Server.NamespaceUris;
            var parts = new List<string>();
            for (int i = 0; i < table.Count; i++)
            {
                parts.Add($"{i}={table.GetString((uint)i)}");
            }
            return string.Join(" | ", parts);
        }

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
                {
                    externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
                }

                var folder = new FolderState(null)
                {
                    SymbolicName = "Conveyors",
                    NodeId = new NodeId("Conveyors", ConveyorNamespaceIndex),
                    BrowseName = new QualifiedName("Conveyors", ConveyorNamespaceIndex),
                    DisplayName = new LocalizedText("Conveyors"),
                    TypeDefinitionId = ObjectTypeIds.FolderType,
                    ReferenceTypeId = ReferenceTypeIds.Organizes,
                    EventNotifier = EventNotifiers.None
                };
                folder.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
                references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, folder.NodeId));
                AddPredefinedNode(SystemContext, folder);

                // 一个节点 id 只建一个节点：设备表里允许几台设备共用同一个读标签（例如三台安全门读同一个位置），
                // 也允许读写用同一个标签（扫描器就是这样），这两种情况都不能建成两个 NodeId 相同的节点。
                var readIds = _devices.Select(d => d.ReadNodeId).Distinct().ToList();
                var writeIds = _devices.Select(d => d.WriteNodeId).Distinct().ToList();
                foreach (var nodeId in readIds.Concat(writeIds).Distinct())
                {
                    bool isRead = readIds.Contains(nodeId);
                    bool isWrite = writeIds.Contains(nodeId);
                    // 只作写节点的标签（例如安全门的 DB1.1001,b,1）没有对应的读设备，初值按它自己的字节数给零
                    byte[] initial;
                    if (isRead)
                    {
                        initial = _devices.First(d => d.ReadNodeId == nodeId).BuildFrame();
                    }
                    else
                    {
                        initial = new byte[ConveyorDevice.BytesOf(_devices.First(d => d.WriteNodeId == nodeId).WriteTag)];
                    }
                    var node = CreateByteNode(nodeId, isRead && isWrite ? "PLC→WCS / WCS→PLC" : isRead ? "PLC→WCS" : "WCS→PLC", initial);
                    if (isWrite)
                    {
                        // 同一个写标签也可能挂在多台设备上，命令发给每一台
                        _devicesByWriteNode[nodeId] = _devices.Where(d => d.WriteNodeId == nodeId).ToList();
                        node.OnWriteValue = (ISystemContext context, NodeState state, NumericRange indexRange,
                            QualifiedName dataEncoding, ref object value, ref StatusCode statusCode, ref DateTime timestamp) =>
                        {
                            if (value is byte[] bytes)
                            {
                                ((BaseVariableState)state).Value = bytes;
                                state.ClearChangeMasks(context, false);
                                foreach (var target in _devicesByWriteNode[nodeId])
                                {
                                    CommandReceived?.Invoke(target, bytes);
                                }
                            }
                            statusCode = StatusCodes.Good;
                            timestamp = DateTime.UtcNow;
                            return ServiceResult.Good;
                        };
                    }
                    folder.AddChild(node);
                    if (isRead)
                    {
                        _readNodes[nodeId] = node;   // PushReadValue 靠它找节点，漏登记则状态变化永远推不到 RCS
                    }
                    if (isWrite)
                    {
                        _writeNodes[nodeId] = node;  // 清 DB 区时要把写区也归零
                    }
                }
            }
        }

        private BaseDataVariableState CreateByteNode(string identifier, string description, byte[] value)
        {
            var node = new BaseDataVariableState(null)
            {
                SymbolicName = identifier,
                NodeId = new NodeId(identifier, ConveyorNamespaceIndex),
                BrowseName = new QualifiedName(identifier, ConveyorNamespaceIndex),
                DisplayName = new LocalizedText(identifier),
                Description = new LocalizedText(description),
                ReferenceTypeId = ReferenceTypeIds.HasComponent,
                TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                DataType = DataTypeIds.ByteString,
                ValueRank = ValueRanks.Scalar,
                Value = value,
                AccessLevel = AccessLevels.CurrentReadOrWrite,
                UserAccessLevel = AccessLevels.CurrentReadOrWrite,
                Historizing = false,
                StatusCode = StatusCodes.Good,
                Timestamp = DateTime.UtcNow
            };
            AddPredefinedNode(SystemContext, node);
            return node;
        }

        /// <summary>把最新反馈帧推给订阅方（值变了订阅回调才会触发）</summary>
        public void PushReadValue(ConveyorDevice device)
        {
            lock (Lock)
            {
                if (!_readNodes.TryGetValue(device.ReadNodeId, out var node))
                {
                    return;
                }
                // 帧没变不推：真实 PLC 的数据块值不变时订阅不会收到通知，重复推送会让 RCS 对同一次完成重复处理、重复写应答
                var frame = device.BuildFrame();
                if (node.Value is byte[] current && current.AsSpan().SequenceEqual(frame))
                {
                    return;
                }
                node.Value = frame;
                node.Timestamp = DateTime.UtcNow;
                node.ClearChangeMasks(SystemContext, false);
            }
        }

        /// <summary>把写区（WCS→PLC）节点清成零：清 DB 区时用，模拟 PLC 侧那段数据被清掉</summary>
        public void ClearWriteArea(ConveyorDevice device)
        {
            lock (Lock)
            {
                if (!_writeNodes.TryGetValue(device.WriteNodeId, out var node))
                {
                    return;
                }
                node.Value = new byte[ConveyorDevice.BytesOf(device.WriteTag)];
                node.Timestamp = DateTime.UtcNow;
                node.ClearChangeMasks(SystemContext, false);
            }
        }

        /// <summary>把界面上手工写进写区的字节推到节点上（让订阅写区的人看到同一份数据）</summary>
        public void PushWriteValue(ConveyorDevice device)
        {
            lock (Lock)
            {
                if (!_writeNodes.TryGetValue(device.WriteNodeId, out var node))
                {
                    return;
                }
                var bytes = device.LastWrittenBytes ?? Array.Empty<byte>();
                node.Value = bytes;
                node.Timestamp = DateTime.UtcNow;
                node.ClearChangeMasks(SystemContext, false);
            }
        }

        /// <summary>收到 WCS 命令（由服务器转给行为层）</summary>
        public event Action<ConveyorDevice, byte[]> CommandReceived;
    }

    /// <summary>输送线一行的视图模型（只读回显；改标签要靠重新生成/导入重建）</summary>
    internal sealed class ConveyorVm : System.ComponentModel.INotifyPropertyChanged
    {
        private readonly ConveyorDevice _device;
        private readonly Action<string> _log;
        private readonly Action<ConveyorDevice> _push;
        private readonly Action<ConveyorDevice, byte[]> _writeAsCommand;
        private string _shownReadArea;    // 上次显示过的两列字节，值没变就不通知（否则 250ms 一刷新会把正在输入的内容顶回去）
        private string _shownWriteArea;
        private string _shownNext;

        public ConveyorVm(ConveyorDevice device, Action<string> log = null, Action<ConveyorDevice> push = null,
            Action<ConveyorDevice, byte[]> writeAsCommand = null)
        {
            _device = device;
            _log = log;
            _push = push;
            _writeAsCommand = writeAsCommand;
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        public ConveyorDevice Device => _device;
        public int DeviceId => _device.DeviceId;
        public string Server => _device.Server;
        public string ReadTag => _device.ReadTag;
        public string WriteTag => _device.WriteTag;
        public byte State => _device.State;
        public ushort Instruction => _device.Instruction;
        public bool HasCargo
        {
            get => _device.HasCargo;
            set
            {
                if (_device.HasCargo == value)
                {
                    return;
                }
                _device.HasCargo = value;
                _device.CargoDirty = true;      // 由 250ms 的 Tick 推给 OPC 订阅方
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasCargo)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CargoText)));
            }
        }
        public string CargoText => _device.HasCargo ? "有货" : "—";
        public int Destination => _device.Destination;
        public string DestinationText => _device.Destination > 0 ? _device.Destination.ToString() : "—";

        /// <summary>下一节设备号（货往哪一节走），可以直接在表里改</summary>
        public string NextText
        {
            get => _device.Next > 0 ? _device.Next.ToString() : "—";
            set
            {
                var text = (value ?? "").Trim();
                int next = 0;
                if (text.Length > 0 && text != "—" && text != "-")
                {
                    if (!int.TryParse(text, out next) || next < 0)
                    {
                        _log?.Invoke($"输送线{DeviceId}：下一节要填设备号（数字），这次没改");
                        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(NextText)));
                        return;
                    }
                    if (next == DeviceId)
                    {
                        _log?.Invoke($"输送线{DeviceId}：下一节不能是自己，这次没改");
                        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(NextText)));
                        return;
                    }
                }
                if (_device.Next == next)
                {
                    return;
                }
                _device.Next = next;
                _log?.Invoke($"输送线{DeviceId} 下一节 → {(next > 0 ? next.ToString() : "无")}");
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(NextText)));
            }
        }

        public byte Action => _device.Action;
        public string ActionText => _device.Action switch { 1 => "正转", 2 => "反转", _ => "—" };
        public int Writes => _device.Writes;
        public string LastWrite => _device.LastWrite;

        /// <summary>读区（PLC→WCS）当前那帧的原文；界面上直接改这段字节 = 直接设定反馈帧</summary>
        public string ReadAreaText
        {
            get => string.Join(",", _device.BuildFrame());
            set
            {
                if (!TryParseBytes(value, out var bytes))
                {
                    _log?.Invoke($"输送线{DeviceId}：读区要填逗号分隔的字节（0-255），这次没改");
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ReadAreaText)));
                    return;
                }
                var frame = new byte[Math.Max(10, _device.FrameLength)];
                Array.Copy(bytes, frame, Math.Min(bytes.Length, frame.Length));
                _device.State = frame[1];
                _device.Instruction = (ushort)((frame[2] << 8) | frame[3]);
                _device.HasCargo = (frame[4] & 1) == 1;
                _device.Action = frame[6];
                _device.Destination = 0;
                _device.BusyTicks = 0;
                _device.HoldTicks = 0;
                _device.WaitingDownstream = false;
                _push?.Invoke(_device);
                _log?.Invoke($"输送线{DeviceId} 读区（DB2）手工改成 [{string.Join(",", frame)}]：状态 {_device.State}、指令号 {_device.Instruction}、有货 {(_device.HasCargo ? 1 : 0)}、方向 {_device.Action}");
            }
        }

        /// <summary>写区（WCS→PLC）那一帧；界面上直接改 = 当作 WCS 写了这条命令进来</summary>
        public string WriteAreaText
        {
            get => _device.LastWrittenBytes is { Length: > 0 } bytes ? string.Join(",", bytes) : "-";
            set
            {
                if (!TryParseBytes(value, out var bytes) || bytes.Length == 0)
                {
                    _log?.Invoke($"输送线{DeviceId}：写区要填逗号分隔的字节（0-255），这次没改");
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(WriteAreaText)));
                    return;
                }
                _writeAsCommand?.Invoke(_device, bytes);   // 走和 OPC 写入一样的那条路
            }
        }

        /// <summary>把界面上的 "0,0,0,1,1,0,0,0,0,0" 解析成字节；空串当作全 0</summary>
        private static bool TryParseBytes(string text, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;   // 空 = 全 0
            }
            var parts = text.Split(new[] { ',', ' ', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<byte>(parts.Length);
            foreach (var part in parts)
            {
                if (!byte.TryParse(part.Trim(), out var b))
                {
                    return false;
                }
                list.Add(b);
            }
            bytes = list.ToArray();
            return true;
        }

        /// <summary>状态文字（表格里给人看）</summary>
        public string StateText => _device.State switch
        {
            0 => _device.Note ?? (_device.Reserved ? "预约接货" : "空闲"),
            1 => _device.Note ?? (_device.WaitingDownstream ? "等下游" : "运行"),
            2 => "完成",
            >= 30 => $"故障{_device.State}",
            _ => _device.State.ToString()
        };

        /// <summary>状态分类（表格着色用）：idle / run / wait / done / fault</summary>
        public string StateKind => _device.State switch
        {
            1 => _device.WaitingDownstream ? "wait" : "run",
            0 when _device.Reserved || _device.InTransfer => "wait",
            2 => "done",
            >= 30 => "fault",
            _ => "idle"
        };

        public void Refresh()
        {
            foreach (var name in new[] { nameof(State), nameof(StateText), nameof(StateKind), nameof(Instruction), nameof(HasCargo), nameof(CargoText), nameof(Destination), nameof(DestinationText), nameof(Action), nameof(ActionText), nameof(Writes), nameof(LastWrite) })
            {
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
            }
            // 「下一节」也是可编辑的，同样只在值变化时才通知
            if (NextText != _shownNext)
            {
                _shownNext = NextText;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(NextText)));
            }
            // 字节列只在值真的变了才通知：这两列是可编辑的，定时刷新不能把用户正在敲的内容覆盖掉
            string read = ReadAreaText;
            if (read != _shownReadArea)
            {
                _shownReadArea = read;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ReadAreaText)));
            }
            string write = WriteAreaText;
            if (write != _shownWriteArea)
            {
                _shownWriteArea = write;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(WriteAreaText)));
            }
        }
    }

    /// <summary>输送线 OPC UA 服务器（无安全通道 + 匿名，方便本机联调）</summary>
    internal sealed class ConveyorOpcServer : StandardServer
    {
        private readonly List<ConveyorDevice> _devices;

        /// <summary>服务器应用 URI。绝不能等于 <see cref="ConveyorNodeManager.NamespaceUri"/>：
        /// 命名空间表 1 位是给应用 URI 的，同名会把输送线命名空间顶到 ns=1，RCS 按 3 取节点就是 BadNodeIdUnknown。</summary>
        public const string ApplicationUriValue = "urn:kean:sim:server";

        public ConveyorOpcServer(List<ConveyorDevice> devices)
        {
            _devices = devices;
        }

        public ConveyorNodeManager NodeManager { get; private set; }

        /// <summary>当前会话数：RCS 连上后会 ≥1，用来看对面到底有没有连过来（连接不等于订阅成功，但连都没连就没订阅）</summary>
        public int SessionCount
        {
            get
            {
                try
                {
                    return CurrentInstance?.SessionManager?.GetSessions()?.Count ?? 0;
                }
                catch
                {
                    return 0;
                }
            }
        }

        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            // 关键：RCS 侧 OPC_NAMESPACE_INDEX 硬编码为 3，节点必须正好落在 ns=3，对不上就是 BadNodeIdUnknown。
            // 表的结构是 0=OPC UA 基址、1=应用 URI、2=占位，所以这里自己把前 3 位铺平，再亲手把输送线命名空间注册进去，
            // 不指望节点管理器追加时能刚好排到第 3 位。
            var uris = server.NamespaceUris;
            if (uris.Count == 1)
            {
                uris.Append(configuration.ApplicationUri);
            }
            while (uris.Count < 3)
            {
                uris.Append($"{ConveyorNodeManager.PadNamespace}{uris.Count}");
            }
            if (uris.GetIndex(ConveyorNodeManager.NamespaceUri) < 0)
            {
                uris.Append(ConveyorNodeManager.NamespaceUri);
            }
            NodeManager = new ConveyorNodeManager(server, configuration, _devices);
            return new MasterNodeManager(server, configuration, null, NodeManager);
        }

        public static ApplicationConfiguration BuildConfiguration(int port, string pkiRoot)
        {
            var config = new ApplicationConfiguration
            {
                ApplicationName = "RGV 模拟器-输送线",
                ApplicationUri = ApplicationUriValue,     // 注意：不能和输送线命名空间同名，否则服务器启动时会先占掉那个索引
                ApplicationType = ApplicationType.Server,
                ServerConfiguration = new ServerConfiguration
                {
                    // 不带路径，和 appsettings 里其他 OPC 条目的写法一致（"S7 connection_x": "opc.tcp://127.0.0.1:xxxxx"）
                    BaseAddresses = { $"opc.tcp://127.0.0.1:{port}" },
                    SecurityPolicies = new ServerSecurityPolicyCollection
                    {
                        new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None }
                    },
                    UserTokenPolicies = new UserTokenPolicyCollection { new UserTokenPolicy(UserTokenType.Anonymous) },
                    DiagnosticsEnabled = false,
                    MaxSessionCount = 100,
                    MinSessionTimeout = 10000,
                    MaxSessionTimeout = 3600000,
                    // 订阅自动过期：RCS 建订阅时请求“永不过期”（KeepAliveCount、LifetimeCount 都是 uint.MaxValue），
                    // 被强行停止（VS 停止调试、关窗口、崩溃）时来不及删订阅，残留订阅会一直占着名额。
                    // 这里把订阅寿命上限压到 30 秒：客户端还在时，发布请求和保活会不断续命，不受影响；
                    // 客户端没了，30 秒内收不到发布请求，服务端自动删掉这些订阅。
                    MinSubscriptionLifetime = 10000,
                    MaxSubscriptionLifetime = 30000,
                    // 订阅总数上限（SDK 缺省 100）：RCS 每次启动要建约 29 个（一台设备一个），留足余量
                    MaxSubscriptionCount = 1000,
                    MaxBrowseContinuationPoints = 10,
                    MaxQueryContinuationPoints = 10,
                    MaxHistoryContinuationPoints = 100
                },
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = pkiRoot + "\\own",
                        SubjectName = "CN=RgvSimulatorGuiConveyor"
                    },
                    TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = pkiRoot + "\\issuers" },
                    TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = pkiRoot + "\\trusted" },
                    RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = pkiRoot + "\\rejected" },
                    AutoAcceptUntrustedCertificates = true,
                    AddAppCertToTrustedStore = true,
                    RejectSHA1SignedCertificates = false
                },
                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas { OperationTimeout = 15000, MaxStringLength = 1048576, MaxByteStringLength = 1048576 },
                TraceConfiguration = new TraceConfiguration()
            };
            return config;
        }
    }
}
