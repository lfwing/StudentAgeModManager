using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace StudentAgeModManager.Core
{
    /// <summary>
    /// 仅用于读取中央文本索引：固定先直连，失败后自动尝试镜像。
    /// 可执行前置全部内嵌在管理器中，绝不再通过此类第三方镜像下载。
    /// </summary>
    public class Downloader
    {
        /// <summary>内置种子镜像（首次拉索引前可用；索引拉到后用索引里的列表覆盖）。</summary>
        public static readonly string[] SeedMirrors =
        {
            "https://ghproxy.net/",
            "https://gh-proxy.com/",
            "https://ghfast.top/",
        };

        public List<string> Mirrors { get; set; } = new List<string>(SeedMirrors);
        public int TimeoutSeconds { get; set; } = 15;

        static Downloader()
        {
            // 老系统 GitHub TLS 兼容
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        private IEnumerable<string> Candidates(string url)
        {
            yield return url;
            foreach (var mirror in Mirrors)
                yield return mirror.TrimEnd('/') + "/" + url;
        }

        /// <summary>下载文本（用于 mods.json）。全部候选失败抛出最后一个异常。</summary>
        public async Task<string> DownloadStringAsync(string url, CancellationToken ct = default(CancellationToken))
        {
            Exception last = null;
            foreach (var candidate in Candidates(url))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using (var wc = CreateClient())
                    using (ct.Register(wc.CancelAsync))
                    {
                        var task = wc.DownloadStringTaskAsync(candidate);
                        if (await Task.WhenAny(task, Task.Delay(TimeoutSeconds * 1000, ct)) != task)
                        {
                            wc.CancelAsync();
                            throw new TimeoutException("请求超时: " + candidate);
                        }
                        return await task;
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    last = ex;
                }
            }
            throw last ?? new Exception("无可用下载源");
        }

        private static WebClient CreateClient()
        {
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "StudentAgeModManager/1.0";
            wc.Encoding = System.Text.Encoding.UTF8; // 默认是 ANSI(GBK)，会把 UTF-8 的 mods.json 解成乱码
            return wc;
        }
    }
}
