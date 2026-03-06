using System.ComponentModel;
using System.Text;

namespace LogGenerator;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new GeneratorForm());
    }
}

internal sealed class GeneratorForm : Form
{
    private readonly TextBox _baseDirTextBox = new() { Width = 420 };
    private readonly NumericUpDown _appsNumeric = new() { Minimum = 1, Maximum = 200, Value = 5, Width = 80 };
    private readonly NumericUpDown _filesPerAppNumeric = new() { Minimum = 1, Maximum = 200, Value = 10, Width = 80 };
    private readonly NumericUpDown _intervalNumeric = new() { Minimum = 50, Maximum = 5000, Value = 500, Increment = 50, Width = 80 };
    private readonly Button _startStopButton = new() { Text = "Start", Width = 100 };
    private readonly Label _statusLabel = new() { AutoSize = true, Text = "Stopped" };
    private readonly DataGridView _grid = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 250 };
    private readonly BindingList<LogFileRow> _rows = new();
    private readonly Random _random = new();

    private CancellationTokenSource? _cts;
    private Task? _writerTask;
    private List<LogTarget> _targets = [];
    private int _intervalMs = 500;

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

        var topPanel = BuildTopPanel();
        BuildGrid();

        Controls.Add(_grid);
        Controls.Add(topPanel);

        _refreshTimer.Tick += (_, _) => RefreshGridCounts();
        _startStopButton.Click += async (_, _) => await ToggleStartStopAsync();
        _intervalNumeric.ValueChanged += (_, _) => _intervalMs = (int)_intervalNumeric.Value;
        FormClosing += async (_, e) =>
        {
            if (_writerTask != null)
            {
                e.Cancel = true;
                await StopGenerationAsync();
                Close();
            }
        };
    }

    private Control BuildTopPanel()
    {
        var browseButton = new Button { Text = "Browse...", Width = 90 };
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

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 70,
            Padding = new Padding(12, 10, 12, 10),
            AutoSize = false,
            WrapContents = true
        };

        panel.Controls.Add(new Label { Text = "Base Directory", AutoSize = true, Margin = new Padding(0, 8, 8, 0) });
        panel.Controls.Add(_baseDirTextBox);
        panel.Controls.Add(browseButton);

        panel.Controls.Add(new Label { Text = "Apps", AutoSize = true, Margin = new Padding(18, 8, 6, 0) });
        panel.Controls.Add(_appsNumeric);

        panel.Controls.Add(new Label { Text = "Files per App", AutoSize = true, Margin = new Padding(18, 8, 6, 0) });
        panel.Controls.Add(_filesPerAppNumeric);

        panel.Controls.Add(new Label { Text = "Interval (ms)", AutoSize = true, Margin = new Padding(18, 8, 6, 0) });
        panel.Controls.Add(_intervalNumeric);

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
    }

    private async Task ToggleStartStopAsync()
    {
        if (_writerTask == null)
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
            _targets = BuildTargets(baseDir, (int)_appsNumeric.Value, (int)_filesPerAppNumeric.Value);
            EnsureFilesExist(_targets);
        }
        catch (Exception ex)
        {
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
                LinesWritten = 0
            });
        }

        _cts = new CancellationTokenSource();
        _writerTask = Task.Run(() => WriterLoopAsync(_cts.Token));
        _refreshTimer.Start();

        _startStopButton.Text = "Stop";
        _baseDirTextBox.Enabled = false;
        _appsNumeric.Enabled = false;
        _filesPerAppNumeric.Enabled = false;
        _intervalNumeric.Enabled = false;
        _statusLabel.Text = $"Running ({_targets.Count} files)";
    }

    private async Task StopGenerationAsync()
    {
        if (_writerTask == null || _cts == null)
            return;

        _cts.Cancel();
        try
        {
            await _writerTask;
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _writerTask = null;
            _refreshTimer.Stop();
            RefreshGridCounts();

            _startStopButton.Text = "Start";
            _baseDirTextBox.Enabled = true;
            _appsNumeric.Enabled = true;
            _filesPerAppNumeric.Enabled = true;
            _intervalNumeric.Enabled = true;
            _statusLabel.Text = "Stopped";
        }
    }

    private async Task WriterLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(_intervalMs, token);
            for (int i = 0; i < _targets.Count; i++)
            {
                if (_random.Next(10) < 3)
                    continue;

                var line = BuildLine(_targets[i].ApplicationName);
                File.AppendAllText(_targets[i].FilePath, line + Environment.NewLine, Encoding.UTF8);
                _targets[i].LinesWritten++;
            }
        }
    }

    private void RefreshGridCounts()
    {
        if (_targets.Count != _rows.Count)
            return;

        for (int i = 0; i < _rows.Count; i++)
            _rows[i].LinesWritten = _targets[i].LinesWritten;

        _grid.Refresh();
    }

    private string BuildLine(string appName)
    {
        var level = _levels[_random.Next(_levels.Length)];
        var messageTemplate = _messages[_random.Next(_messages.Length)];
        var message = messageTemplate
            .Replace("{0}", _random.Next(10, 2000).ToString())
            .Replace("{1}", _random.Next(10000, 99999).ToString())
            .Replace("{2}", _random.Next(1, 500).ToString());

        var correlation = Guid.NewGuid().ToString("N")[..8];
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        return $"{timestamp} [{level,-5}] [{appName}] [corr={correlation}] {message}";
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

    private static void EnsureFilesExist(IEnumerable<LogTarget> targets)
    {
        foreach (var target in targets)
        {
            var dir = Path.GetDirectoryName(target.FilePath)
                      ?? throw new InvalidOperationException($"Invalid file path: {target.FilePath}");
            Directory.CreateDirectory(dir);
            if (!File.Exists(target.FilePath))
                File.WriteAllText(target.FilePath, string.Empty, Encoding.UTF8);
        }
    }
}

internal sealed class LogTarget(string applicationName, string filePath)
{
    public string ApplicationName { get; } = applicationName;
    public string FilePath { get; } = filePath;
    public long LinesWritten { get; set; }
}

internal sealed class LogFileRow
{
    public string Application { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long LinesWritten { get; set; }
}
