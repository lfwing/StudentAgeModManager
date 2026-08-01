using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using StudentAge.WorkshopBridge;

namespace StudentAgeModManager.Core
{
    /// <summary>
    /// 扫描 BepInEx 插件目录，但绝不执行被扫描 DLL 中的代码。插件元数据在可卸载的
    /// ReflectionOnly AppDomain 中读取；任意损坏或缺少依赖的 DLL 都只会被跳过。
    /// </summary>
    public sealed class LocalPluginScanner
    {
        private const long MaxAssemblyBytes = 128L * 1024L * 1024L;
        private const long MaxTotalAssemblyBytes = 512L * 1024L * 1024L;
        private const int MaxDllsPerUnit = 2048;
        private const int MaxScannedDlls = 4096;
        private const string MetadataHostDirectoryPrefix =
            "StudentAge.ModManager.Metadata.";
        private const string MetadataHostFileName = "ModManager.exe";

        public List<LocalPluginUnit> Scan(string gameDir)
        {
            return Scan(gameDir, null);
        }

        public List<LocalPluginUnit> Scan(string gameDir,
            IEnumerable<WorkshopManagedItem> managedWorkshopItems)
        {
            if (string.IsNullOrWhiteSpace(gameDir))
                throw new ArgumentException("游戏目录不能为空。", nameof(gameDir));

            string gameRoot = Path.GetFullPath(gameDir);
            string pluginRoot = Path.Combine(gameRoot, "BepInEx", "plugins");
            string disabledRoot = Path.Combine(gameRoot, "BepInEx", "ModManager", "disabled");
            List<WorkshopManagedItem> workshopItems = managedWorkshopItems?.ToList();
            var result = new List<LocalPluginUnit>();
            if (!Directory.Exists(pluginRoot) && !Directory.Exists(disabledRoot) &&
                (workshopItems == null || workshopItems.Count == 0))
                return result;

            AppDomain metadataDomain = null;
            string metadataHostPath = null;
            try
            {
                PluginMetadataProbeClient probe;
                try
                {
                    metadataHostPath = CreateMetadataHostCopy();
                    metadataDomain = CreateMetadataDomain(
                        Path.GetDirectoryName(metadataHostPath));
                    probe = CreateMetadataProbe(metadataDomain, metadataHostPath);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "无法初始化隔离的插件元数据扫描器。", ex);
                }
                var budget = new ScanBudget();

                ScanEnabledLocalUnits(gameRoot, pluginRoot, probe, budget, result);
                if (workshopItems == null)
                    ScanWorkshopUnits(gameRoot, pluginRoot, probe, budget, result);
                else
                    ScanManagedWorkshopUnits(gameRoot, workshopItems, probe, budget, result);
                ScanDisabledLocalUnits(gameRoot, disabledRoot, probe, budget, result);
            }
            finally
            {
                if (metadataDomain != null)
                    try { AppDomain.Unload(metadataDomain); } catch { }
                CleanupMetadataHostCopy(metadataHostPath);
            }

            foreach (var group in result
                .Where(unit => unit.Source == LocalPluginSource.Local)
                .GroupBy(unit => unit.UnitKey, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1))
            {
                foreach (var unit in group) unit.HasPathConflict = true;
            }

            MarkGuidConflicts(result);

            return result
                .OrderBy(unit => unit.Source)
                .ThenBy(unit => unit.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(unit => unit.UnitKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void ScanManagedWorkshopUnits(string gameRoot,
            IEnumerable<WorkshopManagedItem> items, PluginMetadataProbeClient probe,
            ScanBudget budget, List<LocalPluginUnit> result)
        {
            foreach (WorkshopManagedItem item in items.Where(value => value != null)
                .OrderBy(value => value.WorkshopId, StringComparer.Ordinal))
            {
                LocalPluginUnit unit = null;
                string relative = CombineRelative("Steam Workshop", item.WorkshopId,
                    "BepInEx", "plugins");
                if (item.IsValidBridgePackage && !string.IsNullOrWhiteSpace(item.PluginRootPath))
                {
                    unit = ScanUnit(gameRoot, item.PluginRootPath, relative, relative,
                        ".workshop/" + item.WorkshopId, true, !item.IsEnabled,
                        LocalPluginSource.SteamWorkshop, item.WorkshopId, probe, budget);
                }
                if (unit == null)
                {
                    unit = new LocalPluginUnit
                    {
                        UnitKey = ".workshop/" + item.WorkshopId,
                        DisplayName = "Steam Workshop " + item.WorkshopId,
                        DisplayVersion = "未知",
                        RelativePath = relative,
                        EnabledRelativePath = relative,
                        IsDirectory = true,
                        IsDisabled = !item.IsEnabled,
                        Source = LocalPluginSource.SteamWorkshop,
                        WorkshopId = item.WorkshopId,
                        DllCount = item.DllCount,
                    };
                }

                unit.IsDisabled = !item.IsEnabled;
                unit.IsWorkshopSubscribed = item.IsSubscribed;
                unit.IsWorkshopDownloaded = item.IsDownloaded;
                unit.IsWorkshopConnected = item.IsConnected;
                unit.HasWorkshopManifest = item.HasBridgeManifest;
                unit.IsWorkshopPackageValid = item.IsValidBridgePackage;
                unit.WorkshopValidationError = item.ValidationError;
                unit.WorkshopContentPath = item.ContentPath;
                result.Add(unit);
            }
        }

        private static void MarkGuidConflicts(IList<LocalPluginUnit> units)
        {
            var conflicts = units
                .Where(IsActiveLoadCandidate)
                .SelectMany(unit => unit.Plugins
                    .Where(plugin => !string.IsNullOrWhiteSpace(plugin.Guid))
                    .Select(plugin => new { Unit = unit, Guid = plugin.Guid.Trim() }))
                .GroupBy(item => item.Guid, StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    Guid = group.Key,
                    Units = group.Select(item => item.Unit).Distinct().ToList(),
                })
                .Where(group => group.Units.Count > 1)
                .ToList();

            foreach (var conflict in conflicts)
            {
                foreach (LocalPluginUnit unit in conflict.Units)
                {
                    unit.HasGuidConflict = true;
                    if (!unit.ConflictingPluginGuids.Contains(conflict.Guid,
                        StringComparer.OrdinalIgnoreCase))
                        unit.ConflictingPluginGuids.Add(conflict.Guid);
                }
            }
        }

        private static bool IsActiveLoadCandidate(LocalPluginUnit unit)
        {
            if (unit == null || unit.IsDisabled) return false;
            return unit.Source != LocalPluginSource.SteamWorkshop || unit.IsWorkshopConnected;
        }

        private static string CreateMetadataHostCopy()
        {
            Assembly hostAssembly = typeof(PluginMetadataProbe).Assembly;
            string hostPath = hostAssembly.Location;
            if (string.IsNullOrEmpty(hostPath))
                throw new InvalidOperationException("无法确定管理器程序集路径。");

            // Read the already-running manager, then write only its primary byte stream into a
            // private temporary directory. This deliberately drops Zone.Identifier (MOTW) and
            // prevents a renamed executable from binding to a different sibling ModManager.exe.
            byte[] hostBytes = File.ReadAllBytes(hostPath);
            string hostDirectory = Path.Combine(Path.GetTempPath(),
                MetadataHostDirectoryPrefix + Guid.NewGuid().ToString("N"));
            string trustedHostPath = Path.Combine(hostDirectory, MetadataHostFileName);
            try
            {
                Directory.CreateDirectory(hostDirectory);
                using (var stream = new FileStream(trustedHostPath, FileMode.CreateNew,
                    FileAccess.Write, FileShare.Read))
                {
                    stream.Write(hostBytes, 0, hostBytes.Length);
                }
                return trustedHostPath;
            }
            catch
            {
                CleanupMetadataHostCopy(trustedHostPath);
                throw;
            }
        }

        private static void CleanupMetadataHostCopy(string trustedHostPath)
        {
            if (string.IsNullOrWhiteSpace(trustedHostPath)) return;
            try { File.Delete(trustedHostPath); } catch { }

            string hostDirectory;
            try { hostDirectory = Path.GetDirectoryName(trustedHostPath); }
            catch { return; }
            if (string.IsNullOrWhiteSpace(hostDirectory)) return;
            // The directory is unique and should contain only the host copy. Never recurse if an
            // unexpected file or reparse point appeared while the disposable domain was alive.
            try { Directory.Delete(hostDirectory, false); } catch { }
        }

        private static AppDomain CreateMetadataDomain(string hostDirectory)
        {
            if (string.IsNullOrWhiteSpace(hostDirectory))
                throw new ArgumentException("扫描器宿主目录不能为空。", nameof(hostDirectory));
            var setup = new AppDomainSetup
            {
                ApplicationBase = Path.GetFullPath(hostDirectory),
                ShadowCopyFiles = "false",
            };
            return AppDomain.CreateDomain("StudentAge.PluginMetadata." + Guid.NewGuid().ToString("N"),
                null, setup);
        }

        private static PluginMetadataProbeClient CreateMetadataProbe(AppDomain metadataDomain,
            string trustedHostPath)
        {
            if (metadataDomain == null) throw new ArgumentNullException(nameof(metadataDomain));
            if (string.IsNullOrWhiteSpace(trustedHostPath))
                throw new ArgumentException("扫描器宿主路径不能为空。", nameof(trustedHostPath));

            // Activate by the exact trusted-copy path. Name-based activation would probe
            // <ApplicationBase>/ModManager.exe and could load a stale or unrelated sibling when
            // the running executable was renamed by a browser (for example, "ModManager (4).exe").
            object remoteProbe = metadataDomain.CreateInstanceFromAndUnwrap(
                trustedHostPath, typeof(PluginMetadataProbe).FullName);
            return new PluginMetadataProbeClient(remoteProbe);
        }

        private static void ScanEnabledLocalUnits(string gameRoot, string pluginRoot,
            PluginMetadataProbeClient probe, ScanBudget budget, List<LocalPluginUnit> result)
        {
            if (!Directory.Exists(pluginRoot) || IsReparsePoint(pluginRoot)) return;

            foreach (string entry in EnumerateTopLevel(pluginRoot))
            {
                string name = Path.GetFileName(entry);
                if (string.Equals(name, ".workshop", StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsSafeUnitName(name) || IsReparsePoint(entry)) continue;

                bool isDirectory = Directory.Exists(entry);
                if (!isDirectory && (!File.Exists(entry) ||
                    !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                    continue;

                string relative = CombineRelative("BepInEx", "plugins", name);
                var unit = ScanUnit(gameRoot, entry, relative, relative, name, isDirectory,
                    false, LocalPluginSource.Local, null, probe, budget);
                if (unit != null) result.Add(unit);
            }
        }

        private static void ScanDisabledLocalUnits(string gameRoot, string disabledRoot,
            PluginMetadataProbeClient probe, ScanBudget budget, List<LocalPluginUnit> result)
        {
            if (!Directory.Exists(disabledRoot) || IsReparsePoint(disabledRoot)) return;

            foreach (string entry in EnumerateTopLevel(disabledRoot))
            {
                string name = Path.GetFileName(entry);
                if (!IsSafeUnitName(name) || IsReparsePoint(entry)) continue;

                bool isDirectory = Directory.Exists(entry);
                if (!isDirectory && (!File.Exists(entry) ||
                    !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                    continue;

                string currentRelative = CombineRelative("BepInEx", "ModManager", "disabled", name);
                string enabledRelative = CombineRelative("BepInEx", "plugins", name);
                var unit = ScanUnit(gameRoot, entry, currentRelative, enabledRelative, name,
                    isDirectory, true, LocalPluginSource.Local, null, probe, budget);
                if (unit != null) result.Add(unit);
            }
        }

        private static void ScanWorkshopUnits(string gameRoot, string pluginRoot,
            PluginMetadataProbeClient probe, ScanBudget budget, List<LocalPluginUnit> result)
        {
            string workshopRoot = Path.Combine(pluginRoot, ".workshop");
            if (!Directory.Exists(workshopRoot) || IsReparsePoint(workshopRoot)) return;

            foreach (string entry in EnumerateTopLevel(workshopRoot))
            {
                string workshopId = Path.GetFileName(entry);
                if (!Directory.Exists(entry) || !IsReparsePoint(entry) ||
                    !IsCanonicalWorkshopId(workshopId))
                    continue;

                string relative = CombineRelative("BepInEx", "plugins", ".workshop", workshopId);
                var unit = ScanUnit(gameRoot, entry, relative, relative,
                    ".workshop/" + workshopId, true, false,
                    LocalPluginSource.SteamWorkshop, workshopId, probe, budget);
                if (unit != null)
                {
                    unit.IsWorkshopSubscribed = true;
                    unit.IsWorkshopDownloaded = true;
                    unit.IsWorkshopConnected = true;
                    unit.HasWorkshopManifest = true;
                    unit.IsWorkshopPackageValid = true;
                    result.Add(unit);
                }
            }
        }

        private static LocalPluginUnit ScanUnit(string gameRoot, string path,
            string currentRelativePath, string enabledRelativePath, string unitKey,
            bool isDirectory, bool isDisabled, LocalPluginSource source, string workshopId,
            PluginMetadataProbeClient probe, ScanBudget budget)
        {
            var dllPaths = isDirectory
                ? EnumerateDllsWithoutFollowingLinks(path)
                : new List<string> { path };
            if (dllPaths.Count == 0) return null;

            var plugins = new List<ScannedPlugin>();
            DateTime? newestWriteUtc = null;
            foreach (string dllPath in dllPaths)
            {
                try
                {
                    var info = new FileInfo(dllPath);
                    if (!info.Exists || info.Length <= 0 || info.Length > MaxAssemblyBytes ||
                        !budget.TryConsume(info.Length))
                        continue;
                    if (newestWriteUtc == null || info.LastWriteTimeUtc > newestWriteUtc)
                        newestWriteUtc = info.LastWriteTimeUtc;
                    var discovered = probe.Inspect(dllPath, gameRoot);
                    if (discovered == null) continue;
                    foreach (var plugin in discovered)
                    {
                        plugin.DllFileName = MakeDllDisplayPath(path, dllPath, isDirectory);
                        plugins.Add(plugin);
                    }
                }
                catch
                {
                    // 单个 DLL 不可读、不是托管程序集或依赖缺失时继续扫描其他文件。
                }
            }
            if (plugins.Count == 0) return null;

            plugins = plugins
                .GroupBy(plugin => (plugin.Guid ?? "") + "\n" +
                                   (plugin.Name ?? "") + "\n" + plugin.DllFileName,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            string[] names = plugins.Select(plugin => CleanDisplayValue(plugin.Name))
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            string displayName = names.Length == 0 ? unitKey : names[0];
            if (names.Length > 1) displayName += " 等 " + names.Length + " 个插件";

            string[] versions = plugins.Select(plugin => CleanDisplayValue(plugin.Version))
                .Where(version => version.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new LocalPluginUnit
            {
                UnitKey = unitKey,
                DisplayName = displayName,
                DisplayVersion = versions.Length == 1 ? versions[0] :
                    (versions.Length > 1 ? "多个版本" : "未知"),
                RelativePath = currentRelativePath,
                EnabledRelativePath = enabledRelativePath,
                IsDirectory = isDirectory,
                IsDisabled = isDisabled,
                LastWriteTimeUtc = newestWriteUtc,
                Source = source,
                WorkshopId = workshopId,
                DllCount = dllPaths.Count,
                Plugins = plugins,
            };
        }

        private static List<string> EnumerateDllsWithoutFollowingLinks(string root)
        {
            var result = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root); // Workshop 根本身允许是 Bridge 创建的联接。

            while (pending.Count > 0 && result.Count < MaxDllsPerUnit)
            {
                string current = pending.Pop();
                try
                {
                    foreach (string file in Directory.EnumerateFiles(current, "*.dll",
                        SearchOption.TopDirectoryOnly))
                    {
                        if (result.Count >= MaxDllsPerUnit) break;
                        if (!IsReparsePoint(file)) result.Add(file);
                    }

                    foreach (string directory in Directory.EnumerateDirectories(current, "*",
                        SearchOption.TopDirectoryOnly))
                    {
                        if (!IsReparsePoint(directory)) pending.Push(directory);
                    }
                }
                catch
                {
                    // 无权限、断开的工坊联接或目录在扫描中被 Steam 替换。
                }
            }
            return result;
        }

        private static IEnumerable<string> EnumerateTopLevel(string directory)
        {
            try { return Directory.GetFileSystemEntries(directory); }
            catch { return new string[0]; }
        }

        private static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return true; }
        }

        private static bool IsCanonicalWorkshopId(string value)
        {
            ulong id;
            return !string.IsNullOrEmpty(value) &&
                   value.All(c => c >= '0' && c <= '9') &&
                   ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) &&
                   id != 0 && string.Equals(value, id.ToString(CultureInfo.InvariantCulture),
                       StringComparison.Ordinal);
        }

        private static bool IsSafeUnitName(string value)
        {
            return !string.IsNullOrEmpty(value) && value != "." && value != ".." &&
                   value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                   value.IndexOf(Path.DirectorySeparatorChar) < 0 &&
                   value.IndexOf(Path.AltDirectorySeparatorChar) < 0;
        }

        private static string CleanDisplayValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        }

        private static string MakeDllDisplayPath(string unitRoot, string dllPath,
            bool isDirectory)
        {
            if (!isDirectory) return Path.GetFileName(dllPath);
            string prefix = Path.GetFullPath(unitRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(dllPath);
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(prefix.Length).Replace('\\', '/')
                : Path.GetFileName(dllPath);
        }

        private static string CombineRelative(params string[] parts)
        {
            return Path.Combine(parts);
        }

        /// <summary>
        /// Keeps manager-defined DTOs out of the remoting boundary. The manager may itself have
        /// been loaded from a renamed path or a non-default load context, so directly casting the
        /// transparent proxy (or its List&lt;ScannedPlugin&gt; result) can bind to a sibling assembly.
        /// </summary>
        private sealed class PluginMetadataProbeClient
        {
            private readonly object _remoteProbe;
            private readonly MethodInfo _inspectValuesMethod;

            public PluginMetadataProbeClient(object remoteProbe)
            {
                _remoteProbe = remoteProbe ?? throw new ArgumentNullException(nameof(remoteProbe));
                _inspectValuesMethod = remoteProbe.GetType().GetMethod("InspectValues",
                    new[] { typeof(string), typeof(string) });
                if (_inspectValuesMethod == null)
                    throw new MissingMethodException(remoteProbe.GetType().FullName,
                        "InspectValues");
            }

            public List<ScannedPlugin> Inspect(string assemblyPath, string gameRoot)
            {
                object raw;
                try
                {
                    raw = _inspectValuesMethod.Invoke(_remoteProbe,
                        new object[] { assemblyPath, gameRoot });
                }
                catch (TargetInvocationException ex)
                {
                    if (ex.InnerException != null) throw ex.InnerException;
                    throw;
                }

                var rows = raw as string[][];
                if (rows == null)
                    throw new InvalidDataException("隔离扫描器返回了无效的元数据格式。");
                var result = new List<ScannedPlugin>(rows.Length);
                foreach (string[] row in rows)
                {
                    if (row == null || row.Length != 3)
                        throw new InvalidDataException("隔离扫描器返回了无效的插件记录。");
                    result.Add(new ScannedPlugin
                    {
                        Guid = row[0],
                        Name = row[1],
                        Version = row[2],
                    });
                }
                return result;
            }
        }

        private sealed class ScanBudget
        {
            private int _dllCount;
            private long _totalBytes;

            public bool TryConsume(long bytes)
            {
                if (_dllCount >= MaxScannedDlls || bytes < 0 ||
                    _totalBytes > MaxTotalAssemblyBytes - bytes)
                    return false;
                _dllCount++;
                _totalBytes += bytes;
                return true;
            }
        }
    }

    /// <summary>
    /// 运行在临时 AppDomain 中的元数据读取器。ReflectionOnlyLoad 和
    /// CustomAttributeData 均不会实例化插件类型或执行其静态初始化代码。
    /// </summary>
    public sealed class PluginMetadataProbe : MarshalByRefObject
    {
        private string[] _searchDirectories;
        private readonly Dictionary<string, List<ScannedPlugin>> _metadataByAssemblyIdentity =
            new Dictionary<string, List<ScannedPlugin>>(StringComparer.OrdinalIgnoreCase);

        public string[][] InspectValues(string assemblyPath, string gameRoot)
        {
            return Inspect(assemblyPath, gameRoot)
                .Select(plugin => new[] { plugin.Guid, plugin.Name, plugin.Version })
                .ToArray();
        }

        public List<ScannedPlugin> Inspect(string assemblyPath, string gameRoot)
        {
            string assemblyIdentity = null;
            try { assemblyIdentity = AssemblyName.GetAssemblyName(assemblyPath).FullName; }
            catch { }
            List<ScannedPlugin> cached;
            if (!string.IsNullOrEmpty(assemblyIdentity) &&
                _metadataByAssemblyIdentity.TryGetValue(assemblyIdentity, out cached))
                return ClonePlugins(cached);

            _searchDirectories = BuildSearchDirectories(assemblyPath, gameRoot);

            ResolveEventHandler resolver = ResolveReflectionOnlyAssembly;
            AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += resolver;
            try
            {
                Assembly assembly = Assembly.ReflectionOnlyLoad(File.ReadAllBytes(assemblyPath));
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(type => type != null).ToArray();
                }

                var result = new List<ScannedPlugin>();
                foreach (Type type in types)
                {
                    IList<CustomAttributeData> attributes;
                    try { attributes = CustomAttributeData.GetCustomAttributes(type); }
                    catch { continue; }

                    foreach (CustomAttributeData attribute in attributes)
                    {
                        string attributeName;
                        try { attributeName = attribute.Constructor.DeclaringType.FullName; }
                        catch { continue; }
                        if (!string.Equals(attributeName, "BepInEx.BepInPlugin",
                            StringComparison.Ordinal))
                            continue;

                        var args = attribute.ConstructorArguments;
                        if (args.Count < 3) continue;
                        result.Add(new ScannedPlugin
                        {
                            Guid = args[0].Value as string,
                            Name = args[1].Value as string,
                            Version = args[2].Value as string,
                        });
                    }
                }
                if (!string.IsNullOrEmpty(assemblyIdentity))
                    _metadataByAssemblyIdentity[assemblyIdentity] = ClonePlugins(result);
                return result;
            }
            catch
            {
                var empty = new List<ScannedPlugin>();
                if (!string.IsNullOrEmpty(assemblyIdentity))
                    _metadataByAssemblyIdentity[assemblyIdentity] = empty;
                return empty;
            }
            finally
            {
                AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve -= resolver;
            }
        }

        private static List<ScannedPlugin> ClonePlugins(IEnumerable<ScannedPlugin> source)
        {
            return source.Select(plugin => new ScannedPlugin
            {
                Guid = plugin.Guid,
                Name = plugin.Name,
                Version = plugin.Version,
                DllFileName = plugin.DllFileName,
            }).ToList();
        }

        private static string[] BuildSearchDirectories(string assemblyPath, string gameRoot)
        {
            var directories = new List<string>();
            string pluginRoot = Path.GetFullPath(Path.Combine(gameRoot, "BepInEx", "plugins"))
                .TrimEnd('\\', '/');
            string current = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
            if (!string.IsNullOrEmpty(current)) directories.Add(current);
            while (!string.IsNullOrEmpty(current))
            {
                string normalized = current.TrimEnd('\\', '/');
                if (!string.Equals(normalized, pluginRoot, StringComparison.OrdinalIgnoreCase) &&
                    !normalized.StartsWith(pluginRoot + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    break;
                if (!directories.Contains(current, StringComparer.OrdinalIgnoreCase))
                    directories.Add(current);
                if (string.Equals(normalized, pluginRoot, StringComparison.OrdinalIgnoreCase)) break;
                current = Path.GetDirectoryName(normalized);
            }
            directories.Add(Path.Combine(gameRoot, "BepInEx", "core"));
            directories.Add(Path.Combine(gameRoot, "StudentAge_Data", "Managed"));
            return directories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private Assembly ResolveReflectionOnlyAssembly(object sender, ResolveEventArgs args)
        {
            try
            {
                var requested = new AssemblyName(args.Name);
                foreach (Assembly loaded in AppDomain.CurrentDomain.ReflectionOnlyGetAssemblies())
                {
                    if (string.Equals(loaded.GetName().Name, requested.Name,
                        StringComparison.OrdinalIgnoreCase))
                        return loaded;
                }

                // Framework assemblies should resolve from the GAC before probing game files.
                try { return Assembly.ReflectionOnlyLoad(args.Name); }
                catch { }

                foreach (string directory in _searchDirectories ?? new string[0])
                {
                    if (string.IsNullOrEmpty(directory)) continue;
                    string candidate = Path.Combine(directory, requested.Name + ".dll");
                    if (!File.Exists(candidate)) continue;
                    try { return Assembly.ReflectionOnlyLoad(File.ReadAllBytes(candidate)); }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        public override object InitializeLifetimeService()
        {
            return null;
        }
    }
}
