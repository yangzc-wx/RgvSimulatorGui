using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Kean.Tools.RgvSimulatorGui
{
    /// <summary>
    /// OPC 四向车一行的视图模型。
    /// 约定同 <see cref="CarVm"/>：属性 setter = 用户改值 → 直接改车的反馈并推给订阅方；<see cref="Refresh"/> 只把车的当前值读回来显示。
    /// 全部在界面线程上执行。
    /// </summary>
    internal sealed class HfflCarVm : INotifyPropertyChanged
    {
        private readonly Action<string> _log;
        private readonly Action<HfflRgv> _push;
        private readonly Func<HfflRgv, int> _subscribers;
        private readonly Dictionary<string, object> _shown = new();   // 上次通知过的值：没变就不通知（可编辑列不被 250ms 刷新顶掉）
        private int _subscriberCount;

        public HfflCarVm(HfflRgv car, Action<string> log, Action<HfflRgv> push, Func<HfflRgv, int> subscribers)
        {
            Car = car;
            _log = log;
            _push = push;
            _subscribers = subscribers;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public HfflRgv Car { get; }

        public int Device => Car.DeviceId;

        public string Server => Car.Server;

        public bool IsConnected => _subscriberCount > 0;

        public string ConnectionText => _subscriberCount > 0 ? $"已订阅 {_subscriberCount}" : "未订阅";

        public bool HasFault => Car.HasFault;

        public string StateText => Car.StateText;

        public string StateKind => Car.StateKind;

        public int CommandId => Car.CommandId;

        public string CommandText => HfflRgv.TypeText(Car.CommandType);

        public int CommandState => Car.CommandState;

        public int TaskId => Car.TaskId;

        public string BufferedText => Car.BufferedText;

        public int Writes => Car.Writes;

        public string LastWrite => Car.LastWrite;

        /// <summary>当前位置「层-列-排」，可直接改</summary>
        public string Position
        {
            get => Car.Position;
            set
            {
                if (string.Equals((value ?? "").Trim(), Car.Position))
                {
                    return;
                }
                if (Car.IsBusy)
                {
                    _log($"OPC车{Device} 正在执行命令，先中止再改位置");
                    Raise(nameof(Position));
                    return;
                }
                if (!Car.TrySetPosition(value))
                {
                    _log($"OPC车{Device}：位置要填「层-列-排」，例如 01-03-13，这次没改");
                    Raise(nameof(Position));
                    return;
                }
                Changed(nameof(Position), $"位置 → {Car.Position}");
            }
        }

        public int Battery
        {
            get => Car.Battery;
            set
            {
                if (value < 0 || value > 100)
                {
                    _log($"OPC车{Device}：电量要填 0-100");
                    Raise(nameof(Battery));
                    return;
                }
                if (Car.Battery == value)
                {
                    return;
                }
                Car.Battery = value;
                Changed(nameof(Battery), $"电量 → {value}%");
            }
        }

        public bool Pallet
        {
            get => Car.Pallet;
            set
            {
                if (Car.Pallet == value)
                {
                    return;
                }
                Car.Pallet = value;
                Changed(nameof(Pallet), $"托盘 → {(value ? 1 : 0)}");
            }
        }

        public bool Charging
        {
            get => Car.Charging;
            set
            {
                if (Car.Charging == value)
                {
                    return;
                }
                Car.Charging = value;
                Changed(nameof(Charging), $"充电 → {(value ? 1 : 0)}");
            }
        }

        public bool TowardX
        {
            get => Car.TowardX;
            set
            {
                if (Car.TowardX == value)
                {
                    return;
                }
                Car.TowardX = value;
                Changed(nameof(TowardX), $"朝向 → {(value ? "X" : "Y")}（RCS 据此决定下次发直行还是先换向）");
            }
        }

        public bool Auto
        {
            get => Car.Auto;
            set
            {
                if (Car.Auto == value)
                {
                    return;
                }
                Car.Auto = value;
                Changed(nameof(Auto), value ? "切到自动" : "切到手动（RCS 不会再给它发命令）");
            }
        }

        public uint FaultCode1
        {
            get => Car.FaultCode1;
            set
            {
                if (Car.FaultCode1 == value)
                {
                    return;
                }
                Car.FaultCode1 = value;
                Changed(nameof(FaultCode1), $"故障码1 → {value}");
            }
        }

        public uint FaultCode2
        {
            get => Car.FaultCode2;
            set
            {
                if (Car.FaultCode2 == value)
                {
                    return;
                }
                Car.FaultCode2 = value;
                Changed(nameof(FaultCode2), $"故障码2 → {value}");
            }
        }

        // ===== 选中车操作面板上的开关 =====

        public bool EmptyPick
        {
            get => Car.EmptyPick;
            set => Option(ref Car.EmptyPick, value, nameof(EmptyPick), "空取");
        }

        public bool ChargeNotConnected
        {
            get => Car.ChargeNotConnected;
            set => Option(ref Car.ChargeNotConnected, value, nameof(ChargeNotConnected), "充电未打通");
        }

        public bool StrictStart
        {
            get => Car.StrictStart;
            set => Option(ref Car.StrictStart, value, nameof(StrictStart), "起点校验");
        }

        public bool CallBack
        {
            get => Car.CallBack;
            set
            {
                if (Car.CallBack == value)
                {
                    return;
                }
                Car.CallBack = value;
                Changed(nameof(CallBack), $"回调中 → {(value ? 1 : 0)}{(value ? "（RCS 不会再给它发命令）" : "")}");
            }
        }

        public bool EStop
        {
            get => Car.EStop;
            set
            {
                if (Car.EStop == value)
                {
                    return;
                }
                Car.EStop = value;
                Changed(nameof(EStop), value ? "急停（正在执行的命令原地暂停）" : "解除急停，接着执行");
            }
        }

        /// <summary>反馈帧（PLC→WCS）的十进制字节，选中车面板上显示</summary>
        public string FeedbackText => string.Join(",", Car.BuildFrame());

        /// <summary>最近一帧命令（WCS→PLC）的十进制字节</summary>
        public string CommandFrameText => Car.LastCommandFrame is { Length: > 0 } frame ? string.Join(",", frame) : "（还没收到命令）";

        /// <summary>把车的当前值读回来显示（只通知变了的属性）</summary>
        public void Refresh()
        {
            _subscriberCount = _subscribers?.Invoke(Car) ?? 0;
            Notify(nameof(IsConnected), IsConnected);
            Notify(nameof(ConnectionText), ConnectionText);
            Notify(nameof(HasFault), HasFault);
            Notify(nameof(StateText), StateText);
            Notify(nameof(StateKind), StateKind);
            Notify(nameof(CommandId), CommandId);
            Notify(nameof(CommandText), CommandText);
            Notify(nameof(CommandState), CommandState);
            Notify(nameof(TaskId), TaskId);
            Notify(nameof(BufferedText), BufferedText);
            Notify(nameof(Writes), Writes);
            Notify(nameof(LastWrite), LastWrite);
            Notify(nameof(Position), Position);
            Notify(nameof(Battery), Battery);
            Notify(nameof(Pallet), Pallet);
            Notify(nameof(Charging), Charging);
            Notify(nameof(TowardX), TowardX);
            Notify(nameof(Auto), Auto);
            Notify(nameof(FaultCode1), FaultCode1);
            Notify(nameof(FaultCode2), FaultCode2);
            Notify(nameof(EmptyPick), EmptyPick);
            Notify(nameof(ChargeNotConnected), ChargeNotConnected);
            Notify(nameof(StrictStart), StrictStart);
            Notify(nameof(CallBack), CallBack);
            Notify(nameof(EStop), EStop);
            Notify(nameof(FeedbackText), FeedbackText);
            Notify(nameof(CommandFrameText), CommandFrameText);
        }

        private void Notify(string name, object value)
        {
            if (_shown.TryGetValue(name, out var shown) && Equals(shown, value))
            {
                return;
            }
            _shown[name] = value;
            Raise(name);
        }

        /// <summary>用户改了一个反馈值：记日志、立即推给订阅方、刷新显示</summary>
        private void Changed(string name, string message)
        {
            _log($"OPC车{Device} {message}");
            _push?.Invoke(Car);
            Refresh();
            Raise(name);
        }

        private void Option(ref bool field, bool value, string name, string label)
        {
            if (field == value)
            {
                return;
            }
            field = value;
            _log($"OPC车{Device} {label}：{(value ? "开" : "关")}");
            Raise(name);
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
