using System;
using System.ComponentModel;
using System.Net;

namespace Kean.Tools.RgvSimulatorGui
{
    /// <summary>
    /// 一台模拟车的视图模型。
    /// 约定：属性 setter = 用户改值 → 直接写从站寄存器；<see cref="Refresh"/> 只把寄存器读回来显示，不回写。
    /// </summary>
    internal sealed class CarVm : INotifyPropertyChanged
    {
        private const int R_WORK = ModbusTcpSlave.WORK, R_LOCATION = ModbusTcpSlave.LOCATION, R_BATTERY = ModbusTcpSlave.BATTERY,
            R_WARN_TYPE = ModbusTcpSlave.WARN_TYPE, R_STATUS = ModbusTcpSlave.STATUS,
            R_WARN_CODE = ModbusTcpSlave.WARN_CODE, R_CARGO = ModbusTcpSlave.CARGO_STATUS;

        private const int BIT_MID = 0, BIT_LIFT = 1, BIT_TURN = 2, BIT_CHARGE_ON = 4, BIT_CHARGE_OK = 5, BIT_PALLET = 6;

        private readonly RgvTrack _track;
        private readonly Action<string> _log;
        private readonly byte _unitId;

        private string _ip, _cellCode = "?", _lastWrite = "-";
        private int _port, _location, _battery, _warnType, _warnCode, _clients, _writes;
        private bool _work, _middle, _lift, _turn, _pallet, _chargeOn, _chargeOk;

        // 写入计数由从站的网络线程更新，界面在 Refresh（UI 线程）里再取出来显示，避免跨线程改绑定源
        private readonly object _writeLock = new();
        private int _pendingWrites;
        private string _pendingLastWrite = "-";

        public CarVm(int device, string ip, int port, byte unitId, ModbusTcpSlave slave, RgvTrack track, Action<string> log)
        {
            Device = device;
            _ip = ip;
            _port = port;
            _unitId = unitId;
            _track = track;
            _log = log;
            Slave = slave;
            slave.Written += OnSlaveWritten;
            _location = slave[R_LOCATION];
            UpdateCellCode();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public int Device { get; }

        public ModbusTcpSlave Slave { get; private set; }

        public string CellCode => _cellCode;

        public int Clients => _clients;

        public bool IsConnected => _clients > 0;

        /// <summary>连接列显示：已连接 N / 未连接</summary>
        public string ConnectionText => _clients > 0 ? $"已连接 {_clients}" : "未连接";

        /// <summary>有告警：整行标红</summary>
        public bool HasFault => _warnType != 0 || _warnCode != 0;

        public int Writes => _writes;

        public string LastWrite => _lastWrite;

        public string Ip
        {
            get => _ip;
            set
            {
                var text = (value ?? "").Trim();
                if (text == _ip)
                {
                    return;
                }
                if (!IPAddress.TryParse(text, out _))
                {
                    _log($"车{Device}：IP「{text}」不合法，已还原为 {_ip}");
                    Raise(nameof(Ip));
                    return;
                }
                Rebind(text, _port);
            }
        }

        public int Port
        {
            get => _port;
            set
            {
                if (value == _port)
                {
                    return;
                }
                if (value < 1 || value > 65535)
                {
                    _log($"车{Device}：端口要填 1-65535，已还原为 {_port}");
                    Raise(nameof(Port));
                    return;
                }
                Rebind(_ip, value);
            }
        }

        public int Location
        {
            get => _location;
            set
            {
                if (value < 0 || value > 65535)
                {
                    _log($"车{Device}：码值要填 0-65535");
                    Raise(nameof(Location));
                    return;
                }
                if (_location == value)
                {
                    return;
                }
                _location = value;
                Raise(nameof(Location));
                Slave[R_LOCATION] = (ushort)value;
                UpdateCellCode();
                _log($"车{Device} 码值 → {value}");
            }
        }

        public bool Work
        {
            get => _work;
            set
            {
                if (_work == value)
                {
                    return;
                }
                _work = value;
                Raise(nameof(Work));
                Slave[R_WORK] = (ushort)(value ? 1 : 0);
                _log($"车{Device} work → {(value ? 1 : 0)}");
            }
        }

        public bool Middle
        {
            get => _middle;
            set => SetStatusBit(ref _middle, value, nameof(Middle), BIT_MID);
        }

        public bool Lift
        {
            get => _lift;
            set => SetStatusBit(ref _lift, value, nameof(Lift), BIT_LIFT);
        }

        public bool Turn
        {
            get => _turn;
            set => SetStatusBit(ref _turn, value, nameof(Turn), BIT_TURN);
        }

        public bool ChargeOn
        {
            get => _chargeOn;
            set => SetStatusBit(ref _chargeOn, value, nameof(ChargeOn), BIT_CHARGE_ON);
        }

        public bool ChargeOk
        {
            get => _chargeOk;
            set => SetStatusBit(ref _chargeOk, value, nameof(ChargeOk), BIT_CHARGE_OK);
        }

        public bool Pallet
        {
            get => _pallet;
            set
            {
                if (_pallet == value)
                {
                    return;
                }
                _pallet = value;
                Raise(nameof(Pallet));
                int cargo = Slave[R_CARGO];
                Slave[R_CARGO] = (ushort)(value ? cargo | (1 << BIT_PALLET) : cargo & ~(1 << BIT_PALLET));
                _log($"车{Device} 托盘检测 → {(value ? 1 : 0)}");
            }
        }

        public int Battery
        {
            get => _battery;
            set => SetRegister(ref _battery, value, nameof(Battery), R_BATTERY, "电量");
        }

        public int WarnType
        {
            get => _warnType;
            set => SetRegister(ref _warnType, value, nameof(WarnType), R_WARN_TYPE, "告警类型");
        }

        public int WarnCode
        {
            get => _warnCode;
            set => SetRegister(ref _warnCode, value, nameof(WarnCode), R_WARN_CODE, "告警码");
        }

        public bool EmptyLift
        {
            get => Slave.EmptyLift;
            set
            {
                if (Slave.EmptyLift == value)
                {
                    return;
                }
                Slave.EmptyLift = value;
                Raise(nameof(EmptyLift));
                _log($"车{Device} 空顶：{(value ? "开" : "关")}");
            }
        }

        public bool ChargeNotConnected
        {
            get => Slave.ChargeNotConnected;
            set
            {
                if (Slave.ChargeNotConnected == value)
                {
                    return;
                }
                Slave.ChargeNotConnected = value;
                Raise(nameof(ChargeNotConnected));
                _log($"车{Device} 充电未打通：{(value ? "开" : "关")}");
            }
        }

        public bool Split
        {
            get => Slave.SplitResponse;
            set
            {
                if (Slave.SplitResponse == value)
                {
                    return;
                }
                Slave.SplitResponse = value;
                Raise(nameof(Split));
                _log($"车{Device} 分包响应：{(value ? "开" : "关")}");
            }
        }

        /// <summary>把从站寄存器读回来显示（只读回显，不回写）</summary>
        public void Refresh()
        {
            int work = Slave[R_WORK], location = Slave[R_LOCATION], status = Slave[R_STATUS],
                cargo = Slave[R_CARGO], clients = Slave.ClientCount;

            if (_location != location)
            {
                _location = location;
                Raise(nameof(Location));
                UpdateCellCode();
            }
            if (_work != (work == 1)) { _work = work == 1; Raise(nameof(Work)); }
            if (_middle != Bit(status, BIT_MID)) { _middle = Bit(status, BIT_MID); Raise(nameof(Middle)); }
            if (_lift != Bit(status, BIT_LIFT)) { _lift = Bit(status, BIT_LIFT); Raise(nameof(Lift)); }
            if (_turn != Bit(status, BIT_TURN)) { _turn = Bit(status, BIT_TURN); Raise(nameof(Turn)); }
            if (_chargeOn != Bit(status, BIT_CHARGE_ON)) { _chargeOn = Bit(status, BIT_CHARGE_ON); Raise(nameof(ChargeOn)); }
            if (_chargeOk != Bit(status, BIT_CHARGE_OK)) { _chargeOk = Bit(status, BIT_CHARGE_OK); Raise(nameof(ChargeOk)); }
            if (_pallet != Bit(cargo, BIT_PALLET)) { _pallet = Bit(cargo, BIT_PALLET); Raise(nameof(Pallet)); }
            if (_battery != Slave[R_BATTERY]) { _battery = Slave[R_BATTERY]; Raise(nameof(Battery)); }
            if (_warnType != Slave[R_WARN_TYPE]) { _warnType = Slave[R_WARN_TYPE]; Raise(nameof(WarnType)); Raise(nameof(HasFault)); }
            if (_warnCode != Slave[R_WARN_CODE]) { _warnCode = Slave[R_WARN_CODE]; Raise(nameof(WarnCode)); Raise(nameof(HasFault)); }
            if (_clients != clients)
            {
                _clients = clients;
                Raise(nameof(Clients));
                Raise(nameof(IsConnected));
                Raise(nameof(ConnectionText));
            }
            int writes;
            string lastWrite;
            lock (_writeLock)
            {
                writes = _pendingWrites;
                lastWrite = _pendingLastWrite;
            }
            if (_writes != writes)
            {
                _writes = writes;
                _lastWrite = lastWrite;
                Raise(nameof(Writes));
                Raise(nameof(LastWrite));
            }
        }

        /// <summary>改了 IP/端口：给这台车重新绑定（保留码值等反馈值，原连接断开）</summary>
        private void Rebind(string ip, int port)
        {
            if (!IPAddress.TryParse(ip, out var address))
            {
                return;
            }
            var old = Slave;
            // 从站是独占绑定：只换 IP 不换端口时，必须先放掉旧监听才能绑上新地址；失败再把旧的拉起来
            bool samePort = port == _port;
            if (samePort)
            {
                old.Stop();
            }
            var slave = new ModbusTcpSlave(port, _unitId, address)
            {
                WorkDuration = old.WorkDuration,
                StepDuration = old.StepDuration,
                EmptyLift = old.EmptyLift,
                ChargeNotConnected = old.ChargeNotConnected,
                Mute = old.Mute,
                SplitResponse = old.SplitResponse
            };
            for (int addressIndex = 200; addressIndex <= 220; addressIndex++)
            {
                slave[addressIndex] = old[addressIndex];
            }
            try
            {
                slave.Start();
            }
            catch (Exception ex)
            {
                slave.Dispose();
                string restore = "";
                if (samePort)
                {
                    try { old.Start(); }
                    catch (Exception restartEx) { restore = $"；原地址也没能重新拉起：{restartEx.Message}"; }
                }
                _log($"车{Device} 绑定 {ip}:{port} 失败：{ex.Message}（端口被占用？）已还原为 {_ip}:{_port}{restore}");
                Raise(nameof(Ip));
                Raise(nameof(Port));
                return;
            }

            old.Written -= OnSlaveWritten;
            slave.Written += OnSlaveWritten;
            Slave = slave;
            _ip = ip;
            _port = port;
            Raise(nameof(Ip));
            Raise(nameof(Port));
            old.Dispose();
            _log($"车{Device} 已重新绑定 {ip}:{port}，原端口连接断开（RCS 需按新端口重连）");
        }

        /// <summary>从站收到写请求（网络线程）：只记数，显示交给 Refresh</summary>
        private void OnSlaveWritten(ushort address, ushort[] values)
        {
            lock (_writeLock)
            {
                _pendingWrites++;
                _pendingLastWrite = $"{address}:[{string.Join(",", values)}]";
            }
            _log($"[车{Device}] 写入 地址={address} 值=[{string.Join(", ", values)}]");
        }

        private void SetStatusBit(ref bool field, bool value, string name, int bit)
        {
            if (field == value)
            {
                return;
            }
            field = value;
            Raise(name);
            int status = Slave[R_STATUS];
            Slave[R_STATUS] = (ushort)(value ? status | (1 << bit) : status & ~(1 << bit));
            _log($"车{Device} {BitName(bit)} → {(value ? 1 : 0)}");
        }

        private void SetRegister(ref int field, int value, string name, int address, string label)
        {
            if (value < 0 || value > 65535)
            {
                _log($"车{Device}：{label}要填 0-65535");
                Raise(name);
                return;
            }
            if (field == value)
            {
                return;
            }
            field = value;
            Raise(name);
            Raise(nameof(HasFault));
            Slave[address] = (ushort)value;
            _log($"车{Device} {label} → {value}");
        }

        private void UpdateCellCode()
        {
            var code = _track.TryGetCellCode(_location, out var cellCode) ? cellCode : "?";
            if (_cellCode == code)
            {
                return;
            }
            _cellCode = code;
            Raise(nameof(CellCode));
        }

        private static bool Bit(int value, int bit) => (value >> bit & 1) == 1;

        private static string BitName(int bit) => bit switch
        {
            BIT_MID => "中间位",
            BIT_LIFT => "顶升位",
            BIT_TURN => "换向位",
            BIT_CHARGE_ON => "充电开关",
            BIT_CHARGE_OK => "充电正常",
            _ => $"bit{bit}"
        };

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
