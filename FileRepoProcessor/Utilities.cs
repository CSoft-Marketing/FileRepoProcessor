using Microsoft.Extensions.Options;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace FileRepoProcessor
{
    public class worksheetDimension
    {
        public double width { get; set; }
        public double height { get; set; }

        public worksheetDimension()
        {
            this.width = 0;
            this.height = 0;
        }
        public worksheetDimension(double W, double H)
        {
            this.width = W;
            this.height = H;
        }
    }

    public enum HtmlRenderEngine
    {
        Qt,
        Chrome,
        Edge
    }

    public enum BrowserType
    {
        None,
        Chrome,
        Edge,
        Chromium
    }
    public class BrowserInfo
    {
        public BrowserType Type { get; set; }

        public string ExecutablePath { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public bool HasChromiumBrowser =>
            !string.IsNullOrWhiteSpace(ExecutablePath) &&
            File.Exists(ExecutablePath);
        public bool isUsable {  get; set; }
    }
    public class HtmlEngineInfo
    {
        public HtmlRenderEngine Engine { get; set; }

        public string ExecutablePath { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public bool IsAvailable => !string.IsNullOrWhiteSpace(ExecutablePath);
    }

    public static class BrowserLocator
    {
        public static HtmlEngineInfo? Detect()
        {
            // Implementation will come next.
            return null; //will be replaced later
        }
        public static BrowserInfo Locate()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new BrowserInfo
                {
                    Type = BrowserType.None
                };
            }

            // Chrome
            var browser = FindChrome();
            if (browser != null)
                return browser;

            // Edge
            browser = FindEdge();
            if (browser != null)
                return browser;

            return new BrowserInfo
            {
                Type = BrowserType.None
            };
        }

        private static BrowserInfo? FindChrome()
        {
            return FindBrowser(
                BrowserType.Chrome,
                "chrome.exe",
                new[]
                {
                    @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                    @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"Google\Chrome\Application\chrome.exe")
                });
        }

        private static BrowserInfo? FindEdge()
        {
            return FindBrowser(
                BrowserType.Edge,
                "msedge.exe",
                new[]
                {
                    @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                    @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
                });
        }

        private static BrowserInfo? FindBrowser(
            BrowserType type,
            string exeName,
            IEnumerable<string> knownLocations)
        {
            //
            // Registry
            //
            var browser = FindFromRegistry(type, exeName);

            if (browser != null)
                return browser;

            //
            // Standard install folders
            //
            foreach (var path in knownLocations)
            {
                if (File.Exists(path))
                {
                    return CreateBrowserInfo(type, path);
                }
            }

            return null;
        }

        private static BrowserInfo? FindFromRegistry(
            BrowserType type,
            string exeName)
        {
            string[] keys =
            {
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}",
                $@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\{exeName}"
            };

            foreach (var key in keys)
            {
                foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                {
                    using var regKey = hive.OpenSubKey(key);

                    if (regKey == null)
                        continue;

                    var path = regKey.GetValue(null) as string;

                    if (!string.IsNullOrWhiteSpace(path) &&
                        File.Exists(path))
                    {
                        return CreateBrowserInfo(type, path);
                    }
                }
            }

            return null;
        }

        private static BrowserInfo CreateBrowserInfo(
            BrowserType type,
            string executablePath)
        {
            //var version =
            //    FileVersionInfo
            //        .GetVersionInfo(executablePath)
            //        .FileVersion ?? "";

            //return new BrowserInfo
            //{
            //    Type = type,
            //    ExecutablePath = executablePath,
            //    Version = version
            //};
            var info = FileVersionInfo.GetVersionInfo(executablePath);

            return new BrowserInfo
            {
                Type = type,
                ExecutablePath = executablePath,
                Version = info.ProductVersion ?? info.FileVersion ?? string.Empty
            };
        }
    }
}
