using System;
using System.IO;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using Timer = System.Windows.Forms.Timer;

namespace OpenSourceMP4Player
{
    public class CustomStreamMediaInput : StreamMediaInput
    {
        private readonly Stream _stream; // 这是您要播放的内存流
        private readonly bool _seekable; // 指示您的流是否支持寻址（例如 MemoryStream 支持，NetworkStream 不支持）

        public CustomStreamMediaInput(Stream stream, bool seekable = true)
            : base(stream)
        {
            _stream = stream;
            _seekable = seekable;
        }

        // 当 VLC 需要数据时，会调用此方法。
        // 你需要从你的_stream中读取数据，并填充到提供的`buffer`中。
        public override int Read(IntPtr buf, uint len)
        {
            byte[] buffer = new byte[len];
            int read = _stream.Read(buffer, 0, (int)len);
            if (read > 0)
            {
                // 将托管数组中的数据复制到 VLC 提供的非托管内存指针中
                System.Runtime.InteropServices.Marshal.Copy(buffer, 0, buf, read);
            }
            return read;
        }

        // 当 VLC 需要跳转到流的某个位置时（例如用户拖动了进度条），会调用此方法。
        // 如果您的流不支持寻址（如实时流），应返回 false。
        public override bool Seek(ulong offset)
        {
            if (_seekable)
            {
                _stream.Seek((long)offset, SeekOrigin.Begin);
                return true;
            }
            return false;
        }
    }
    // 自定义解密流：在读取时实时解密
    public class DecryptingStream : Stream
    {
        private readonly FileStream _sourceStream;
        private readonly int _key;
        private readonly byte[] _buffer = new byte[8192]; // 8KB缓冲区

        public DecryptingStream(string filePath, int key)
        {
            _sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            _key = key;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _sourceStream.Length;

        public override long Position
        {
            get => _sourceStream.Position;
            set => _sourceStream.Position = value;
        }

        // 核心：读取时实时解密
        public override int Read(byte[] buffer, int offset, int count)
        {
            // 从加密文件读取原始数据
            int bytesRead = _sourceStream.Read(_buffer, 0, Math.Min(count, _buffer.Length));

            // 实时解密读取到的数据
            //for (int i = 0; i < bytesRead; i++)
            //{
            //    buffer[offset + i] = (byte)(_buffer[i] ^ _key);
            //}
            // 异步解密（需注意线程安全）
            Parallel.For(0, bytesRead, i =>
            {
                buffer[offset + i] = (byte)(_buffer[i] ^ _key);
            });

            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            // 支持 seek 操作（拖动进度条时需要）
            return _sourceStream.Seek(offset, origin);
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => _sourceStream.Flush();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _sourceStream.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    public partial class MainForm : Form
    {
        private LibVLC _libVlc;
        private MediaPlayer _mediaPlayer;
        private VideoView _videoView;
        private DecryptingStream _decryptingStream; // 解密流实例
        private string _currentFilePath = string.Empty;
        private string _decryptedTempFile = string.Empty; // 解密后的临时文件路径
        private ToolStrip _toolStrip;
        private TrackBar _progressTrackBar;
        private ToolStripLabel _timeLabel;
        private Timer _progressTimer;
        private bool _isDragging = false;
        private int _encryptionKey = 0x0; // 加密解密共用密钥

        public MainForm()
        {
            Core.Initialize();
            _libVlc = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVlc);
            _videoView = new VideoView();

            // 提高进度更新频率，从1000ms改为100ms，实现更实时的显示
            _progressTimer = new Timer();
            _progressTimer.Interval = 1000;
            //_progressTimer.Tick += ProgressTimer_Tick;

            InitializeComponent();
            SetupUI();
            SetupEvents();
            Task.Run(() =>
            {
                while (true)
                {
                    if (_progressTimer.Enabled)
                        if (!_isDragging)
                        {
                            UpdateProgressDisplay();
                        }
                    Thread.Sleep(500);
                }
            });

        }
        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            //_progressTimer = new System.Windows.Forms.Timer(components);
            SuspendLayout();
            //_progressTimer.Interval = 100;
            //_progressTimer.Tick += ProgressTimer_Tick;
            // 
            // MainForm
            // 
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(800, 450);
            Name = "MainForm";
            Text = "";
            ResumeLayout(false);
        }

        private void SetupUI()
        {
            this.Text = "开源流式解密视频播放器";
            this.Size = new System.Drawing.Size(1024, 768);

            _videoView.Dock = DockStyle.Fill;
            _videoView.MediaPlayer = _mediaPlayer;

            var menuStrip = new MenuStrip();
            var fileMenu = new ToolStripMenuItem("文件");
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("打开视频", null, OpenMenuNoENCItem_Click));
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("打开加密视频", null, OpenMenuItem_Click));
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("加密文件", null, EncryptFileMenuItem_Click));
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("解密文件", null, DecryptFileMenuItem_Click));
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("设置密钥", null, SetKeyMenuItem_Click));
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("退出", null, ExitMenuItem_Click));
            menuStrip.Items.Add(fileMenu);

            _toolStrip = new ToolStrip();
            _toolStrip.Items.Add(new ToolStripButton("播放", null, PlayButton_Click));
            _toolStrip.Items.Add(new ToolStripButton("暂停", null, PauseButton_Click));
            _toolStrip.Items.Add(new ToolStripButton("停止", null, StopButton_Click));
            _toolStrip.Items.Add(new ToolStripSeparator());

            // 音量控制
            _toolStrip.Items.Add(new ToolStripLabel("音量:"));
            var volumeTrackBar = new TrackBar();
            volumeTrackBar.Minimum = 0;
            volumeTrackBar.Maximum = 100;
            volumeTrackBar.Value = 70;
            volumeTrackBar.Width = 100;
            volumeTrackBar.Scroll += (s, e) => _mediaPlayer.Volume = volumeTrackBar.Value;
            var trackBarHost = new ToolStripControlHost(volumeTrackBar);
            _toolStrip.Items.Add(trackBarHost);

            _toolStrip.Items.Add(new ToolStripSeparator());

            // 进度控制
            _toolStrip.Items.Add(new ToolStripLabel("进度:"));
            _progressTrackBar = new TrackBar();
            _progressTrackBar.Minimum = 0;
            _progressTrackBar.Maximum = 1000;
            _progressTrackBar.Width = 500;
            _progressTrackBar.Scroll += ProgressTrackBar_Scroll;
            _progressTrackBar.MouseDown += ProgressTrackBar_MouseDown;
            _progressTrackBar.MouseUp += ProgressTrackBar_MouseUp;
            var progressHost = new ToolStripControlHost(_progressTrackBar);
            _toolStrip.Items.Add(progressHost);

            _timeLabel = new ToolStripLabel("00:00 / 00:00");
            _toolStrip.Items.Add(_timeLabel);

            _toolStrip.Items.Add(new ToolStripSeparator());
            var statusLabel = new ToolStripLabel("就绪");
            statusLabel.Name = "statusLabel";
            _toolStrip.Items.Add(statusLabel);

            this.Controls.Add(_videoView);
            this.Controls.Add(_toolStrip);
            this.Controls.Add(menuStrip);
            this.MainMenuStrip = menuStrip;

            menuStrip.Dock = DockStyle.Top;
            _toolStrip.Dock = DockStyle.Bottom;
        }

        private void SetupEvents()
        {
            _mediaPlayer.Playing += (s, e) =>
            {
                UpdateStatus("正在播放");
                _progressTimer.Start();
                _progressTimer.Enabled = true;
            };

            _mediaPlayer.Paused += (s, e) => UpdateStatus("已暂停");
            _mediaPlayer.Stopped += (s, e) =>
            {
                UpdateStatus("已停止");
                _progressTimer.Stop();
            };

            _mediaPlayer.EndReached += (s, e) =>
            {
                UpdateStatus("播放结束");
                _progressTimer.Stop();
            };

            _mediaPlayer.EncounteredError += (s, e) => UpdateStatus($"错误: 播放失败 (可能密钥不正确)");

            _mediaPlayer.MediaChanged += (s, e) =>
            {
                if (_mediaPlayer.Media != null)
                {
                    _mediaPlayer.Media.ParsedChanged += (sender, args) =>
                    {
                        if (_mediaPlayer.Media.ParsedStatus == MediaParsedStatus.Done)
                        {
                            UpdateProgressDisplay();
                        }
                    };
                    _mediaPlayer.Media.Parse(MediaParseOptions.ParseLocal);
                }
            };
        }

        // 加密方法（与解密对应的XOR加密）
        private void EncryptFile(string sourceFilePath, string destinationFilePath)
        {
            try
            {
                using (var sourceStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read))
                using (var destStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write))
                {
                    byte[] buffer = new byte[8192];
                    int bytesRead;
                    while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        for (int i = 0; i < bytesRead; i++)
                        {
                            buffer[i] = (byte)(buffer[i] ^ _encryptionKey);
                        }
                        destStream.Write(buffer, 0, bytesRead);
                    }                
				}
            }
            catch (Exception ex)
            {
                throw new Exception($"加密失败: {ex.Message}");
            }
        }
        // 解密方法（XOR解密）
        private string DecryptFile(string encryptedFilePath)
        {
            try
            {
                // 创建临时文件
                _decryptedTempFile = Path.Combine(Path.GetTempPath(), 
                    $"{Guid.NewGuid()}_{Path.GetFileName(encryptedFilePath)}");
                
                byte[] encryptedData = File.ReadAllBytes(encryptedFilePath);
                byte[] decryptedData = new byte[encryptedData.Length];
                
                // XOR解密
                for (int i = 0; i < encryptedData.Length; i++)
                {
                    decryptedData[i] = (byte)(encryptedData[i] ^ _encryptionKey);
                }
                
                File.WriteAllBytes(_decryptedTempFile, decryptedData);
                return _decryptedTempFile;
            }
            catch (Exception ex)
            {
                throw new Exception($"解密失败: {ex.Message}");
            }
        }

        // 加密文件菜单事件
        private void EncryptFileMenuItem_Click(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Filter = "视频文件|*.mp4;*.avi;*.mkv;*.mov;*.flv;*.wmv|所有文件|*.*";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    using (var sfd = new SaveFileDialog())
                    {
                        sfd.FileName = Path.GetFileNameWithoutExtension(ofd.FileName) + "_encrypted" + Path.GetExtension(ofd.FileName);
                        sfd.Filter = ofd.Filter;

                        if (sfd.ShowDialog() == DialogResult.OK)
                        {
                            try
                            {
                                EncryptFile(ofd.FileName, sfd.FileName);
                                MessageBox.Show($"文件已成功加密至:\n{sfd.FileName}", "成功",
                                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show(ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                        }
                    }
                }
            }
        }

        // 解密文件菜单事件（用于解密并保存文件，区别于播放时的临时解密）
        private void DecryptFileMenuItem_Click(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Filter = "加密视频文件|*.mp4;*.avi;*.mkv;*.mov;*.flv;*.wmv|所有文件|*.*";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    using (var sfd = new SaveFileDialog())
                    {
                        sfd.FileName = Path.GetFileNameWithoutExtension(ofd.FileName) + "_decrypted" + Path.GetExtension(ofd.FileName);
                        sfd.Filter = ofd.Filter;
                        
                        if (sfd.ShowDialog() == DialogResult.OK)
                        {
                            try
                            {
                                // 直接解密并保存，不使用临时文件
                                byte[] encryptedData = File.ReadAllBytes(ofd.FileName);
                                byte[] decryptedData = new byte[encryptedData.Length];
                                
                                for (int i = 0; i < encryptedData.Length; i++)
                                {
                                    decryptedData[i] = (byte)(encryptedData[i] ^ _encryptionKey);
                                }
                                
                                File.WriteAllBytes(sfd.FileName, decryptedData);
                                MessageBox.Show($"文件已成功解密至:\n{sfd.FileName}", "成功", 
                                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show(ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                        }
                    }
                }
            }
        }

        // 设置密钥菜单事件
        private void SetKeyMenuItem_Click(object sender, EventArgs e)
        {
            using (var inputForm = new Form())
            {
                inputForm.Text = "设置加密解密密钥";
                inputForm.Size = new System.Drawing.Size(300, 150);
                inputForm.StartPosition = FormStartPosition.CenterParent;

                var label = new Label { Text = "请输入密钥(0-255):", Top = 20, Left = 20, Width = 240 };
                var textBox = new TextBox { Top = 50, Left = 20, Width = 240, Text = _encryptionKey.ToString() };
                var okButton = new Button { Text = "确定", Top = 80, Left = 180, Width = 80, DialogResult = DialogResult.OK };

                inputForm.Controls.Add(label);
                inputForm.Controls.Add(textBox);
                inputForm.Controls.Add(okButton);

                if (inputForm.ShowDialog() == DialogResult.OK)
                {
                    if (int.TryParse(textBox.Text, out int key) && key >= 0 && key <= 255)
                    {
                        _encryptionKey = key;
                        MessageBox.Show($"密钥已设置为: {key}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        MessageBox.Show("请输入有效的密钥(0-255)", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        // 进度定时器事件（更频繁地更新进度）
        private void ProgressTimer_Tick(object sender, EventArgs e)
        {
            //todo:无法运行，原因未知
            //if (!_isDragging)
            //{
            //    UpdateProgressDisplay();
            //}
        }

        // 实时更新进度显示
        private void UpdateProgressDisplay()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(UpdateProgressDisplay));
                return;
            }

            if (_mediaPlayer.Media == null || _mediaPlayer.Length <= 0)
            {
                _progressTrackBar.Value = 0;
                _timeLabel.Text = "00:00 / 00:00";
                return;
            }

            // 实时更新进度条位置
            var position = (float)_mediaPlayer.Position;
            _progressTrackBar.Value = (int)(position * _progressTrackBar.Maximum);

            // 实时更新时间显示（精确到秒）
            var currentTime = TimeSpan.FromMilliseconds(_mediaPlayer.Time);
            var totalTime = TimeSpan.FromMilliseconds(_mediaPlayer.Length);
            _timeLabel.Text = $"{FormatTime(currentTime)} / {FormatTime(totalTime)}";
        }

        private string FormatTime(TimeSpan time)
        {
            return $"{(int)time.TotalMinutes:D2}:{time.Seconds:D2}";
        }

        private void ProgressTrackBar_Scroll(object sender, EventArgs e)
        {
            if (_isDragging && _mediaPlayer.Media != null && _mediaPlayer.Length > 0)
            {
                var position = (float)_progressTrackBar.Value / _progressTrackBar.Maximum;
                _mediaPlayer.Position = position;
                UpdateProgressDisplay();
            }
        }

        private void ProgressTrackBar_MouseDown(object sender, MouseEventArgs e)
        {
            _isDragging = true;
        }

        private void ProgressTrackBar_MouseUp(object sender, MouseEventArgs e)
        {
            _isDragging = false;
            if (_mediaPlayer.Media != null && _mediaPlayer.Length > 0)
            {
                var position = (float)_progressTrackBar.Value / _progressTrackBar.Maximum;
                _mediaPlayer.Position = position;
            }
        }

        private void UpdateStatus(string status)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<string>(UpdateStatus), status);
                return;
            }

            var statusLabel = _toolStrip.Items["statusLabel"] as ToolStripLabel;
            if (statusLabel != null)
                statusLabel.Text = status;
        }
        private void OpenMenuNoENCItem_Click(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Filter = "视频文件|*.mp4;*.avi;*.mkv;*.mov;*.flv;*.wmv|所有文件|*.*";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        _currentFilePath = ofd.FileName;
                        _mediaPlayer.Media?.Dispose();


                        var media = new Media(_libVlc, _currentFilePath);
                        _mediaPlayer.Media = media;
                        this.Text = $"开源播放器 - {Path.GetFileName(_currentFilePath)}";
                        UpdateStatus("文件已加载，点击播放开始");
                        UpdateProgressDisplay();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"打开失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }
        //打开解密文件后，播放
        private void OpenMenuItem_Click_old(object sender, EventArgs e)
        {
            // 清理之前的临时文件
            if (!string.IsNullOrEmpty(_decryptedTempFile) && File.Exists(_decryptedTempFile))
            {
                try { File.Delete(_decryptedTempFile); } catch { }
            }

            using (var ofd = new OpenFileDialog())
            {
                ofd.Filter = "视频文件|*.mp4;*.avi;*.mkv;*.mov;*.flv;*.wmv|所有文件|*.*";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        _currentFilePath = ofd.FileName;
                        _mediaPlayer.Media?.Dispose();
                        
                        // 解密文件用于播放
                        string playFilePath = DecryptFile(_currentFilePath);
                        if (string.IsNullOrEmpty(playFilePath))
                        {
                            UpdateStatus("解密失败，无法播放");
                            return;
                        }
                        
                        var media = new Media(_libVlc, playFilePath);
                        _mediaPlayer.Media = media;
                        this.Text = $"开源播放器 - {Path.GetFileName(_currentFilePath)}";
                        UpdateStatus("文件已加载，点击播放开始");
                        UpdateProgressDisplay();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"打开失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }
        // 打开文件并使用流式解密播放
        private void OpenMenuItem_Click(object sender, EventArgs e)
        {
            // 清理之前的流
            if (_decryptingStream != null)
            {
                _mediaPlayer.Stop();
                _decryptingStream.Dispose();
                _decryptingStream = null;
            }

            using (var ofd = new OpenFileDialog())
            {
                ofd.Filter = "视频文件|*.mp4;*.avi;*.mkv;*.mov;*.flv;*.wmv|所有文件|*.*";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        _currentFilePath = ofd.FileName;
                        _mediaPlayer.Media?.Dispose();

                        // 创建解密流（核心修改）
                        _decryptingStream = new DecryptingStream(_currentFilePath, _encryptionKey);

                        // 直接从解密流创建媒体（无需临时文件）
                        var customInput = new CustomStreamMediaInput(_decryptingStream, true);
                        // 2. 从 MediaInput 创建 Media 对象
                        //    第二个参数是选项，可以指定 VLC 如何解析流。
                        //    因为流是原始数据，通常需要提示 VLC 它的格式，如 "mp4", "mpeg2", "h264" 等。
                        //    如果不确定，可以留空让 VLC 自动探测，但这可能失败。
                        //var media = new Media(libVLC: _libVlc,input: customInput,options: ":demux=mp4");
                        var media = new Media(libVLC: _libVlc, input: customInput, options: "");


                        _mediaPlayer.Media = media;
                        this.Text = $"媒体播放器 - {Path.GetFileName(_currentFilePath)}";
                        UpdateStatus("文件已加载，点击播放开始");
                        UpdateProgressDisplay();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"打开失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void PlayButton_Click_old(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(_decryptedTempFile) && _mediaPlayer.Media != null)
            {
                _mediaPlayer.Play();
            }
            else
            {
                MessageBox.Show("请先打开视频文件", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        private void PlayButton_Click(object sender, EventArgs e)
        {
            //if (_decryptingStream != null && _mediaPlayer.Media != null)
            if (_mediaPlayer.Media != null)
            {
                _mediaPlayer.Play();
            }
            else
            {
                MessageBox.Show("请先打开视频文件", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void PauseButton_Click(object sender, EventArgs e) => _mediaPlayer.Pause();

        private void StopButton_Click(object sender, EventArgs e) => _mediaPlayer.Stop();

        private void ExitMenuItem_Click(object sender, EventArgs e) => Close();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _progressTimer?.Stop();
                _progressTimer?.Dispose();
                _mediaPlayer?.Stop();
                _mediaPlayer?.Dispose();
                _decryptingStream?.Dispose(); // 释放解密流
                _libVlc?.Dispose();
                _videoView?.Dispose();
                
                // 清理临时文件
                if (!string.IsNullOrEmpty(_decryptedTempFile) && File.Exists(_decryptedTempFile))
                {
                    try { File.Delete(_decryptedTempFile); } catch { }
                }
            }
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region 设计器生成代码
        private System.ComponentModel.IContainer components = null;

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _progressTimer?.Stop();
            _mediaPlayer?.Stop();
            _decryptingStream?.Dispose(); // 关闭时释放流

            // 清理临时文件
            if (!string.IsNullOrEmpty(_decryptedTempFile) && File.Exists(_decryptedTempFile))
            {
                try { File.Delete(_decryptedTempFile); } catch { }
            }
            base.OnClosing(e);
        }
        #endregion
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
