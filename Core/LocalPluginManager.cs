using System;
using System.Globalization;
using System.IO;

namespace StudentAgeModManager.Core
{
    /// <summary>
    /// 只负责启用/禁用本地未收录插件。操作粒度是 plugins 根目录中的一个直接子项，
    /// 从不删除文件，也不操作 Workshop Bridge 创建的目录联接。
    /// 路径冲突（同名单元同时存在于启用区与禁用区，常见于禁用后开发构建又
    /// 重新部署）不再是死胡同：带冲突标记的单元仍可启用/禁用，被顶替的另一份
    /// 副本会先整体归档到 ModManager/conflict-backup，随后照常移动。
    /// </summary>
    public sealed class LocalPluginManager
    {
        private readonly string _pluginRoot;
        private readonly string _disabledRoot;
        private readonly string _conflictBackupRoot;

        public LocalPluginManager(LocalState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            _pluginRoot = Path.GetFullPath(Path.Combine(state.GameDir, "BepInEx", "plugins"));
            _disabledRoot = Path.GetFullPath(state.DisabledDir);
            _conflictBackupRoot = Path.GetFullPath(state.ConflictBackupDir);
        }

        /// <summary>返回被归档的冲突副本路径；无冲突处理时为 null。</summary>
        public string Disable(LocalPluginUnit unit)
        {
            if (unit == null) throw new ArgumentNullException(nameof(unit));
            if (unit.IsDisabled) return null;
            EnsureLocalUnit(unit);
            EnsureGameNotRunning();

            string source = ResolveImmediateChild(_pluginRoot, unit.RelativePath);
            string target = Path.Combine(_disabledRoot, Path.GetFileName(source));
            string archived = unit.HasPathConflict ? ArchiveConflictTarget(target) : null;
            MoveWithoutOverwrite(source, target, unit.IsDirectory);
            return archived;
        }

        /// <summary>返回被归档的冲突副本路径；无冲突处理时为 null。</summary>
        public string Enable(LocalPluginUnit unit)
        {
            if (unit == null) throw new ArgumentNullException(nameof(unit));
            if (!unit.IsDisabled) return null;
            EnsureLocalUnit(unit);
            EnsureGameNotRunning();

            string source = ResolveImmediateChild(_disabledRoot, unit.RelativePath);
            string target = ResolveImmediateChild(_pluginRoot, unit.EnabledRelativePath);
            string archived = unit.HasPathConflict ? ArchiveConflictTarget(target) : null;
            MoveWithoutOverwrite(source, target, unit.IsDirectory);
            return archived;
        }

        /// <summary>
        /// 冲突解决只在扫描确认过 HasPathConflict 的单元上执行；目标此刻不存在
        /// 则视作冲突已被外部清理，退回普通的不覆盖移动。归档目录带时间戳，
        /// 同秒内重名再追加序号，保证从不覆盖任何已归档内容。
        /// </summary>
        private string ArchiveConflictTarget(string target)
        {
            bool isDirectory = Directory.Exists(target);
            if (!isDirectory && !File.Exists(target)) return null;
            if (IsReparsePoint(target))
                throw new InvalidDataException(
                    "冲突位置是目录联接，可能由工坊同步管理，管理器不会移动它：" + target);

            Directory.CreateDirectory(_conflictBackupRoot);
            string baseName = Path.GetFileName(target.TrimEnd('\\', '/')) + "-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string destination = Path.Combine(_conflictBackupRoot, baseName);
            for (int sequence = 2;
                 Directory.Exists(destination) || File.Exists(destination);
                 sequence++)
                destination = Path.Combine(_conflictBackupRoot, baseName + "-" + sequence);

            if (isDirectory) Directory.Move(target, destination);
            else File.Move(target, destination);
            return destination;
        }

        private static void EnsureLocalUnit(LocalPluginUnit unit)
        {
            if (unit.Source != LocalPluginSource.Local)
                throw new InvalidOperationException(
                    "Steam 工坊 Mod 请使用工坊开关或游戏“本地”页管理，管理器不会移动其文件。");
        }

        private static string ResolveImmediateChild(string expectedRoot, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new InvalidDataException("插件路径为空。");

            string full = Path.GetFullPath(Path.Combine(expectedRoot,
                Path.GetFileName(relativePath.TrimEnd('\\', '/'))));
            string parent = Path.GetDirectoryName(full.TrimEnd('\\', '/'));
            if (!string.Equals(parent, expectedRoot.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("插件路径越界。");
            return full;
        }

        private static void MoveWithoutOverwrite(string source, string target, bool isDirectory)
        {
            bool sourceExists = isDirectory ? Directory.Exists(source) : File.Exists(source);
            if (!sourceExists)
                throw new FileNotFoundException("插件不存在或已被移动。", source);
            if (IsReparsePoint(source))
                throw new InvalidDataException("该目录由工坊同步管理，管理器不会移动它。");
            if (Directory.Exists(target) || File.Exists(target))
                throw new IOException("目标位置已存在同名插件，未执行覆盖: " + target);

            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (isDirectory) Directory.Move(source, target);
            else File.Move(source, target);
        }

        private static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return true; }
        }

        private static void EnsureGameNotRunning()
        {
            if (ModInstaller.IsGameRunning())
                throw new InvalidOperationException(
                    "检测到游戏正在运行，DLL 可能被占用。请先关闭游戏再操作。");
        }
    }
}
