using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CanScanmatik;

internal sealed class MainForm : Form
{
    private const int LogTrimThreshold = 140_000;
    private const int LogTargetLength = 100_000;
    private const int VisibleByteColumns = 8;
    private const int GridRefreshBatchSize = 500;
    private const int ChangedByteHoldMs = 250;
    private static readonly Color ChangedByteBackColor = Color.FromArgb(255, 255, 0);
    private static readonly Color ChangedByteForeColor = Color.FromArgb(180, 0, 0);

    private readonly ComboBox driverCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly ComboBox bitrateCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 95 };
    private readonly ComboBox frameFormatCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 125 };
    private readonly ComboBox busCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly ComboBox sortCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly TextBox filterTextBox = new() { Width = 100, PlaceholderText = "208" };
    private readonly Button refreshButton = new() { Text = "Refresh", AutoSize = true };
    private readonly Button configButton = new() { Text = "Config", AutoSize = true };
    private readonly Button connectButton = new() { Text = "Connect", AutoSize = true };
    private readonly Button clearButton = new() { Text = "Clear Bus", AutoSize = true };
    private readonly Button exportButton = new() { Text = "Save CSV", AutoSize = true };
    private readonly Button saveIdsButton = new() { Text = "Save IDs", AutoSize = true };
    private readonly Label statusLabel = new() { AutoSize = true, Text = "Disconnected" };
    private readonly Label adapterInfoLabel = new() { AutoSize = true };
    private readonly Label busHintLabel = new() { AutoSize = true };
    private readonly DataGridView frameGrid = new();
    private readonly TextBox logBox = new();
    private readonly System.Windows.Forms.Timer uiTimer = new() { Interval = 40 };
    private readonly ConcurrentQueue<QueuedFrame> pendingFrames = new();
    private readonly Dictionary<string, BusWorkspace> busWorkspaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BitrateOption> bitrateOptions = BitrateOption.CreateDefaults();
    private readonly List<BusProfileOption> busOptions = BusProfileOption.CreateDefaults();
    private readonly List<SortModeOption> sortOptions = SortModeOption.CreateDefaults();

    private CancellationTokenSource? readLoopCancellation;
    private Task? readLoopTask;
    private PassThruApi? api;
    private string? activeConnectionBusName;
    private StreamWriter? sessionLogWriter;
    private string? sessionLogFilePath;
    private int uiDrainScheduled;

    public MainForm()
    {
        InitializeUi();
        HookEvents();
        EnsureBusWorkspaces();
        LoadDrivers();
        RefreshGrid();
    }

    private void InitializeUi()
    {
        Text = "CanScanmatik";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1280, 820);
        Size = new Size(1460, 920);

        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 220f));
        Controls.Add(root);

        GroupBox connectionGroup = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Text = "Scanmatik J2534"
        };
        root.Controls.Add(connectionGroup, 0, 0);

        FlowLayoutPanel connectionPanel = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(10),
            WrapContents = true
        };
        connectionGroup.Controls.Add(connectionPanel);

        connectionPanel.Controls.Add(CreateLabel("Adapter"));
        connectionPanel.Controls.Add(driverCombo);
        connectionPanel.Controls.Add(refreshButton);
        connectionPanel.Controls.Add(configButton);

        connectionPanel.Controls.Add(CreateLabel("Bus"));
        busCombo.Items.AddRange(busOptions.Cast<object>().ToArray());
        busCombo.SelectedIndex = 0;
        connectionPanel.Controls.Add(busCombo);

        connectionPanel.Controls.Add(CreateLabel("Bitrate"));
        bitrateCombo.Items.AddRange(bitrateOptions.Cast<object>().ToArray());
        bitrateCombo.SelectedItem = bitrateOptions.First(static item => item.BaudRate == 500000);
        connectionPanel.Controls.Add(bitrateCombo);

        connectionPanel.Controls.Add(CreateLabel("Frames"));
        frameFormatCombo.Items.AddRange(new object[]
        {
            new FrameFormatOption("11-bit", false),
            new FrameFormatOption("29-bit", true)
        });
        frameFormatCombo.SelectedIndex = 0;
        connectionPanel.Controls.Add(frameFormatCombo);

        connectionPanel.Controls.Add(CreateLabel("Sort"));
        sortCombo.Items.AddRange(sortOptions.Cast<object>().ToArray());
        sortCombo.SelectedIndex = 0;
        connectionPanel.Controls.Add(sortCombo);

        connectionPanel.Controls.Add(CreateLabel("Filter ID"));
        connectionPanel.Controls.Add(filterTextBox);

        connectButton.Margin = new Padding(14, 3, 0, 3);
        connectionPanel.Controls.Add(connectButton);
        connectionPanel.Controls.Add(clearButton);
        connectionPanel.Controls.Add(exportButton);
        connectionPanel.Controls.Add(saveIdsButton);

        statusLabel.Margin = new Padding(14, 8, 0, 0);
        connectionPanel.Controls.Add(statusLabel);

        adapterInfoLabel.Margin = new Padding(0, 10, 0, 0);
        adapterInfoLabel.MaximumSize = new Size(1320, 0);
        connectionPanel.SetFlowBreak(statusLabel, true);
        connectionPanel.Controls.Add(adapterInfoLabel);

        busHintLabel.Margin = new Padding(0, 4, 0, 4);
        busHintLabel.MaximumSize = new Size(1320, 0);
        busHintLabel.Text = "Bus selection separates sessions in the app. Physical line routing is still configured by the Scanmatik driver.";
        connectionPanel.SetFlowBreak(adapterInfoLabel, true);
        connectionPanel.Controls.Add(busHintLabel);

        frameGrid.Dock = DockStyle.Fill;
        frameGrid.AllowUserToAddRows = false;
        frameGrid.AllowUserToDeleteRows = false;
        frameGrid.AllowUserToResizeRows = false;
        frameGrid.ReadOnly = true;
        frameGrid.MultiSelect = false;
        frameGrid.RowHeadersVisible = false;
        frameGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        frameGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        frameGrid.BackgroundColor = Color.White;
        frameGrid.BorderStyle = BorderStyle.Fixed3D;
        frameGrid.Columns.Add("Time", "Time");
        frameGrid.Columns.Add("Bus", "Bus");
        frameGrid.Columns.Add("Id", "ID");
        frameGrid.Columns.Add("Format", "Fmt");
        frameGrid.Columns.Add("Dlc", "DLC");
        for (int index = 0; index < VisibleByteColumns; index++)
        {
            frameGrid.Columns.Add($"D{index}", $"D{index}");
        }
        frameGrid.Columns.Add("Count", "Cnt");
        frameGrid.Columns.Add("Period", "Period ms");
        frameGrid.Columns.Add("DriverTs", "Tstamp");
        frameGrid.Columns[0].FillWeight = 105;
        frameGrid.Columns[1].FillWeight = 50;
        frameGrid.Columns[2].FillWeight = 70;
        frameGrid.Columns[3].FillWeight = 50;
        frameGrid.Columns[4].FillWeight = 42;
        for (int index = 5; index < 13; index++)
        {
            frameGrid.Columns[index].FillWeight = 42;
        }
        frameGrid.Columns[13].FillWeight = 45;
        frameGrid.Columns[14].FillWeight = 70;
        frameGrid.Columns[15].FillWeight = 80;
        root.Controls.Add(frameGrid, 0, 1);

        GroupBox logGroup = new()
        {
            Dock = DockStyle.Fill,
            Text = "CAN Log"
        };
        root.Controls.Add(logGroup, 0, 2);

        logBox.Dock = DockStyle.Fill;
        logBox.Multiline = true;
        logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Vertical;
        logBox.Font = new Font("Consolas", 9f);
        logGroup.Controls.Add(logBox);
    }

    private void HookEvents()
    {
        refreshButton.Click += (_, _) => LoadDrivers();
        configButton.Click += (_, _) => OpenDriverConfig();
        connectButton.Click += async (_, _) => await ToggleConnectionAsync();
        clearButton.Click += (_, _) => ClearActiveBus();
        exportButton.Click += (_, _) => ExportCsv();
        saveIdsButton.Click += (_, _) => SaveIdsToFile();
        filterTextBox.TextChanged += (_, _) => RefreshGrid();
        sortCombo.SelectedIndexChanged += (_, _) => RefreshGrid();
        busCombo.SelectedIndexChanged += (_, _) => RefreshGrid();
        driverCombo.SelectedIndexChanged += (_, _) => UpdateAdapterDescription();
        uiTimer.Tick += (_, _) => DrainPendingFrames();
        FormClosing += async (_, _) => await DisconnectAsync();
    }

    private void EnsureBusWorkspaces()
    {
        foreach (BusProfileOption option in busOptions)
        {
            GetOrCreateWorkspace(option.Name);
        }
    }

    private void LoadDrivers()
    {
        IReadOnlyList<PassThruDriverInfo> drivers = PassThruRegistry.GetInstalledDrivers();

        driverCombo.BeginUpdate();
        try
        {
            driverCombo.Items.Clear();
            foreach (PassThruDriverInfo driver in drivers)
            {
                driverCombo.Items.Add(driver);
            }
        }
        finally
        {
            driverCombo.EndUpdate();
        }

        if (driverCombo.Items.Count > 0)
        {
            driverCombo.SelectedIndex = 0;
            statusLabel.Text = "Select adapter settings and click Connect.";
        }
        else
        {
            statusLabel.Text = "No J2534 drivers found. Install the Scanmatik PassThru driver.";
        }

        UpdateAdapterDescription();
    }

    private void UpdateAdapterDescription()
    {
        PassThruDriverInfo? driver = driverCombo.SelectedItem as PassThruDriverInfo;
        if (driver is null)
        {
            adapterInfoLabel.Text = "J2534 registry is empty. Scanmatik SM2/SM3 must be installed with PassThru support.";
            configButton.Enabled = false;
            return;
        }

        StringBuilder builder = new();
        builder.Append("DLL: ").Append(driver.FunctionLibrary);

        if (!string.IsNullOrWhiteSpace(driver.ConfigApplication))
        {
            builder.Append(" | Config: ").Append(driver.ConfigApplication);
        }

        builder.Append(" | Build target: x86");
        adapterInfoLabel.Text = builder.ToString();
        configButton.Enabled = !string.IsNullOrWhiteSpace(driver.ConfigApplication) && File.Exists(driver.ConfigApplication);
    }

    private async Task ToggleConnectionAsync()
    {
        if (api is not null)
        {
            await DisconnectAsync();
            return;
        }

        await ConnectAsync();
    }

    private Task ConnectAsync()
    {
        PassThruDriverInfo? driver = driverCombo.SelectedItem as PassThruDriverInfo;
        BitrateOption? bitrate = bitrateCombo.SelectedItem as BitrateOption;
        FrameFormatOption? frameFormat = frameFormatCombo.SelectedItem as FrameFormatOption;
        BusProfileOption? busProfile = busCombo.SelectedItem as BusProfileOption;
        PassThruApi? newApi = null;

        if (driver is null)
        {
            MessageBox.Show(this, "Select a Scanmatik J2534 adapter first.", "CanScanmatik", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return Task.CompletedTask;
        }

        if (bitrate is null || frameFormat is null || busProfile is null)
        {
            MessageBox.Show(this, "Unable to read the selected connection parameters.", "CanScanmatik", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return Task.CompletedTask;
        }

        connectButton.Enabled = false;
        refreshButton.Enabled = false;
        configButton.Enabled = false;
        driverCombo.Enabled = false;
        bitrateCombo.Enabled = false;
        frameFormatCombo.Enabled = false;
        busCombo.Enabled = false;
        statusLabel.Text = "Opening J2534 adapter...";

        try
        {
            newApi = new PassThruApi(driver);
            (string firmwareVersion, string dllVersion, string apiVersion) = newApi.OpenAndReadVersion();
            newApi.ConnectCan(bitrate.BaudRate, frameFormat.UseExtendedIdentifiers);

            api = newApi;
            activeConnectionBusName = busProfile.Name;
            readLoopCancellation = new CancellationTokenSource();
            readLoopTask = Task.Run(() => ReadLoopAsync(newApi, activeConnectionBusName, readLoopCancellation.Token));
            StartSessionLog(driver, busProfile, bitrate, frameFormat, firmwareVersion, dllVersion, apiVersion);
            uiTimer.Start();

            connectButton.Text = "Disconnect";
            statusLabel.Text = $"Connected: {driver.DisplayName} | {busProfile.Name} | {bitrate.DisplayName} | {(frameFormat.UseExtendedIdentifiers ? "29-bit" : "11-bit")}";
            adapterInfoLabel.Text = $"Firmware: {firmwareVersion} | DLL: {dllVersion} | API: {apiVersion} | {driver.FunctionLibrary}";
            AppendLog($"OPEN  {driver.DisplayName}");
            AppendLog($"BUS   {busProfile.Name}");
            AppendLog($"LINK  {bitrate.DisplayName}, {(frameFormat.UseExtendedIdentifiers ? "extended 29-bit" : "standard 11-bit")}");
        }
        catch (Exception ex)
        {
            newApi?.Dispose();
            api = null;
            activeConnectionBusName = null;
            StopSessionLog();
            statusLabel.Text = "Connection failed.";
            MessageBox.Show(this, ex.Message, "Scanmatik / J2534 Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            RestoreControlsAfterDisconnect();
        }
        finally
        {
            connectButton.Enabled = true;
            refreshButton.Enabled = true;
        }

        return Task.CompletedTask;
    }

    private async Task DisconnectAsync()
    {
        uiTimer.Stop();

        CancellationTokenSource? cancellation = readLoopCancellation;
        Task? readTask = readLoopTask;
        readLoopCancellation = null;
        readLoopTask = null;

        if (cancellation is not null)
        {
            cancellation.Cancel();
        }

        if (readTask is not null)
        {
            try
            {
                await readTask.ConfigureAwait(true);
            }
            catch
            {
            }
        }

        cancellation?.Dispose();

        if (activeConnectionBusName is not null)
        {
            SaveIdsToDefaultFile(GetOrCreateWorkspace(activeConnectionBusName), overwriteLastFile: true);
        }

        api?.Dispose();
        api = null;
        activeConnectionBusName = null;
        StopSessionLog();
        RestoreControlsAfterDisconnect();
    }

    private async Task ReadLoopAsync(PassThruApi currentApi, string busName, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IReadOnlyList<CanFrame> frames = currentApi.ReadCanFrames(timeoutMs: 100, batchSize: 32);
                foreach (CanFrame frame in frames)
                {
                    pendingFrames.Enqueue(new QueuedFrame(busName, frame));
                }

                if (frames.Count > 0)
                {
                    RequestUiDrain();
                }

                if (frames.Count == 0)
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                BeginInvoke(() =>
                {
                    AppendLog($"ERROR {ex.Message}");
                    MessageBox.Show(this, ex.Message, "CAN Read Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                });
            }
        }
    }

    private void DrainPendingFrames()
    {
        Interlocked.Exchange(ref uiDrainScheduled, 0);

        string activeBusName = GetSelectedBusName();
        HashSet<string> changedKeys = new(StringComparer.OrdinalIgnoreCase);
        int processed = 0;

        while (processed < GridRefreshBatchSize && pendingFrames.TryDequeue(out QueuedFrame queuedFrame))
        {
            processed++;
            UpdateSnapshot(queuedFrame.BusName, queuedFrame.Frame);
            AppendFrameLog(queuedFrame.BusName, queuedFrame.Frame);
            if (string.Equals(queuedFrame.BusName, activeBusName, StringComparison.OrdinalIgnoreCase))
            {
                changedKeys.Add(GetFrameKey(queuedFrame.Frame.Id, queuedFrame.Frame.IsExtended));
            }
        }

        if (changedKeys.Count > 0)
        {
            UpdateVisibleGridRows(activeBusName, changedKeys);
        }
        else
        {
            RefreshVisibleHighlightState();
        }

        if (!pendingFrames.IsEmpty)
        {
            RequestUiDrain();
        }
    }

    private void RequestUiDrain()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (Interlocked.Exchange(ref uiDrainScheduled, 1) == 1)
        {
            return;
        }

        try
        {
            BeginInvoke((Action)(() =>
            {
                if (!IsDisposed)
                {
                    DrainPendingFrames();
                }
            }));
        }
        catch
        {
            Interlocked.Exchange(ref uiDrainScheduled, 0);
        }
    }

    private void UpdateSnapshot(string busName, CanFrame frame)
    {
        string key = GetFrameKey(frame.Id, frame.IsExtended);
        BusWorkspace workspace = GetOrCreateWorkspace(busName);
        if (!workspace.Frames.TryGetValue(key, out FrameSnapshot? snapshot))
        {
            snapshot = new FrameSnapshot(busName, frame.Id, frame.IsExtended);
            workspace.Frames[key] = snapshot;
        }

        snapshot.Count++;
        snapshot.PreviousSeenUtc = snapshot.LastSeenUtc == DateTime.MinValue ? null : snapshot.LastSeenUtc;
        snapshot.LastSeenUtc = frame.ReceivedAtUtc;
        snapshot.Dlc = frame.Dlc;
        snapshot.DeviceTimestamp = frame.DeviceTimestamp;
        snapshot.ChangedBytes = ComputeChangedBytes(snapshot.Data, frame.Data);
        DateTime highlightUntilUtc = DateTime.UtcNow.AddMilliseconds(ChangedByteHoldMs);
        for (int index = 0; index < VisibleByteColumns; index++)
        {
            if (snapshot.ChangedBytes[index])
            {
                snapshot.ChangedByteUntilUtc[index] = highlightUntilUtc;
            }
        }
        snapshot.Data = frame.Data.ToArray();
    }

    private void RefreshGrid()
    {
        BusWorkspace workspace = GetOrCreateWorkspace(GetSelectedBusName());
        string? filterText = NormalizeHexFilter(filterTextBox.Text);
        SortModeOption sortMode = (sortCombo.SelectedItem as SortModeOption) ?? sortOptions[0];
        IEnumerable<FrameSnapshot> rows = workspace.Frames.Values.Where(snapshot => MatchesFilter(snapshot, filterText));

        rows = sortMode.Mode switch
        {
            SortMode.ByActivity => rows
                .OrderByDescending(static snapshot => snapshot.LastSeenUtc)
                .ThenBy(static snapshot => snapshot.Id),
            _ => rows
                .OrderBy(static snapshot => snapshot.IsExtended)
                .ThenBy(static snapshot => snapshot.Id)
                .ThenByDescending(static snapshot => snapshot.LastSeenUtc)
        };

        frameGrid.SuspendLayout();
        frameGrid.Rows.Clear();
        workspace.VisibleRows.Clear();

        foreach (FrameSnapshot snapshot in rows)
        {
            int rowIndex = frameGrid.Rows.Add();
            DataGridViewRow row = frameGrid.Rows[rowIndex];
            row.Tag = GetFrameKey(snapshot.Id, snapshot.IsExtended);
            workspace.VisibleRows[(string)row.Tag] = row;
            UpdateGridRow(row, snapshot);
        }

        frameGrid.ResumeLayout();
    }

    private void ClearActiveBus()
    {
        BusWorkspace workspace = GetOrCreateWorkspace(GetSelectedBusName());
        workspace.Frames.Clear();
        workspace.VisibleRows.Clear();
        frameGrid.Rows.Clear();
        logBox.Clear();
        AppendLog($"CLEAR {workspace.Name}");
    }

    private void ExportCsv()
    {
        BusWorkspace workspace = GetOrCreateWorkspace(GetSelectedBusName());
        if (workspace.Frames.Count == 0)
        {
            MessageBox.Show(this, "The current bus has no frames to export.", "CanScanmatik", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using SaveFileDialog dialog = new()
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = $"{workspace.Name}_frames_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            Title = "Save CAN table"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        StringBuilder csv = new();
        csv.AppendLine("LastSeen,Bus,Id,Format,Dlc,D0,D1,D2,D3,D4,D5,D6,D7,Count,PeriodMs,DriverTimestamp");

        foreach (FrameSnapshot snapshot in workspace.Frames.Values.OrderBy(static item => item.Id).ThenBy(static item => item.IsExtended))
        {
            double? periodMs = snapshot.PreviousSeenUtc.HasValue
                ? (snapshot.LastSeenUtc - snapshot.PreviousSeenUtc.Value).TotalMilliseconds
                : null;

            csv.Append(snapshot.LastSeenUtc == DateTime.MinValue ? string.Empty : snapshot.LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(',');
            csv.Append(snapshot.BusName).Append(',');
            csv.Append(FormatId(snapshot.Id, snapshot.IsExtended)).Append(',');
            csv.Append(snapshot.IsExtended ? "29-bit" : "11-bit").Append(',');
            csv.Append(snapshot.Dlc.ToString(CultureInfo.InvariantCulture));
            for (int index = 0; index < VisibleByteColumns; index++)
            {
                csv.Append(',').Append(FormatByteCell(snapshot.Data, index));
            }
            csv.Append(',').Append(snapshot.Count.ToString(CultureInfo.InvariantCulture));
            csv.Append(',').Append(periodMs.HasValue ? periodMs.Value.ToString("F1", CultureInfo.InvariantCulture) : string.Empty);
            csv.Append(',').Append(snapshot.DeviceTimestamp.ToString(CultureInfo.InvariantCulture));
            csv.AppendLine();
        }

        File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(false));
        AppendLog($"SAVE  CSV {dialog.FileName}");
    }

    private void SaveIdsToFile()
    {
        BusWorkspace workspace = GetOrCreateWorkspace(GetSelectedBusName());
        if (workspace.Frames.Count == 0)
        {
            MessageBox.Show(this, "The current bus has no IDs to save.", "CanScanmatik", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using SaveFileDialog dialog = new()
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"{workspace.Name}_ids_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            Title = "Save CAN IDs"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SaveIds(workspace, dialog.FileName);
        AppendLog($"SAVE  IDS {dialog.FileName}");
    }

    private void SaveIdsToDefaultFile(BusWorkspace workspace, bool overwriteLastFile)
    {
        if (workspace.Frames.Count == 0)
        {
            return;
        }

        string idsDirectory = Path.Combine(AppContext.BaseDirectory, "ids");
        Directory.CreateDirectory(idsDirectory);
        string fileName = overwriteLastFile
            ? $"{workspace.Name}_last_ids.txt"
            : $"{workspace.Name}_ids_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
        SaveIds(workspace, Path.Combine(idsDirectory, fileName));
    }

    private void SaveIds(BusWorkspace workspace, string filePath)
    {
        StringBuilder builder = new();
        builder.AppendLine($"# Bus: {workspace.Name}");
        builder.AppendLine($"# Saved: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"# Count: {workspace.Frames.Count.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine();

        foreach (FrameSnapshot snapshot in workspace.Frames.Values.OrderBy(static item => item.IsExtended).ThenBy(static item => item.Id))
        {
            builder.AppendLine(FormatId(snapshot.Id, snapshot.IsExtended));
        }

        File.WriteAllText(filePath, builder.ToString(), new UTF8Encoding(false));
    }

    private void OpenDriverConfig()
    {
        PassThruDriverInfo? driver = driverCombo.SelectedItem as PassThruDriverInfo;
        if (driver is null || string.IsNullOrWhiteSpace(driver.ConfigApplication))
        {
            MessageBox.Show(this, "No Scanmatik config application is registered for the selected adapter.", "CanScanmatik", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!File.Exists(driver.ConfigApplication))
        {
            MessageBox.Show(this, $"Config application not found:{Environment.NewLine}{driver.ConfigApplication}", "CanScanmatik", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = driver.ConfigApplication,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to open adapter config", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StartSessionLog(PassThruDriverInfo driver, BusProfileOption busProfile, BitrateOption bitrate, FrameFormatOption frameFormat, string firmwareVersion, string dllVersion, string apiVersion)
    {
        StopSessionLog();

        string logsDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logsDirectory);
        sessionLogFilePath = Path.Combine(logsDirectory, $"{busProfile.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        sessionLogWriter = new StreamWriter(sessionLogFilePath, append: false, new UTF8Encoding(false))
        {
            AutoFlush = true
        };

        sessionLogWriter.WriteLine($"# Start: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sessionLogWriter.WriteLine($"# Adapter: {driver.DisplayName}");
        sessionLogWriter.WriteLine($"# Bus: {busProfile.Name}");
        sessionLogWriter.WriteLine($"# Bitrate: {bitrate.DisplayName}");
        sessionLogWriter.WriteLine($"# Frames: {(frameFormat.UseExtendedIdentifiers ? "29-bit" : "11-bit")}");
        sessionLogWriter.WriteLine($"# Firmware: {firmwareVersion}");
        sessionLogWriter.WriteLine($"# DLL: {dllVersion}");
        sessionLogWriter.WriteLine($"# API: {apiVersion}");
        sessionLogWriter.WriteLine();
        AppendLog($"LOG   {sessionLogFilePath}");
    }

    private void StopSessionLog()
    {
        if (sessionLogWriter is not null)
        {
            try
            {
                sessionLogWriter.Flush();
                sessionLogWriter.Dispose();
            }
            catch
            {
            }
        }

        sessionLogWriter = null;
        sessionLogFilePath = null;
    }

    private void RestoreControlsAfterDisconnect()
    {
        connectButton.Text = "Connect";
        connectButton.Enabled = true;
        refreshButton.Enabled = true;
        driverCombo.Enabled = true;
        bitrateCombo.Enabled = true;
        frameFormatCombo.Enabled = true;
        busCombo.Enabled = true;
        statusLabel.Text = "Disconnected";
        UpdateAdapterDescription();
    }

    private void UpdateVisibleGridRows(string busName, IEnumerable<string> changedKeys)
    {
        if (!string.Equals(busName, GetSelectedBusName(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        BusWorkspace workspace = GetOrCreateWorkspace(busName);
        SortModeOption sortMode = (sortCombo.SelectedItem as SortModeOption) ?? sortOptions[0];
        string? filterText = NormalizeHexFilter(filterTextBox.Text);

        if (sortMode.Mode != SortMode.ById)
        {
            RefreshGrid();
            return;
        }

        bool requiresFullRefresh = false;
        foreach (string key in changedKeys)
        {
            if (!workspace.Frames.TryGetValue(key, out FrameSnapshot? snapshot))
            {
                continue;
            }

            bool matchesFilter = MatchesFilter(snapshot, filterText);
            bool hasRow = workspace.VisibleRows.TryGetValue(key, out DataGridViewRow? row) && row is not null && row.DataGridView == frameGrid;

            if (!matchesFilter)
            {
                if (hasRow)
                {
                    frameGrid.Rows.Remove(row!);
                    workspace.VisibleRows.Remove(key);
                }

                continue;
            }

            if (!hasRow)
            {
                requiresFullRefresh = true;
                break;
            }

            UpdateGridRow(row!, snapshot);
        }

        if (requiresFullRefresh)
        {
            RefreshGrid();
        }
    }

    private void UpdateGridRow(DataGridViewRow row, FrameSnapshot snapshot)
    {
        double? periodMs = snapshot.PreviousSeenUtc.HasValue
            ? (snapshot.LastSeenUtc - snapshot.PreviousSeenUtc.Value).TotalMilliseconds
            : null;

        row.Cells[0].Value = snapshot.LastSeenUtc == DateTime.MinValue
            ? "-"
            : snapshot.LastSeenUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        row.Cells[1].Value = snapshot.BusName;
        row.Cells[2].Value = FormatId(snapshot.Id, snapshot.IsExtended);
        row.Cells[3].Value = snapshot.IsExtended ? "29" : "11";
        row.Cells[4].Value = snapshot.Dlc.ToString(CultureInfo.InvariantCulture);
        for (int index = 0; index < VisibleByteColumns; index++)
        {
            row.Cells[5 + index].Value = FormatByteCell(snapshot.Data, index);
        }
        row.Cells[13].Value = snapshot.Count.ToString(CultureInfo.InvariantCulture);
        row.Cells[14].Value = periodMs.HasValue ? periodMs.Value.ToString("F1", CultureInfo.InvariantCulture) : "-";
        row.Cells[15].Value = snapshot.DeviceTimestamp.ToString(CultureInfo.InvariantCulture);
        ApplyRowHighlight(row, snapshot);
    }

    private void RefreshVisibleHighlightState()
    {
        string busName = GetSelectedBusName();
        BusWorkspace workspace = GetOrCreateWorkspace(busName);
        bool anyVisibleRows = workspace.VisibleRows.Count > 0;
        if (!anyVisibleRows)
        {
            return;
        }

        foreach (KeyValuePair<string, DataGridViewRow> pair in workspace.VisibleRows.ToArray())
        {
            string key = pair.Key;
            DataGridViewRow row = pair.Value;
            if (row.DataGridView != frameGrid)
            {
                workspace.VisibleRows.Remove(key);
                continue;
            }

            if (workspace.Frames.TryGetValue(key, out FrameSnapshot? snapshot))
            {
                ApplyRowHighlight(row, snapshot);
            }
        }
    }

    private void ApplyRowHighlight(DataGridViewRow row, FrameSnapshot snapshot)
    {
        row.DefaultCellStyle.BackColor = Color.White;
        row.DefaultCellStyle.ForeColor = Color.Black;
        DateTime nowUtc = DateTime.UtcNow;

        for (int index = 0; index < VisibleByteColumns; index++)
        {
            DataGridViewCell cell = row.Cells[5 + index];
            cell.Style.BackColor = Color.White;
            cell.Style.ForeColor = Color.Black;
            if (snapshot.ChangedByteUntilUtc[index] > nowUtc)
            {
                cell.Style.BackColor = ChangedByteBackColor;
                cell.Style.ForeColor = ChangedByteForeColor;
                cell.Style.SelectionBackColor = ChangedByteBackColor;
                cell.Style.SelectionForeColor = ChangedByteForeColor;
            }
            else
            {
                cell.Style.SelectionBackColor = Color.Gainsboro;
                cell.Style.SelectionForeColor = Color.Black;
            }
        }
    }

    private void AppendFrameLog(string busName, CanFrame frame)
    {
        string dataText = string.Join(' ', frame.Data.Select(static value => value.ToString("X2", CultureInfo.InvariantCulture)));
        if (string.IsNullOrWhiteSpace(dataText))
        {
            dataText = "-";
        }

        AppendLog($"{frame.ReceivedAtUtc.ToLocalTime():HH:mm:ss.fff}  {busName,-4}  {FormatId(frame.Id, frame.IsExtended),-8}  [{frame.Dlc}]  {dataText}");
    }

    private void AppendLog(string line)
    {
        string text = $"{line}{Environment.NewLine}";
        logBox.AppendText(text);
        sessionLogWriter?.Write(text);

        if (logBox.TextLength > LogTrimThreshold)
        {
            logBox.Text = logBox.Text[^LogTargetLength..];
        }

        logBox.SelectionStart = logBox.TextLength;
        logBox.ScrollToCaret();
    }

    private BusWorkspace GetOrCreateWorkspace(string busName)
    {
        if (!busWorkspaces.TryGetValue(busName, out BusWorkspace? workspace))
        {
            workspace = new BusWorkspace(busName);
            busWorkspaces[busName] = workspace;
        }

        return workspace;
    }

    private string GetSelectedBusName()
    {
        return (busCombo.SelectedItem as BusProfileOption)?.Name ?? busOptions[0].Name;
    }

    private static bool MatchesFilter(FrameSnapshot snapshot, string? filterText)
    {
        if (filterText is null)
        {
            return true;
        }

        return FormatId(snapshot.Id, snapshot.IsExtended).Contains(filterText, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFrameKey(uint identifier, bool isExtended)
    {
        return $"{identifier:X8}:{(isExtended ? 'E' : 'S')}";
    }

    private static bool[] ComputeChangedBytes(byte[] previousData, byte[] currentData)
    {
        bool[] changedBytes = new bool[VisibleByteColumns];
        for (int index = 0; index < VisibleByteColumns; index++)
        {
            byte? previous = index < previousData.Length ? previousData[index] : null;
            byte? current = index < currentData.Length ? currentData[index] : null;
            changedBytes[index] = previous.HasValue && current.HasValue
                ? previous.Value != current.Value
                : previous.HasValue != current.HasValue;
        }

        return changedBytes;
    }

    private static string FormatId(uint identifier, bool isExtended)
    {
        return isExtended
            ? identifier.ToString("X8", CultureInfo.InvariantCulture)
            : identifier.ToString("X3", CultureInfo.InvariantCulture);
    }

    private static string FormatByteCell(byte[] data, int index)
    {
        if (index >= data.Length)
        {
            return "--";
        }

        return data[index].ToString("X2", CultureInfo.InvariantCulture);
    }

    private static string? NormalizeHexFilter(string text)
    {
        string normalized = text.Trim().Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized.ToUpperInvariant();
    }

    private static Label CreateLabel(string text)
    {
        return new Label
        {
            AutoSize = true,
            Margin = new Padding(8, 8, 0, 0),
            Text = text
        };
    }

    private readonly record struct QueuedFrame(string BusName, CanFrame Frame);

    private sealed class BusWorkspace
    {
        public BusWorkspace(string name)
        {
            Name = name;
            Frames = new Dictionary<string, FrameSnapshot>(StringComparer.OrdinalIgnoreCase);
            VisibleRows = new Dictionary<string, DataGridViewRow>(StringComparer.OrdinalIgnoreCase);
        }

        public string Name { get; }
        public Dictionary<string, FrameSnapshot> Frames { get; }
        public Dictionary<string, DataGridViewRow> VisibleRows { get; }
    }

    private sealed class BitrateOption
    {
        public BitrateOption(string displayName, int baudRate)
        {
            DisplayName = displayName;
            BaudRate = baudRate;
        }

        public string DisplayName { get; }
        public int BaudRate { get; }

        public override string ToString()
        {
            return DisplayName;
        }

        public static List<BitrateOption> CreateDefaults()
        {
            return
            [
                new BitrateOption("10k", 10000),
                new BitrateOption("20k", 20000),
                new BitrateOption("33.3k", 33300),
                new BitrateOption("50k", 50000),
                new BitrateOption("62.5k", 62500),
                new BitrateOption("83.3k", 83300),
                new BitrateOption("95.2k", 95200),
                new BitrateOption("100k", 100000),
                new BitrateOption("125k", 125000),
                new BitrateOption("250k", 250000),
                new BitrateOption("500k", 500000),
                new BitrateOption("800k", 800000),
                new BitrateOption("1000k", 1000000)
            ];
        }
    }

    private sealed class FrameFormatOption
    {
        public FrameFormatOption(string displayName, bool useExtendedIdentifiers)
        {
            DisplayName = displayName;
            UseExtendedIdentifiers = useExtendedIdentifiers;
        }

        public string DisplayName { get; }
        public bool UseExtendedIdentifiers { get; }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    private sealed class BusProfileOption
    {
        public BusProfileOption(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public override string ToString()
        {
            return Name;
        }

        public static List<BusProfileOption> CreateDefaults()
        {
            return
            [
                new BusProfileOption("CAN1"),
                new BusProfileOption("CAN2"),
                new BusProfileOption("CAN3"),
                new BusProfileOption("CAN4")
            ];
        }
    }

    private enum SortMode
    {
        ById,
        ByActivity
    }

    private sealed class SortModeOption
    {
        public SortModeOption(string displayName, SortMode mode)
        {
            DisplayName = displayName;
            Mode = mode;
        }

        public string DisplayName { get; }
        public SortMode Mode { get; }

        public override string ToString()
        {
            return DisplayName;
        }

        public static List<SortModeOption> CreateDefaults()
        {
            return
            [
                new SortModeOption("ID", SortMode.ById),
                new SortModeOption("Activity", SortMode.ByActivity)
            ];
        }
    }

    private sealed class FrameSnapshot
    {
        public FrameSnapshot(string busName, uint id, bool isExtended)
        {
            BusName = busName;
            Id = id;
            IsExtended = isExtended;
            Data = Array.Empty<byte>();
            ChangedBytes = new bool[VisibleByteColumns];
            ChangedByteUntilUtc = new DateTime[VisibleByteColumns];
            LastSeenUtc = DateTime.MinValue;
        }

        public string BusName { get; }
        public uint Id { get; }
        public bool IsExtended { get; }
        public int Count { get; set; }
        public int Dlc { get; set; }
        public byte[] Data { get; set; }
        public bool[] ChangedBytes { get; set; }
        public DateTime[] ChangedByteUntilUtc { get; set; }
        public uint DeviceTimestamp { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public DateTime? PreviousSeenUtc { get; set; }
    }
}
