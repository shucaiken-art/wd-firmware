using Build190FromTracks.Services;

namespace Build190FromTracks;

public sealed class MainForm : Form
{
    private readonly TextBox _txtDataFolder = new() { Width = 420, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
    private readonly TextBox _txtTracksFolder = new() { Width = 420, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
    private readonly TextBox _txt01Rpm = new() { Width = 420, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
    private readonly TextBox _txtOutput = new() { Width = 420, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
    private readonly NumericUpDown _numFirstTrack = new() { Minimum = 1, Maximum = 9999, Value = TrackAssembler.DefaultFirstTrack, Width = 80 };
    private readonly NumericUpDown _numMinTrack = new() { Minimum = 0, Maximum = 9999, Value = TrackAssembler.DefaultMinTrack, Width = 80 };
    private readonly NumericUpDown _numRegionOffset = new() { Minimum = 0, Maximum = decimal.MaxValue, Value = 0, Width = 120 };
    private readonly NumericUpDown _numSectors = new() { Minimum = 1, Maximum = decimal.MaxValue, Value = 303286, Width = 120 };
    private readonly CheckBox _chkUse01 = new() { Text = "从 01.rpm 读取模块 190 分配长度", Checked = true, AutoSize = true };
    private readonly CheckBox _chkVerifyHeads = new() { Text = "校验头 02 与 03 磁道内容一致", Checked = true, AutoSize = true };
    private readonly ComboBox _cmbPrimaryHead = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    private readonly ProgressBar _progress = new() { Height = 22, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
    private readonly TextBox _txtLog = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        ReadOnly = true,
        Font = new Font(FontFamily.GenericMonospace, 9f),
        Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        Height = 220
    };

    private CancellationTokenSource? _cts;

    public MainForm()
    {
        Text = "WD 模块 190 — 从 SA 区域 2/3 磁道生成 190.rpm";
        MinimumSize = new Size(720, 560);
        Size = new Size(820, 640);
        StartPosition = FormStartPosition.CenterScreen;

        _cmbPrimaryHead.Items.AddRange(["2 (区域 2)", "3 (区域 3)"]);
        _cmbPrimaryHead.SelectedIndex = 0;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Padding = new Padding(12),
            AutoSize = true
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));

        int row = 0;
        AddFolderRow(layout, ref row, "Data 目录", _txtDataFolder, BrowseDataFolder);
        AddFolderRow(layout, ref row, "Tracks 目录", _txtTracksFolder, () => BrowseFolder(_txtTracksFolder));
        AddFolderRow(layout, ref row, "01.rpm", _txt01Rpm, () => BrowseFile(_txt01Rpm, "RPM|*.rpm|All|*.*"));
        AddFolderRow(layout, ref row, "输出 190.rpm", _txtOutput, () => BrowseSave(_txtOutput));

        AddLabelRow(layout, ref row, "参数", "");
        AddControlRow(layout, ref row, "起始磁道", _numFirstTrack, "346 → 0006 递减拼接（WD80EAAZ 样本）");
        AddControlRow(layout, ref row, "最小磁道", _numMinTrack, "低于此磁道号则报错");
        AddControlRow(layout, ref row, "区域扇区偏移", _numRegionOffset, "模块在区域 2/3 内的起始扇区（通常为 0）");
        AddControlRow(layout, ref row, "输出扇区数", _numSectors, "×512 字节；勾选下方时从 01.rpm 自动填充");
        AddControlRow(layout, ref row, "主用磁道头", _cmbPrimaryHead, "备份工具标签 02=区域2，03=区域3");

        var optPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        optPanel.Controls.Add(_chkUse01);
        optPanel.Controls.Add(_chkVerifyHeads);
        layout.Controls.Add(new Label { Text = "选项", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        layout.Controls.Add(optPanel, 1, row);
        layout.SetColumnSpan(optPanel, 2);
        row++;

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var btnAnalyze = new Button { Text = "预览 / 解析", AutoSize = true };
        var btnBuild = new Button { Text = "生成 190.rpm", AutoSize = true };
        var btnCancel = new Button { Text = "取消", AutoSize = true, Enabled = false };
        btnAnalyze.Click += (_, _) => RunAnalyze();
        btnBuild.Click += async (_, _) => await RunBuildAsync(btnBuild, btnCancel);
        btnCancel.Click += (_, _) => _cts?.Cancel();
        buttons.Controls.Add(btnAnalyze);
        buttons.Controls.Add(btnBuild);
        buttons.Controls.Add(btnCancel);
        layout.Controls.Add(new Label { Text = "操作", AutoSize = true }, 0, row);
        layout.Controls.Add(buttons, 1, row);
        layout.SetColumnSpan(buttons, 2);
        row++;

        layout.Controls.Add(_progress, 0, row);
        layout.SetColumnSpan(_progress, 3);
        row++;

        layout.Controls.Add(_txtLog, 0, row);
        layout.SetColumnSpan(_txtLog, 3);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Controls.Add(layout);

        _chkUse01.CheckedChanged += (_, _) => _numSectors.Enabled = !_chkUse01.Checked;

        AppendLog("说明：新款 WD 盘模块 190 常位于 SA 区域 2、3（磁道标签 _02 / _03），不在 _00/_01。");
        AppendLog("本工具按已验证顺序拼接 .trk：0346 → 0006，每道 3010×512 字节。");
    }

    private void BrowseDataFolder()
    {
        if (!TryPickFolder(out var path))
            return;

        _txtDataFolder.Text = path;
        _txtTracksFolder.Text = Path.Combine(path, "Tracks");
        _txt01Rpm.Text = Path.Combine(path, @"SABackup\Modules\Copy 0\01.rpm");
        if (string.IsNullOrWhiteSpace(_txtOutput.Text))
            _txtOutput.Text = Path.Combine(path, "190.rpm");
    }

    private static void BrowseFolder(TextBox target)
    {
        if (TryPickFolder(out var path))
            target.Text = path;
    }

    private static void BrowseFile(TextBox target, string filter)
    {
        using var dlg = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (dlg.ShowDialog() == DialogResult.OK)
            target.Text = dlg.FileName;
    }

    private static void BrowseSave(TextBox target)
    {
        using var dlg = new SaveFileDialog { Filter = "RPM|*.rpm", FileName = "190.rpm", OverwritePrompt = true };
        if (dlg.ShowDialog() == DialogResult.OK)
            target.Text = dlg.FileName;
    }

    private static bool TryPickFolder(out string path)
    {
        path = "";
        using var dlg = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = "选择 HDD Profile 的 Data 目录" };
        if (dlg.ShowDialog() != DialogResult.OK)
            return false;
        path = dlg.SelectedPath;
        return true;
    }

    private void AddFolderRow(TableLayoutPanel layout, ref int row, string label, TextBox box, Action browse)
    {
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        layout.Controls.Add(box, 1, row);
        var btn = new Button { Text = "浏览…", AutoSize = true };
        btn.Click += (_, _) => browse();
        layout.Controls.Add(btn, 2, row);
        row++;
    }

    private static void AddLabelRow(TableLayoutPanel layout, ref int row, string label, string hint)
    {
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) }, 0, row);
        if (!string.IsNullOrEmpty(hint))
            layout.Controls.Add(new Label { Text = hint, AutoSize = true }, 1, row);
        layout.SetColumnSpan(layout.GetControlFromPosition(1, row)!, 2);
        row++;
    }

    private static void AddControlRow(TableLayoutPanel layout, ref int row, string label, Control control, string hint)
    {
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        panel.Controls.Add(control);
        panel.Controls.Add(new Label { Text = hint, AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
        layout.Controls.Add(panel, 1, row);
        layout.SetColumnSpan(panel, 2);
        row++;
    }

    private void RunAnalyze()
    {
        try
        {
            ClearLog();
            long bytes = ResolveOutputBytes(out var dirInfo);
            if (dirInfo != null)
                AppendLog(Module190Directory.Describe(dirInfo));

            int first = (int)_numFirstTrack.Value;
            long offset = (long)_numRegionOffset.Value;
            int min = (int)_numMinTrack.Value;
            AppendLog(TrackAssembler.PreviewPlan(bytes, first, offset, min));

            string tracks = _txtTracksFolder.Text.Trim();
            if (Directory.Exists(tracks))
            {
                int head = _cmbPrimaryHead.SelectedIndex == 0 ? 2 : 3;
                int missing = CountMissingTracks(tracks, head, first, bytes, offset, min);
                AppendLog(missing == 0
                    ? $"Tracks 目录检查：头 {head:D2} 所需 .trk 文件齐全。"
                    : $"Tracks 目录检查：缺少 {missing} 个所需 .trk 文件。");
            }
            else
            {
                AppendLog("Tracks 目录不存在，跳过文件检查。");
            }
        }
        catch (Exception ex)
        {
            AppendLog("错误: " + ex.Message);
        }
    }

    private async Task RunBuildAsync(Button buildBtn, Button cancelBtn)
    {
        if (_cts != null)
            return;

        try
        {
            ClearLog();
            long bytes = ResolveOutputBytes(out var dirInfo);
            if (dirInfo != null)
                AppendLog(Module190Directory.Describe(dirInfo));

            string tracks = _txtTracksFolder.Text.Trim();
            string output = _txtOutput.Text.Trim();
            if (!Directory.Exists(tracks))
                throw new DirectoryNotFoundException("请指定有效的 Tracks 目录（含 XXXX_02.trk / XXXX_03.trk）。");
            if (string.IsNullOrWhiteSpace(output))
                throw new InvalidOperationException("请指定输出 190.rpm 路径。");
            if (File.Exists(output))
            {
                if (MessageBox.Show(
                        $"文件已存在：{output}\n是否覆盖？",
                        "确认覆盖",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }

            int primaryHead = _cmbPrimaryHead.SelectedIndex == 0 ? 2 : 3;
            int secondaryHead = primaryHead == 2 ? 3 : 2;

            var options = new TrackAssembler.BuildOptions
            {
                TracksDirectory = tracks,
                OutputPath = output,
                OutputByteLength = bytes,
                FirstTrack = (int)_numFirstTrack.Value,
                MinTrack = (int)_numMinTrack.Value,
                RegionStartSectorOffset = (long)_numRegionOffset.Value,
                PrimaryHead = primaryHead,
                SecondaryHead = secondaryHead,
                VerifySecondaryHead = _chkVerifyHeads.Checked,
                Log = new Progress<string>(AppendLog),
                Progress = new Progress<(int, int)>(p =>
                {
                    if (p.Item2 > 0)
                    {
                        _progress.Maximum = p.Item2;
                        _progress.Value = Math.Min(p.Item1, p.Item2);
                    }
                })
            };

            buildBtn.Enabled = false;
            cancelBtn.Enabled = true;
            _progress.Value = 0;
            _cts = new CancellationTokenSource();

            TrackAssembler.BuildResult result = await Task.Run(() =>
            {
                var asm = new TrackAssembler();
                return options.RegionStartSectorOffset == 0
                    ? asm.BuildSequentialTracks(options, _cts.Token)
                    : asm.Build(options, _cts.Token);
            }, _cts.Token);

            AppendLog("");
            AppendLog("生成完成。");
            AppendLog($"写入: {result.BytesWritten:N0} 字节");
            AppendLog($"磁道: {result.FirstTrackUsed:D4} → {result.LastTrackUsed:D4}，共 {result.TracksUsed} 个文件");
            AppendLog($"最后一道使用: {result.LastTrackBytesUsed:N0} 字节");
            AppendLog(result.HeaderSummary ?? "");
            AppendLog($"SHA-256: {result.Sha256}");
            AppendLog($"输出: {output}");

            MessageBox.Show("190.rpm 已生成。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            AppendLog("已取消。");
        }
        catch (Exception ex)
        {
            AppendLog("错误: " + ex.Message);
            MessageBox.Show(ex.Message, "生成失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            buildBtn.Enabled = true;
            cancelBtn.Enabled = false;
            _progress.Value = 0;
        }
    }

    private long ResolveOutputBytes(out Module190Directory.EntryInfo? info)
    {
        info = null;
        if (_chkUse01.Checked)
        {
            string path01 = _txt01Rpm.Text.Trim();
            if (string.IsNullOrWhiteSpace(path01) || !File.Exists(path01))
                throw new FileNotFoundException("找不到 01.rpm，请指定 SABackup\\Modules\\Copy 0\\01.rpm。");

            info = Module190Directory.ParseFrom01Rpm(path01);
            _numSectors.Value = Math.Min(_numSectors.Maximum, info.SectorCount);
            return info.ByteLength;
        }

        return (long)_numSectors.Value * TrackAssembler.SectorSize;
    }

    private static int CountMissingTracks(string tracksDir, int head, int firstTrack, long outputBytes, long regionOffset, int minTrack)
    {
        long totalSectors = outputBytes / TrackAssembler.SectorSize;
        long endRegionSector = regionOffset + totalSectors - 1;
        int lastTrack = firstTrack - (int)(endRegionSector / TrackAssembler.SectorsPerTrack);
        if (lastTrack < minTrack)
            return 999;

        int missing = 0;
        for (int t = firstTrack; t >= lastTrack; t--)
        {
            string path = Path.Combine(tracksDir, $"{t:D4}_{head:D2}.trk");
            if (!File.Exists(path))
                missing++;
        }

        return missing;
    }

    private void AppendLog(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(line));
            return;
        }

        _txtLog.AppendText(line + Environment.NewLine);
    }

    private void ClearLog()
    {
        if (InvokeRequired)
        {
            BeginInvoke(ClearLog);
            return;
        }

        _txtLog.Clear();
    }
}
