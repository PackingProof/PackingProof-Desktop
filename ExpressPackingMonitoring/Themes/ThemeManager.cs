#nullable disable
using Microsoft.Win32;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Linq;

namespace ExpressPackingMonitoring.Themes
{
    public enum AppTheme
    {
        Auto,
        Light,
        Dark
    }

    public static class ThemeManager
    {
        private static AppTheme _currentTheme = AppTheme.Auto;
        private static bool _isListening = false;

        /// <summary>上一次真正插进应用资源的主题字典，以及它对应的是不是深色：
        /// 用来判断"这次要不要真的换"，避免没改主题时白刷一遍全局资源。</summary>
        private static ResourceDictionary _appliedThemeDictionary;
        private static bool _appliedDarkTheme;

        public static void ApplyConfiguredTheme(string theme) =>
            ApplyTheme(ResolveConfiguredTheme(theme));

        internal static AppTheme ResolveConfiguredTheme(string theme) =>
            Enum.TryParse(theme, out AppTheme parsed) ? parsed : AppTheme.Auto;

        public static void ApplyTheme(AppTheme theme)
        {
            _currentTheme = theme;

            // 界面线程已经不在了（宿主结束、测试宿主）时不能再同步 Invoke：那个 Dispatcher
            // 已经没人泵消息，Invoke 会一直阻塞。直接在当前线程应用即可
            Dispatcher dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null
                || dispatcher.CheckAccess()
                || dispatcher.HasShutdownStarted
                || !dispatcher.Thread.IsAlive)
            {
                ApplyThemeInternal();
                return;
            }

            dispatcher.Invoke(() => ApplyThemeInternal());
        }

        private static void ApplyThemeInternal()
        {
            UpdateTheme();
            
            if (_currentTheme == AppTheme.Auto && !_isListening)
            {
                SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
                _isListening = true;
            }
            else if (_currentTheme != AppTheme.Auto && _isListening)
            {
                SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
                _isListening = false;
            }
        }

        private static void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General)
            {
                if (System.Windows.Application.Current != null)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() => UpdateTheme());
                }
            }
        }

        public static void UpdateTheme()
        {
            bool useDarkTheme = false;

            if (_currentTheme == AppTheme.Auto)
            {
                useDarkTheme = IsWindowsInDarkMode();
            }
            else
            {
                useDarkTheme = _currentTheme == AppTheme.Dark;
            }

            // 主题没变就别再换一次资源字典：换字典会让整个应用（主窗口那棵大树和几路视频都在内）
            // 把所有 DynamicResource 重新解析一遍。设置页一打开，"外观主题"下拉的绑定就会调到这里，
            // 明明没改主题也要付这份代价，打开设置自然就慢。
            if (_appliedThemeDictionary is { } applied
                && _appliedDarkTheme == useDarkTheme
                && Application.Current?.Resources.MergedDictionaries.Contains(applied) == true)
            {
                return;
            }

            string themeUri = BuildThemeUri(useDarkTheme);

            var newDictionary = new ResourceDictionary { Source = new Uri(themeUri) };

            // Find existing theme dictionary
            ResourceDictionary existingThemeDict = null;
            foreach (var dict in Application.Current.Resources.MergedDictionaries)
            {
                if (dict.Source != null && (dict.Source.OriginalString.Contains("LightTheme.xaml") || dict.Source.OriginalString.Contains("DarkTheme.xaml")))
                {
                    existingThemeDict = dict;
                    break;
                }
            }

            if (existingThemeDict != null)
            {
                Application.Current.Resources.MergedDictionaries.Remove(existingThemeDict);
            }
            
            Application.Current.Resources.MergedDictionaries.Insert(0, newDictionary);
            _appliedThemeDictionary = newDictionary;
            _appliedDarkTheme = useDarkTheme;
        }

        /// <summary>
        /// 主题资源按本程序集拼 pack URI。不带程序集名的相对 URI 会去"入口程序集"里找，
        /// 自动化宿主、测试宿主加载本程序集时那里没有主题资源，主题与主窗口都会加载失败。
        /// </summary>
        internal static string BuildThemeUri(bool useDarkTheme)
        {
            string assemblyName = typeof(ThemeManager).Assembly.GetName().Name;
            string themeFile = useDarkTheme ? "DarkTheme.xaml" : "LightTheme.xaml";
            return $"pack://application:,,,/{assemblyName};component/Themes/{themeFile}";
        }

        private static bool IsWindowsInDarkMode()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object registryValueObject = key.GetValue("AppsUseLightTheme");
                        if (registryValueObject != null)
                        {
                            int registryValue = (int)registryValueObject;
                            return registryValue == 0;
                        }
                    }
                }
            }
            catch
            {
                // Fallback if we can't read registry
            }
            return false; // Default to light
        }
    }
}

