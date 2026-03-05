// LogGenerator — appends realistic log lines to multiple files concurrently.
// Run from the LogGenerator folder:  dotnet run
// Or point at specific files:        dotnet run -- path/to/a.log path/to/b.log
//
// Interactive controls (press while running):
//   + / -   increase / decrease write interval (faster / slower)
//   b       burst: write 20 lines to every file immediately
//   e       force an ERROR line on every file immediately
//   c       write a correlated request across all files (same correlation-id)
//   q       quit

using System.Text;

// ── Configuration ────────────────────────────────────────────────────────────

var testLogsDir = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "test-logs"));

static bool TryBuildGeneratedTargets(string[] args, out string[] files, out string? error)
{
    files = [];
    error = null;
    if (args.Length == 0)
        return false;

    // Named-arg generation mode:
    // --base-dir <path> --folders <count> --files-per-folder <count>
    bool hasFlag = args.Any(a => a.StartsWith("--", StringComparison.Ordinal));
    if (!hasFlag)
        return false;

    string? baseDir = null;
    int? folderCount = null;
    int? filesPerFolder = null;

    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            error = $"Unexpected positional argument: {arg}";
            return true;
        }

        if (i + 1 >= args.Length)
        {
            error = $"Missing value for {arg}.";
            return true;
        }

        var value = args[++i];
        switch (arg)
        {
            case "--base-dir":
                baseDir = value;
                break;
            case "--folders":
                if (!int.TryParse(value, out var parsedFolders) || parsedFolders <= 0)
                {
                    error = "folders must be a positive integer.";
                    return true;
                }
                folderCount = parsedFolders;
                break;
            case "--files-per-folder":
                if (!int.TryParse(value, out var parsedFiles) || parsedFiles <= 0)
                {
                    error = "files-per-folder must be a positive integer.";
                    return true;
                }
                filesPerFolder = parsedFiles;
                break;
            default:
                error = $"Unknown option: {arg}";
                return true;
        }
    }

    if (string.IsNullOrWhiteSpace(baseDir))
    {
        error = "Missing required option: --base-dir <path>";
        return true;
    }

    if (folderCount == null)
    {
        error = "Missing required option: --folders <count>";
        return true;
    }

    if (filesPerFolder == null)
    {
        error = "Missing required option: --files-per-folder <count>";
        return true;
    }

    var targetList = new List<string>(folderCount.Value * filesPerFolder.Value);
    for (int folder = 1; folder <= folderCount.Value; folder++)
    {
        var folderName = $"folder-{folder:D3}";
        var folderPath = Path.Combine(baseDir, folderName);
        for (int file = 1; file <= filesPerFolder.Value; file++)
        {
            var fileName = $"application{folder}_instance{file}.log";
            targetList.Add(Path.Combine(folderPath, fileName));
        }
    }

    files = targetList.ToArray();
    return true;
}

string[] targetFiles;
if (TryBuildGeneratedTargets(args, out var generatedTargets, out var generationError))
{
    if (generationError != null)
    {
        Console.WriteLine($"Invalid args: {generationError}");
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- --base-dir <baseDir> --folders <folderCount> --files-per-folder <filesPerFolder>");
        Console.WriteLine("  dotnet run -- <path/to/a.log> <path/to/b.log> ...");
        return;
    }

    targetFiles = generatedTargets;
}
else if (args.Length > 0)
{
    targetFiles = args;
}
else
{
    targetFiles = Directory.Exists(testLogsDir)
        ? Directory.GetFiles(testLogsDir, "*.log").OrderBy(f => f).ToArray()
        : [];
}

if (targetFiles.Length == 0)
{
    Console.WriteLine("No log files found. Pass file paths as arguments or ensure test-logs/ exists.");
    Console.WriteLine("Folder generation mode:");
    Console.WriteLine("  dotnet run -- --base-dir <baseDir> --folders <folderCount> --files-per-folder <filesPerFolder>");
    return;
}

// Ensure all files exist (create if missing)
foreach (var f in targetFiles)
{
    var dir = Path.GetDirectoryName(f);
    if (dir != null) Directory.CreateDirectory(dir);
    if (!File.Exists(f)) File.WriteAllText(f, "", Encoding.UTF8);
}

// ── State ─────────────────────────────────────────────────────────────────────

var intervalMs = 500;              // ms between automatic writes
var minInterval = 50;
var maxInterval = 5000;
var cts = new CancellationTokenSource();
var random = new Random();
var lineCounters = new long[targetFiles.Length];
var locks = targetFiles.Select(_ => new object()).ToArray();

// ── Log content pools ─────────────────────────────────────────────────────────

static string[] ServiceName(string filePath)
{
    var name = Path.GetFileNameWithoutExtension(filePath);
    // derive a plausible service name from the file name
    return name.Contains("application1") ? ["OrderService", "OrderService.Worker"]
         : name.Contains("application2") ? ["InventoryService", "InventoryService.Cache"]
         : name.Contains("application3") ? ["NotificationService", "EmailDispatcher"]
         : [name, $"{name}.Worker"];
}

string[][] serviceNames = targetFiles.Select(ServiceName).ToArray();

string[] logLevels = ["INFO", "INFO", "INFO", "INFO", "DEBUG", "DEBUG", "WARN", "ERROR"];

string[] infoMessages =
[
    "Request received: GET /api/v1/orders",
    "Request received: POST /api/v1/items",
    "Database query executed in {0}ms",
    "Cache hit for key: user:{1}",
    "Cache miss for key: product:{1}, fetching from DB",
    "Response sent: 200 OK in {0}ms",
    "Processed batch of {2} records",
    "Heartbeat OK — uptime {3}s",
    "Config refreshed successfully",
    "Connection pool: {4} active / 20 max",
    "Message enqueued: topic=events, partition={4}",
    "Message consumed: offset={3}, lag=0",
];

string[] debugMessages =
[
    "Entering method: ProcessOrder, orderId={1}",
    "Exiting method: ProcessOrder, elapsed={0}ms",
    "SQL: SELECT * FROM orders WHERE id={1} LIMIT 1",
    "Serializing response payload ({2} bytes)",
    "Thread pool queue depth: {4}",
    "DI scope created for request {1}",
    "Middleware pipeline: 5 handlers registered",
    "Retry attempt 1/3 for operation: FetchInventory",
];

string[] warnMessages =
[
    "Response time {0}ms exceeded threshold 300ms",
    "Cache eviction rate high: {4} evictions/min",
    "Retry succeeded after {4} attempts",
    "Disk usage at {4}% — consider cleanup",
    "Connection pool nearing limit: {4}/20",
    "JWT token expiring in {0}s",
    "Rate limit approaching for client {1}",
];

string[] errorMessages =
[
    "Unhandled exception in request pipeline",
    "Database connection timed out after {0}ms",
    "Failed to deserialize response from upstream service",
    "NullReferenceException in OrderProcessor.Finalize",
    "HTTP 503 from InventoryService — circuit breaker open",
    "Message processing failed: DLQ enqueued, key={1}",
    "Disk write error: path=/var/log/app, errno=28",
];

string FormatMessage(string template, int fileIndex)
{
    return template
        .Replace("{0}", random.Next(10, 2000).ToString())
        .Replace("{1}", random.Next(10000, 99999).ToString())
        .Replace("{2}", random.Next(1, 500).ToString())
        .Replace("{3}", random.Next(100, 99999).ToString())
        .Replace("{4}", random.Next(1, 20).ToString());
}

string BuildLine(int fileIndex, string? level = null, string? correlationId = null)
{
    level ??= logLevels[random.Next(logLevels.Length)];
    var service = serviceNames[fileIndex][random.Next(serviceNames[fileIndex].Length)];
    var corrId = correlationId ?? Guid.NewGuid().ToString("N")[..8];
    var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

    var pool = level switch
    {
        "DEBUG" => debugMessages,
        "WARN"  => warnMessages,
        "ERROR" => errorMessages,
        _       => infoMessages,
    };

    var msg = FormatMessage(pool[random.Next(pool.Length)], fileIndex);
    return $"{ts} [{level,-5}] [{service}] [corr={corrId}] {msg}";
}

void AppendLine(int fileIndex, string line)
{
    lock (locks[fileIndex])
    {
        File.AppendAllText(targetFiles[fileIndex], line + Environment.NewLine, Encoding.UTF8);
        Interlocked.Increment(ref lineCounters[fileIndex]);
    }
}

// ── Background writer ─────────────────────────────────────────────────────────

async Task AutoWriteLoop(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        await Task.Delay(intervalMs, ct).ContinueWith(_ => { }); // swallow cancel
        if (ct.IsCancellationRequested) break;

        // Each file writes on a slightly staggered schedule to simulate independent services
        for (int i = 0; i < targetFiles.Length; i++)
        {
            // ~30% chance any given file skips this tick (realistic: services have different activity levels)
            if (random.Next(10) < 3) continue;
            AppendLine(i, BuildLine(i));
        }

        DrawStatus();
    }
}

// ── Status display ────────────────────────────────────────────────────────────

int statusRow = -1;

void DrawStatus()
{
    try
    {
        if (statusRow < 0) return;
        var saved = Console.CursorTop;
        Console.SetCursorPosition(0, statusRow);
        Console.Write($"\r[interval={intervalMs}ms] Lines: ");
        for (int i = 0; i < targetFiles.Length; i++)
        {
            Console.Write($"{Path.GetFileName(targetFiles[i])}={lineCounters[i]}  ");
        }
        Console.Write("  ");
        Console.SetCursorPosition(0, saved);
    }
    catch { /* console resize race */ }
}

// ── Main ──────────────────────────────────────────────────────────────────────

Console.Clear();
Console.WriteLine("LogGenerator — tailing stress tool");
Console.WriteLine("====================================");
Console.WriteLine($"Writing to {targetFiles.Length} file(s):");
for (int i = 0; i < targetFiles.Length; i++)
    Console.WriteLine($"  [{i + 1}] {targetFiles[i]}");
Console.WriteLine();
Console.WriteLine("Controls: [+/-] speed  [b] burst  [e] force error  [c] correlate  [q] quit");
Console.WriteLine();
statusRow = Console.CursorTop;
Console.WriteLine(); // reserve status line

var writerTask = Task.Run(() => AutoWriteLoop(cts.Token));

// Key handler loop
while (true)
{
    var key = Console.ReadKey(intercept: true).Key;

    if (key == ConsoleKey.Q)
    {
        cts.Cancel();
        await writerTask;
        Console.WriteLine("\nStopped.");
        break;
    }

    if (key == ConsoleKey.OemPlus || key == ConsoleKey.Add)
    {
        intervalMs = Math.Max(minInterval, intervalMs - (intervalMs <= 100 ? 10 : 100));
        DrawStatus();
    }
    else if (key == ConsoleKey.OemMinus || key == ConsoleKey.Subtract)
    {
        intervalMs = Math.Min(maxInterval, intervalMs + (intervalMs < 100 ? 10 : 100));
        DrawStatus();
    }
    else if (key == ConsoleKey.B)
    {
        // Burst: 20 lines per file
        for (int i = 0; i < targetFiles.Length; i++)
            for (int j = 0; j < 20; j++)
                AppendLine(i, BuildLine(i));
        DrawStatus();
    }
    else if (key == ConsoleKey.E)
    {
        // Force ERROR on every file
        for (int i = 0; i < targetFiles.Length; i++)
            AppendLine(i, BuildLine(i, level: "ERROR"));
        DrawStatus();
    }
    else if (key == ConsoleKey.C)
    {
        // Correlated request: same correlation-id across all files
        var corrId = Guid.NewGuid().ToString("N")[..8];
        for (int i = 0; i < targetFiles.Length; i++)
            AppendLine(i, BuildLine(i, correlationId: corrId));
        DrawStatus();
    }
}
