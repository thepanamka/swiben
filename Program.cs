using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace VinylNowPlaying
{
    public class Program : Application
    {
        private static Mutex _appMutex;
        private NotifyIcon _trayIcon;
        private MainWindow _mainWindow;
        private MediaManager _mediaManager;

        [STAThread]
        public static void Main(string[] args)
        {
            bool createdNew;
            _appMutex = new Mutex(true, "VinylNowPlaying_SingleInstance_Mutex_98741", out createdNew);
            if (!createdNew)
            {
                System.Windows.MessageBox.Show(
                    "Vinyl Now Playing уже запущен!\nИщите значок пластинки в трее Windows (возле часов).",
                    "Vinyl Now Playing",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            Program app = new Program();
            app.Run();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _mediaManager = new MediaManager();
            _mainWindow = new MainWindow(_mediaManager);
            _mainWindow.Show();

            SetupTrayIcon();

            if (e.Args.Length > 0 && e.Args[0].Equals("--test", StringComparison.OrdinalIgnoreCase))
            {
                _mainWindow.TriggerTestAnimation();
            }
        }

        private void SetupTrayIcon()
        {
            _trayIcon = new NotifyIcon();

            try
            {
                if (File.Exists("app.ico"))
                {
                    _trayIcon.Icon = new Icon("app.ico");
                }
                else
                {
                    _trayIcon.Icon = SystemIcons.Application;
                }
            }
            catch
            {
                _trayIcon.Icon = SystemIcons.Application;
            }

            _trayIcon.Text = "Vinyl Now Playing - Виджет пластинки";
            _trayIcon.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.BackColor = System.Drawing.Color.FromArgb(28, 28, 32);
            menu.ForeColor = System.Drawing.Color.White;
            menu.ShowImageMargin = false;

            ToolStripMenuItem itemToggle = new ToolStripMenuItem("Показать / Скрыть виджет");
            itemToggle.Click += (s, e) => _mainWindow.ToggleVisibility();
            menu.Items.Add(itemToggle);

            ToolStripMenuItem itemTest = new ToolStripMenuItem("🎬 Тест анимации (Демо трек)");
            itemTest.Click += (s, e) => _mainWindow.TriggerTestAnimation();
            menu.Items.Add(itemTest);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem itemReset = new ToolStripMenuItem("📍 Сбросить позицию (в угол)");
            itemReset.Click += (s, e) => _mainWindow.ResetPosition();
            menu.Items.Add(itemReset);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem itemExit = new ToolStripMenuItem("❌ Выход");
            itemExit.ForeColor = System.Drawing.Color.FromArgb(255, 120, 120);
            itemExit.Click += (s, e) => Shutdown();
            menu.Items.Add(itemExit);

            _trayIcon.ContextMenuStrip = menu;

            _trayIcon.DoubleClick += (s, e) => _mainWindow.ToggleVisibility();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            if (_appMutex != null)
            {
                _appMutex.ReleaseMutex();
                _appMutex.Dispose();
            }
            base.OnExit(e);
        }
    }
}
