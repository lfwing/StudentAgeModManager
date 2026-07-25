using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace StudentAgeModManager.Core
{
    /// <summary>安装 BepInEx 前置与创意工坊 DLL Bridge。</summary>
    public class ModInstaller
    {
        public const string BepInExVersion = "5.4.23";
        public const string WorkshopBridgeFileName = "StudentAge.WorkshopBridge.dll";
        public const string EmbeddedBepInExPackageSha256 =
            "D1C85CDC44F999883BF36587AD1C1DD03B149C7A9FB2700D651FFD6ED433B971";
        private const string BepInExPackageResourceName =
            "StudentAgeModManager.Resources.BepInEx-5.4.23-package.zip";
        private const string WorkshopBridgeResourceName =
            "StudentAgeModManager.Resources.StudentAge.WorkshopBridge.dll";

        private readonly LocalState _state;

        public ModInstaller(LocalState state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        public static bool IsGameRunning()
        {
            return Process.GetProcessesByName("StudentAge").Length > 0;
        }

        public bool IsBepInExInstalled()
        {
            return File.Exists(Path.Combine(_state.GameDir, "winhttp.dll"))
                && Directory.Exists(Path.Combine(_state.GameDir, "BepInEx", "core"));
        }

        public string WorkshopBridgePath => Path.Combine(_state.GameDir,
            "BepInEx", "patchers", WorkshopBridgeFileName);

        public bool IsWorkshopBridgeInstalled()
        {
            return File.Exists(WorkshopBridgePath);
        }

        public bool IsWorkshopBridgeCurrent()
        {
            if (!IsWorkshopBridgeInstalled()) return false;
            try { return GetFileHash(WorkshopBridgePath) == GetEmbeddedBridgeHash(); }
            catch { return false; }
        }

        /// <summary>从管理器内嵌的固定 BepInEx 包离线安装完整前置。</summary>
        public Task InstallBepInExAsync(Action<int, string> progress,
            CancellationToken ct = default(CancellationToken))
        {
            EnsureGameNotRunning();
            return Task.Run(() => InstallBepInExCore(progress, ct), ct);
        }

        private void InstallBepInExCore(Action<int, string> progress, CancellationToken ct)
        {
            // Recheck on the worker immediately before touching files in case the game was
            // launched after the UI scheduled installation.
            EnsureGameNotRunning();
            ct.ThrowIfCancellationRequested();
            const string sourceLabel = "内置 BepInEx 5.4.23";
            progress?.Invoke(0, sourceLabel);

            using (var hashStream = OpenEmbeddedBepInExPackage())
            {
                string actualHash = GetStreamHashHex(hashStream);
                if (!string.Equals(actualHash, EmbeddedBepInExPackageSha256,
                    StringComparison.Ordinal))
                    throw new InvalidDataException("内嵌 BepInEx 安装包校验失败。期望 " +
                        EmbeddedBepInExPackageSha256 + "，实际 " + actualHash + "。");
            }

            using (var package = OpenEmbeddedBepInExPackage())
            using (var zip = new ZipArchive(package, ZipArchiveMode.Read, false))
            {
                string prefix = DetectZipRoot(zip);
                int totalFiles = 0;
                foreach (var entry in zip.Entries)
                    if (!string.IsNullOrEmpty(entry.Name)) totalFiles++;

                int extractedFiles = 0;
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    string relativePath = entry.FullName.Replace('/', '\\');
                    if (prefix.Length > 0)
                    {
                        if (!relativePath.StartsWith(prefix,
                            StringComparison.OrdinalIgnoreCase))
                            continue;
                        relativePath = relativePath.Substring(prefix.Length);
                    }
                    string destination = ResolvePackageDestination(relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    using (var source = entry.Open())
                    using (var output = new FileStream(destination, FileMode.Create,
                        FileAccess.Write, FileShare.None))
                        source.CopyTo(output);

                    extractedFiles++;
                    progress?.Invoke(totalFiles == 0 ? 90 :
                        Math.Min(90, extractedFiles * 90 / totalFiles), sourceLabel);
                }
            }

            // The immutable base package intentionally excludes the Bridge. Always deploy
            // the exact Bridge embedded in this manager after the BepInEx files.
            InstallWorkshopBridgeCore();
            progress?.Invoke(100, sourceLabel);
        }

        /// <summary>
        /// Installs or repairs only the workshop bridge on top of an existing BepInEx.
        /// </summary>
        public void InstallWorkshopBridge()
        {
            EnsureGameNotRunning();
            if (!IsBepInExInstalled())
                throw new InvalidOperationException("请先安装 BepInEx，再安装创意工坊 DLL 支持。");
            InstallWorkshopBridgeCore();
        }

        private void InstallWorkshopBridgeCore()
        {
            var destination = WorkshopBridgePath;
            var directory = Path.GetDirectoryName(destination);
            Directory.CreateDirectory(directory);

            var temp = destination + ".tmp";
            try
            {
                using (var source = OpenEmbeddedBridge())
                using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    source.CopyTo(output);
                File.Copy(temp, destination, true);
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }

            if (!IsWorkshopBridgeCurrent())
                throw new IOException("创意工坊 DLL 桥接器写入后校验失败。");
        }

        private static Stream OpenEmbeddedBridge()
        {
            var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(WorkshopBridgeResourceName);
            if (stream == null)
                throw new InvalidDataException("管理器内未找到创意工坊 DLL 桥接器资源。");
            return stream;
        }

        private static Stream OpenEmbeddedBepInExPackage()
        {
            var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(BepInExPackageResourceName);
            if (stream == null)
                throw new InvalidDataException("管理器内未找到 BepInEx 安装包资源。");
            return stream;
        }

        private static string GetEmbeddedBridgeHash()
        {
            using (var stream = OpenEmbeddedBridge())
            using (var sha256 = SHA256.Create())
                return Convert.ToBase64String(sha256.ComputeHash(stream));
        }

        private static string GetFileHash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha256 = SHA256.Create())
                return Convert.ToBase64String(sha256.ComputeHash(stream));
        }

        private static string GetStreamHashHex(Stream stream)
        {
            using (var sha256 = SHA256.Create())
                return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", "");
        }

        private string ResolvePackageDestination(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
                throw new InvalidDataException("内嵌 BepInEx 安装包包含无效路径: " +
                    relativePath);
            foreach (string segment in relativePath.Split('\\'))
                if (segment.Length == 0 || segment == "." || segment == ".." ||
                    segment.IndexOf(':') >= 0)
                    throw new InvalidDataException("内嵌 BepInEx 安装包包含无效路径: " +
                        relativePath);

            string gameRoot = Path.GetFullPath(_state.GameDir);
            string rootPrefix = gameRoot.EndsWith(Path.DirectorySeparatorChar.ToString(),
                StringComparison.Ordinal) ? gameRoot : gameRoot + Path.DirectorySeparatorChar;
            string destination = Path.GetFullPath(Path.Combine(gameRoot, relativePath));
            if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("内嵌 BepInEx 安装包路径越界: " +
                    relativePath);
            return destination;
        }

        /// <summary>找 winhttp.dll 在 zip 中的目录前缀（"" 表示就在根）。</summary>
        private static string DetectZipRoot(ZipArchive zip)
        {
            foreach (var e in zip.Entries)
            {
                if (e.Name.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase))
                {
                    var full = e.FullName.Replace('/', '\\');
                    return full.Substring(0, full.Length - e.Name.Length);
                }
            }
            throw new InvalidDataException("内嵌 BepInEx 安装包缺少 winhttp.dll。");
        }

        private static void EnsureGameNotRunning()
        {
            if (IsGameRunning())
                throw new InvalidOperationException("检测到游戏正在运行，DLL 被占用。请先关闭游戏再操作。");
        }
    }
}
