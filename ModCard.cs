using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using StudentAgeModManager.Core;

namespace StudentAgeModManager
{
    /// <summary>工坊目录条目或本地已安装插件的卡片控件。</summary>
    public class ModCard : Panel
    {
        private static readonly Color SourceColor = Color.RoyalBlue;
        private static readonly Color PositiveColor = Color.FromArgb(45, 135, 70);
        private static readonly Color NegativeColor = Color.Firebrick;
        // 未收录只表示不在推荐列表，不是异常，用中性灰而不是红色。
        private static readonly Color NeutralColor = Color.FromArgb(130, 130, 130);
        private static readonly Font TitleFont =
            new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
        private static readonly Font CardFont = new Font("Microsoft YaHei UI", 9f);

        private readonly Label _title = new Label();
        private readonly Label _desc = new Label();
        private readonly Panel _statusPanel = new Panel();
        private readonly Label _status = new Label();
        private readonly Label _statusRegistration = new Label();
        private readonly Label _statusState = new Label();
        private readonly Button _btnMain = new Button();
        private readonly Button _btnToggle = new Button();

        private BoundKind _boundKind;
        private bool _busy;

        public ModEntry Entry { get; private set; }
        public LocalPluginUnit LocalUnit { get; private set; }
        public event Action<string> WorkshopPageClicked;
        public event Action<LocalPluginUnit> ToggleLocalClicked;

        public string StatusText => string.Join(" · ",
            new[] { _status, _statusRegistration, _statusState }
                .Where(label => !string.IsNullOrEmpty(label.Text))
                .Select(label => label.Text.TrimStart('·', ' ')));

        private enum BoundKind
        {
            None,
            WorkshopIndex,
            LocalPlugin,
        }

        public ModCard()
        {
            Size = new Size(560, 96);
            BorderStyle = BorderStyle.FixedSingle;
            BackColor = Color.White;
            Margin = new Padding(6);

            _title.Font = TitleFont;
            _title.Location = new Point(12, 8);
            _title.Size = new Size(258, 24);
            _title.AutoEllipsis = true;

            _statusPanel.Location = new Point(276, 8);
            _statusPanel.Size = new Size(271, 24);
            _statusPanel.BackColor = Color.White;
            SetupStatusLabel(_status);
            SetupStatusLabel(_statusRegistration);
            SetupStatusLabel(_statusState);
            _statusPanel.Controls.AddRange(new Control[]
                { _status, _statusRegistration, _statusState });

            _desc.Font = CardFont;
            _desc.ForeColor = Color.FromArgb(90, 90, 90);
            _desc.Location = new Point(13, 34);
            // 高度由 UpdateDescriptionLayout 按实际行数决定；AutoEllipsis 会强制单行，因此不使用。
            _desc.Size = new Size(534, 20);
            _desc.AutoEllipsis = false;

            SetupButton(_btnMain, 12, 120);
            SetupButton(_btnToggle, 12, 84);

            _btnMain.Click += (s, e) =>
            {
                string workshopId;
                if (TryGetWorkshopId(out workshopId))
                    WorkshopPageClicked?.Invoke(workshopId);
            };
            _btnToggle.Click += (s, e) =>
            {
                if (LocalUnit != null) ToggleLocalClicked?.Invoke(LocalUnit);
            };

            Controls.AddRange(new Control[]
            {
                _title, _statusPanel, _desc, _btnMain, _btnToggle,
            });
        }

        private static void SetupStatusLabel(Label label)
        {
            label.Font = CardFont;
            label.TextAlign = ContentAlignment.MiddleCenter;
            label.AutoEllipsis = false;
            label.Visible = false;
        }

        private static void SetupButton(Button button, int x, int width)
        {
            button.Location = new Point(x, 60);
            button.Size = new Size(width, 27);
            button.Font = CardFont;
            button.UseVisualStyleBackColor = true;
        }

        public void Bind(ModEntry entry, LocalPluginUnit installedUnit = null)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (installedUnit != null &&
                installedUnit.Source != LocalPluginSource.SteamWorkshop)
                throw new ArgumentException("工坊索引只能与 Steam 工坊插件单元合并。",
                    nameof(installedUnit));
            Entry = entry;
            LocalUnit = installedUnit;
            _boundKind = BoundKind.WorkshopIndex;
            ApplyVisualState();
        }

        public void BindLocal(LocalPluginUnit unit)
        {
            if (unit == null) throw new ArgumentNullException(nameof(unit));
            Entry = null;
            LocalUnit = unit;
            _boundKind = BoundKind.LocalPlugin;
            ApplyVisualState();
        }

        private void ApplyVisualState()
        {
            ResetButtons();
            if (_boundKind == BoundKind.WorkshopIndex)
                ApplyWorkshopIndexState();
            else if (_boundKind == BoundKind.LocalPlugin)
                ApplyLocalPluginState();
            UpdateDescriptionLayout();
            ApplyBusyState();
        }

        /// <summary>简介一行放不下时自动换行，按钮和卡片高度跟随实际行数下移。</summary>
        private void UpdateDescriptionLayout()
        {
            int lineHeight = TextRenderer.MeasureText("测", CardFont).Height;
            int textHeight = string.IsNullOrEmpty(_desc.Text)
                ? lineHeight
                : TextRenderer.MeasureText(_desc.Text, CardFont,
                    new Size(_desc.Width, int.MaxValue), TextFormatFlags.WordBreak).Height;
            int lines = Math.Max(1, Math.Min(3,
                (textHeight + lineHeight - 1) / lineHeight));
            _desc.Height = lines * lineHeight + 3;
            int buttonTop = _desc.Bottom + 6;
            _btnMain.Top = buttonTop;
            _btnToggle.Top = buttonTop;
            Height = buttonTop + _btnMain.Height + 9;
        }

        private void ApplyWorkshopIndexState()
        {
            _title.Text = string.IsNullOrWhiteSpace(Entry.name) ? Entry.id : Entry.name;
            string workshopId;
            bool valid = WorkshopItem.TryGetId(Entry, out workshopId);
            bool discovered = valid && LocalUnit != null;

            if (discovered)
            {
                // 已安装时也保留索引简介，只在其后追加本地状态，避免收录简介被技术信息完全顶掉。
                string detail = BuildWorkshopDescription(LocalUnit, workshopId);
                _desc.Text = string.IsNullOrWhiteSpace(Entry.description)
                    ? detail
                    : Entry.description + " · " + detail;
                ApplyWorkshopStatus(LocalUnit, true);
            }
            else
            {
                _desc.Text = string.IsNullOrWhiteSpace(Entry.description)
                    ? WorkshopMetadataService.DefaultDescription
                    : Entry.description;
                if (valid)
                    SetStatus("Steam 工坊", SourceColor,
                        "已收录", PositiveColor, "未启用", NegativeColor);
                else
                    SetStatus("Steam 工坊", SourceColor,
                        "信息无效", NegativeColor, null, NegativeColor);
            }

            _btnMain.Visible = true;
            _btnMain.Enabled = valid;
            _btnMain.Text = valid ? "打开工坊页面" : "工坊信息无效";
            ConfigureWorkshopToggle(LocalUnit);
        }

        private void ApplyLocalPluginState()
        {
            var unit = LocalUnit;
            _title.Text = string.IsNullOrWhiteSpace(unit.DisplayName)
                ? unit.UnitKey
                : unit.DisplayName;
            string pluginCount = unit.Plugins.Count + " 个插件";
            string dllCount = unit.DllCount == unit.Plugins.Count
                ? string.Empty
                : " / " + unit.DllCount + " 个 DLL";
            _desc.Text = "版本 " + (unit.DisplayVersion ?? "未知") + " · " + pluginCount +
                         dllCount + " · " + unit.RelativePath;

            if (unit.Source == LocalPluginSource.SteamWorkshop)
            {
                _desc.Text = BuildWorkshopDescription(unit, unit.WorkshopId);
                ApplyWorkshopStatus(unit, false);
                _btnMain.Visible = true;
                _btnMain.Enabled = true;
                _btnMain.Text = "打开工坊页面";
                ConfigureWorkshopToggle(unit);
                return;
            }

            if (unit.HasPathConflict)
            {
                SetStatus("本地", SourceColor,
                    "未收录", NeutralColor, "路径冲突", Color.DarkOrange);
                // 冲突不能是死胡同：仍允许启用/禁用本副本。管理器会先把另一
                // 路径上的同名副本归档到 ModManager\conflict-backup，从不覆盖丢失。
                // 冲突双方版本号常常相同（开发构建不总递增版本），文件时间是
                // 作者分辨新旧的唯一线索，必须摆在卡片上。
                if (unit.LastWriteTimeUtc != null)
                    _desc.Text += " · 本副本更新于 " + unit.LastWriteTimeUtc.Value
                        .ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                _desc.Text += " · 点“" + (unit.IsDisabled ? "启用" : "禁用")
                    + "”将保留本副本，另一路径的副本会归档到 ModManager\\conflict-backup";
                _btnToggle.Visible = true;
                _btnToggle.Enabled = true;
                _btnToggle.Text = unit.IsDisabled ? "启用" : "禁用";
                return;
            }

            if (unit.HasGuidConflict)
            {
                SetStatus("本地", SourceColor,
                    "未收录", NeutralColor, "重复 GUID", Color.DarkOrange);
                _btnToggle.Visible = true;
                _btnToggle.Enabled = true;
                _btnToggle.Text = unit.IsDisabled ? "启用" : "禁用";
                return;
            }

            SetStatus("本地", SourceColor,
                "未收录", NeutralColor,
                unit.IsDisabled ? "未启用" : "已启用",
                unit.IsDisabled ? NegativeColor : PositiveColor);
            _btnToggle.Visible = true;
            _btnToggle.Enabled = true;
            _btnToggle.Text = unit.IsDisabled ? "启用" : "禁用";
        }

        private static string BuildWorkshopDescription(LocalPluginUnit unit, string workshopId)
        {
            // 订阅与下载情况已由右上角状态表达，这里只保留排查用的版本号和 ID。
            string version = unit.Plugins.Count > 0
                ? "版本 " + (unit.DisplayVersion ?? "未知") + " · "
                : string.Empty;
            string detail = string.IsNullOrWhiteSpace(unit.WorkshopValidationError)
                ? string.Empty
                : " · " + unit.WorkshopValidationError;
            return version + "ID " + workshopId + detail;
        }

        private void ApplyWorkshopStatus(LocalPluginUnit unit, bool registered)
        {
            string state;
            Color stateColor;
            if (unit.HasGuidConflict)
            {
                state = "重复 GUID";
                stateColor = Color.DarkOrange;
            }
            else if (!unit.HasWorkshopManifest)
            {
                state = "缺少描述文件";
                stateColor = NegativeColor;
            }
            else if (!unit.IsWorkshopPackageValid)
            {
                state = "文件不完整";
                stateColor = NegativeColor;
            }
            else if (unit.IsDisabled)
            {
                state = "未启用";
                stateColor = NegativeColor;
            }
            else if (!unit.IsWorkshopDownloaded)
            {
                state = "下载中";
                stateColor = Color.DarkOrange;
            }
            else if (unit.IsWorkshopConnected)
            {
                state = "已启用";
                stateColor = PositiveColor;
            }
            else
            {
                state = "待刷新";
                stateColor = Color.DarkOrange;
            }

            SetStatus("Steam 工坊", SourceColor,
                registered ? "已收录" : "未收录",
                registered ? PositiveColor : NeutralColor, state, stateColor);
        }

        private void ConfigureWorkshopToggle(LocalPluginUnit unit)
        {
            if (unit == null || !unit.IsWorkshopSubscribed) return;
            _btnMain.Left = 12;
            _btnToggle.Left = 140;
            _btnToggle.Visible = true;
            _btnToggle.Text = unit.IsDisabled ? "启用" : "禁用";
            _btnToggle.Enabled = !unit.IsDisabled ||
                (unit.IsWorkshopDownloaded && unit.IsWorkshopPackageValid);
        }

        private void SetStatus(string source, Color sourceColor,
            string registration, Color registrationColor, string state, Color stateColor)
        {
            SetStatusLabel(_status, source, sourceColor);
            SetStatusLabel(_statusRegistration, registration, registrationColor);
            SetStatusLabel(_statusState, state, stateColor);

            Label[] labels = { _status, _statusRegistration, _statusState };
            int right = _statusPanel.ClientSize.Width;
            for (int i = labels.Length - 1; i >= 0; i--)
            {
                Label label = labels[i];
                if (string.IsNullOrEmpty(label.Text)) continue;
                string displayText = i == 0 ? label.Text : "· " + label.Text;
                int width = TextRenderer.MeasureText(displayText, label.Font,
                    Size.Empty, TextFormatFlags.NoPadding).Width + 5;
                right -= width;
                label.Bounds = new Rectangle(Math.Max(0, right), 0,
                    Math.Min(width, _statusPanel.ClientSize.Width), _statusPanel.ClientSize.Height);
                label.Text = displayText;
            }
        }

        private static void SetStatusLabel(Label label, string text, Color color)
        {
            label.Text = text ?? string.Empty;
            label.ForeColor = color;
            label.Visible = !string.IsNullOrEmpty(text);
        }

        private void ResetButtons()
        {
            _btnMain.Left = 12;
            _btnToggle.Left = 12;
            _btnMain.Visible = false;
            _btnMain.Enabled = false;
            _btnToggle.Visible = false;
            _btnToggle.Enabled = false;
        }

        private bool TryGetWorkshopId(out string workshopId)
        {
            workshopId = LocalUnit != null &&
                LocalUnit.Source == LocalPluginSource.SteamWorkshop
                ? LocalUnit.WorkshopId
                : null;
            return !string.IsNullOrEmpty(workshopId) ||
                   (Entry != null && WorkshopItem.TryGetId(Entry, out workshopId));
        }

        public void SetBusy(bool busy)
        {
            _busy = busy;
            if (_boundKind != BoundKind.None) ApplyVisualState();
        }

        private void ApplyBusyState()
        {
            if (!_busy) return;
            _btnMain.Enabled = false;
            _btnToggle.Enabled = false;
        }
    }
}
