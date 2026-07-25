using System;
using System.Diagnostics;

namespace StudentAgeModManager.Core
{
    public enum WorkshopPageTarget
    {
        SteamClient,
        WebBrowser,
    }

    /// <summary>
    /// Opens a canonical Workshop item in the running Steam client when possible, and otherwise
    /// falls back to the trusted HTTPS page. Callers never supply a complete URL.
    /// </summary>
    public sealed class WorkshopPageLauncher
    {
        private readonly Func<bool> _isSteamRunning;
        private readonly Action<string> _openUrl;

        public WorkshopPageLauncher()
            : this(IsSteamRunning, OpenUrl)
        {
        }

        public WorkshopPageLauncher(Func<bool> isSteamRunning, Action<string> openUrl)
        {
            _isSteamRunning = isSteamRunning ??
                throw new ArgumentNullException(nameof(isSteamRunning));
            _openUrl = openUrl ?? throw new ArgumentNullException(nameof(openUrl));
        }

        public WorkshopPageTarget Open(string normalizedWorkshopId)
        {
            // Validate before consulting external state. Both destinations are constructed only
            // from a canonical numeric ID, never from an index-provided URL.
            string webUrl = WorkshopItem.PageUrl(normalizedWorkshopId);
            string steamUrl = WorkshopItem.SteamClientUrl(normalizedWorkshopId);

            bool steamRunning;
            try { steamRunning = _isSteamRunning(); }
            catch { steamRunning = false; }

            if (steamRunning)
            {
                try
                {
                    _openUrl(steamUrl);
                    return WorkshopPageTarget.SteamClient;
                }
                catch
                {
                    // A broken protocol association should not prevent the HTTPS fallback.
                }
            }

            _openUrl(webUrl);
            return WorkshopPageTarget.WebBrowser;
        }

        private static bool IsSteamRunning()
        {
            Process[] processes = Process.GetProcessesByName("steam");
            try { return processes.Length > 0; }
            finally
            {
                foreach (Process process in processes) process.Dispose();
            }
        }

        private static void OpenUrl(string url)
        {
            Process process = Process.Start(url);
            if (process != null) process.Dispose();
        }
    }
}
