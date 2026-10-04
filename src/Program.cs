using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using Microsoft.Win32;

namespace AeroProxy
{
    public class ProxyItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public bool Enabled { get; set; }
        public string Mode { get; set; }

        public ProxyItem()
        {
            Enabled = true;
            Mode = "smart";
        }
    }

    public class ProxyConfig
    {
        public string Protocol { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string BypassList { get; set; }
        public bool AutoStartApp { get; set; }
        public bool AutoStartProxy { get; set; }
        public List<ProxyItem> Apps { get; set; }

        public ProxyConfig()
        {
            Protocol = "http";
            Host = "127.0.0.1";
            Port = 10808;
            BypassList = "localhost;127.0.0.1;*.local;192.168.*;10.*";
            AutoStartApp = false;
            AutoStartProxy = false;
            Apps = new List<ProxyItem>();
        }
    }

    public static class ConfigStore
    {
        private static readonly string ConfigDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aeroproxy");
        private static readonly string ConfigFile = System.IO.Path.Combine(ConfigDir, "config.json");
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static ProxyConfig Load()
        {
            try
            {
                if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
                if (File.Exists(ConfigFile))
                {
                    string json = File.ReadAllText(ConfigFile, Encoding.UTF8);
                    var cfg = Serializer.Deserialize<ProxyConfig>(json);
                    if (cfg != null && cfg.Apps != null && cfg.Apps.Count > 0)
                        return cfg;
                }
            }
            catch { }

            var def = new ProxyConfig();
            string antiPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "antigravity", "Antigravity.exe");

            def.Apps.Add(new ProxyItem
            {
                Id = "antigravity-default",
                Name = "Antigravity",
                Path = antiPath,
                Enabled = true,
                Mode = "smart"
            });

            Save(def);
            return def;
        }

        public static void Save(ProxyConfig cfg)
        {
            try
            {
                if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
                string json = Serializer.Serialize(cfg);
                File.WriteAllText(ConfigFile, json, Encoding.UTF8);
            }
            catch { }
        }
    }

    public static class StartupHelper
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "AeroProxy";

        public static void SyncStartup(bool enable, bool autoProxy)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;
                    if (enable)
                    {
                        string exePath = Process.GetCurrentProcess().MainModule.FileName;
                        string args = autoProxy ? " --auto-proxy" : "";
                        key.SetValue(AppName, string.Format("\"{0}\"{1}", exePath, args));
                    }
                    else
                    {
                        key.DeleteValue(AppName, false);
                    }
                }
            }
            catch { }
        }
    }

    public static class ProxyEngine
    {
        public static bool IsElectronOrChromium(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            string lower = exePath.ToLower();
            if (lower.Contains("antigravity") || lower.Contains("electron") || lower.Contains("code") ||
                lower.Contains("cursor") || lower.Contains("slack") || lower.Contains("chrome") || lower.Contains("edge"))
                return true;

            string dir = System.IO.Path.GetDirectoryName(exePath);
            if (!string.IsNullOrEmpty(dir) && File.Exists(System.IO.Path.Combine(dir, "resources", "app.asar")))
                return true;

            return false;
        }

        public static int LaunchWithProxy(ProxyItem item, ProxyConfig cfg)
        {
            if (!File.Exists(item.Path))
                throw new FileNotFoundException("未找到可执行文件: " + item.Path);

            string proxyUrl = string.Format("{0}://{1}:{2}", cfg.Protocol, cfg.Host, cfg.Port);
            var psi = new ProcessStartInfo();
            psi.FileName = item.Path;
            psi.WorkingDirectory = System.IO.Path.GetDirectoryName(item.Path);
            psi.UseShellExecute = false;

            // Set HTTP & HTTPS proxies for Node.js / CLI tools
            psi.EnvironmentVariables["HTTP_PROXY"] = proxyUrl;
            psi.EnvironmentVariables["HTTPS_PROXY"] = proxyUrl;
            psi.EnvironmentVariables["http_proxy"] = proxyUrl;
            psi.EnvironmentVariables["https_proxy"] = proxyUrl;

            // Strict localhost bypass for Go/Node/Python to protect internal gRPC and UI servers
            string noProxy = "127.0.0.1,localhost,::1";
            psi.EnvironmentVariables["NO_PROXY"] = noProxy;
            psi.EnvironmentVariables["no_proxy"] = noProxy;
            psi.EnvironmentVariables["NODE_TLS_REJECT_UNAUTHORIZED"] = "1";

            // DO NOT set ALL_PROXY! ALL_PROXY routes internal gRPC loopback sockets through the proxy!

            var sb = new StringBuilder();
            if (item.Mode == "smart" || IsElectronOrChromium(item.Path))
            {
                // In Chromium/Electron:
                // 1. Never put quotes around the proxy-server value (causes URI scheme parse errors)
                // 2. bypass-list MUST contain <-loopback>;127.0.0.1;localhost;<local>
                //    so that internal electron webviews (e.g. Antigravity at https://127.0.0.1:port)
                //    will never be hijacked by proxy!
                sb.AppendFormat("--proxy-server={0} --proxy-bypass-list=\"<-loopback>;127.0.0.1;localhost;::1;<local>\"", proxyUrl);
            }
            psi.Arguments = sb.ToString();

            Process proc = Process.Start(psi);
            return proc != null ? proc.Id : 0;
        }
    }

    public class MainWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private ProxyConfig _config;
        private StackPanel _appListPanel;
        private TextBlock _statusPillText;
        private TextBlock _listCountText;
        private System.Windows.Forms.NotifyIcon _notifyIcon;

        // Pages
        private Grid _pageApps;
        private Grid _pageSettings;
        private Border _tabBtnApps;
        private Border _tabBtnSettings;
        private TextBlock _tabTextApps;
        private TextBlock _tabTextSettings;

        // Settings Controls
        private TextBox _txtHost;
        private TextBox _txtPort;
        private CheckBox _chkAutoStart;
        private RadioButton _rbAutoStartAppOnly;
        private RadioButton _rbAutoStartWithProxy;

        // Apple Palette
        private static readonly SolidColorBrush BrushWindowBg = new SolidColorBrush(Color.FromRgb(20, 20, 22));
        private static readonly SolidColorBrush BrushHeaderBg = new SolidColorBrush(Color.FromRgb(26, 26, 29));
        private static readonly SolidColorBrush BrushCardBg = new SolidColorBrush(Color.FromRgb(30, 30, 34));
        private static readonly SolidColorBrush BrushBorder = new SolidColorBrush(Color.FromRgb(46, 46, 52));
        private static readonly SolidColorBrush BrushAppleBlue = new SolidColorBrush(Color.FromRgb(10, 132, 255));
        private static readonly SolidColorBrush BrushAppleGreen = new SolidColorBrush(Color.FromRgb(48, 209, 88));
        private static readonly SolidColorBrush BrushTextPrimary = new SolidColorBrush(Color.FromRgb(255, 255, 255));
        private static readonly SolidColorBrush BrushTextSecondary = new SolidColorBrush(Color.FromRgb(161, 161, 170));
        private static readonly SolidColorBrush BrushTextTertiary = new SolidColorBrush(Color.FromRgb(113, 113, 122));
        private static readonly SolidColorBrush BrushBtnHover = new SolidColorBrush(Color.FromRgb(44, 44, 50));
        private static readonly SolidColorBrush BrushCloseHover = new SolidColorBrush(Color.FromRgb(239, 68, 68));
        private static readonly SolidColorBrush BrushSegmentTrack = new SolidColorBrush(Color.FromRgb(34, 34, 38));
        private static readonly SolidColorBrush BrushSegmentActive = new SolidColorBrush(Color.FromRgb(50, 50, 56));

        public MainWindow()
        {
            _config = ConfigStore.Load();

            Title = "AeroProxy";
            Width = 650;
            Height = 580;
            MinWidth = 540;
            MinHeight = 440;
            WindowStyle = WindowStyle.None;
            Background = BrushWindowBg;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
            if (!File.Exists(iconPath))
            {
                iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "icon.ico");
            }
            if (File.Exists(iconPath))
            {
                try
                {
                    Icon = BitmapFrame.Create(new Uri(iconPath));
                }
                catch { }
            }

            var chrome = new WindowChrome
            {
                CaptionHeight = 48,
                CornerRadius = new CornerRadius(10),
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(6)
            };
            WindowChrome.SetWindowChrome(this, chrome);

            SourceInitialized += delegate
            {
                try
                {
                    var handle = new WindowInteropHelper(this).Handle;
                    int preference = 2; // DWMWCP_ROUND
                    DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
                }
                catch { }
            };

            AllowDrop = true;
            Drop += delegate(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0)
                    {
                        foreach (var f in files)
                        {
                            if (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                            {
                                AddAppByPath(f);
                            }
                        }
                    }
                }
            };

            // Press ESC to hide to tray
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) Hide();
            };

            BuildUI();
            RefreshAppList();
            InitSystemTray();
        }

        private void InitSystemTray()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            _notifyIcon.Text = "AeroProxy - 免 TUN 代理分流中枢";

            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
            if (!File.Exists(iconPath))
            {
                iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "icon.ico");
            }

            if (File.Exists(iconPath))
            {
                try
                {
                    _notifyIcon.Icon = new System.Drawing.Icon(iconPath);
                }
                catch
                {
                    _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
                }
            }
            else
            {
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }

            _notifyIcon.Visible = true;

            // Click tray to restore window
            _notifyIcon.MouseClick += delegate(object s, System.Windows.Forms.MouseEventArgs e)
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    RestoreFromTray();
                }
            };

            // Tray Context Menu
            var menu = new System.Windows.Forms.ContextMenu();

            var itemOpen = new System.Windows.Forms.MenuItem("显示主界面");
            itemOpen.Click += delegate { RestoreFromTray(); };
            menu.MenuItems.Add(itemOpen);

            menu.MenuItems.Add(new System.Windows.Forms.MenuItem("-"));

            var itemExit = new System.Windows.Forms.MenuItem("退出 AeroProxy");
            itemExit.Click += delegate { FullExit(); };
            menu.MenuItems.Add(itemExit);

            _notifyIcon.ContextMenu = menu;
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void FullExit()
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            Application.Current.Shutdown();
        }

        protected override void OnClosed(EventArgs e)
        {
            FullExit();
            base.OnClosed(e);
        }

        private void BuildUI()
        {
            var rootBorder = new Border
            {
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1),
                Background = BrushWindowBg,
                CornerRadius = new CornerRadius(10),
                ClipToBounds = true
            };

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) }); // TitleBar & Tabs (Height: 48px)
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // View Container
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Footer

            // ---------------------------------------------------------
            // 1. TitleBar with Generous Segmented Tabs & Window Controls
            // ---------------------------------------------------------
            var titleBarGrid = new Grid { Background = BrushHeaderBg };
            titleBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Brand
            titleBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Segmented Tabs
            titleBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // - and X

            // Brand Logo & Title
            var brandStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 16, 0)
            };
            var emblemBorder = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(6),
                Background = new LinearGradientBrush(Color.FromRgb(10, 132, 255), Color.FromRgb(94, 92, 230), 45),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var emblemGrid = new Grid();
            var wingPath = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M3,10 L17,4 L11,16 L10,11 Z"),
                Fill = Brushes.White,
                Width = 13,
                Height = 13,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            emblemGrid.Children.Add(wingPath);
            emblemBorder.Child = emblemGrid;
            brandStack.Children.Add(emblemBorder);

            var titleText = new TextBlock
            {
                Text = "AeroProxy",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = BrushTextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            brandStack.Children.Add(titleText);
            Grid.SetColumn(brandStack, 0);
            titleBarGrid.Children.Add(brandStack);

            // Generous Apple Style Segmented Tab Controller
            var segmentTrack = new Border
            {
                Background = BrushSegmentTrack,
                CornerRadius = new CornerRadius(7),
                BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            WindowChrome.SetIsHitTestVisibleInChrome(segmentTrack, true);

            var segmentStack = new StackPanel { Orientation = Orientation.Horizontal };

            _tabBtnApps = CreateSegmentPill("分流清单", true, out _tabTextApps);
            _tabBtnApps.MouseLeftButtonDown += delegate { SwitchTab(true); };
            segmentStack.Children.Add(_tabBtnApps);

            _tabBtnSettings = CreateSegmentPill("设置", false, out _tabTextSettings);
            _tabBtnSettings.MouseLeftButtonDown += delegate { SwitchTab(false); };
            segmentStack.Children.Add(_tabBtnSettings);

            segmentTrack.Child = segmentStack;
            Grid.SetColumn(segmentTrack, 1);
            titleBarGrid.Children.Add(segmentTrack);

            // Controls (- and X: Minimize to Tray)
            var controlsStack = new StackPanel { Orientation = Orientation.Horizontal };

            var btnMin = new Button
            {
                Content = "—",
                FontSize = 11,
                Width = 46,
                Height = 48,
                Background = Brushes.Transparent,
                Foreground = BrushTextSecondary,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            ApplySimpleButtonTemplate(btnMin, BrushBtnHover);
            WindowChrome.SetIsHitTestVisibleInChrome(btnMin, true);
            btnMin.Click += delegate { WindowState = WindowState.Minimized; };
            controlsStack.Children.Add(btnMin);

            // Close button: Hides to System Tray like Clash Verge!
            var btnClose = new Button
            {
                Content = "✕",
                FontSize = 12,
                Width = 46,
                Height = 48,
                Background = Brushes.Transparent,
                Foreground = BrushTextSecondary,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            ApplySimpleButtonTemplate(btnClose, BrushCloseHover, Colors.White);
            WindowChrome.SetIsHitTestVisibleInChrome(btnClose, true);
            btnClose.Click += delegate
            {
                Hide(); // Minimize to system tray in background!
            };
            controlsStack.Children.Add(btnClose);

            Grid.SetColumn(controlsStack, 2);
            titleBarGrid.Children.Add(controlsStack);

            Grid.SetRow(titleBarGrid, 0);
            mainGrid.Children.Add(titleBarGrid);

            // ---------------------------------------------------------
            // 2. Main Views (Page 1: Apps / Page 2: Settings)
            // ---------------------------------------------------------
            var viewGrid = new Grid();

            _pageApps = BuildAppsView();
            viewGrid.Children.Add(_pageApps);

            _pageSettings = BuildSettingsView();
            _pageSettings.Visibility = Visibility.Collapsed;
            viewGrid.Children.Add(_pageSettings);

            Grid.SetRow(viewGrid, 1);
            mainGrid.Children.Add(viewGrid);

            // ---------------------------------------------------------
            // 3. Footer Strip
            // ---------------------------------------------------------
            var footerBorder = new Border
            {
                Background = BrushHeaderBg,
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(16, 8, 16, 8)
            };

            var footerGrid = new Grid();
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _statusPillText = new TextBlock
            {
                Text = "就绪 · 免 TUN 进程沙盒模式运行中",
                FontSize = 11,
                Foreground = BrushTextTertiary,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_statusPillText, 0);
            footerGrid.Children.Add(_statusPillText);

            var helpText = new TextBlock
            {
                Text = string.Format("上游 {0}:{1}", _config.Host, _config.Port),
                FontSize = 11,
                Foreground = BrushAppleBlue,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(helpText, 1);
            footerGrid.Children.Add(helpText);

            Grid.SetRow(footerBorder, 2);
            mainGrid.Children.Add(footerBorder);

            rootBorder.Child = mainGrid;
            Content = rootBorder;
        }

        private Border CreateSegmentPill(string text, bool isActive, out TextBlock textBlock)
        {
            var border = new Border
            {
                Background = isActive ? BrushSegmentActive : Brushes.Transparent,
                BorderBrush = isActive ? new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)) : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(20, 5, 20, 5),
                Cursor = Cursors.Hand
            };

            textBlock = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isActive ? BrushTextPrimary : BrushTextSecondary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            border.Child = textBlock;

            border.MouseEnter += delegate(object s, MouseEventArgs e)
            {
                if (border.Background == Brushes.Transparent)
                {
                    border.Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255));
                }
            };

            border.MouseLeave += delegate(object s, MouseEventArgs e)
            {
                if (border.BorderBrush == Brushes.Transparent)
                {
                    border.Background = Brushes.Transparent;
                }
            };

            return border;
        }

        private void SetPillState(Border pill, TextBlock label, bool active)
        {
            pill.Background = active ? BrushSegmentActive : Brushes.Transparent;
            pill.BorderBrush = active ? new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)) : Brushes.Transparent;
            label.Foreground = active ? BrushTextPrimary : BrushTextSecondary;
            label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        private void SwitchTab(bool toApps)
        {
            if (toApps)
            {
                _pageApps.Visibility = Visibility.Visible;
                _pageSettings.Visibility = Visibility.Collapsed;
                SetPillState(_tabBtnApps, _tabTextApps, true);
                SetPillState(_tabBtnSettings, _tabTextSettings, false);
            }
            else
            {
                _pageApps.Visibility = Visibility.Collapsed;
                _pageSettings.Visibility = Visibility.Visible;
                SetPillState(_tabBtnApps, _tabTextApps, false);
                SetPillState(_tabBtnSettings, _tabTextSettings, true);
            }
        }

        private Grid BuildAppsView()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Toolbar
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Apps Scroll

            // Action Toolbar (Header)
            var actionStrip = new Border
            {
                Background = BrushWindowBg,
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(16, 12, 16, 12)
            };

            var actionGrid = new Grid();
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var listHeaderStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var listTitle = new TextBlock
            {
                Text = "分流程序清单",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = BrushTextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            _listCountText = new TextBlock
            {
                Text = string.Format("({0})", _config.Apps.Count),
                FontSize = 11,
                Foreground = BrushTextTertiary,
                Margin = new Thickness(6, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            listHeaderStack.Children.Add(listTitle);
            listHeaderStack.Children.Add(_listCountText);
            Grid.SetColumn(listHeaderStack, 0);
            actionGrid.Children.Add(listHeaderStack);

            var actionButtons = new StackPanel { Orientation = Orientation.Horizontal };

            var btnAddApp = CreateAppleButton("+ 挑选程序 (.exe)", true);
            btnAddApp.Padding = new Thickness(14, 6, 14, 6);
            btnAddApp.FontSize = 12;
            btnAddApp.Click += delegate { AddCustomAppDialog(); };
            actionButtons.Children.Add(btnAddApp);

            var btnRadarApp = CreateAppleButton("从运行中添加", false);
            btnRadarApp.Margin = new Thickness(8, 0, 0, 0);
            btnRadarApp.Padding = new Thickness(12, 6, 12, 6);
            btnRadarApp.FontSize = 12;
            btnRadarApp.Click += delegate { ShowProcessRadarWindow(); };
            actionButtons.Children.Add(btnRadarApp);

            Grid.SetColumn(actionButtons, 1);
            actionGrid.Children.Add(actionButtons);

            actionStrip.Child = actionGrid;
            Grid.SetRow(actionStrip, 0);
            grid.Children.Add(actionStrip);

            // Apps List
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(16, 12, 16, 12)
            };
            _appListPanel = new StackPanel();
            scroll.Content = _appListPanel;
            Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);

            return grid;
        }

        private Grid BuildSettingsView()
        {
            var grid = new Grid { Margin = new Thickness(16) };
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var stack = new StackPanel();

            // 1. Upstream Proxy Configuration Card
            var proxyCard = new Border
            {
                Background = BrushCardBg,
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(18),
                Margin = new Thickness(0, 0, 0, 16)
            };

            var proxyCardStack = new StackPanel();

            var pTitle = new TextBlock
            {
                Text = "上游代理服务器设置",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = BrushTextPrimary,
                Margin = new Thickness(0, 0, 0, 12)
            };
            proxyCardStack.Children.Add(pTitle);

            var inputGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var hostBox = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            hostBox.Children.Add(new TextBlock { Text = "代理 IP / 地址", FontSize = 11, Foreground = BrushTextTertiary, Margin = new Thickness(0, 0, 0, 4) });
            _txtHost = CreateAppleTextBox(_config.Host);
            hostBox.Children.Add(_txtHost);
            Grid.SetColumn(hostBox, 0);
            inputGrid.Children.Add(hostBox);

            var portBox = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            portBox.Children.Add(new TextBlock { Text = "端口 (Port)", FontSize = 11, Foreground = BrushTextTertiary, Margin = new Thickness(0, 0, 0, 4) });
            _txtPort = CreateAppleTextBox(_config.Port.ToString());
            portBox.Children.Add(_txtPort);
            Grid.SetColumn(portBox, 1);
            inputGrid.Children.Add(portBox);

            var btnSaveProxy = CreateAppleButton("保存", true);
            btnSaveProxy.VerticalAlignment = VerticalAlignment.Bottom;
            btnSaveProxy.Padding = new Thickness(16, 6, 16, 6);
            btnSaveProxy.Click += delegate { SaveProxySettings(); };
            Grid.SetColumn(btnSaveProxy, 2);
            inputGrid.Children.Add(btnSaveProxy);

            proxyCardStack.Children.Add(inputGrid);

            var presetLabel = new TextBlock
            {
                Text = "常用客户端预设快捷套用:",
                FontSize = 11,
                Foreground = BrushTextTertiary,
                Margin = new Thickness(0, 0, 0, 8)
            };
            proxyCardStack.Children.Add(presetLabel);

            var presetsWrap = new WrapPanel();
            presetsWrap.Children.Add(CreatePresetButton("v2rayN/Xray (10808)", 10808));
            presetsWrap.Children.Add(CreatePresetButton("Clash/Mihomo (7890)", 7890));
            presetsWrap.Children.Add(CreatePresetButton("Clash Verge (7897)", 7897));
            presetsWrap.Children.Add(CreatePresetButton("SOCKS5 (10809)", 10809, "socks5"));
            presetsWrap.Children.Add(CreatePresetButton("通用 (1080)", 1080));
            proxyCardStack.Children.Add(presetsWrap);

            proxyCard.Child = proxyCardStack;
            stack.Children.Add(proxyCard);

            // 2. Startup Behavior Configuration Card
            var startupCard = new Border
            {
                Background = BrushCardBg,
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(18)
            };

            var startupStack = new StackPanel();
            var sTitle = new TextBlock
            {
                Text = "开机自启动与运行选项",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = BrushTextPrimary,
                Margin = new Thickness(0, 0, 0, 14)
            };
            startupStack.Children.Add(sTitle);

            _chkAutoStart = new CheckBox
            {
                Content = "开机自动启动 AeroProxy",
                IsChecked = _config.AutoStartApp,
                Foreground = BrushTextPrimary,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 12),
                Cursor = Cursors.Hand
            };
            _chkAutoStart.Click += delegate { OnStartupSettingChanged(); };
            startupStack.Children.Add(_chkAutoStart);

            var subOptionsBorder = new Border
            {
                Background = BrushHeaderBg,
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(18, 0, 0, 0)
            };

            var subOptionsStack = new StackPanel();

            _rbAutoStartAppOnly = new RadioButton
            {
                Content = "仅自启动应用",
                GroupName = "AutoStartMode",
                IsChecked = !_config.AutoStartProxy,
                Foreground = BrushTextSecondary,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand
            };
            _rbAutoStartAppOnly.Click += delegate { OnStartupSettingChanged(); };
            subOptionsStack.Children.Add(_rbAutoStartAppOnly);

            _rbAutoStartWithProxy = new RadioButton
            {
                Content = "自启动后同时自动开启代理接管",
                GroupName = "AutoStartMode",
                IsChecked = _config.AutoStartProxy,
                Foreground = BrushTextSecondary,
                FontSize = 11,
                Cursor = Cursors.Hand
            };
            _rbAutoStartWithProxy.Click += delegate { OnStartupSettingChanged(); };
            subOptionsStack.Children.Add(_rbAutoStartWithProxy);

            subOptionsBorder.Child = subOptionsStack;
            startupStack.Children.Add(subOptionsBorder);

            startupCard.Child = startupStack;
            stack.Children.Add(startupCard);

            scroll.Content = stack;
            grid.Children.Add(scroll);
            return grid;
        }

        private void OnStartupSettingChanged()
        {
            bool autoStart = _chkAutoStart.IsChecked == true;
            bool autoProxy = _rbAutoStartWithProxy.IsChecked == true;

            _config.AutoStartApp = autoStart;
            _config.AutoStartProxy = autoProxy;
            ConfigStore.Save(_config);

            StartupHelper.SyncStartup(autoStart, autoProxy);
            FlashStatus(autoStart ? "已更新自启动设置 (已开启)" : "已关闭开机自启动");
        }

        private void SaveProxySettings()
        {
            string host = _txtHost.Text.Trim();
            int port;
            if (!int.TryParse(_txtPort.Text.Trim(), out port) || port <= 0 || port > 65535)
            {
                MessageBox.Show("请输入有效的端口号 (1-65535)", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _config.Host = string.IsNullOrEmpty(host) ? "127.0.0.1" : host;
            _config.Port = port;
            ConfigStore.Save(_config);

            FlashStatus("上游代理配置已保存并生效");
        }

        private Button CreatePresetButton(string text, int port, string proto = "http")
        {
            var btn = CreateAppleButton(text, false);
            btn.FontSize = 10;
            btn.Padding = new Thickness(8, 4, 8, 4);
            btn.Margin = new Thickness(0, 0, 6, 6);
            btn.Click += delegate
            {
                _txtHost.Text = "127.0.0.1";
                _txtPort.Text = port.ToString();
                _config.Protocol = proto;
                SaveProxySettings();
            };
            return btn;
        }

        private TextBox CreateAppleTextBox(string text)
        {
            return new TextBox
            {
                Text = text,
                FontSize = 12,
                Background = BrushHeaderBg,
                Foreground = BrushTextPrimary,
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 5, 8, 5),
                CaretBrush = BrushTextPrimary
            };
        }

        private void ApplySimpleButtonTemplate(Button btn, SolidColorBrush hoverBg, Color? hoverFg = null)
        {
            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.Name = "border";
            borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));

            var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(contentFactory);

            template.VisualTree = borderFactory;

            var trigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            trigger.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "border"));
            if (hoverFg.HasValue)
            {
                trigger.Setters.Add(new Setter(Button.ForegroundProperty, new SolidColorBrush(hoverFg.Value)));
            }
            template.Triggers.Add(trigger);

            btn.Template = template;
        }

        private Button CreateAppleButton(string text, bool isPrimary)
        {
            var btn = new Button
            {
                Content = text,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Padding = new Thickness(14, 6, 14, 6),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(isPrimary ? 0 : 1),
                Background = isPrimary ? BrushAppleBlue : BrushCardBg,
                Foreground = BrushTextPrimary,
                BorderBrush = isPrimary ? Brushes.Transparent : BrushBorder
            };

            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.Name = "btnBorder";
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));

            var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            contentFactory.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Button.PaddingProperty));
            borderFactory.AppendChild(contentFactory);

            template.VisualTree = borderFactory;

            var trigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            trigger.Setters.Add(new Setter(Border.BackgroundProperty, isPrimary ? new SolidColorBrush(Color.FromRgb(0, 113, 227)) : BrushBtnHover, "btnBorder"));
            template.Triggers.Add(trigger);

            btn.Template = template;
            return btn;
        }

        private void RefreshAppList()
        {
            _appListPanel.Children.Clear();
            _listCountText.Text = string.Format("({0})", _config.Apps.Count);

            if (_config.Apps == null || _config.Apps.Count == 0)
            {
                var emptyBorder = new Border
                {
                    Background = BrushCardBg,
                    CornerRadius = new CornerRadius(8),
                    BorderBrush = BrushBorder,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(24),
                    Margin = new Thickness(0, 20, 0, 0),
                    Cursor = Cursors.Hand
                };

                var emptyStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                emptyStack.Children.Add(new TextBlock
                {
                    Text = "+ 点击挑选需要走代理的程序 (.exe)",
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = BrushAppleBlue,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                emptyStack.Children.Add(new TextBlock
                {
                    Text = "也可以直接从桌面将程序拖拽到此窗口中",
                    FontSize = 11,
                    Foreground = BrushTextTertiary,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 0)
                });
                emptyBorder.Child = emptyStack;
                emptyBorder.MouseLeftButtonDown += delegate { AddCustomAppDialog(); };
                _appListPanel.Children.Add(emptyBorder);
                return;
            }

            foreach (var app in _config.Apps)
            {
                _appListPanel.Children.Add(CreateAppCard(app));
            }

            // Quick Add Bottom Strip
            var quickAddBorder = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 4, 0, 8),
                Cursor = Cursors.Hand
            };
            var quickAddText = new TextBlock
            {
                Text = "+ 挑选或拖入更多程序...",
                FontSize = 11,
                Foreground = BrushTextSecondary,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            quickAddBorder.Child = quickAddText;
            quickAddBorder.MouseEnter += delegate { quickAddBorder.Background = BrushCardBg; };
            quickAddBorder.MouseLeave += delegate { quickAddBorder.Background = Brushes.Transparent; };
            quickAddBorder.MouseLeftButtonDown += delegate { AddCustomAppDialog(); };
            _appListPanel.Children.Add(quickAddBorder);
        }

        private UIElement CreateAppCard(ProxyItem app)
        {
            var cardBorder = new Border
            {
                Background = BrushCardBg,
                CornerRadius = new CornerRadius(8),
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var metaStack = new StackPanel();
            var titleStack = new StackPanel { Orientation = Orientation.Horizontal };
            var nameText = new TextBlock
            {
                Text = app.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushTextPrimary
            };
            titleStack.Children.Add(nameText);

            bool isRunning = IsProcessRunning(app.Path);
            if (isRunning)
            {
                var runningBadge = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(30, 48, 209, 88)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(8, 0, 0, 0)
                };
                runningBadge.Child = new TextBlock
                {
                    Text = "运行中",
                    FontSize = 10,
                    Foreground = BrushAppleGreen
                };
                titleStack.Children.Add(runningBadge);
            }

            metaStack.Children.Add(titleStack);

            var pathText = new TextBlock
            {
                Text = app.Path,
                FontSize = 11,
                Foreground = BrushTextTertiary,
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 340
            };
            metaStack.Children.Add(pathText);
            Grid.SetColumn(metaStack, 0);
            grid.Children.Add(metaStack);

            // Action: Concise "代理" Button
            var btnStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var btnProxyOnly = new Button
            {
                Content = app.Enabled ? "代理" : "未代理",
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Padding = new Thickness(14, 5, 14, 5),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
                Background = app.Enabled ? new SolidColorBrush(Color.FromArgb(35, 48, 209, 88)) : BrushHeaderBg,
                Foreground = app.Enabled ? BrushAppleGreen : BrushTextTertiary,
                BorderBrush = app.Enabled ? new SolidColorBrush(Color.FromArgb(80, 48, 209, 88)) : BrushBorder
            };

            var pTemplate = new ControlTemplate(typeof(Button));
            var pBorderFactory = new FrameworkElementFactory(typeof(Border));
            pBorderFactory.Name = "pBorder";
            pBorderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            pBorderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            pBorderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            pBorderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));

            var pContentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            pContentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            pContentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            pContentFactory.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Button.PaddingProperty));
            pBorderFactory.AppendChild(pContentFactory);

            pTemplate.VisualTree = pBorderFactory;
            btnProxyOnly.Template = pTemplate;

            btnProxyOnly.Click += delegate
            {
                app.Enabled = !app.Enabled;
                ConfigStore.Save(_config);
                RefreshAppList();
                if (app.Enabled)
                {
                    bool running = IsProcessRunning(app.Path);
                    FlashStatus(running
                        ? string.Format("已开启 [{0}] 代理。当前正在运行，建议重启以生效", app.Name)
                        : string.Format("已开启 [{0}] 代理。可点击「启动」运行", app.Name));
                }
                else
                {
                    FlashStatus(string.Format("已关闭 [{0}] 代理接管", app.Name));
                }
            };
            btnStack.Children.Add(btnProxyOnly);

            if (app.Enabled)
            {
                var btnRun = new Button
                {
                    Content = "启动",
                    FontSize = 11,
                    FontWeight = FontWeights.Medium,
                    Padding = new Thickness(12, 5, 12, 5),
                    Margin = new Thickness(6, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    BorderThickness = new Thickness(1),
                    Background = new SolidColorBrush(Color.FromArgb(35, 0, 113, 227)),
                    Foreground = BrushAppleBlue,
                    BorderBrush = new SolidColorBrush(Color.FromArgb(80, 0, 113, 227))
                };
                btnRun.Template = pTemplate;
                btnRun.Click += delegate
                {
                    try
                    {
                        ProxyEngine.LaunchWithProxy(app, _config);
                        FlashStatus(string.Format("已以代理模式启动 [{0}]", app.Name));
                        RefreshAppList();
                    }
                    catch (Exception ex)
                    {
                        FlashStatus("启动失败: " + ex.Message);
                    }
                };
                btnStack.Children.Add(btnRun);
            }

            var btnDel = new TextBlock
            {
                Text = "×",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = BrushTextTertiary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 2, 0),
                Cursor = Cursors.Hand
            };
            btnDel.MouseEnter += delegate { btnDel.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); };
            btnDel.MouseLeave += delegate { btnDel.Foreground = BrushTextTertiary; };
            btnDel.MouseLeftButtonDown += delegate
            {
                _config.Apps.Remove(app);
                ConfigStore.Save(_config);
                RefreshAppList();
            };
            btnStack.Children.Add(btnDel);

            Grid.SetColumn(btnStack, 1);
            grid.Children.Add(btnStack);

            cardBorder.Child = grid;
            return cardBorder;
        }

        private bool IsProcessRunning(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            string fileName = System.IO.Path.GetFileNameWithoutExtension(exePath);
            var procs = Process.GetProcessesByName(fileName);
            return procs != null && procs.Length > 0;
        }

        private void AddAppByPath(string path)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);

            foreach (var a in _config.Apps)
            {
                if (string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    FlashStatus("该程序已在列表中: " + name);
                    return;
                }
            }

            _config.Apps.Add(new ProxyItem
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                Path = path,
                Enabled = true,
                Mode = "smart"
            });

            ConfigStore.Save(_config);
            RefreshAppList();
            FlashStatus("已成功添加程序: " + name);
        }

        private void AddCustomAppDialog()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "挑选需要走代理的可执行程序 (.exe)",
                Filter = "可执行程序 (*.exe;*.bat)|*.exe;*.bat|所有文件 (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                AddAppByPath(dialog.FileName);
            }
        }

        private void ShowProcessRadarWindow()
        {
            var radarWin = new Window
            {
                Title = "选择活跃进程",
                Width = 480,
                Height = 440,
                WindowStyle = WindowStyle.None,
                Background = BrushWindowBg,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            TextOptions.SetTextFormattingMode(radarWin, TextFormattingMode.Display);

            var border = new Border
            {
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1),
                Background = BrushWindowBg,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16)
            };

            var radarGrid = new Grid();
            radarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            radarGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            radarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var rTitle = new TextBlock
            {
                Text = "选择当前正在运行的程序",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = BrushTextPrimary,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(rTitle, 0);
            radarGrid.Children.Add(rTitle);

            var listStack = new StackPanel();
            var scroll = new ScrollViewer { Content = listStack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            var procs = Process.GetProcesses();
            var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in procs)
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero || string.IsNullOrEmpty(p.MainWindowTitle))
                        continue;

                    string pPath = p.MainModule.FileName;
                    if (addedPaths.Contains(pPath)) continue;
                    addedPaths.Add(pPath);

                    var itemBtn = new Button
                    {
                        Content = string.Format("{0} ({1})", p.ProcessName, p.MainWindowTitle),
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Background = BrushCardBg,
                        Foreground = BrushTextPrimary,
                        Padding = new Thickness(10, 7, 10, 7),
                        Margin = new Thickness(0, 0, 0, 5),
                        BorderThickness = new Thickness(1),
                        BorderBrush = BrushBorder,
                        Cursor = Cursors.Hand
                    };

                    itemBtn.Click += delegate
                    {
                        AddAppByPath(pPath);
                        radarWin.Close();
                    };

                    listStack.Children.Add(itemBtn);
                }
                catch { }
            }

            Grid.SetRow(scroll, 1);
            radarGrid.Children.Add(scroll);

            var btnCloseRadar = CreateAppleButton("关闭", false);
            btnCloseRadar.HorizontalAlignment = HorizontalAlignment.Right;
            btnCloseRadar.Margin = new Thickness(0, 10, 0, 0);
            btnCloseRadar.Click += delegate { radarWin.Close(); };
            Grid.SetRow(btnCloseRadar, 2);
            radarGrid.Children.Add(btnCloseRadar);

            border.Child = radarGrid;
            radarWin.Content = border;
            radarWin.KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Escape) radarWin.Close(); };
            radarWin.ShowDialog();
        }

        private void FlashStatus(string msg)
        {
            _statusPillText.Text = msg;
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            var app = new Application();
            var win = new MainWindow();
            app.Run(win);
        }
    }
}
