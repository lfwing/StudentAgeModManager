using System;
using System.IO;

namespace StudentAgeModManager.Core
{
    /// <summary>管理器在游戏目录中的本地路径。</summary>
    public class LocalState
    {
        public string GameDir { get; }
        public string StateDir => Path.Combine(GameDir, "BepInEx", "ModManager");

        /// <summary>
        /// 本地未收录插件的禁用区。旧版管理器也曾把直装插件移动到这里；
        /// 扫描器会将其中仍含 BepInPlugin 的内容统一显示为本地未收录插件。
        /// </summary>
        public string DisabledDir => Path.Combine(StateDir, "disabled");

        /// <summary>
        /// 路径冲突解决时被顶替副本的归档区。管理器从不删除文件：
        /// 冲突下启用/禁用会先把目标位置的另一份副本整体搬到这里。
        /// </summary>
        public string ConflictBackupDir => Path.Combine(StateDir, "conflict-backup");

        public string WorkshopMetadataCachePath =>
            Path.Combine(StateDir, "workshop-metadata.json");

        public LocalState(string gameDir)
        {
            if (string.IsNullOrWhiteSpace(gameDir))
                throw new ArgumentException("游戏目录不能为空。", nameof(gameDir));
            GameDir = Path.GetFullPath(gameDir);
        }

        /// <summary>宽松版本比较：去掉 v 前缀，逐段数字比较。返回 -1/0/1。</summary>
        public static int VersionCompare(string a, string b)
        {
            a = (a ?? "").TrimStart('v', 'V');
            b = (b ?? "").TrimStart('v', 'V');
            var pa = a.Split('.', '-');
            var pb = b.Split('.', '-');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int va = 0, vb = 0;
                if (i < pa.Length) int.TryParse(pa[i], out va);
                if (i < pb.Length) int.TryParse(pb[i], out vb);
                if (va != vb) return va < vb ? -1 : 1;
            }
            return 0;
        }
    }
}
