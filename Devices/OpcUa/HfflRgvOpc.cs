using Opc.Ua;
using Opc.Ua.Server;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Kean.Tools.RgvSimulatorGui
{
    /// <summary>
    /// OPC 四向车 WCS→PLC 的一条命令（110 字节，大端），口径见 RCS 的 FourWayVehicleAbstractDriver.EncodeOutput(int, object)
    /// </summary>
    internal sealed class HfflCommand
    {
        public int Door;        // 00-01 安全门（RCS 里叫 Heart，实际填的是 intDoorOpen）
        public int Id;          // 02-03 命令序列号（RCS 每次发送都换一个新的 TaskIDSend）
        public int Type;        // 04-05 命令类型：1 直行 2 先换向再直行 3 取货 4 放货 5 充电 6 取消充电
        public int TaskId;      // 06-07 任务序列号（RCS 的小任务号 instructions_no）
        public int FromBay, FromRow, FromTier;   // 10-15 起点 列/排/层
        public int ToBay, ToRow, ToTier;         // 18-23 终点 列/排/层
        public byte[] PalletId = new byte[20];   // 24-43 托盘号（ASCII）
        public int PointCount;  // 60-61 经过的货格数

        public static HfflCommand Parse(byte[] f) => new()
        {
            Door = HfflRgv.ReadU16(f, 0),
            Id = HfflRgv.ReadU16(f, 2),
            Type = HfflRgv.ReadU16(f, 4),
            TaskId = HfflRgv.ReadU16(f, 6),
            FromBay = HfflRgv.ReadU16(f, 10),
            FromRow = HfflRgv.ReadU16(f, 12),
            FromTier = HfflRgv.ReadU16(f, 14),
            ToBay = HfflRgv.ReadU16(f, 18),
            ToRow = HfflRgv.ReadU16(f, 20),
            ToTier = HfflRgv.ReadU16(f, 22),
            PalletId = f.Length >= 44 ? f[24..44] : new byte[20],
            PointCount = HfflRgv.ReadU16(f, 60)
        };

        /// <summary>界面上手工下命令：按 RCS 的写法拼一帧 110 字节（只填序列号、类型、任务号、起终点）</summary>
        public static byte[] Build(int id, int type, int taskId, int fromTier, int fromBay, int fromRow, int toTier, int toBay, int toRow)
        {
            var f = new byte[HfflRgv.COMMAND_LENGTH];
            foreach (var (at, value) in new[] { (2, id), (4, type), (6, taskId), (10, fromBay), (12, fromRow), (14, fromTier), (18, toBay), (20, toRow), (22, toTier) })
            {
                f[at] = (byte)(value >> 8);
                f[at + 1] = (byte)value;
            }
            return f;
        }

        public string From => HfflRgv.CellText(FromTier, FromBay, FromRow);

        public string To => HfflRgv.CellText(ToTier, ToBay, ToRow);

        public string Pallet => HfflRgv.AsciiText(PalletId);

        public string Summary => $"#{Id} {HfflRgv.TypeText(Type)} {From}→{To}";

        public string Describe() =>
            $"命令 #{Id} {HfflRgv.TypeText(Type)}，起点 {From} → 终点 {To}，任务号 {TaskId}" +
            (PointCount > 0 ? $"，经过 {PointCount} 格" : "") +
            (Pallet.Length > 0 ? $"，托盘 {Pallet}" : "");
    }

    /// <summary>
    /// OPC 四向车（世仓协议，经 OPC UA 订阅）：一台车两个节点——
    /// 读 ns=3;s={server}.DB553.0,b,84（PLC→WCS 反馈 84 字节），写 ns=3;s={server}.DB552.0,b,110（WCS→PLC 命令 110 字节）。
    /// 反馈字段口径以 RCS 的 FourWayVehicleAbstractDriver.DecodeInput 为准，多字节都是大端。
    /// </summary>
    /// <remarks>
    /// 行为：收到命令 → 状态 1（接收）→ 3（执行中）→ 4（完成）；直行逐格推进列/排，取货置托盘位、放货清托盘位、充电置充电位。
    /// 执行中再收到命令放进缓存（缓存命令号/缓存状态 2），当前这条完成后自动接着执行（RCS 的"直行 + 直行"靠它）。
    /// 所有成员只在界面线程上读写：OPC 写入由 MainWindow 转回界面线程再交给 <see cref="HandleCommand"/>。
    /// </remarks>
    internal sealed class HfflRgv
    {
        public const int FEEDBACK_LENGTH = 84, COMMAND_LENGTH = 110;

        // 反馈帧偏移（DecodeInput）
        private const int F_HEART = 0, F_COMMAND_ID = 2, F_COMMAND_TYPE = 4, F_COMMAND_STATE = 6, F_BUF_ID = 8, F_BUF_STATE = 10,
            F_TASK_ID = 12, F_TASK_FLAG = 14, F_BAY = 16, F_ROW = 18, F_TIER = 20, F_HIGH = 22, F_BATTERY = 30, F_FLAGS = 32, F_FLAGS2 = 33,
            F_SPEED = 34, F_FAULT1 = 46, F_FAULT2 = 50, F_PALLET_ID = 54, F_WEIGHT = 74, F_MILEAGE = 76, F_RUNTIME = 80;

        // 命令状态（RCS 注释：1 接收成功 2 校验成功 3 执行中 4 成功完成 5 校验失败 6 执行失败）
        public const int STATE_ACCEPTED = 1, STATE_CHECKED = 2, STATE_RUNNING = 3, STATE_DONE = 4, STATE_CHECK_FAIL = 5, STATE_EXEC_FAIL = 6;

        private const int ACCEPT_MS = 300;   // "接收成功"保持时间：比 RCS 的采样间隔（100ms）长，状态 1 能被订阅到
        private const int HOLD_MS = 500;     // 完成后停多久再接着执行缓存命令（让 RCS 看到"完成 + 缓存 2"这一帧）
        private const int CHARGE_MS = 2000;  // 充电时每 2 秒电量 +1

        private enum Phase { Idle, Accepting, Turning, Moving, Working, Holding }

        private readonly Action<string> _log;
        private HfflCommand _current;    // 正在执行（或刚完成）的命令
        private HfflCommand _buffered;   // 缓存命令：执行中收到的下一条
        private Phase _phase = Phase.Idle;
        private int _phaseMs, _heartMs, _chargeMs;

        public HfflRgv(int deviceId, string server, string readTag, string writeTag, Action<string> log)
        {
            DeviceId = deviceId;
            Server = server;
            ReadTag = readTag;
            WriteTag = writeTag;
            _log = log ?? (_ => { });
        }

        public int DeviceId { get; }
        public string Server { get; }
        public string ReadTag { get; }
        public string WriteTag { get; }
        public string ReadNodeId => $"{Server}.{ReadTag}";
        public string WriteNodeId => $"{Server}.{WriteTag}";

        /// <summary>反馈帧长度：跟读标签里的字节数走（DB553.0,b,84 → 84），不足 84 按 84（RCS 解析要读到第 83 字节）</summary>
        public int FrameLength => Math.Max(FEEDBACK_LENGTH, ConveyorDevice.BytesOf(ReadTag));

        // ===== 反馈字段（PLC→WCS）=====
        public int Heart, CommandId, CommandType, CommandState, BufCommandId, BufCommandState, TaskId, TaskFlag;
        public int Bay, Row, Tier, HighStatus = 1, Battery = 90, Weight;
        public bool Pallet;              // byte32 bit0 托盘状态
        public bool Auto = true;         // byte32 bit1 控制模式：1 自动（RCS 的 hand_status）
        public bool Normal = true;       // byte32 bit2 工作状态：1 正常，0 故障（RCS 判成 Error）
        public bool EStop;               // byte32 bit3 急停：推进暂停
        public bool Moving;              // byte32 bit4 运动状态（RCS 发命令前要求为 0）
        public bool Charging;            // byte32 bit6 充电状态
        public bool TowardX;             // byte32 bit7 X 运动方向（RCS 据此决定发"直行"还是"先换向再直行"）
        public bool CallBack;            // byte33 bit2 回调中（RCS 不发命令）
        public bool CyclicDischarge;     // byte33 bit3 循环满充
        public uint FaultCode1, FaultCode2;
        public byte[] PalletId = new byte[20];
        public float Speed, Mileage, RunTime;

        // ===== 模拟选项 =====
        public bool EmptyPick;           // 空取：取货完成但不置托盘位
        public bool ChargeNotConnected;  // 充电未打通：充电命令完成但不置充电位
        public bool StrictStart;         // 起点校验：命令起点和车当前位置（或缓存时的预计位置）不符 → 校验失败 5
        public bool FailNextCheck;       // 下一条命令校验失败（一次性）
        public bool FailNextExec;        // 下一条命令执行失败（一次性）

        // ===== 统计 =====
        public int Writes;
        public string LastWrite = "-";
        public byte[] LastCommandFrame;

        public bool IsBusy => _phase is not Phase.Idle;

        public bool HasFault => !Normal || FaultCode1 != 0 || FaultCode2 != 0;

        public bool LowBattery => Battery <= 20;

        public bool FullBattery => Battery >= 100;

        public string Position => CellText(Tier, Bay, Row);

        public string BufferedText => _buffered != null ? $"#{_buffered.Id} 待执行" : BufCommandId != 0 ? $"#{BufCommandId}/{BufCommandState}" : "—";

        /// <summary>状态文字（表格里给人看）</summary>
        public string StateText => _phase switch
        {
            Phase.Accepting => "接收",
            Phase.Turning => "换向中",
            Phase.Moving => EStop || !Normal ? "暂停" : "行驶中",
            Phase.Working => EStop || !Normal ? "暂停" : $"{TypeText(CommandType)}中",
            Phase.Holding => "完成→缓存",
            _ => CommandState switch
            {
                STATE_DONE => "完成",
                STATE_CHECK_FAIL => "校验失败",
                STATE_EXEC_FAIL => "执行失败",
                _ => "空闲"
            }
        };

        /// <summary>状态分类（表格着色用）：idle / run / wait / done / fault</summary>
        public string StateKind => _phase switch
        {
            Phase.Idle when CommandState is STATE_CHECK_FAIL or STATE_EXEC_FAIL => "fault",
            Phase.Idle when CommandState == STATE_DONE => "done",
            Phase.Idle => "idle",
            _ when EStop || !Normal => "wait",
            Phase.Holding => "done",
            _ => "run"
        };

        /// <summary>设当前位置（层-列-排）；格式不对返回 false</summary>
        public bool TrySetPosition(string text)
        {
            if (!TryParseCell(text, out int tier, out int bay, out int row))
            {
                return false;
            }
            Tier = tier;
            Bay = bay;
            Row = row;
            return true;
        }

        // ===== 命令 =====

        /// <summary>收到 WCS 写进写区的一帧（界面线程）</summary>
        public void HandleCommand(byte[] frame)
        {
            Writes++;
            LastCommandFrame = (byte[])frame.Clone();
            if (frame.Length < 24)
            {
                LastWrite = $"{frame.Length} 字节";
                _log($"[OPC车{DeviceId}] 命令只有 {frame.Length} 字节（应为 {COMMAND_LENGTH}），忽略");
                return;
            }
            var cmd = HfflCommand.Parse(frame);
            LastWrite = cmd.Summary;
            if (cmd.Id == 0 && cmd.Type == 0)
            {
                _log($"[OPC车{DeviceId}] 收到全 0 命令（命令序列号 0、类型 0），视为清命令区，不执行");
                return;
            }
            // 世仓车只认新序列号（RCS 注释："每次发送任务的时候小任务号必须是新的"）：和当前/缓存命令同号的一律当重复写入
            if (cmd.Id == CommandId || cmd.Id == _buffered?.Id)
            {
                _log($"[OPC车{DeviceId}] 命令 #{cmd.Id} 与当前/缓存命令的序列号相同，视为重复写入，忽略（车只认新序列号）");
                return;
            }
            _log($"[OPC车{DeviceId}] 收到{cmd.Describe()}");

            string reject = null;
            if (FailNextCheck)
            {
                FailNextCheck = false;
                reject = "模拟「下条校验失败」";
            }
            else if (!Normal)
            {
                reject = "车处于故障状态";
            }
            else if (!Auto)
            {
                reject = "车在手动模式";
            }
            else if (cmd.Type is < 1 or > 6)
            {
                reject = $"命令类型 {cmd.Type} 不认识";
            }

            if (IsBusy)
            {
                // 执行中又来一条：放进缓存，当前完成后接着执行；缓存已占用就回缓存校验失败
                if (reject == null && _buffered != null)
                {
                    reject = $"缓存里已有命令 #{_buffered.Id}";
                }
                if (reject == null && StrictStart && !SameCell(cmd.FromTier, cmd.FromBay, cmd.FromRow, _current))
                {
                    reject = $"起点 {cmd.From} 与当前命令终点 {_current.To} 不符";
                }
                BufCommandId = cmd.Id;
                if (reject != null)
                {
                    BufCommandState = STATE_CHECK_FAIL;
                    _log($"[OPC车{DeviceId}] 命令 #{cmd.Id} 缓存校验失败（缓存状态 5）：{reject}");
                    return;
                }
                _buffered = cmd;
                BufCommandState = STATE_CHECKED;
                _log($"[OPC车{DeviceId}] 正在执行 #{CommandId}，命令 #{cmd.Id} 放入缓存（缓存状态 2），完成后接着执行");
                return;
            }

            if (reject == null && StrictStart && (cmd.FromTier != Tier || cmd.FromBay != Bay || cmd.FromRow != Row))
            {
                reject = $"起点 {cmd.From} 与车当前位置 {Position} 不符";
            }
            if (reject != null)
            {
                _current = cmd;
                CommandId = cmd.Id;
                CommandType = cmd.Type;
                TaskId = cmd.TaskId;
                CommandState = STATE_CHECK_FAIL;
                _log($"[OPC车{DeviceId}] 命令 #{cmd.Id} 校验失败（状态 5）：{reject}");
                return;
            }
            if (cmd.Type is 1 or 2 && (cmd.FromTier != Tier || cmd.FromBay != Bay || cmd.FromRow != Row))
            {
                _log($"[OPC车{DeviceId}] 注意：命令起点 {cmd.From} 与车当前位置 {Position} 不同，按当前位置出发");
            }
            Start(cmd);
        }

        private void Start(HfflCommand cmd)
        {
            _current = cmd;
            CommandId = cmd.Id;
            CommandType = cmd.Type;
            TaskId = cmd.TaskId;
            CommandState = STATE_ACCEPTED;
            _phase = Phase.Accepting;
            _phaseMs = 0;
        }

        /// <summary>中止当前命令：原地停下，状态 6（执行失败），缓存命令作废</summary>
        public void Abort()
        {
            if (!IsBusy)
            {
                _log($"OPC车{DeviceId} 当前没有在执行的命令");
                return;
            }
            Moving = false;
            Speed = 0;
            CommandState = STATE_EXEC_FAIL;
            _phase = Phase.Idle;
            if (_buffered != null)
            {
                _log($"OPC车{DeviceId} 缓存命令 #{_buffered.Id} 一并作废");
                _buffered = null;
            }
            _log($"OPC车{DeviceId} 已中止命令 #{CommandId}，停在 {Position}（状态 6 执行失败）");
        }

        // ===== 周期推进 =====

        /// <param name="elapsedMs">距上次推进的毫秒数</param>
        /// <param name="stepMs">行驶每格耗时</param>
        /// <param name="workMs">换向 / 取放货 / 充电动作耗时</param>
        /// <param name="heartbeat">是否跳心跳（每秒 +1）</param>
        public void Tick(int elapsedMs, int stepMs, int workMs, bool heartbeat)
        {
            if (heartbeat)
            {
                _heartMs += elapsedMs;
                if (_heartMs >= 1000)
                {
                    _heartMs = 0;
                    Heart = (Heart + 1) & 0xFFFF;
                }
            }
            if (Charging && Battery < 100)
            {
                _chargeMs += elapsedMs;
                if (_chargeMs >= CHARGE_MS)
                {
                    _chargeMs = 0;
                    Battery++;
                }
            }
            else
            {
                _chargeMs = 0;
            }

            if (_phase == Phase.Idle)
            {
                return;
            }
            if (EStop || !Normal)
            {
                Speed = 0;   // 急停 / 故障：原地暂停，恢复后接着走
                return;
            }
            _phaseMs += elapsedMs;
            switch (_phase)
            {
                case Phase.Accepting:
                    if (_phaseMs < ACCEPT_MS)
                    {
                        return;
                    }
                    CommandState = STATE_RUNNING;
                    _phaseMs = 0;
                    if (_current.Type == 1)
                    {
                        BeginMove();
                    }
                    else if (_current.Type == 2)
                    {
                        _phase = Phase.Turning;
                        Moving = true;
                    }
                    else
                    {
                        _phase = Phase.Working;
                    }
                    break;

                case Phase.Turning:
                    if (_phaseMs < workMs)
                    {
                        return;
                    }
                    TowardX = !TowardX;
                    _log($"OPC车{DeviceId} 换向完成，当前朝向 {(TowardX ? "X" : "Y")}");
                    BeginMove();
                    break;

                case Phase.Moving:
                    Speed = 1;
                    if (_phaseMs < stepMs)
                    {
                        return;
                    }
                    _phaseMs = 0;
                    StepOnce(stepMs);
                    if (AtTarget)
                    {
                        Finish();
                    }
                    break;

                case Phase.Working:
                    if (_phaseMs < workMs)
                    {
                        return;
                    }
                    ApplyWork();
                    Finish();
                    break;

                case Phase.Holding:
                    if (_phaseMs < HOLD_MS)
                    {
                        return;
                    }
                    var next = _buffered;
                    _buffered = null;
                    if (next == null)
                    {
                        _phase = Phase.Idle;
                        return;
                    }
                    // 缓存命令号 / 缓存状态 PLC 不清（RCS 的"直行 + 直行"要在执行中看到 缓存号 = 当前号、缓存状态 2）
                    _log($"OPC车{DeviceId} 接着执行缓存命令 #{next.Id}");
                    Start(next);
                    break;
            }
        }

        private bool AtTarget => Bay == _current.ToBay && Row == _current.ToRow && Tier == _current.ToTier;

        private void BeginMove()
        {
            _phase = Phase.Moving;
            _phaseMs = 0;
            Moving = true;
            Speed = 1;
            if (_current.ToBay != Bay && _current.ToRow != Row)
            {
                _log($"OPC车{DeviceId} 注意：{Position} → {_current.To} 不在一条直线上，先走列再走排");
            }
            if (AtTarget)
            {
                Finish();   // 原地命令：起点就是终点
            }
        }

        /// <summary>走一格：先列（Bay）后排（Row），都到了再换层</summary>
        private void StepOnce(int stepMs)
        {
            if (Bay != _current.ToBay)
            {
                Bay += Math.Sign(_current.ToBay - Bay);
            }
            else if (Row != _current.ToRow)
            {
                Row += Math.Sign(_current.ToRow - Row);
            }
            else if (Tier != _current.ToTier)
            {
                _log($"OPC车{DeviceId} 换层 {Tier} → {_current.ToTier}");
                Tier = _current.ToTier;
            }
            Mileage += 1;
            RunTime += stepMs / 1000f;
        }

        private void ApplyWork()
        {
            switch (_current.Type)
            {
                case 3:
                    Pallet = !EmptyPick;
                    HighStatus = 3;
                    if (EmptyPick)
                    {
                        _log($"OPC车{DeviceId} 取货完成，但「空取」开着：托盘位不置 1");
                    }
                    else
                    {
                        PalletId = (byte[])_current.PalletId.Clone();
                    }
                    break;
                case 4:
                    Pallet = false;
                    HighStatus = 1;
                    PalletId = new byte[20];
                    break;
                case 5:
                    Charging = !ChargeNotConnected;
                    if (ChargeNotConnected)
                    {
                        _log($"OPC车{DeviceId} 充电命令完成，但「充电未打通」开着：充电位不置 1");
                    }
                    break;
                case 6:
                    Charging = false;
                    break;
            }
        }

        private void Finish()
        {
            Moving = false;
            Speed = 0;
            _phaseMs = 0;
            if (FailNextExec)
            {
                FailNextExec = false;
                CommandState = STATE_EXEC_FAIL;
                _phase = Phase.Idle;
                _log($"OPC车{DeviceId} 命令 #{CommandId} 模拟执行失败（状态 6），停在 {Position}" + (_buffered != null ? $"，缓存命令 #{_buffered.Id} 作废" : ""));
                _buffered = null;
                return;
            }
            CommandState = STATE_DONE;
            _log($"OPC车{DeviceId} 命令 #{CommandId} {TypeText(CommandType)}完成（状态 4），位置 {Position}" +
                 $"，托盘 {(Pallet ? 1 : 0)}，充电 {(Charging ? 1 : 0)}");
            _phase = _buffered != null ? Phase.Holding : Phase.Idle;
        }

        // ===== 报文 =====

        public byte[] BuildFrame()
        {
            var f = new byte[FrameLength];
            WriteU16(f, F_HEART, Heart);
            WriteU16(f, F_COMMAND_ID, CommandId);
            WriteU16(f, F_COMMAND_TYPE, CommandType);
            WriteU16(f, F_COMMAND_STATE, CommandState);
            WriteU16(f, F_BUF_ID, BufCommandId);
            WriteU16(f, F_BUF_STATE, BufCommandState);
            WriteU16(f, F_TASK_ID, TaskId);
            WriteU16(f, F_TASK_FLAG, TaskFlag);
            WriteU16(f, F_BAY, Bay);
            WriteU16(f, F_ROW, Row);
            WriteU16(f, F_TIER, Tier);
            WriteU16(f, F_HIGH, HighStatus);
            WriteU16(f, F_BATTERY, Battery);
            f[F_FLAGS] = (byte)((Pallet ? 1 : 0) | (Auto ? 2 : 0) | (Normal ? 4 : 0) | (EStop ? 8 : 0) | (Moving ? 16 : 0)
                                | (LowBattery ? 32 : 0) | (Charging ? 64 : 0) | (TowardX ? 128 : 0));
            f[F_FLAGS2] = (byte)((FullBattery ? 1 : 0) | (CallBack ? 4 : 0) | (CyclicDischarge ? 8 : 0));
            BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(F_SPEED), Speed);
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(F_FAULT1), FaultCode1);
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(F_FAULT2), FaultCode2);
            Array.Copy(PalletId, 0, f, F_PALLET_ID, Math.Min(20, PalletId.Length));
            WriteU16(f, F_WEIGHT, Weight);
            BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(F_MILEAGE), Mileage);
            BinaryPrimitives.WriteSingleBigEndian(f.AsSpan(F_RUNTIME), RunTime);
            return f;
        }

        /// <summary>界面上手工改整帧反馈：按帧里的字节回填各字段（不足 84 字节的部分按 0）。低电量/满电位按电量算，不取帧里的值</summary>
        public void LoadFrame(byte[] bytes)
        {
            var f = new byte[FrameLength];
            Array.Copy(bytes, f, Math.Min(bytes.Length, f.Length));
            Heart = ReadU16(f, F_HEART);
            CommandId = ReadU16(f, F_COMMAND_ID);
            CommandType = ReadU16(f, F_COMMAND_TYPE);
            CommandState = ReadU16(f, F_COMMAND_STATE);
            BufCommandId = ReadU16(f, F_BUF_ID);
            BufCommandState = ReadU16(f, F_BUF_STATE);
            TaskId = ReadU16(f, F_TASK_ID);
            TaskFlag = ReadU16(f, F_TASK_FLAG);
            Bay = ReadU16(f, F_BAY);
            Row = ReadU16(f, F_ROW);
            Tier = ReadU16(f, F_TIER);
            HighStatus = ReadU16(f, F_HIGH);
            Battery = ReadU16(f, F_BATTERY);
            byte flags = f[F_FLAGS], flags2 = f[F_FLAGS2];
            Pallet = (flags & 1) != 0;
            Auto = (flags & 2) != 0;
            Normal = (flags & 4) != 0;
            EStop = (flags & 8) != 0;
            Moving = (flags & 16) != 0;
            Charging = (flags & 64) != 0;
            TowardX = (flags & 128) != 0;
            CallBack = (flags2 & 4) != 0;
            CyclicDischarge = (flags2 & 8) != 0;
            Speed = BinaryPrimitives.ReadSingleBigEndian(f.AsSpan(F_SPEED));
            FaultCode1 = BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(F_FAULT1));
            FaultCode2 = BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(F_FAULT2));
            PalletId = f[F_PALLET_ID..(F_PALLET_ID + 20)];
            Weight = ReadU16(f, F_WEIGHT);
            Mileage = BinaryPrimitives.ReadSingleBigEndian(f.AsSpan(F_MILEAGE));
            RunTime = BinaryPrimitives.ReadSingleBigEndian(f.AsSpan(F_RUNTIME));
            // 手工改帧 = 直接定状态：正在跑的命令和缓存都作废
            _phase = Phase.Idle;
            _buffered = null;
        }

        // ===== 工具 =====

        internal static int ReadU16(byte[] f, int at) => f.Length >= at + 2 ? (f[at] << 8) | f[at + 1] : 0;

        private static void WriteU16(byte[] f, int at, int value)
        {
            f[at] = (byte)(value >> 8);
            f[at + 1] = (byte)value;
        }

        internal static string CellText(int tier, int bay, int row) => $"{tier:D2}-{bay:D2}-{row:D2}";

        internal static string AsciiText(byte[] bytes) =>
            new string(bytes.TakeWhile(b => b != 0).Select(b => b is >= 32 and < 127 ? (char)b : '?').ToArray()).Trim();

        internal static string TypeText(int type) => type switch
        {
            1 => "直行",
            2 => "换向直行",
            3 => "取货",
            4 => "放货",
            5 => "充电",
            6 => "取消充电",
            0 => "—",
            _ => $"类型{type}"
        };

        private static readonly Regex CellPattern = new(@"^\s*(\d{1,3})\s*-\s*(\d{1,3})\s*-\s*(\d{1,3})\s*$");

        /// <summary>解析「层-列-排」（如 01-03-13）</summary>
        internal static bool TryParseCell(string text, out int tier, out int bay, out int row)
        {
            tier = bay = row = 0;
            var match = CellPattern.Match(text ?? "");
            if (!match.Success)
            {
                return false;
            }
            tier = int.Parse(match.Groups[1].Value);
            bay = int.Parse(match.Groups[2].Value);
            row = int.Parse(match.Groups[3].Value);
            return true;
        }

        private static bool SameCell(int tier, int bay, int row, HfflCommand cmd) =>
            cmd != null && tier == cmd.ToTier && bay == cmd.ToBay && row == cmd.ToRow;
    }

    /// <summary>把 OPC 四向车变成 OPC 节点；命名空间固定落在 ns=3（RCS 里 OPC_NAMESPACE_INDEX 硬编码为 3）</summary>
    internal sealed class HfflRgvNodeManager : CustomNodeManager2
    {
        public const string PadNamespace = "urn:kean:sim:hffl:pad";   // 占位，保证车的命名空间落到 3
        public const string NamespaceUri = "urn:kean:sim:hffl:rgv";

        private readonly List<HfflRgv> _cars;
        private readonly Dictionary<string, BaseDataVariableState> _readNodes = new();
        private readonly Dictionary<string, BaseDataVariableState> _writeNodes = new();

        public HfflRgvNodeManager(IServerInternal server, ApplicationConfiguration configuration, List<HfflRgv> cars)
            : base(server, configuration, NamespaceUri)
        {
            _cars = cars;
            var index = server.NamespaceUris.GetIndex(NamespaceUri);
            RgvNamespaceIndex = (ushort)(index >= 0 ? index : NamespaceIndexes[0]);
        }

        /// <summary>车命名空间的实际索引（目标 3）</summary>
        public ushort RgvNamespaceIndex { get; }

        /// <summary>收到 WCS 命令（OPC 服务器线程）</summary>
        public event Action<HfflRgv, byte[]> CommandReceived;

        public string DescribeNamespaces()
        {
            var table = Server.NamespaceUris;
            return string.Join(" | ", Enumerable.Range(0, (int)table.Count).Select(i => $"{i}={table.GetString((uint)i)}"));
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
                    SymbolicName = "HfflRgv",
                    NodeId = new NodeId("HfflRgv", RgvNamespaceIndex),
                    BrowseName = new QualifiedName("HfflRgv", RgvNamespaceIndex),
                    DisplayName = new LocalizedText("HfflRgv"),
                    TypeDefinitionId = ObjectTypeIds.FolderType,
                    ReferenceTypeId = ReferenceTypeIds.Organizes,
                    EventNotifier = EventNotifiers.None
                };
                folder.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
                references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, folder.NodeId));
                AddPredefinedNode(SystemContext, folder);

                foreach (var car in _cars)
                {
                    // 节点 id 已存在（设备表写重了）就跳过，不能建两个 NodeId 相同的节点
                    if (!_readNodes.ContainsKey(car.ReadNodeId))
                    {
                        var readNode = CreateByteNode(car.ReadNodeId, $"车{car.DeviceId} PLC→WCS", car.BuildFrame());
                        folder.AddChild(readNode);
                        _readNodes[car.ReadNodeId] = readNode;
                    }
                    if (!_writeNodes.ContainsKey(car.WriteNodeId) && !_readNodes.ContainsKey(car.WriteNodeId))
                    {
                        var target = car;
                        var writeNode = CreateByteNode(car.WriteNodeId, $"车{car.DeviceId} WCS→PLC", new byte[ConveyorDevice.BytesOf(car.WriteTag)]);
                        writeNode.OnWriteValue = (ISystemContext context, NodeState state, NumericRange indexRange,
                            QualifiedName dataEncoding, ref object value, ref StatusCode statusCode, ref DateTime timestamp) =>
                        {
                            if (value is byte[] bytes)
                            {
                                ((BaseVariableState)state).Value = bytes;
                                state.ClearChangeMasks(context, false);
                                CommandReceived?.Invoke(target, bytes);
                            }
                            statusCode = StatusCodes.Good;
                            timestamp = DateTime.UtcNow;
                            return ServiceResult.Good;
                        };
                        folder.AddChild(writeNode);
                        _writeNodes[car.WriteNodeId] = writeNode;
                    }
                }
            }
        }

        private BaseDataVariableState CreateByteNode(string identifier, string description, byte[] value)
        {
            var node = new BaseDataVariableState(null)
            {
                SymbolicName = identifier,
                NodeId = new NodeId(identifier, RgvNamespaceIndex),
                BrowseName = new QualifiedName(identifier, RgvNamespaceIndex),
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

        /// <summary>把最新反馈帧推给订阅方；帧没变不推（真实 PLC 值不变时订阅也收不到通知）</summary>
        public void PushReadValue(HfflRgv car)
        {
            lock (Lock)
            {
                if (!_readNodes.TryGetValue(car.ReadNodeId, out var node))
                {
                    return;
                }
                var frame = car.BuildFrame();
                if (node.Value is byte[] current && current.AsSpan().SequenceEqual(frame))
                {
                    return;
                }
                node.Value = frame;
                node.Timestamp = DateTime.UtcNow;
                node.ClearChangeMasks(SystemContext, false);
            }
        }

        /// <summary>界面上手工下的命令也写到写区节点上（和 RCS 写进来的效果一致）</summary>
        public void PushWriteValue(HfflRgv car, byte[] frame)
        {
            lock (Lock)
            {
                if (!_writeNodes.TryGetValue(car.WriteNodeId, out var node))
                {
                    return;
                }
                node.Value = frame;
                node.Timestamp = DateTime.UtcNow;
                node.ClearChangeMasks(SystemContext, false);
            }
        }

        /// <summary>这台车的反馈节点上挂着几个数据订阅（RCS 连上并订阅成功后 ≥1）</summary>
        public int SubscriberCount(HfflRgv car)
        {
            lock (Lock)
            {
                if (!_readNodes.TryGetValue(car.ReadNodeId, out var node) || !MonitoredNodes.TryGetValue(node.NodeId, out var monitored))
                {
                    return 0;
                }
                return monitored?.DataChangeMonitoredItems?.Count ?? 0;
            }
        }
    }

    /// <summary>四向车 OPC UA 服务器（无安全通道 + 匿名，方便本机联调）；和输送线的服务器各自独立、端口分开</summary>
    internal sealed class HfflRgvOpcServer : StandardServer
    {
        private readonly List<HfflRgv> _cars;

        /// <summary>服务器应用 URI：不能和车命名空间同名（同名会把它顶到 ns=1），也不和输送线服务器重名</summary>
        public const string ApplicationUriValue = "urn:kean:sim:hffl:server";

        /// <summary>自签名证书的主题名（删旧证书时按它的前缀找文件）</summary>
        public const string CertificateName = "RgvSimulatorGuiHfflRgv";

        public HfflRgvOpcServer(List<HfflRgv> cars)
        {
            _cars = cars;
        }

        public HfflRgvNodeManager NodeManager { get; private set; }

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
            // 同输送线：RCS 侧 OPC_NAMESPACE_INDEX 硬编码为 3，先把 0=基址、1=应用 URI、2=占位铺好，再把车的命名空间放到第 3 位
            var uris = server.NamespaceUris;
            if (uris.Count == 1)
            {
                uris.Append(configuration.ApplicationUri);
            }
            while (uris.Count < 3)
            {
                uris.Append($"{HfflRgvNodeManager.PadNamespace}{uris.Count}");
            }
            if (uris.GetIndex(HfflRgvNodeManager.NamespaceUri) < 0)
            {
                uris.Append(HfflRgvNodeManager.NamespaceUri);
            }
            NodeManager = new HfflRgvNodeManager(server, configuration, _cars);
            return new MasterNodeManager(server, configuration, null, NodeManager);
        }

        public static ApplicationConfiguration BuildConfiguration(int port, string pkiRoot) => new()
        {
            ApplicationName = "RGV 模拟器-四向车 OPC",
            ApplicationUri = ApplicationUriValue,
            ApplicationType = ApplicationType.Server,
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = { $"opc.tcp://127.0.0.1:{port}" },
                SecurityPolicies = new ServerSecurityPolicyCollection
                {
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None }
                },
                UserTokenPolicies = new UserTokenPolicyCollection { new UserTokenPolicy(UserTokenType.Anonymous) },
                DiagnosticsEnabled = false,
                MaxSessionCount = 100,              // RCS 每台车一个连接（一个会话）
                MinSessionTimeout = 10000,
                MaxSessionTimeout = 3600000,
                MinSubscriptionLifetime = 10000,    // 同输送线：RCS 被强停时残留的订阅 30 秒内自动清掉
                MaxSubscriptionLifetime = 30000,
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
                    SubjectName = "CN=" + CertificateName
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
    }
}
