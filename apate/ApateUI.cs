namespace apate
{
    public partial class ApateUI : Form
    {
        private byte[] maskBytes = Array.Empty<byte>();
        private string maskExtension = "";
        private bool busy;
        private CancellationTokenSource? cancellation;
        private readonly ToolStripStatusLabel cancelLink = new("取消") { IsLink = true, Visible = false };
        private readonly ToolStripStatusLabel detailsLink = new("查看失败详情") { IsLink = true, Visible = false };
        private readonly ToolStripProgressBar progressBar = new() { Visible = false, Width = 100 };
        private List<string> lastErrors = new();

        public ApateUI()
        {
            InitializeComponent();
            ModeSelect(ModeEnum.OneKey);
            toolStripStatusLabel1.Spring = true;
            toolStripStatusLabel1.TextAlign = ContentAlignment.MiddleLeft;
            toolStripStatusLabel1.AutoToolTip = true;
            statusStrip1.ShowItemToolTips = true;
            var legacyItem = new ToolStripMenuItem("还原旧版文件…");
            legacyItem.Click += RevealLegacy_Click;
            选项ToolStripMenuItem.DropDownItems.Insert(0, legacyItem);
            选项ToolStripMenuItem.DropDownItems.Insert(1, new ToolStripSeparator());
            statusStrip1.Items.AddRange(new ToolStripItem[] { progressBar, cancelLink, detailsLink });
            cancelLink.Click += (_, _) =>
            {
                cancellation?.Cancel();
                toolStripStatusLabel1.Text = "正在取消，原文件会被保留…";
            };
            detailsLink.Click += (_, _) => ShowErrors();
            FormClosing += (_, e) =>
            {
                if (!busy) return;
                cancellation?.Cancel();
                e.Cancel = true;
                toolStripStatusLabel1.Text = "正在取消，完成后可关闭窗口…";
            };
        }

        private void 退出ToolStripMenuItem_Click(object sender, EventArgs e) => Close();

        private void 关于ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using var aboutUI = new AboutUI { TopMost = TopMost };
            aboutUI.ShowDialog(this);
        }

        private static string[] DroppedPaths(DragEventArgs e) =>
            e.Data?.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();

        private async void MaskFileLabel_DragDrop(object sender, DragEventArgs e)
        {
            if (busy) return;
            ModeSelect(ModeEnum.Mask);
            string[] paths = DroppedPaths(e);
            if (paths.Length != 1 || Directory.Exists(paths[0]))
            {
                toolStripStatusLabel1.Text = "请拖入一个具有扩展名的面具文件。";
                return;
            }
            SetBusy(true);
            toolStripStatusLabel1.Text = "正在读取面具…";
            try
            {
                string extension = Path.GetExtension(paths[0]);
                if (string.IsNullOrEmpty(extension) || extension == ".")
                    throw new ArgumentException("面具文件必须具有扩展名。");
                byte[] loaded = await Task.Run(() => FileTransformer.LoadMask(paths[0], cancellation!.Token));
                maskBytes = loaded;
                maskExtension = extension;
                MaskFileDragLabel.Image = Properties.Resources.accept;
                MaskFileDragLabel.Text = "";
                TrueFileDragLabel.Image = Properties.Resources.drag;
                TrueFileDragLabel.Text = "\r\n\r\n\r\n拖入\r\n进行伪装";
                toolStripStatusLabel1.Text = "面具已载入，处理时会保留原文件。";
            }
            catch (OperationCanceledException)
            {
                toolStripStatusLabel1.Text = "已取消读取面具。";
            }
            catch (Exception ex)
            {
                toolStripStatusLabel1.Text = "面具读取失败：" + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void MaskFileLabel_DragEnter(object sender, DragEventArgs e) => SetDragEffect(e);
        private void TrueFileLabel_DragEnter(object sender, DragEventArgs e) => SetDragEffect(e);
        private void RevealMaskLabel_DragEnter(object sender, DragEventArgs e) => SetDragEffect(e);

        private void SetDragEffect(DragEventArgs e) =>
            e.Effect = !busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true
                ? DragDropEffects.Link : DragDropEffects.None;

        private async void TrueFileLabel_DragDrop(object sender, DragEventArgs e)
        {
            if (busy) return;
            if (maskBytes.Length == 0)
            {
                toolStripStatusLabel1.Text = "请先载入有效的面具文件。";
                return;
            }
            await RunBatchAsync(DroppedPaths(e), FileOperation.Disguise);
        }

        private async void RevealMaskLabel_DragDrop(object sender, DragEventArgs e)
        {
            if (!busy) await RunBatchAsync(DroppedPaths(e), FileOperation.Reveal);
        }

        private async void RevealLegacy_Click(object? sender, EventArgs e)
        {
            if (busy) return;
            using var dialog = new OpenFileDialog
            {
                Title = "选择确定由 v1.4.2 或更早版本伪装的文件",
                Multiselect = true,
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            if (MessageBox.Show(this,
                "旧版文件没有可验证的格式标识，无法确认文件身份或完整性。\r\n请仅选择确定由旧版 apate 伪装的文件。还原会保留原件，输出结果需自行核对。\r\n是否继续？",
                "旧版兼容还原", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            await RunBatchAsync(dialog.FileNames, FileOperation.RevealLegacy);
        }

        private async Task RunBatchAsync(string[] paths, FileOperation operation)
        {
            if (busy || paths.Length == 0) return;
            SetBusy(true);
            lastErrors = new List<string>();
            detailsLink.Visible = false;
            toolStripStatusLabel1.Text = "正在收集文件，原文件会被保留…";
            byte[] currentMask = maskBytes;
            string currentExtension = maskExtension;
            CancellationTokenSource currentCancellation = cancellation!;
            var progress = new Progress<BatchProgress>(p =>
            {
                if (!busy || !ReferenceEquals(cancellation, currentCancellation)) return;
                progressBar.Maximum = Math.Max(1, p.Total);
                progressBar.Value = Math.Min(p.Completed, progressBar.Maximum);
                toolStripStatusLabel1.Text = $"正在处理 {p.Completed}/{p.Total}：{p.Name}";
            });
            try
            {
                BatchResult result = await Task.Run(() => BatchProcessor.Run(paths, operation,
                    currentMask, currentExtension, progress, currentCancellation.Token));
                lastErrors = result.Errors;
                toolStripStatusLabel1.Text = $"{(result.Cancelled ? "已取消" : "完成")}：成功 {result.Succeeded} 个，失败/跳过 {result.Errors.Count} 项；原件已保留";
                detailsLink.Visible = lastErrors.Count > 0;
            }
            catch (Exception ex)
            {
                lastErrors.Add(ex.Message);
                detailsLink.Visible = true;
                toolStripStatusLabel1.Text = "处理已停止，原件已保留：" + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetBusy(bool value)
        {
            busy = value;
            menuStrip1.Enabled = !value;
            MaskFileDragLabel.AllowDrop = !value && 面具伪装ToolStripMenuItem.Checked;
            TrueFileDragLabel.AllowDrop = !value && maskBytes.Length > 0;
            RevealFileDragLabel.AllowDrop = !value;
            cancelLink.Visible = value;
            progressBar.Visible = value;
            if (value)
            {
                cancellation = new CancellationTokenSource();
                progressBar.Value = 0;
            }
            else
            {
                cancellation?.Dispose();
                cancellation = null;
            }
        }

        private void ShowErrors()
        {
            using var window = new Form
            {
                Text = "失败或跳过的文件",
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(760, 400),
                TopMost = TopMost
            };
            window.Controls.Add(new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                Text = string.Join(Environment.NewLine + Environment.NewLine, lastErrors)
            });
            window.ShowDialog(this);
        }

        private void ModeSelect(ModeEnum mode)
        {
            一键伪装ToolStripMenuItem.Checked = false;
            面具伪装ToolStripMenuItem.Checked = false;
            简易伪装ToolStripMenuItem.Checked = false;
            eXEToolStripMenuItem.Checked = false;
            jPGToolStripMenuItem.Checked = false;
            mP4ToolStripMenuItem.Checked = false;
            mOVToolStripMenuItem.Checked = false;
            maskBytes = new byte[] { };
            maskExtension = "";
            MaskFileDragLabel.AllowDrop = false;
            MaskFileDragLabel.Image = Properties.Resources.cancel;
            MaskFileDragLabel.Text = "";
            TrueFileDragLabel.AllowDrop = false;
            TrueFileDragLabel.Image = Properties.Resources.cancel;
            TrueFileDragLabel.Text = "";
            switch (mode)
            {

                case ModeEnum.OneKey://一键伪装
                    一键伪装ToolStripMenuItem.Checked = true;
                    maskBytes = Properties.Resources.mask;
                    maskExtension = ".mp4";
                    TrueFileDragLabel.AllowDrop = true;
                    TrueFileDragLabel.Image = Properties.Resources.drag;
                    TrueFileDragLabel.Text = "\r\n\r\n\r\n拖入\r\n一键伪装";
                    break;
                case ModeEnum.Mask://面具伪装
                    面具伪装ToolStripMenuItem.Checked = true;
                    MaskFileDragLabel.AllowDrop = true;
                    MaskFileDragLabel.Image = Properties.Resources.drag;
                    MaskFileDragLabel.Text = "\r\n\r\n\r\n拖入\r\n面具文件";
                    break;
                case ModeEnum.Exe://EXE
                    简易伪装ToolStripMenuItem.Checked = true;
                    eXEToolStripMenuItem.Checked = true;
                    maskBytes = Program.exeHead;
                    maskExtension = ".exe";
                    TrueFileDragLabel.AllowDrop = true;
                    TrueFileDragLabel.Image = Properties.Resources.drag;
                    TrueFileDragLabel.Text = "\r\n\r\n\r\n拖入\r\n伪装为EXE";
                    break;
                case ModeEnum.Jpg://JPG
                    简易伪装ToolStripMenuItem.Checked = true;
                    jPGToolStripMenuItem.Checked = true;
                    maskBytes = Program.jpgHead;
                    maskExtension = ".jpg";
                    TrueFileDragLabel.AllowDrop = true;
                    TrueFileDragLabel.Image = Properties.Resources.drag;
                    TrueFileDragLabel.Text = "\r\n\r\n\r\n拖入\r\n伪装为JPG";
                    break;
                case ModeEnum.Mp4://MP4
                    简易伪装ToolStripMenuItem.Checked = true;
                    mP4ToolStripMenuItem.Checked = true;
                    maskBytes = Program.mp4Head;
                    maskExtension = ".mp4";
                    TrueFileDragLabel.AllowDrop = true;
                    TrueFileDragLabel.Image = Properties.Resources.drag;
                    TrueFileDragLabel.Text = "\r\n\r\n\r\n拖入\r\n伪装为MP4";
                    break;
                case ModeEnum.Mov://MOV
                    简易伪装ToolStripMenuItem.Checked = true;
                    mOVToolStripMenuItem.Checked = true;
                    maskBytes = Program.movHead;
                    maskExtension = ".mov";
                    TrueFileDragLabel.AllowDrop = true;
                    TrueFileDragLabel.Image = Properties.Resources.drag;
                    TrueFileDragLabel.Text = "\r\n\r\n\r\n拖入\r\n伪装为MOV";
                    break;
            }
        }

        private void 一键伪装ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ModeSelect(ModeEnum.OneKey);
        }

        private void 拼接伪装模式ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ModeSelect(ModeEnum.Mask);
        }

        private void eXEToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ModeSelect(ModeEnum.Exe);

        }

        private void jPGToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ModeSelect(ModeEnum.Jpg);
        }

        private void mP4ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ModeSelect(ModeEnum.Mp4);
        }

        private void mOVToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ModeSelect(ModeEnum.Mov);
        }

        private void 窗口置顶ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            TopMost = !TopMost;
            窗口置顶ToolStripMenuItem.Checked = !窗口置顶ToolStripMenuItem.Checked;
        }

    }
}
