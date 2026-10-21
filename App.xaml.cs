using System;
using System.Linq;
using System.Windows;

namespace Kean.Tools.RgvSimulatorGui
{
    /// <summary>
    /// 四向车 Modbus 模拟器的可视化外壳（WPF）：一屏起 N 台车，实时看/改反馈寄存器。
    /// 协议层在同工程 Simulator 目录（ModbusTcpSlave / RgvTrack），本工程只做界面。
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            bool autoStart = e.Args?.Any(a => string.Equals(a, "--start", StringComparison.OrdinalIgnoreCase)) == true;
            new MainWindow(autoStart).Show();
        }
    }
}
