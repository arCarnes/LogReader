using System.ComponentModel;
using System.Text;

namespace LogGenerator;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ApplicationConfiguration.Initialize();
        Application.Run(new GeneratorForm());
    }
}

internal sealed class GeneratorForm : Form
{
    private const int FailureRetryWindowMs = 5000;

    private enum GeneratorEncoding
    {
        Utf8,
        Utf8Bom,
        Ansi,
        Utf16,
        Utf16Be
    }

    private sealed class EncodingChoice
    {
        public string Label { get; init; } = string.Empty;
        public GeneratorEncoding Value { get; init; }
    }

    private readonly TextBox _baseDirTextBox = new() { Width = 420 };
    private readonly NumericUpDown _appsNumeric = new() { Minimum = 1, Maximum = 100, Value = 5, Width = 80 };
    private readonly NumericUpDown _filesPerAppNumeric = new() { Minimum = 1, Maximum = 100, Value = 10, Width = 80 };
    private readonly NumericUpDown _intervalNumeric = new() { Minimum = 1, Maximum = 5000, Value = 100, Increment = 1, Width = 80 };
    private readonly ComboBox _encodingCombo = new() { Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _startStopButton = new()
    {
        Text = "Start",
        Width = 100,
        Height = 30,
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleCenter,
        Margin = new Padding(6, 4, 6, 4),
        FlatStyle = FlatStyle.System
    };
    private readonly Label _statusLabel = new() { AutoSize = true, Text = "Stopped" };
    private readonly DataGridView _grid = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 250 };
    private readonly BindingList<LogFileRow> _rows = new();
    private CancellationTokenSource? _cts;
    private Task[]? _writerTasks;
    private List<LogTarget> _targets = [];
    private int _intervalMs = 100;
    private long _lastTotalLines;
    private DateTime _lastRateCheck = DateTime.UtcNow;
    private Encoding _activeEncoding = new UTF8Encoding(false);

    private readonly string[] _levels = ["INFO", "INFO", "INFO", "DEBUG", "WARN", "ERROR"];
    private readonly string[] _messages =
    [
        "Request received: GET /api/v1/orders",
        "Database query executed in {0}ms",
        "Cache hit for key: user:{1}",
        "Response sent: 200 OK in {0}ms",
        "Processed batch of {2} records",
        "Heartbeat OK",
        "Retry attempt 1/3 for operation",
        "Response time {0}ms exceeded threshold",
        "Unhandled exception in request pipeline",
        "Database connection timed out after {0}ms"
    ];

    public GeneratorForm()
    {
        Text = "LogGenerator";
        Width = 1120;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        var topPanel = BuildTopPanel();
        BuildGrid();

        Controls.Add(_grid);
        Controls.Add(topPanel);

        _refreshTimer.Tick += (_, _) => RefreshGridCounts();
        _startStopButton.Click += async (_, _) => await ToggleStartStopAsync();
        _intervalNumeric.ValueChanged += (_, _) =>
        {
            _intervalMs = (int)_intervalNumeric.Value;
            if (_writerTasks != null)
                _statusLabel.Text = $"Running ({_targets.Count} files, {_intervalMs}ms, {GetActiveEncodingLabel()})";
        };
        FormClosing += async (_, e) =>
        {
            if (_writerTasks != null)
            {
                e.Cancel = true;
                await StopGenerationAsync();
                Close();
            }
        };
    }

    private Control BuildTopPanel()
    {
        var browseButton = new Button
        {
            Text = "Browse...",
            Width = 90,
            Height = 30,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(6, 4, 6, 4),
            FlatStyle = FlatStyle.System
        };
        browseButton.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Choose base output directory",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog(this) == DialogResult.OK)
                _baseDirTextBox.Text = dialog.SelectedPath;
        };

        _encodingCombo.DataSource = new[]
        {
            new EncodingChoice { Label = "UTF-8", Value = GeneratorEncoding.Utf8 },
            new EncodingChoice { Label = "UTF-8 (BOM)", Value = GeneratorEncoding.Utf8Bom },
            new EncodingChoice { Label = "ANSI (Windows-1252)", Value = GeneratorEncoding.Ansi },
            new EncodingChoice { Label = "UTF-16 LE", Value = GeneratorEncoding.Utf16 },
            new EncodingChoice { Label = "UTF-16 BE", Value = GeneratorEncoding.Utf16Be }
        };
        _encodingCombo.DisplayMember = nameof(EncodingChoice.Label);
        _encodingCombo.ValueMember = nameof(EncodingChoice.Value);
        _encodingCombo.SelectedValue = GeneratorEncoding.Utf8;

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 104,
            Padding = new Padding(12, 12, 12, 12),
            AutoSize = false,
            WrapContents = true
        };

        panel.Controls.Add(new Label { Text = "Base Directory", AutoSize = true, Margin = new Padding(0, 9, 8, 0) });
        panel.Controls.Add(_baseDirTextBox);
        panel.Controls.Add(browseButton);

        panel.Controls.Add(new Label { Text = "Apps", AutoSize = true, Margin = new Padding(18, 9, 6, 0) });
        panel.Controls.Add(_appsNumeric);

        panel.Controls.Add(new Label { Text = "Files per App", AutoSize = true, Margin = new Padding(18, 9, 6, 0) });
        panel.Controls.Add(_filesPerAppNumeric);

        panel.Controls.Add(new Label { Text = "Interval (ms)", AutoSize = true, Margin = new Padding(18, 9, 6, 0) });
        panel.Controls.Add(_intervalNumeric);

        panel.Controls.Add(new Label { Text = "Encoding", AutoSize = true, Margin = new Padding(18, 9, 6, 0) });
        panel.Controls.Add(_encodingCombo);

        panel.Controls.Add(_startStopButton);
        panel.Controls.Add(_statusLabel);

        return panel;
    }

    private void BuildGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AutoGenerateColumns = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.RowHeadersVisible = false;
        _grid.ScrollBars = ScrollBars.Both;
        _grid.DataSource = _rows;

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(LogFileRow.Application),
            HeaderText = "Application",
            Width = 120
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(LogFileRow.FileName),
            HeaderText = "File",
            Width = 220
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(LogFileRow.FilePath),
            HeaderText = "Path",
            Width = 620
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(LogFileRow.LinesWritten),
            HeaderText = "Lines Written",
            Width = 120
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(LogFileRow.Status),
            HeaderText = "Status",
            Width = 220
        });
    }

    private async Task ToggleStartStopAsync()
    {
        if (_writerTasks == null)
        {
            StartGeneration();
        }
        else
        {
            await StopGenerationAsync();
        }
    }

    private void StartGeneration()
    {
        var baseDir = _baseDirTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            MessageBox.Show(this, "Pick a base directory first.", "Missing Directory", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            _activeEncoding = ResolveSelectedEncoding();
            _targets = BuildTargets(baseDir, (int)_appsNumeric.Value, (int)_filesPerAppNumeric.Value);
            EnsureFilesExist(_targets, _activeEncoding);
            OpenWriters(_targets, _activeEncoding);
        }
        catch (Exception ex)
        {
            CloseWriters(_targets);
            MessageBox.Show(this, ex.Message, "Unable to Start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _rows.Clear();
        foreach (var target in _targets)
        {
            _rows.Add(new LogFileRow
            {
                Application = target.ApplicationName,
                FileName = Path.GetFileName(target.FilePath),
                FilePath = target.FilePath,
                LinesWritten = 0,
                Status = target.Status
            });
        }

        int stripeCount = Math.Min(Environment.ProcessorCount, _targets.Count);
        _cts = new CancellationTokenSource();
        _lastTotalLines = 0;
        _lastRateCheck = DateTime.UtcNow;
        _writerTasks = Enumerable.Range(0, stripeCount)
            .Select(s =>
            {
                int startDelayMs = stripeCount > 1
                    ? (int)Math.Round((double)s * _intervalMs / stripeCount)
                    : 0;
                return Task.Run(() => StripeTaskAsync(s, stripeCount, startDelayMs, _cts.Token));
            })
            .ToArray();
        _refreshTimer.Start();

        _startStopButton.Text = "Stop";
        _baseDirTextBox.Enabled = false;
        _appsNumeric.Enabled = false;
        _filesPerAppNumeric.Enabled = false;
        _encodingCombo.Enabled = false;
        _statusLabel.Text = $"Running ({_targets.Count} files, {_intervalMs}ms, {GetActiveEncodingLabel()})";
    }

    private async Task StopGenerationAsync()
    {
        if (_writerTasks == null || _cts == null)
            return;

        _cts.Cancel();
        try
        {
            await Task.WhenAll(_writerTasks);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        finally
        {
            CloseWriters(_targets);
            _cts.Dispose();
            _cts = null;
            _writerTasks = null;
            _refreshTimer.Stop();
            RefreshGridCounts();

            _startStopButton.Text = "Start";
            _baseDirTextBox.Enabled = true;
            _appsNumeric.Enabled = true;
            _filesPerAppNumeric.Enabled = true;
            _intervalNumeric.Enabled = true;
            _encodingCombo.Enabled = true;
            _statusLabel.Text = "Stopped";
        }
    }

    private async Task StripeTaskAsync(int stripe, int stripeCount, int startDelayMs, CancellationToken token)
    {
        try
        {
            if (startDelayMs > 0)
                await Task.Delay(startDelayMs, token);

            while (!token.IsCancellationRequested)
            {
                for (int i = stripe; i < _targets.Count; i += stripeCount)
                {
                    var target = _targets[i];
                    if (target.IsDisabled)
                        continue;

                    try
                    {
                        target.Writer!.WriteLine(BuildLineForTarget(target));
                        Interlocked.Increment(ref target.LinesWritten);
                        target.ConsecutiveFailures = 0;
                        target.LastErrorMessage = string.Empty;
                        target.IsDisabled = false;
                    }
                    catch (Exception) when (!token.IsCancellationRequested)
                    {
                        target.ConsecutiveFailures++;
                        if (target.ConsecutiveFailures >= GetMaxConsecutiveFailures())
                        {
                            target.IsDisabled = true;
                            target.LastErrorMessage = "Disabled: repeated write failures";
                        }
                    }
                }
                await Task.Delay(_intervalMs, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    private void RefreshGridCounts()
    {
        if (_targets.Count != _rows.Count)
            return;

        for (int i = 0; i < _rows.Count; i++)
        {
            _rows[i].LinesWritten = Interlocked.Read(ref _targets[i].LinesWritten);
            _rows[i].Status = _targets[i].Status;
        }

        _grid.Refresh();

        if (_writerTasks != null)
        {
            var totalLines = _targets.Sum(t => Interlocked.Read(ref t.LinesWritten));
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastRateCheck).TotalSeconds;
            if (elapsed > 0)
            {
                var rate = (totalLines - _lastTotalLines) / elapsed;
                _statusLabel.Text = $"Running ({_targets.Count} files, {_intervalMs}ms, {GetActiveEncodingLabel()}) – {rate:N0} lines/sec";
                _lastTotalLines = totalLines;
                _lastRateCheck = now;
            }
        }
    }

    private int GetMaxConsecutiveFailures()
    {
        return Math.Max(3, (int)Math.Ceiling((double)FailureRetryWindowMs / _intervalMs));
    }

    private string BuildLineForTarget(LogTarget target)
    {
        var rng = target.Rng;
        var level = _levels[rng.Next(_levels.Length)];
        var messageTemplate = _messages[rng.Next(_messages.Length)];
        var message = messageTemplate
            .Replace("{0}", rng.Next(10, 2000).ToString())
            .Replace("{1}", rng.Next(10000, 99999).ToString())
            .Replace("{2}", rng.Next(1, 500).ToString());

        var correlation = Guid.NewGuid().ToString("N")[..8];
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        return $"{timestamp} {level,-5} [{target.ApplicationName}] [corr={correlation}] {message}";
    }

    private static List<LogTarget> BuildTargets(string baseDir, int appCount, int filesPerApp)
    {
        var result = new List<LogTarget>(appCount * filesPerApp);

        for (int app = 1; app <= appCount; app++)
        {
            var appName = $"application{app}";
            var appDir = Path.Combine(baseDir, appName);
            for (int file = 1; file <= filesPerApp; file++)
            {
                var fileName = $"{appName}_instance{file}.log";
                result.Add(new LogTarget(appName, Path.Combine(appDir, fileName)));
            }
        }

        return result;
    }

    private static void EnsureFilesExist(IEnumerable<LogTarget> targets, Encoding encoding)
    {
        foreach (var target in targets)
        {
            var dir = Path.GetDirectoryName(target.FilePath)
                      ?? throw new InvalidOperationException($"Invalid file path: {target.FilePath}");
            Directory.CreateDirectory(dir);
            if (!File.Exists(target.FilePath))
                File.WriteAllText(target.FilePath, string.Empty, encoding);
        }
    }

    private static void OpenWriters(List<LogTarget> targets, Encoding encoding)
    {
        foreach (var target in targets)
        {
            var stream = new FileStream(
                target.FilePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                4096,
                FileOptions.None);
            target.Writer = new StreamWriter(stream, encoding) { AutoFlush = true };
        }
    }

    private static void CloseWriters(List<LogTarget> targets)
    {
        foreach (var target in targets)
            target.Dispose();
    }

    private Encoding ResolveSelectedEncoding()
    {
        var selected = _encodingCombo.SelectedValue is GeneratorEncoding value
            ? value
            : GeneratorEncoding.Utf8;
        return selected switch
        {
            GeneratorEncoding.Utf8 => new UTF8Encoding(false),
            GeneratorEncoding.Utf8Bom => new UTF8Encoding(true),
            GeneratorEncoding.Ansi => Encoding.GetEncoding(1252),
            GeneratorEncoding.Utf16 => Encoding.Unicode,
            GeneratorEncoding.Utf16Be => Encoding.BigEndianUnicode,
            _ => new UTF8Encoding(false)
        };
    }

    private string GetActiveEncodingLabel()
    {
        return (_encodingCombo.SelectedItem as EncodingChoice)?.Label ?? "UTF-8";
    }
}

internal sealed class LogTarget(string applicationName, string filePath) : IDisposable
{
    public string ApplicationName { get; } = applicationName;
    public string FilePath { get; } = filePath;
    public long LinesWritten;
    public int ConsecutiveFailures;
    public bool IsDisabled;
    public string LastErrorMessage { get; set; } = string.Empty;
    public StreamWriter? Writer { get; set; }
    public Random Rng { get; } = new();
    public string Status => IsDisabled ? LastErrorMessage : "Active";

    public void Dispose()
    {
        Writer?.Dispose();
        Writer = null;
    }
}

internal sealed class LogFileRow
{
    public string Application { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long LinesWritten { get; set; }
    public string Status { get; set; } = string.Empty;
}
