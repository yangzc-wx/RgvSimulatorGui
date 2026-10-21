using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Opc.Ua.Configuration;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace Kean.Tools.RgvSimulatorGui
{
    /// <summary>
    /// 一屏式模拟器控制台：左侧切换设备类型（四向车 / 提升机 / 输送线），右侧看并直接改每台设备的反馈，底部是共用日志。
    /// 三类设备共用一个 250ms 的界面定时器：只要还有任何一类在跑，定时器就不停（提升机的生命计数、输送线的完成回写都靠它）。
    /// </summary>
    public partial class MainWindow : FluentWindow
    {
        private readonly ObservableCollection<CarVm> _cars = new();
        private readonly ObservableCollection<HoistVm> _hoists = new();
        private readonly ObservableCollection<ConveyorVm> _conveyors = new();
        private readonly List<ConveyorDevice> _conveyorDevices = new();   // 当前 OPC 服务器上的设备（运行态）
        private List<ConveyorDevice> _importedConveyors;                  // 剪贴板导入的设备表；null = 按参数生成
        private ConveyorOpcServer _conveyorServer;
        private readonly RgvTrack _track = new();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly bool _autoStart;
        private int _appliedStep = 1000, _appliedWorkDur = 1500;
        private int _conveyorSessions;          // 上一轮的 OPC 会话数，用来在日志里提示连上/断开
        private string _conveyorEndpoint = "";  // 运行中的 OPC 端点，标题栏上显示
        private int _conveyorNamespace;         // 输送线命名空间索引（RCS 要 3）

        /// <summary>日志文件（按天一个）：界面上往下滚看不到的老日志都能在这里翻到</summary>
        private static readonly string LogFolder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RgvSimulatorGui", "logs");
        private static readonly string LogFile = System.IO.Path.Combine(LogFolder, $"{DateTime.Now:yyyyMMdd}.log");
        private readonly ConveyorFlow _flow;            // 输送线运行 → 交货
        private readonly HoistTransferLink _hoistLink;  // 输送线 PLC ↔ 提升机 PLC 的交接（不经 WCS）

        /// <summary>配色方案：Color 为 null 表示跟随 Windows 系统强调色</summary>
        private sealed record AccentScheme(string Key, string Name, Color? Color);

        private static readonly AccentScheme[] Schemes =
        {
            new("green", "翠绿", Color.FromRgb(0x10, 0x89, 0x3E)),
            new("teal", "青碧", Color.FromRgb(0x00, 0x82, 0x72)),
            new("olive", "橄榄", Color.FromRgb(0x49, 0x82, 0x05)),
            new("blue", "海蓝", Color.FromRgb(0x00, 0x78, 0xD4)),
            new("purple", "靛紫", Color.FromRgb(0x87, 0x64, 0xB8)),
            new("amber", "琥珀", Color.FromRgb(0xCA, 0x50, 0x10)),
            new("system", "跟随系统", null)
        };

        private static readonly string AppearanceFile = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RgvSimulatorGui", "appearance.txt");

        private AccentScheme _scheme = Schemes[0];
        private ApplicationTheme _theme = ApplicationTheme.Light;

        public MainWindow(bool autoStart = false)
        {
            _autoStart = autoStart;
            InitializeComponent();
            _flow = new ConveyorFlow(FindConveyor, ConveyorRunTicks, Log);
            _hoistLink = new HoistTransferLink(HoistTransferLink.DefaultDocks, FindConveyor,
                id => _hoists.FirstOrDefault(h => h.Device == id)?.Behavior, _flow, Log);
            _flow.Hoists = _hoistLink;

            IpBox.ItemsSource = BindableAddresses();
            IpBox.Text = "0.0.0.0";
            CarGrid.ItemsSource = _cars;
            HoistGrid.ItemsSource = _hoists;
            ConveyorGrid.ItemsSource = _conveyors;
            InitHffl();                         // OPC 四向车页（MainWindow.Hffl.cs）
            CarGrid.SelectionChanged += OnSelectionChanged;
            _timer.Tick += (_, __) => OnTick();
            BuildSchemePicker();

            Loaded += (_, __) =>
            {
                LoadAppearance();
                ApplyAppearance();
                // FluentWindow 加载背景材质时会把强调色刷回系统色，等首帧渲染完再套一次方案
                Dispatcher.BeginInvoke(new Action(ApplyAppearance), DispatcherPriority.ContextIdle);
                if (_autoStart)
                {
                    StartCars();
                }
                Log($"日志文件：{LogFile}（窗口里只留最近一段，完整的在这个文件里）");
                RefreshStatus();
            };
            Closing += (_, __) =>
            {
                StopCars();
                StopHffl();
                StopHoists();
                StopConveyors();
            };
        }

        private CarVm SelectedCar => CarGrid.SelectedItem as CarVm;

        private HoistVm SelectedHoist => HoistGrid.SelectedItem as HoistVm;

        private ConveyorVm SelectedConveyor => ConveyorGrid.SelectedItem as ConveyorVm;

        // ===== 公共：定时器 / 状态 / 页面切换 / 主题 =====

        private void OnTick()
        {
            foreach (var car in _cars)
            {
                car.Refresh();
            }
            foreach (var hoist in _hoists)
            {
                hoist.Behavior.Tick();          // 跳生命计数、处理 WCS 写区、推进平台
                hoist.Behavior.AckTaskUpdateIfRead();
            }
            _hoistLink.Tick();                  // 输送线与提升机交接（在提升机推进之后、输送线交货之前）
            foreach (var hoist in _hoists)
            {
                hoist.Refresh();
            }
            foreach (var conveyor in _conveyors)
            {
                if (TickConveyor(conveyor.Device))
                {
                    _conveyorServer?.NodeManager?.PushReadValue(conveyor.Device);
                }
                conveyor.Refresh();
            }
            TickHffl();
            RefreshNavStatus();
            RefreshOpsTitle();
        }

        /// <summary>输送线的一个 250ms 周期（逻辑在 ConveyorFlow，含经提升机交接）。返回 true 表示反馈值变了，要推给 OPC 订阅方</summary>
        private bool TickConveyor(ConveyorDevice device) => _flow.Tick(device);

        private ConveyorDevice FindConveyor(int deviceId) =>
            _conveyorDevices.FirstOrDefault(d => d.DeviceId == deviceId);

        /// <summary>开始一次"运行"：状态=1，方向按指令类型，运行时间到了由 TickConveyor 交货</summary>
        private string StartConveyorRun(ConveyorDevice device, int type, int instruction, int destination) =>
            _flow.Start(device, type, instruction, destination);

        /// <summary>一节输送线的运行耗时（毫秒）折算成周期数，按界面上的参数</summary>
        private int ConveyorRunTicks() => Math.Max(1, Math.Clamp(ParseInt(ConveyorRunBox.Text, 1500), 250, 60000) / 250);

        /// <summary>任何一类设备还在跑就保持定时器；全停了才停</summary>
        private void UpdateTimer()
        {
            bool any = _cars.Count > 0 || _hoists.Count > 0 || _conveyorServer != null || _hfflServer != null;
            if (any && !_timer.IsEnabled)
            {
                _timer.Start();
            }
            else if (!any && _timer.IsEnabled)
            {
                _timer.Stop();
            }
        }

        /// <summary>同步按钮可用状态、空表提示、左侧导航上的运行摘要</summary>
        private void RefreshStatus()
        {
            StopBtn.IsEnabled = _cars.Count > 0;
            RemoveCarBtn.IsEnabled = SelectedCar != null;
            OpsPanel.IsEnabled = SelectedCar != null;
            HoistStartBtn.IsEnabled = _hoists.Count == 0;
            HoistStopBtn.IsEnabled = _hoists.Count > 0;
            ConveyorStartBtn.IsEnabled = _conveyorServer == null;
            ConveyorStopBtn.IsEnabled = _conveyorServer != null;
            CarEmptyHint.Visibility = _cars.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HoistEmptyHint.Visibility = _hoists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ConveyorEmptyHint.Visibility = _conveyors.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ConveyorClearImportBtn.Visibility = _importedConveyors != null ? Visibility.Visible : Visibility.Collapsed;
            ConveyorSourceText.Text = _importedConveyors != null
                ? $"设备表：剪贴板导入的 {_importedConveyors.Count} 台（启动时使用；上面的设备号/台数参数不生效）"
                : "设备表：按上面的参数生成（标签 = DB{写DB}.{偏移}，每台 10 字节）";
            RefreshHfflStatus();
            UpdateTimer();
            RefreshNavStatus();
            RefreshOpsTitle();
        }

        private void RefreshNavStatus()
        {
            CarNavStatus.Text = Summary(_cars.Count, _cars.Count(c => c.IsConnected), _cars.Select(c => c.Port));
            HoistNavStatus.Text = Summary(_hoists.Count, _hoists.Count(h => h.IsConnected), _hoists.Select(h => h.Port));
            RefreshHfflNavStatus();
            ConveyorNavStatus.Text = _conveyorServer == null
                ? (_importedConveyors != null ? $"未启动 · 已导入 {_importedConveyors.Count} 台" : "未启动")
                : $"{_conveyors.Count} 节 · {_conveyors.Count(c => c.State == 1)} 运行 · {_conveyors.Count(c => c.HasCargo)} 有货 · 会话 {_conveyorServer.SessionCount}";
            if (_conveyorServer != null)
            {
                // 会话数变化就在日志里说一声：对面连上/断开能一眼看到，省得猜"到底有没有订阅"
                int sessions = _conveyorServer.SessionCount;
                ConveyorEndpointText.Text = $"运行中 · {_conveyorEndpoint} · ns={_conveyorNamespace} · 会话 {sessions}";
                if (sessions != _conveyorSessions)
                {
                    int before = _conveyorSessions;
                    _conveyorSessions = sessions;
                    Log(sessions switch
                    {
                        0 => $"OPC 会话已全部断开（原有 {before} 个）",
                        var s when s > before => $"有客户端连上 OPC 服务器（会话 {s}）",
                        _ => $"有客户端断开，还剩 {sessions} 个会话"
                    });
                }
            }

            static string Summary(int count, int connected, IEnumerable<int> ports)
            {
                if (count == 0)
                {
                    return "未启动";
                }
                var list = ports.ToList();
                int min = list.Min(), max = list.Max();
                return $"{count} 台 · {connected} 已连 · {(min == max ? $"{min}" : $"{min}-{max}")}";
            }
        }

        private void OnNavChecked(object sender, RoutedEventArgs e)
        {
            if (CarPage == null || sender is not FrameworkElement nav)
            {
                return; // InitializeComponent 期间页面还没建好
            }
            var page = nav.Tag as string;
            CarPage.Visibility = page == "car" ? Visibility.Visible : Visibility.Collapsed;
            HfflPage.Visibility = page == "hffl" ? Visibility.Visible : Visibility.Collapsed;
            HoistPage.Visibility = page == "hoist" ? Visibility.Visible : Visibility.Collapsed;
            ConveyorPage.Visibility = page == "conveyor" ? Visibility.Visible : Visibility.Collapsed;
        }

        // ===== 外观：亮/暗 + 配色方案（记在 %LocalAppData%\RgvSimulatorGui\appearance.txt，下次打开沿用）=====

        private void OnThemeClick(object sender, RoutedEventArgs e)
        {
            _theme = _theme == ApplicationTheme.Dark ? ApplicationTheme.Light : ApplicationTheme.Dark;
            ApplyAppearance();
            SaveAppearance();
        }

        /// <summary>左下角的色板：每个方案一个圆点</summary>
        private void BuildSchemePicker()
        {
            foreach (var scheme in Schemes)
            {
                var swatch = new RadioButton
                {
                    Style = (Style)FindResource("Swatch"),
                    Tag = scheme,
                    ToolTip = scheme.Name,
                    Background = new SolidColorBrush(scheme.Color ?? ApplicationAccentColorManager.GetColorizationColor())
                };
                if (scheme.Color == null)
                {
                    // 跟随系统：圆点里画个小 A，和固定配色区分开
                    swatch.Content = new System.Windows.Controls.TextBlock { Text = "A", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
                }
                swatch.Checked += (sender, _) =>
                {
                    var picked = (AccentScheme)((FrameworkElement)sender).Tag;
                    if (picked == _scheme)
                    {
                        return;
                    }
                    _scheme = picked;
                    ApplyAppearance();
                    SaveAppearance();
                };
                SchemePanel.Children.Add(swatch);
            }
        }

        private void ApplyAppearance()
        {
            // 先换亮/暗（自定义配色时不让它把强调色改回系统色），再按亮/暗套一遍方案主色——暗色下 WPF-UI 会自动取更亮的色阶
            ApplicationThemeManager.Apply(_theme, WindowBackdropType.Mica, _scheme.Color == null);
            if (_scheme.Color is Color accent)
            {
                ApplicationAccentColorManager.Apply(accent, _theme, false, false);
            }
            var tint = _scheme.Color ?? ApplicationAccentColorManager.SystemAccent;
            TintLayer.Background = new LinearGradientBrush(
                Color.FromArgb(_theme == ApplicationTheme.Dark ? (byte)0x22 : (byte)0x1C, tint.R, tint.G, tint.B),
                Color.FromArgb(_theme == ApplicationTheme.Dark ? (byte)0x0A : (byte)0x08, tint.R, tint.G, tint.B),
                new Point(0, 0), new Point(1, 1));
            ThemeBtn.Icon = new SymbolIcon { Symbol = _theme == ApplicationTheme.Dark ? SymbolRegular.WeatherSunny24 : SymbolRegular.WeatherMoon24 };
            SchemeNameText.Text = $"{_scheme.Name} · {(_theme == ApplicationTheme.Dark ? "暗" : "亮")}";
            foreach (RadioButton swatch in SchemePanel.Children)
            {
                swatch.IsChecked = ReferenceEquals(swatch.Tag, _scheme);
            }
        }

        /// <summary>读上次的外观；没有记录时：翠绿 + 跟随系统的亮/暗</summary>
        private void LoadAppearance()
        {
            _theme = ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
            try
            {
                if (!System.IO.File.Exists(AppearanceFile))
                {
                    return;
                }
                foreach (var line in System.IO.File.ReadAllLines(AppearanceFile))
                {
                    var pair = line.Split('=', 2);
                    if (pair.Length != 2)
                    {
                        continue;
                    }
                    if (pair[0] == "scheme")
                    {
                        _scheme = Schemes.FirstOrDefault(x => x.Key == pair[1].Trim()) ?? _scheme;
                    }
                    else if (pair[0] == "theme" && Enum.TryParse(pair[1].Trim(), out ApplicationTheme theme) && theme is ApplicationTheme.Light or ApplicationTheme.Dark)
                    {
                        _theme = theme;
                    }
                }
            }
            catch
            {
                // 读不到就用默认外观
            }
        }

        private void SaveAppearance()
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(AppearanceFile));
                System.IO.File.WriteAllLines(AppearanceFile, new[] { $"scheme={_scheme.Key}", $"theme={_theme}" });
            }
            catch
            {
                // 外观记不住不影响使用
            }
        }

        /// <summary>WPF-UI 风格的确认框（替代系统 MessageBox）</summary>
        private async Task<bool> ConfirmAsync(string title, string message, string okText)
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Owner = this,
                Title = title,
                Content = message,
                PrimaryButtonText = okText,
                CloseButtonText = "取消"
            };
            return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
        }

        private bool TryGetBindAddress(out IPAddress address)
        {
            var text = (IpBox.Text ?? "").Trim();
            if (IPAddress.TryParse(text.Length > 0 ? text : "0.0.0.0", out address))
            {
                return true;
            }
            Log($"绑定 IP「{IpBox.Text}」不合法（左下角的 Modbus 绑定 IP）");
            return false;
        }

        // ===== 四向车：启停 =====

        private async void OnStartClick(object sender, RoutedEventArgs e)
        {
            if (_cars.Count > 0 &&
                !await ConfirmAsync("按参数重建", $"当前有 {_cars.Count} 台车在跑。启动会先全部停掉，再按参数重建，继续？", "重建"))
            {
                return;
            }
            StartCars();
        }

        private void OnStopClick(object sender, RoutedEventArgs e) => StopCars();

        private void StartCars()
        {
            if (!TryGetBindAddress(out var address))
            {
                return;
            }
            StopCars();

            int count = Math.Clamp(ParseInt(CountBox.Text, 3), 1, 13);
            int firstPort = Math.Clamp(ParseInt(PortBox.Text, 504), 1, 65535);
            byte unit = (byte)Math.Clamp(ParseInt(UnitBox.Text, 1), 1, 247);
            var locations = EnsureLocations(ParseLocationList(LocationsBox.Text), count);

            for (int i = 0; i < count && i < locations.Count; i++)
            {
                AddCar(1001 + i, address, firstPort + i, unit, locations[i]);
            }

            if (_cars.Count > 0)
            {
                CarGrid.SelectedIndex = 0;
            }
            RefreshStatus();
        }

        private void StopCars()
        {
            foreach (var car in _cars)
            {
                car.Slave.Dispose();
            }
            if (_cars.Count > 0)
            {
                Log($"已停止 {_cars.Count} 台车，端口已释放");
            }
            _cars.Clear();
            RefreshStatus();
        }

        /// <summary>起一台车并加进表格；失败返回 null，已启动的车不受影响</summary>
        private CarVm AddCar(int device, IPAddress address, int port, byte unit, ushort location)
        {
            var slave = NewSlave(port, unit, address);
            slave[ModbusTcpSlave.LOCATION] = location;
            slave[ModbusTcpSlave.BATTERY] = 900;
            try
            {
                slave.Start();
            }
            catch (Exception ex)
            {
                Log($"车{device} 绑定 {address}:{port} 失败：{ex.Message}（端口被占用？）");
                slave.Dispose();
                return null;
            }

            var car = new CarVm(device, address.ToString(), port, unit, slave, _track, Log);
            _cars.Add(car);
            Log($"车{device} 已启动：{address}:{port} 站号 {unit} 码值 {location}（{car.CellCode}）");
            RefreshStatus();
            return car;
        }

        /// <summary>单独加一台：车号取最大值 +1，端口按车号顺延，不动已在跑的车</summary>
        private void OnAddCarClick(object sender, RoutedEventArgs e)
        {
            if (!TryGetBindAddress(out var address))
            {
                return;
            }
            int device = _cars.Count == 0 ? 1001 : _cars.Max(c => c.Device) + 1;
            int firstPort = Math.Clamp(ParseInt(PortBox.Text, 504), 1, 65535);
            // 端口跟车号走：以已在跑的最小车号为基准，保持"车号差 = 端口差"；没有车时才用起始端口
            int anchorDevice = _cars.Count == 0 ? 1001 : _cars.Min(c => c.Device);
            int anchorPort = _cars.Count == 0 ? firstPort : _cars.First(c => c.Device == anchorDevice).Port;
            int port = anchorPort + (device - anchorDevice);
            if (_cars.Any(c => c.Port == port))
            {
                port = _cars.Max(c => c.Port) + 1;
                Log($"端口 {anchorPort + (device - anchorDevice)} 已被占用，车{device} 改用 {port}");
            }
            byte unit = (byte)Math.Clamp(ParseInt(UnitBox.Text, 1), 1, 247);
            var locations = EnsureLocations(ParseLocationList(LocationsBox.Text), _cars.Count + 1);
            ushort location = locations[Math.Min(_cars.Count, locations.Count - 1)];

            var car = AddCar(device, address, port, unit, location);
            if (car != null)
            {
                CarGrid.SelectedItem = car;
                CarGrid.ScrollIntoView(car);
                Log($"车号↔端口：车{device} ↔ 端口 {port}（以车{anchorDevice}/{anchorPort} 为基准顺延）");
            }
        }

        /// <summary>停掉并移除选中那台，端口立即释放，其余车照旧</summary>
        private void OnRemoveCarClick(object sender, RoutedEventArgs e)
        {
            var car = SelectedCar;
            if (car == null)
            {
                Log("先在车辆状态里选中一行");
                return;
            }
            int index = _cars.IndexOf(car);
            _cars.Remove(car);
            car.Slave.Dispose();
            Log($"车{car.Device} 已移除，端口 {car.Port} 已释放");
            if (_cars.Count > 0)
            {
                CarGrid.SelectedIndex = Math.Min(index, _cars.Count - 1);
            }
            RefreshStatus();
        }

        private ModbusTcpSlave NewSlave(int port, byte unit, IPAddress address) => new(port, unit, address)
        {
            WorkDuration = Math.Clamp(ParseInt(WorkDurBox.Text, 1500), 0, 60000),
            StepDuration = Math.Clamp(ParseInt(StepBox.Text, 1000), 10, 60000)
        };

        // ===== 四向车：选中车操作 =====

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SelectedCar is CarVm car)
            {
                PositionBox.Text = car.Location.ToString();
            }
            RefreshStatus();
        }

        private void RefreshOpsTitle()
        {
            OpsTitle.Text = SelectedCar is CarVm car
                ? $"车 {car.Device} · 货位 {car.CellCode} · 码值 {car.Location} · {car.Ip}:{car.Port}"
                : "选中车操作（先在上表选中一行）";
            RefreshHfflOpsTitle();
        }

        private void OnSetPositionClick(object sender, RoutedEventArgs e)
        {
            var car = SelectedCar;
            if (car == null)
            {
                Log("先在车辆状态里选中一行");
                return;
            }
            if (!int.TryParse(PositionBox.Text.Trim(), out var value))
            {
                Log("位置码值要填数字");
                return;
            }
            if (!_track.TryGetCellCode(value, out _))
            {
                Log($"码值 {value} 不在轨道表里（货位会显示为 ?），仍按要求写入");
            }
            car.Location = value;
        }

        private void OnPositionKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OnSetPositionClick(sender, e);
            }
        }

        private void OnCarActionClick(object sender, RoutedEventArgs e)
        {
            var car = SelectedCar;
            if (car == null)
            {
                Log("先在车辆状态里选中一行");
                return;
            }
            switch ((sender as Button)?.Tag as string)
            {
                case "work1":
                    car.Work = true;
                    break;
                case "work0":
                    car.Work = false;
                    break;
                case "fault":
                    car.WarnType = 1;
                    car.WarnCode = 101;
                    break;
                case "clear":
                    car.WarnType = 0;
                    car.WarnCode = 0;
                    break;
                case "drop":
                    car.Slave.DropClients();
                    Log($"车{car.Device} 已断开全部连接（模拟 PLC 主动断开）");
                    break;
                case "mute":
                    car.Slave.Mute = true;
                    Log($"车{car.Device} 静默：收到请求不应答");
                    break;
                case "resume":
                    car.Slave.Mute = false;
                    Log($"车{car.Device} 恢复正常应答");
                    break;
                case "dropresp":
                    car.Slave.DropResponses = 1;
                    Log($"车{car.Device} 下一个请求不应答（模拟丢包）");
                    break;
                case "delayresp":
                    car.Slave.DelayNextResponse = 1000;
                    Log($"车{car.Device} 下一个响应延迟 1000ms");
                    break;
            }
        }

        /// <summary>每格 / work 时长：回车或离开输入框时才应用，避免边打字边生效、刷一串日志</summary>
        private void OnDurationCommit(object sender, RoutedEventArgs e) => ApplyDurations();

        private void OnDurationKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ApplyDurations();
            }
        }

        private void ApplyDurations()
        {
            int step = Math.Clamp(ParseInt(StepBox.Text, 1000), 10, 60000);
            int workDuration = Math.Clamp(ParseInt(WorkDurBox.Text, 1500), 0, 60000);
            StepBox.Text = step.ToString();
            WorkDurBox.Text = workDuration.ToString();
            if (_cars.Count == 0 || (step == _appliedStep && workDuration == _appliedWorkDur))
            {
                return;
            }
            foreach (var car in _cars)
            {
                car.Slave.StepDuration = step;
                car.Slave.WorkDuration = workDuration;
            }
            _appliedStep = step;
            _appliedWorkDur = workDuration;
            Log($"时长已应用到 {_cars.Count} 台车：{step}ms/格，{workDuration}ms/work");
        }

        // ===== 输送线（OPC UA 服务器，节点 ns=3;s={连接名}.{标签}）=====

        private async void OnConveyorStartClick(object sender, RoutedEventArgs e) => await StartConveyorsAsync();

        private void OnConveyorStopClick(object sender, RoutedEventArgs e) => StopConveyors();

        private async Task StartConveyorsAsync()
        {
            StopConveyors();
            List<ConveyorDevice> template;
            if (_importedConveyors != null)
            {
                template = _importedConveyors;
            }
            else
            {
                int startId = Math.Clamp(ParseInt(ConveyorStartIdBox.Text, 12001), 1, 999999);
                int count = Math.Clamp(ParseInt(ConveyorCountBox.Text, 23), 1, 500);
                int offset = Math.Max(0, ParseInt(ConveyorOffsetBox.Text, 0));
                int db = Math.Clamp(ParseInt(ConveyorDbBox.Text, 1), 1, 999);
                string server = (ConveyorServerBox.Text ?? "").Trim();
                if (server.Length == 0)
                {
                    Log("连接名不能为空（对应 appsettings 的 Connections:OpcUa:{名字}）");
                    return;
                }
                template = BuildConveyors(server, startId, count, offset, db);
            }
            // 每次启动都用新对象：运行态（状态/指令号/写次数）从零开始，导入的设备表本身不被改动
            _conveyorDevices.AddRange(template.Select(t => new ConveyorDevice
            {
                DeviceId = t.DeviceId,
                Server = t.Server,
                ReadTag = t.ReadTag,
                WriteTag = t.WriteTag,
                Next = t.Next
            }));

            int port = Math.Clamp(ParseInt(ConveyorPortBox.Text, 49320), 1, 65535);
            string pkiRoot = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RgvSimulatorGui", "pki");
            var config = ConveyorOpcServer.BuildConfiguration(port, pkiRoot);
            ConveyorStartBtn.IsEnabled = false;
            if (!await TryStartConveyorServerAsync(config, pkiRoot))
            {
                return;
            }
            // RCS 侧把命名空间索引硬编码成 3，节点不落在 ns=3 就是 BadNodeIdUnknown；没落到就重启一次再试
            if (_conveyorServer.NodeManager.ConveyorNamespaceIndex != 3)
            {
                int bad = _conveyorServer.NodeManager.ConveyorNamespaceIndex;
                Log($"命名空间索引落在 ns={bad}（RCS 要 3），重启一次 OPC 服务器再试……");
                try { _conveyorServer?.Stop(); } catch { }
                _conveyorServer = null;
                if (!await TryStartConveyorServerAsync(config, pkiRoot))
                {
                    return;
                }
            }

            _conveyors.Clear();
            foreach (var device in _conveyorDevices)
            {
                _conveyors.Add(new ConveyorVm(device, Log,
                    d => _conveyorServer?.NodeManager?.PushReadValue(d),
                    (d, bytes) => HandleConveyorCommand(d, bytes)));
                _conveyorServer.NodeManager.PushReadValue(device);
            }
            int ns = _conveyorServer.NodeManager.ConveyorNamespaceIndex;
            string endpoint = $"opc.tcp://127.0.0.1:{port}";
            _conveyorEndpoint = endpoint;
            _conveyorNamespace = ns;
            ConveyorEndpointText.Text = $"运行中 · {endpoint} · ns={ns} · 会话 {_conveyorServer.SessionCount}";
            Log($"输送线已启动：{endpoint}，{_conveyors.Count} 台，命名空间索引 ns={ns}");
            // RCS 侧是按设备表里的 server 名去取地址、再拼 ns=3;s={server}.{readTag} 订阅的，这里把该填的东西直白打出来
            var first = _conveyorDevices[0];
            Log($"RCS 的 appsettings.json 要加：Connections:OpcUa:{first.Server} = {endpoint}（设备表里这些设备的 server 参数也必须是 {first.Server}）");
            Log($"RCS 订阅的节点：ns={ns};s={first.Server}.{first.ReadTag}（写是 ns={ns};s={first.Server}.{first.WriteTag}）");
            Log($"命名空间表：{_conveyorServer.NodeManager.DescribeNamespaces()}");
            if (ns != 3)
            {
                Log($"注意：命名空间索引不是 3，RCS 侧 OPC_NAMESPACE_INDEX 对不上，取节点会报 BadNodeIdUnknown。点「停止」再「启动」一次通常能落到 3");
            }
            RefreshStatus();
        }

        /// <summary>起一次 OPC 服务器；失败时把日志和运行态收拾干净，返回 false</summary>
        private async Task<bool> TryStartConveyorServerAsync(Opc.Ua.ApplicationConfiguration config, string pkiRoot)
        {
            int port = Math.Clamp(ParseInt(ConveyorPortBox.Text, 49320), 1, 65535);
            try
            {
                var app = new ApplicationInstance
                {
                    ApplicationName = config.ApplicationName,
                    ApplicationType = Opc.Ua.ApplicationType.Server,
                    ApplicationConfiguration = config
                };
                if (!await app.CheckApplicationInstanceCertificate(false, 2048))
                {
                    Log("OPC 应用证书创建失败，输送线未启动");
                    RefreshStatus();
                    return false;
                }
                if (config.ApplicationUri != ConveyorOpcServer.ApplicationUriValue)
                {
                    // 本机 pki 里可能还躺着更早版本生成的证书（那时的应用 URI 就是输送线命名空间），
                    // ApplicationInstance 会把配置里的 ApplicationUri 同步成证书里的值；一旦和节点命名空间同名，
                    // 命名空间表 1 位就被顶掉，节点落到 ns=1，RCS 那边就取不到了。这里把旧证书删掉重生成。
                    Log($"提示：证书里的应用 URI 是 {config.ApplicationUri}，与配置的 {ConveyorOpcServer.ApplicationUriValue} 不一致（旧版本留下的），删掉旧证书重新生成");
                    config.ApplicationUri = ConveyorOpcServer.ApplicationUriValue;
                    DeleteOwnCertificateFiles(pkiRoot);
                    await app.CheckApplicationInstanceCertificate(false, 2048);
                    config.ApplicationUri = ConveyorOpcServer.ApplicationUriValue;
                }
                config.CertificateValidator.CertificateValidation += (_, args) => args.Accept = true;
                _conveyorServer = new ConveyorOpcServer(_conveyorDevices);
                _conveyorServer.Start(config);
                _conveyorServer.NodeManager.CommandReceived += OnConveyorCommand;
                return true;
            }
            catch (Exception ex)
            {
                // 栈经常只给一句 "Unexpected error starting application"，真正的原因在 InnerException 里
                string hint = ex.InnerException?.Message ?? "";
                if (ex.InnerException?.InnerException != null)
                {
                    hint += " → " + ex.InnerException.InnerException.Message;
                }
                Log($"OPC 服务器启动失败：{ex.Message}（端口 {port} 被占用？）" + (hint.Length > 0 ? $"，原因：{hint}" : ""));
                try { _conveyorServer?.Stop(); } catch { }
                _conveyorServer = null;
                _conveyorDevices.Clear();
                _conveyors.Clear();
                RefreshStatus();
                return false;
            }
        }

        /// <summary>删掉输送线自己那套自签名证书（只动 <c>pki\own</c> 下本模拟器的两个文件），下次会按当前配置重建</summary>
        private static void DeleteOwnCertificateFiles(string pkiRoot)
        {
            foreach (var dir in new[] { System.IO.Path.Combine(pkiRoot, "own", "certs"), System.IO.Path.Combine(pkiRoot, "own", "private") })
            {
                if (!System.IO.Directory.Exists(dir))
                {
                    continue;
                }
                foreach (var file in System.IO.Directory.GetFiles(dir, "RgvSimulatorGuiConveyor*"))
                {
                    try { System.IO.File.Delete(file); } catch { }
                }
            }
        }

        /// <summary>停 OPC 服务器、清运行态；导入的设备表保留（下次启动还用它）</summary>
        private void StopConveyors()
        {
            bool wasRunning = _conveyorServer != null;
            if (_conveyorServer != null)
            {
                try { _conveyorServer.NodeManager.CommandReceived -= OnConveyorCommand; } catch { }
                try { _conveyorServer.Stop(); } catch { }
                _conveyorServer = null;
            }
            _hoistLink.Reset();                 // 输送线没了，进行中的交接作废，释放提升机
            _conveyors.Clear();
            _conveyorDevices.Clear();
            ConveyorEndpointText.Text = "输送线设备";
            if (wasRunning)
            {
                Log("输送线 OPC 服务器已停止");
            }
            ShowImportedPreview();
            RefreshStatus();
        }

        /// <summary>没在跑时，把导入的设备表先列出来给人核对</summary>
        private void ShowImportedPreview()
        {
            if (_conveyorServer != null || _importedConveyors == null)
            {
                return;
            }
            _conveyors.Clear();
            foreach (var device in _importedConveyors)
            {
                _conveyors.Add(new ConveyorVm(device, Log));
            }
        }

        /// <summary>WCS 往写节点写命令（OPC 服务器线程）：转回界面线程再处理，避免和定时器同时改设备状态</summary>
        private void OnConveyorCommand(ConveyorDevice device, byte[] frame)
        {
            var copy = (byte[])frame.Clone();
            Dispatcher.BeginInvoke(new Action(() => HandleConveyorCommand(device, copy)));
        }

        /// <summary>解析 10 字节报文并开始"运行"</summary>
        private void HandleConveyorCommand(ConveyorDevice device, byte[] frame)
        {
            if (!_conveyorDevices.Contains(device))
            {
                return; // 服务器已停 / 已重启，旧设备的命令丢掉
            }
            device.Writes++;
            device.LastWrite = string.Join(",", frame);
            device.LastWrittenBytes = (byte[])frame.Clone();
            _conveyorServer?.NodeManager?.PushWriteValue(device);   // 手工改的写区也要反映到节点上
            if (frame.Length == 0 || frame[0] == 2)
            {
                HandleConveyorAck(device);
                return;
            }
            if (frame.Length < 8)
            {
                Log($"[输送线{device.DeviceId}] 报文只有 {frame.Length} 字节（应为 10），忽略：{device.LastWrite}");
                return;
            }
            // 报文口径见 ConveyorAbstractDriver.EncodeOutput：目标设备是 4 字节大端（data[4] 是高字节）
            int type = frame[1];
            int instruction = (frame[2] << 8) | frame[3];   // 指令号大端（data[2] 是高字节），和 RCS 的取法一致
            int destination = (frame[4] << 24) | (frame[5] << 16) | (frame[6] << 8) | frame[7];
            var rejected = StartConveyorRun(device, type, instruction, destination);
            if (rejected != null)
            {
                Log($"[输送线{device.DeviceId}] 收到 WCS 命令：{ConveyorTypeText(type)}、指令号 {instruction}、目标 {destination}，不接收：{rejected}");
                return;
            }
            Log($"[输送线{device.DeviceId}] 收到 WCS 命令：{ConveyorTypeText(type)}、指令号 {instruction}、目标 " +
                $"{(destination > 0 ? destination.ToString() : "未指定")}，本机{(device.HasCargo ? "有货 → 运行中" : "没货 → 等货送来")}");
            _conveyorServer?.NodeManager?.PushReadValue(device);
        }

        /// <summary>
        /// WCS 应答（标志位 2）：报完成后 RCS 写应答，PLC 收到后清掉这次任务在 DB 区的内容——
        /// 反馈区（DB2）的状态、指令号、方向、目标清零，写区（DB1）的命令清零；有货位按光电保持（货还在这节上）。
        /// 不在完成状态时收到的应答（重复应答等）只记日志，不动数据。
        /// </summary>
        private void HandleConveyorAck(ConveyorDevice device)
        {
            if (device.State != 2)
            {
                Log($"[输送线{device.DeviceId}] 收到 WCS 应答帧（当前不是完成状态，不处理）");
                return;
            }
            var instruction = device.Instruction;
            device.State = 0;
            device.Instruction = 0;
            device.Action = 0;
            device.Destination = 0;
            device.BusyTicks = 0;
            device.HoldTicks = 0;
            device.WaitingDownstream = false;
            device.Note = null;
            _conveyorServer?.NodeManager?.PushReadValue(device);
            device.LastWrittenBytes = null;
            device.LastWrite = "-";
            _conveyorServer?.NodeManager?.ClearWriteArea(device);
            Log($"[输送线{device.DeviceId}] 收到 WCS 应答 → 清除 DB 区（指令号 {instruction} 的状态、指令号与写区命令），回空闲；有货位保持{(device.HasCargo ? "（有货）" : "")}");
        }

        private static string ConveyorTypeText(int type) => type switch
        {
            1 => "正转 1201",
            2 => "反转 1202",
            6 => "送出 1206",
            _ => $"类型 {type}"
        };

        private void OnConveyorActionClick(object sender, RoutedEventArgs e)
        {
            string tag = (sender as Button)?.Tag as string;
            var vm = SelectedConveyor;
            if (vm == null || _conveyorServer == null || !_conveyorDevices.Contains(vm.Device))
            {
                Log("先启动输送线，并在表里选中一行");
                return;
            }
            var device = vm.Device;
            switch (tag)
            {
                case "fault":
                    device.State = 30;
                    device.BusyTicks = 0;
                    device.HoldTicks = 0;
                    device.WaitingDownstream = false;
                    Log($"输送线{device.DeviceId} 置故障（状态 30）");
                    break;
                case "clear":
                    device.State = 0;
                    device.Action = 0;
                    device.BusyTicks = 0;
                    device.HoldTicks = 0;
                    device.WaitingDownstream = false;
                    Log($"输送线{device.DeviceId} 清故障，回到空闲");
                    break;
                case "load":
                    device.HasCargo = !device.HasCargo;
                    device.CargoDirty = true;
                    Log($"输送线{device.DeviceId} 有货 → {(device.HasCargo ? "有" : "无")}");
                    break;
            }
            _conveyorServer.NodeManager.PushReadValue(device);
            vm.Refresh();
        }

        /// <summary>
        /// 清一台设备的 DB 区，两块可以分开清：
        /// DB2（反馈区 PLC→WCS）= 状态/指令号/有货/方向/目标；DB1（写区 WCS→PLC）= WCS 写进来的命令原文与写次数。
        /// </summary>
        private void ClearConveyorDb(ConveyorDevice device, bool writeArea, bool feedbackArea)
        {
            if (feedbackArea)
            {
                device.State = 0;
                device.Instruction = 0;
                device.Action = 0;
                device.HasCargo = false;
                device.CargoDirty = true;
                device.Destination = 0;
                device.BusyTicks = 0;
                device.HoldTicks = 0;
                device.WaitingDownstream = false;
                device.Note = null;
                _conveyorServer?.NodeManager?.PushReadValue(device);
            }
            if (writeArea)
            {
                device.Writes = 0;
                device.LastWrite = "-";
                device.LastWrittenBytes = null;
                _conveyorServer?.NodeManager?.ClearWriteArea(device);
            }
        }

        /// <summary>「清除」按钮：按下拉框选的块清掉表里选中那一节的 DB1（写区）或 DB2（反馈区）</summary>
        private void OnConveyorClearClick(object sender, RoutedEventArgs e)
        {
            if (_conveyorServer == null || _conveyorDevices.Count == 0)
            {
                Log("先启动输送线");
                return;
            }
            var vm = SelectedConveyor;
            if (vm == null || !_conveyorDevices.Contains(vm.Device))
            {
                Log("先启动输送线，并在表里选中一行");
                return;
            }
            string mode = (ClearTargetBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "db1";
            ClearConveyorDb(vm.Device, writeArea: mode == "db1", feedbackArea: mode == "db2");
            vm.Refresh();
            Log($"输送线{vm.Device.DeviceId}：已清除{(mode == "db1" ? "DB1（写区 WCS→PLC）" : "DB2（反馈区 PLC→WCS）")}");
        }

        private static List<ConveyorDevice> BuildConveyors(string server, int startId, int count, int offset, int db)
        {
            var list = new List<ConveyorDevice>();
            for (int i = 0; i < count; i++)
            {
                int bytes = offset + i * 10;
                list.Add(new ConveyorDevice
                {
                    DeviceId = startId + i,
                    Server = server,
                    ReadTag = $"DB{db + 1}.{bytes},b,10",
                    WriteTag = $"DB{db}.{bytes},b,10",
                    Next = i + 1 < count ? startId + i + 1 : 0   // 默认按设备号顺序串起来，表里可以改
                });
            }
            return list;
        }

        /// <summary>从剪贴板导入（Excel 里复制几列直接粘）：设备号[,连接名,读标签,写标签]</summary>
        private async void OnConveyorPasteClick(object sender, RoutedEventArgs e)
        {
            string text;
            try { text = Clipboard.GetText(); }
            catch { text = null; }
            if (string.IsNullOrWhiteSpace(text))
            {
                Log("剪贴板里没有文本。先在 Excel 里复制这几列：设备号 / 连接名 / 读标签 / 写标签（后三列可省）");
                return;
            }

            string defaultServer = (ConveyorServerBox.Text ?? "").Trim();
            int offset = Math.Max(0, ParseInt(ConveyorOffsetBox.Text, 0));
            int db = Math.Clamp(ParseInt(ConveyorDbBox.Text, 1), 1, 999);
            var imported = new List<ConveyorDevice>();
            var nodeIds = new HashSet<string>();
            var deviceIds = new HashSet<int>();
            int index = 0, skipped = 0;
            int sharedNodes = 0;
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // Excel 复制出来是 Tab 分隔；标签本身带逗号（DB2.0,b,10），所以有 Tab 时只按 Tab 拆
                var separators = line.Contains('\t') ? new[] { '\t' } : new[] { ',', ';' };
                var parts = line.Split(separators);
                for (int i = 0; i < parts.Length; i++)
                {
                    parts[i] = parts[i].Trim().Trim('"');   // 从 Excel/CSV 里带出来的引号（标签里有逗号时会被加引号）
                }
                if (parts.Length == 0 || !int.TryParse(parts[0].Trim(), out var id))
                {
                    continue;   // 跳过表头/空行
                }
                if (!deviceIds.Add(id))
                {
                    Log($"导入跳过设备 {id}：设备号和前面的行重复");
                    skipped++;
                    continue;
                }
                int bytes = offset + index * 10;
                int next = 0;
                if (parts.Length > 4 && int.TryParse(parts[4], out var parsedNext))
                {
                    next = parsedNext;   // 第 5 列可选：下一节设备号
                }
                var device = new ConveyorDevice
                {
                    DeviceId = id,
                    Server = parts.Length > 1 && parts[1].Trim().Length > 0 ? parts[1].Trim() : defaultServer,
                    ReadTag = parts.Length > 2 && parts[2].Trim().Length > 0 ? parts[2].Trim() : $"DB{db + 1}.{bytes},b,10",
                    WriteTag = parts.Length > 3 && parts[3].Trim().Length > 0 ? parts[3].Trim() : $"DB{db}.{bytes},b,10",
                    Next = next
                };
                // 共用节点是允许的（几台设备读同一个标签、或读写用同一个标签，比如扫描器），
                // 节点管理器会按唯一节点 id 建节点，这里只统计一下
                int nodesBefore = nodeIds.Count;
                nodeIds.Add(device.ReadNodeId);
                if (device.WriteNodeId != device.ReadNodeId)
                {
                    nodeIds.Add(device.WriteNodeId);
                }
                if (nodeIds.Count - nodesBefore < (device.WriteNodeId == device.ReadNodeId ? 1 : 2))
                {
                    sharedNodes++;
                }
                imported.Add(device);
                index++;
            }
            if (imported.Count == 0)
            {
                Log("没解析出设备行（第一列要是设备号）");
                return;
            }
            // 没给「下一节」的，按导入顺序串起来（第一行 → 第二行 → …），表里可以随时改
            for (int i = 0; i < imported.Count; i++)
            {
                if (imported[i].Next == 0 && i + 1 < imported.Count)
                {
                    imported[i].Next = imported[i + 1].DeviceId;
                }
            }

            bool wasRunning = _conveyorServer != null;
            _importedConveyors = imported;
            Log($"已导入 {imported.Count} 台输送线" + (skipped > 0 ? $"（跳过 {skipped} 行重复）" : "") +
                (sharedNodes > 0 ? $"（{sharedNodes} 台和前面共用节点，正常）" : "") +
                (wasRunning ? "，正在用新表重启 OPC 服务器……" : "，点「启动」开 OPC 服务器"));
            if (wasRunning)
            {
                await StartConveyorsAsync();
            }
            else
            {
                ShowImportedPreview();
                RefreshStatus();
            }
        }

        private void OnConveyorClearImportClick(object sender, RoutedEventArgs e)
        {
            _importedConveyors = null;
            if (_conveyorServer == null)
            {
                _conveyors.Clear();
            }
            Log("已丢掉导入的设备表，下次启动按参数生成" + (_conveyorServer != null ? "（当前运行中的服务器不受影响）" : ""));
            RefreshStatus();
        }

        private void OnConveyorExportClick(object sender, RoutedEventArgs e)
        {
            var rows = _conveyors.Count > 0
                ? _conveyors.Select(vm => vm.Device).ToList()
                : BuildConveyors((ConveyorServerBox.Text ?? "").Trim(),
                    Math.Clamp(ParseInt(ConveyorStartIdBox.Text, 12001), 1, 999999),
                    Math.Clamp(ParseInt(ConveyorCountBox.Text, 23), 1, 500),
                    Math.Max(0, ParseInt(ConveyorOffsetBox.Text, 0)),
                    Math.Clamp(ParseInt(ConveyorDbBox.Text, 1), 1, 999));
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "输送线设备表.csv", Filter = "CSV 文件|*.csv" };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            var text = new System.Text.StringBuilder("设备号,连接名,读标签,写标签" + Environment.NewLine);
            foreach (var device in rows)
            {
                // 标签里本身有逗号（DB2.0,b,10），按 CSV 规则加引号，Excel 打开才不会拆成多列
                text.AppendLine($"{device.DeviceId},{Csv(device.Server)},{Csv(device.ReadTag)},{Csv(device.WriteTag)}");
            }
            try
            {
                System.IO.File.WriteAllText(dialog.FileName, text.ToString(), new System.Text.UTF8Encoding(true));
                Log($"已导出 {rows.Count} 台到 {dialog.FileName}");
            }
            catch (Exception ex)
            {
                Log($"导出失败：{ex.Message}（文件被 Excel 占用？）");
            }

            static string Csv(string value) =>
                value.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
        }

        // ===== 提升机（Modbus，协议 V2.3）=====

        private void OnHoistStartClick(object sender, RoutedEventArgs e) => StartHoists();

        private void OnHoistStopClick(object sender, RoutedEventArgs e) => StopHoists();

        private void StartHoists()
        {
            if (!TryGetBindAddress(out var address))
            {
                return;
            }
            StopHoists();

            int count = Math.Clamp(ParseInt(HoistCountBox.Text, 2), 1, 8);
            int firstPort = Math.Clamp(ParseInt(HoistPortBox.Text, 502), 1, 65535);
            int layers = Math.Clamp(ParseInt(HoistLayerBox.Text, 3), 1, 20);
            int startLayer = Math.Clamp(ParseInt(HoistStartLayerBox.Text, 1), 1, layers);
            byte unit = (byte)Math.Clamp(ParseInt(HoistUnitBox.Text, 1), 1, 247);

            for (int i = 0; i < count; i++)
            {
                int port = firstPort + i;
                int device = 13001 + i;
                // 提升机不走四向车的任务模拟（RgvBehavior 会盯着地址 16 的 update），行为全在 HoistBehavior 里
                var slave = new ModbusTcpSlave(port, unit, address) { RgvBehavior = false };
                try
                {
                    slave.Start();
                }
                catch (Exception ex)
                {
                    Log($"提升机{device} 绑定 {address}:{port} 失败：{ex.Message}（端口被占用？）");
                    slave.Dispose();
                    continue;
                }
                var behavior = new HoistBehavior(slave, device, layers, startLayer)
                {
                    LifeCountEnabled = HoistLifeToggle.IsChecked == true
                };
                _hoists.Add(new HoistVm(device, port, behavior, slave, Log));
                Log($"提升机{device} 已启动：{address}:{port} 站号 {unit}，{layers} 层、当前 {startLayer} 层（写区 {HoistReg.W_LifeCount}、反馈区 {HoistReg.F_LifeCount}）");
            }

            if (_hoists.Count > 0)
            {
                HoistGrid.SelectedIndex = 0;
            }
            if (_hoists.Count > 0)
            {
                // 输送线 PLC 呼叫提升机的对接关系：WCS 给输送线下"起点 → 终点"，两端是同一台提升机的两个对接口时自动交接
                Log("提升机对接口：" + string.Join("；", _hoistLink.Docks.GroupBy(d => d.Hoist)
                    .Select(g => $"{g.Key}：" + string.Join("、", g.Select(d => $"{d.Layer}层{d.Side} {d.Conveyor}")))));
            }
            RefreshStatus();
        }

        private void StopHoists()
        {
            _hoistLink.Reset();                 // 提升机要停了，进行中的交接作废
            foreach (var hoist in _hoists)
            {
                hoist.Slave.Dispose();
            }
            if (_hoists.Count > 0)
            {
                Log($"已停止 {_hoists.Count} 台提升机，端口已释放");
            }
            _hoists.Clear();
            RefreshStatus();
        }

        private void OnHoistActionClick(object sender, RoutedEventArgs e)
        {
            var hoist = SelectedHoist;
            if (hoist == null)
            {
                Log("先在提升机表里选中一行");
                return;
            }
            switch ((sender as Button)?.Tag as string)
            {
                case "reset":
                    hoist.Slave[HoistReg.W_SysRest] = 1;
                    Log($"提升机{hoist.Device} 复位（模拟 WCS 写 sysRest=1：清报警并解除暂停）");
                    break;
                case "pause":
                    hoist.Slave[HoistReg.W_SysStop] = 1;
                    Log($"提升机{hoist.Device} 暂停");
                    break;
                case "resume":
                    hoist.Slave[HoistReg.W_SysStop] = 2;
                    Log($"提升机{hoist.Device} 启动信号");
                    break;
            }
        }

        private void OnHoistLifeChanged(object sender, RoutedEventArgs e)
        {
            if (LogBox == null)
            {
                return;
            }
            bool on = HoistLifeToggle.IsChecked == true;
            foreach (var hoist in _hoists)
            {
                hoist.Behavior.LifeCountEnabled = on;
            }
            Log($"提升机生命计数：{(on ? "开" : "关（RCS 会在 lifeTimeout 之后判为离线）")}");
        }

        // ===== 工具 =====

        /// <summary>绑定 IP 的候选：0.0.0.0（监听所有网卡）、127.0.0.1（只允许本机）、以及本机各网卡地址</summary>
        private static List<string> BindableAddresses()
        {
            var list = new List<string> { "0.0.0.0", "127.0.0.1" };
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    {
                        continue;
                    }
                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        var ip = unicast.Address;
                        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !list.Contains(ip.ToString()))
                        {
                            list.Add(ip.ToString());
                        }
                    }
                }
            }
            catch
            {
                // 取不到网卡列表就只用前两个候选
            }
            return list;
        }

        private static int ParseInt(string text, int fallback) =>
            int.TryParse((text ?? "").Trim(), out var value) ? value : fallback;

        /// <summary>解析"初始码值"输入框：逗号/空格/分号/Tab 分隔，空则默认 8267</summary>
        private static List<ushort> ParseLocationList(string text)
        {
            var values = new List<ushort>();
            foreach (var part in (text ?? "").Split(new[] { ',', '，', ' ', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (ushort.TryParse(part.Trim(), out var value))
                {
                    values.Add(value);
                }
            }
            if (values.Count == 0)
            {
                values.Add(8267);
            }
            return values;
        }

        /// <summary>
        /// 码值列表不足 count 个时自动补齐：从最后一个码值往后 +1，跳过轨道上不存在的码；
        /// 补过就把新列表回写到输入框并记日志（车数改大、点"增加一台" 都走这里）。
        /// </summary>
        private List<ushort> EnsureLocations(List<ushort> values, int count)
        {
            var added = new List<ushort>();
            while (values.Count < count)
            {
                int next = values[^1] + 1;
                while (next <= ushort.MaxValue && !_track.TryGetCellCode(next, out _))
                {
                    next++;
                }
                if (next > ushort.MaxValue)
                {
                    break;
                }
                values.Add((ushort)next);
                added.Add((ushort)next);
            }
            if (added.Count > 0)
            {
                LocationsBox.Text = string.Join(",", values);
                Log($"码值不够 {count} 个，已自动补：{string.Join("、", added)}");
            }
            return values;
        }

        /// <summary>车数改大时立刻把码值补齐（运行中不动，避免打乱在跑的车）</summary>
        private void OnCountChanged(object sender, TextChangedEventArgs e)
        {
            if (LocationsBox == null || LogBox == null || _cars.Count > 0)
            {
                return;
            }
            int count = Math.Clamp(ParseInt(CountBox.Text, 3), 1, 13);
            EnsureLocations(ParseLocationList(LocationsBox.Text), count);
        }

        private void OnClearLogClick(object sender, RoutedEventArgs e) => LogBox.Clear();

        private void OnCopyLogClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(LogBox.Text);
            }
            catch (Exception ex)
            {
                Log($"复制日志失败：{ex.Message}");
            }
        }

        private void Log(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => Log(message)));
                return;
            }
            string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
            LogBox.AppendText(line + Environment.NewLine);
            AppendLogFile(line);   // 同一行也写进日志文件，完整日志去文件里翻
            if (LogBox.Text.Length > 300_000)
            {
                LogBox.Text = LogBox.Text[^200_000..];
            }
            if (AutoScrollToggle.IsChecked == true)
            {
                LogBox.ScrollToEnd();
            }
        }

        /// <summary>同一行也写进日志文件，失败不影响界面</summary>
        private static void AppendLogFile(string line)
        {
            try
            {
                System.IO.Directory.CreateDirectory(LogFolder);
                System.IO.File.AppendAllText(LogFile, line + Environment.NewLine);
            }
            catch
            {
                // 写文件失败（权限/占用）不影响模拟器使用
            }
        }

    }
}
