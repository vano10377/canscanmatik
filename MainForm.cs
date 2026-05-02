using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CanScanmatik;

internal sealed class MainForm : Form
{
    private const int LogTrimThreshold = 140_000;
    private const int LogTargetLength = 100_000;
    private const int VisibleByteColumns = 8;
    private const int GridRefreshBatchSize = 500;
    private const int ReadLoopTimeoutMs = 100;
    private const int ReadLoopBatchSize = 32;
    private const int ReadLoopIdleDelayMs = 1;
    private const int ChangedByteHoldMs = 250;
    private static readonly Color ChangedByteBackColor = Color.FromArgb(255, 255, 0);
    private static readonly Color ChangedByteForeColor = Color.FromArgb(180, 0, 0);

    private readonly ComboBox driverCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly ComboBox bitrateCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 95 };
    private readonly ComboBox frameFormatCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 125 };
    private readonly ComboBox busCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 126 };
    private readonly ComboBox sortCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly TextBox filterTextBox = new() { Width = 100, PlaceholderText = "208" };
    private readonly Button refreshButton = new() { Text = "Refresh", AutoSize = true };
    private readonly Button configButton = new() { Text = "Config", AutoSize = true };
    private readonly Button connectButton = new() { Text = "Connect", AutoSize = true };
    private readonly Button clearButton = new() { Text = "Clear Bus", AutoSize = true };
    private readonly Button exportButton = new() { Text = "Save CSV", AutoSize = true };
    private readonly Button saveIdsButton = new() { Text = "Save IDs", AutoSize = true };
    private readonly Button logToggleButton = new() { Text = "Start Log", AutoSize = true, Enabled = false };
    private readonly Label statusLabel = new() { AutoSize = true, Text = "Disconnected" };
    private readonly Label rxInfoLabel = new() { AutoSize = true, Text = "RX: 0" };
    private readonly Label adapterInfoLabel = new() { AutoSize = true };
    private readonly Label busHintLabel = new() { AutoSize = true };
    private readonly TextBox txIdTextBox = new() { Width = 110, Text = "208", PlaceholderText = "208" };
    private readonly NumericUpDown txDlcUpDown = new() { Minimum = 0, Maximum = VisibleByteColumns, Width = 55, Value = 8 };
    private readonly TextBox[] txByteTextBoxes = CreateTransmitByteTextBoxes();
    private readonly NumericUpDown txIntervalUpDown = new() { Minimum = 5, Maximum = 5000, Width = 75, Value = 100 };
    private readonly Button captureRowButton = new() { Text = "Capture Row", AutoSize = true };
    private readonly Button addTxButton = new() { Text = "Add TX", AutoSize = true };
    private readonly Button updateTxButton = new() { Text = "Update TX", AutoSize = true };
    private readonly Button removeTxButton = new() { Text = "Remove TX", AutoSize = true };
    private readonly Button clearTxListButton = new() { Text = "Clear TX", AutoSize = true };
    private readonly Button loadTxListButton = new() { Text = "Load TX", AutoSize = true };
    private readonly Button saveTxListButton = new() { Text = "Save TX", AutoSize = true };
    private readonly Button sendOnceButton = new() { Text = "Send Once", AutoSize = true };
    private readonly Button sendListButton = new() { Text = "Send List", AutoSize = true };
    private readonly Button startTransmitButton = new() { Text = "Start TX", AutoSize = true };
    private readonly Button stopTransmitButton = new() { Text = "Stop TX", AutoSize = true, Enabled = false };
    private readonly CheckBox sweepEnabledCheckBox = new() { AutoSize = true, Text = "Sweep byte" };
    private readonly ComboBox sweepByteCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 58 };
    private readonly TextBox sweepFromTextBox = new() { Width = 42, Text = "00", MaxLength = 2 };
    private readonly TextBox sweepToTextBox = new() { Width = 42, Text = "FF", MaxLength = 2 };
    private readonly TextBox sweepStepTextBox = new() { Width = 42, Text = "01", MaxLength = 2 };
    private readonly Label txHintLabel = new() { AutoSize = true };
    private readonly BufferedDataGridView frameGrid = new();
    private readonly BufferedDataGridView txQueueGrid = new();
    private readonly TextBox logBox = new();
    private readonly System.Windows.Forms.Timer uiTimer = new() { Interval = 40 };
    private readonly System.Windows.Forms.Timer transmitTimer = new() { Interval = 100 };
    private readonly ConcurrentQueue<QueuedFrame> pendingFrames = new();
    private readonly List<TransmitPlan> transmitQueue = [];
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
    private TransmitPlan? activeTransmitPlan;
    private List<TransmitPlan>? activeTransmitSequence;
    private int activeTransmitSequenceIndex;
    private PassThruDriverInfo? connectedDriver;
    private BitrateOption? connectedBitrate;
    private FrameFormatOption? connectedFrameFormat;
    private BusProfileOption? connectedBusProfile;
    private string? connectedFirmwareVersion;
    private string? connectedDllVersion;
    private string? connectedApiVersion;
    private long totalReceivedFrames;
    private DateTime lastReceivedAtUtc;
    private string? lastReceivedSummary;

    public MainForm()
    {
        InitializeUi();
        HookEvents();
        EnsureBusWorkspaces();
        LoadDrivers();
        RefreshGrid();
        RefreshTransmitQueueGrid();
        ResetReceiveIndicator();
        ApplyTransmitDlcState();
        UpdateTransmitHint();
        UpdateTransmitButtonState();
        UpdateLogButtonState();
        UpdateBusHint();
    }

    private void InitializeUi()
    {
        Text = "CanScanmatik";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1024, 680);

        Rectangle workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1460, 920);
        Size preferredSize = new(1460, 920);
        Size = new Size(
            Math.Min(preferredSize.Width, workingArea.Width),
            Math.Min(preferredSize.Height, workingArea.Height));

        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 240f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 140f));
        Controls.Add(root);

        GroupBox connectionGroup = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Text = "Scanmatik J2534"
        };
        connectionGroup.Margin = new Padding(0, 0, 0, 8);
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
        connectionPanel.Controls.Add(logToggleButton);

        statusLabel.Margin = new Padding(14, 8, 0, 0);
        connectionPanel.Controls.Add(statusLabel);
        rxInfoLabel.Margin = new Padding(14, 8, 0, 0);
        connectionPanel.Controls.Add(rxInfoLabel);

        adapterInfoLabel.Margin = new Padding(0, 10, 0, 0);
        adapterInfoLabel.MaximumSize = new Size(1320, 0);
        connectionPanel.SetFlowBreak(rxInfoLabel, true);
        connectionPanel.Controls.Add(adapterInfoLabel);

        busHintLabel.Margin = new Padding(0, 4, 0, 4);
        busHintLabel.MaximumSize = new Size(1320, 0);
        connectionPanel.SetFlowBreak(adapterInfoLabel, true);
        connectionPanel.Controls.Add(busHintLabel);

        frameGrid.Dock = DockStyle.Fill;
        frameGrid.Margin = new Padding(0, 0, 0, 8);
        frameGrid.AllowUserToAddRows = false;
        frameGrid.AllowUserToDeleteRows = false;
        frameGrid.AllowUserToResizeRows = false;
        frameGrid.ReadOnly = true;
        frameGrid.MultiSelect = false;
        frameGrid.RowHeadersVisible = false;
        frameGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        frameGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        frameGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        frameGrid.BackgroundColor = Color.White;
        frameGrid.BorderStyle = BorderStyle.Fixed3D;
        frameGrid.ScrollBars = ScrollBars.Both;
        frameGrid.RowTemplate.Height = 22;
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
        ConfigureFrameGridColumns();
        root.Controls.Add(frameGrid, 0, 1);

        GroupBox transmitGroup = new()
        {
            Dock = DockStyle.Fill,
            Text = "Transmit"
        };
        root.Controls.Add(transmitGroup, 0, 2);

        TableLayoutPanel transmitRoot = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(6)
        };
        transmitRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        transmitRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        transmitRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        transmitGroup.Controls.Add(transmitRoot);

        FlowLayoutPanel transmitPanel = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(4),
            WrapContents = true
        };
        transmitRoot.Controls.Add(transmitPanel, 0, 0);

        transmitPanel.Controls.Add(CreateLabel("ID"));
        transmitPanel.Controls.Add(txIdTextBox);
        transmitPanel.Controls.Add(CreateLabel("DLC"));
        transmitPanel.Controls.Add(txDlcUpDown);

        for (int index = 0; index < txByteTextBoxes.Length; index++)
        {
            transmitPanel.Controls.Add(CreateLabel($"D{index}"));
            transmitPanel.Controls.Add(txByteTextBoxes[index]);
        }

        transmitPanel.Controls.Add(CreateLabel("Interval ms"));
        transmitPanel.Controls.Add(txIntervalUpDown);
        captureRowButton.Margin = new Padding(16, 3, 0, 3);
        transmitPanel.Controls.Add(captureRowButton);
        transmitPanel.Controls.Add(addTxButton);
        transmitPanel.Controls.Add(updateTxButton);
        transmitPanel.Controls.Add(removeTxButton);
        transmitPanel.Controls.Add(clearTxListButton);
        transmitPanel.Controls.Add(loadTxListButton);
        transmitPanel.Controls.Add(saveTxListButton);
        transmitPanel.Controls.Add(sendOnceButton);
        transmitPanel.Controls.Add(sendListButton);
        transmitPanel.Controls.Add(startTransmitButton);
        transmitPanel.Controls.Add(stopTransmitButton);

        sweepEnabledCheckBox.Margin = new Padding(20, 7, 0, 0);
        transmitPanel.Controls.Add(sweepEnabledCheckBox);
        sweepByteCombo.Items.AddRange(
        [
            "D0",
            "D1",
            "D2",
            "D3",
            "D4",
            "D5",
            "D6",
            "D7"
        ]);
        sweepByteCombo.SelectedIndex = 0;
        transmitPanel.Controls.Add(sweepByteCombo);
        transmitPanel.Controls.Add(CreateLabel("From"));
        transmitPanel.Controls.Add(sweepFromTextBox);
        transmitPanel.Controls.Add(CreateLabel("To"));
        transmitPanel.Controls.Add(sweepToTextBox);
        transmitPanel.Controls.Add(CreateLabel("Step"));
        transmitPanel.Controls.Add(sweepStepTextBox);

        txHintLabel.Margin = new Padding(0, 10, 0, 0);
        txHintLabel.MaximumSize = new Size(1320, 0);
        transmitPanel.SetFlowBreak(stopTransmitButton, true);
        transmitPanel.Controls.Add(txHintLabel);

        txQueueGrid.Dock = DockStyle.Fill;
        txQueueGrid.AllowUserToAddRows = false;
        txQueueGrid.AllowUserToDeleteRows = false;
        txQueueGrid.AllowUserToResizeRows = false;
        txQueueGrid.ReadOnly = true;
        txQueueGrid.MultiSelect = false;
        txQueueGrid.RowHeadersVisible = false;
        txQueueGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        txQueueGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        txQueueGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        txQueueGrid.BackgroundColor = Color.White;
        txQueueGrid.BorderStyle = BorderStyle.Fixed3D;
        txQueueGrid.RowTemplate.Height = 22;
        txQueueGrid.Columns.Add("Seq", "#");
        txQueueGrid.Columns.Add("Id", "ID");
        txQueueGrid.Columns.Add("Fmt", "Fmt");
        txQueueGrid.Columns.Add("Dlc", "DLC");
        txQueueGrid.Columns.Add("Data", "Data");
        txQueueGrid.Columns.Add("Interval", "Int ms");
        txQueueGrid.Columns.Add("Mode", "Mode");
        ConfigureTransmitQueueGridColumns();
        transmitRoot.Controls.Add(txQueueGrid, 0, 1);

        GroupBox logGroup = new()
        {
            Dock = DockStyle.Fill,
            Text = "CAN Log"
        };
        logGroup.Margin = Padding.Empty;
        root.Controls.Add(logGroup, 0, 3);

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
        logToggleButton.Click += (_, _) => ToggleFrameLogging();
        filterTextBox.TextChanged += (_, _) => RefreshGrid();
        sortCombo.SelectedIndexChanged += (_, _) => RefreshGrid();
        busCombo.SelectedIndexChanged += (_, _) =>
        {
            RefreshGrid();
            UpdateBusHint();
            UpdateTransmitHint();
        };
        frameFormatCombo.SelectedIndexChanged += (_, _) => UpdateTransmitHint();
        driverCombo.SelectedIndexChanged += (_, _) =>
        {
            UpdateAdapterDescription();
            UpdateBusHint();
        };
        captureRowButton.Click += (_, _) => CaptureSelectedFrameToTransmitPanel(showMessageWhenMissingSelection: true);
        addTxButton.Click += (_, _) => AddTransmitPlanToQueue();
        updateTxButton.Click += (_, _) => UpdateSelectedTransmitPlanInQueue();
        removeTxButton.Click += (_, _) => RemoveSelectedTransmitPlanFromQueue();
        clearTxListButton.Click += (_, _) => ClearTransmitQueue();
        loadTxListButton.Click += (_, _) => LoadTransmitQueueFromFile();
        saveTxListButton.Click += (_, _) => SaveTransmitQueueToFile();
        sendOnceButton.Click += (_, _) => SendTransmitFrameOnce();
        sendListButton.Click += (_, _) => SendTransmitQueueOnce();
        startTransmitButton.Click += (_, _) => StartTransmitLoop();
        stopTransmitButton.Click += (_, _) => StopTransmitLoop();
        frameGrid.CellDoubleClick += (_, _) => CaptureSelectedFrameToTransmitPanel(showMessageWhenMissingSelection: false);
        txQueueGrid.CellDoubleClick += (_, _) => LoadSelectedTransmitPlanIntoEditor();
        txQueueGrid.SelectionChanged += (_, _) => UpdateTransmitButtonState();
        uiTimer.Tick += (_, _) => DrainPendingFrames();
        transmitTimer.Tick += (_, _) => HandleTransmitTimerTick();
        FormClosing += async (_, _) => await DisconnectAsync();

        txDlcUpDown.ValueChanged += (_, _) => ApplyTransmitDlcState();
        sweepEnabledCheckBox.CheckedChanged += (_, _) => UpdateTransmitHint();
        foreach (TextBox box in txByteTextBoxes)
        {
            box.Leave += (_, _) => NormalizeHexByteTextBox(box);
        }

        sweepFromTextBox.Leave += (_, _) => NormalizeHexByteTextBox(sweepFromTextBox);
        sweepToTextBox.Leave += (_, _) => NormalizeHexByteTextBox(sweepToTextBox);
        sweepStepTextBox.Leave += (_, _) => NormalizeHexByteTextBox(sweepStepTextBox, fallbackValue: "01");
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
            busHintLabel.Text = "Physical CAN routing is unavailable until a Scanmatik J2534 driver is selected.";
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

    private void UpdateBusHint()
    {
        BusProfileOption selectedBus = (busCombo.SelectedItem as BusProfileOption) ?? busOptions[0];
        PassThruDriverInfo? driver = driverCombo.SelectedItem as PassThruDriverInfo;

        StringBuilder builder = new();
        builder.Append("Physical bus route: ").Append(FormatBusProfileLabel(selectedBus)).Append(". ");
        builder.Append(selectedBus.UsesDefaultCanChannel
            ? "This route uses the standard J2534 CAN channel on pins 6-14."
            : "This route uses J2534-2 CAN_PS with J1962_PINS switching.");

        if (selectedBus.Name == "CAN3")
        {
            builder.Append(" CAN3 is mapped as 12H-13L in this build.");
        }

        if (driver is not null &&
            driver.DisplayName.Contains("SM2", StringComparison.OrdinalIgnoreCase) &&
            !driver.DisplayName.Contains("SM3", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append(" The SM2 driver name does not show whether the hardware is SM2 or SM2-PRO.");
            builder.Append(" Officially: plain SM2 supports CAN1 6-14 and CAN2 3-11; CAN3 12-13 needs SM2-PRO/SM3; CAN4 1-9 and CAN5 2-10 are SM3-class routes.");
        }
        else
        {
            builder.Append(" Official Scanmatik CAN routes include 6-14, 3-11, 12-13, 1-9, and 2-10 depending on hardware.");
        }

        busHintLabel.Text = builder.ToString();
    }

    private void ConfigureFrameGridColumns()
    {
        int[] widths =
        [
            110,
            60,
            78,
            46,
            46,
            52,
            52,
            52,
            52,
            52,
            52,
            52,
            52,
            58,
            82,
            92
        ];

        for (int index = 0; index < widths.Length && index < frameGrid.Columns.Count; index++)
        {
            frameGrid.Columns[index].Width = widths[index];
            frameGrid.Columns[index].SortMode = DataGridViewColumnSortMode.NotSortable;
        }
    }

    private void ConfigureTransmitQueueGridColumns()
    {
        int[] widths =
        [
            36,
            86,
            46,
            46,
            420,
            64,
            170
        ];

        for (int index = 0; index < widths.Length && index < txQueueGrid.Columns.Count; index++)
        {
            txQueueGrid.Columns[index].Width = widths[index];
            txQueueGrid.Columns[index].SortMode = DataGridViewColumnSortMode.NotSortable;
        }
    }

    private void ApplyTransmitDlcState()
    {
        bool isLoopRunning = activeTransmitPlan is not null || activeTransmitSequence is not null;
        int dlc = Decimal.ToInt32(txDlcUpDown.Value);
        for (int index = 0; index < txByteTextBoxes.Length; index++)
        {
            TextBox box = txByteTextBoxes[index];
            bool withinDlc = index < dlc;
            box.Enabled = !isLoopRunning && withinDlc;
            box.BackColor = withinDlc ? Color.White : Color.Gainsboro;
            if (string.IsNullOrWhiteSpace(box.Text))
            {
                box.Text = "00";
            }
        }

        sweepByteCombo.Enabled = !isLoopRunning && sweepEnabledCheckBox.Checked;
        sweepFromTextBox.Enabled = !isLoopRunning && sweepEnabledCheckBox.Checked;
        sweepToTextBox.Enabled = !isLoopRunning && sweepEnabledCheckBox.Checked;
        sweepStepTextBox.Enabled = !isLoopRunning && sweepEnabledCheckBox.Checked;
    }

    private void UpdateTransmitHint()
    {
        bool useExtendedIdentifiers = (frameFormatCombo.SelectedItem as FrameFormatOption)?.UseExtendedIdentifiers ?? false;
        string formatText = useExtendedIdentifiers ? "29-bit" : "11-bit";
        string busText = FormatBusProfileLabel((busCombo.SelectedItem as BusProfileOption) ?? busOptions[0]);
        string stateText = api is null
            ? "Connect first to send frames."
            : $"TX uses the current live CAN connection on {busText}.";
        txHintLabel.Text = $"{stateText} Current TX format: {formatText}. Add TX stores the editor frame into the list. If the list is not empty, Start TX cycles through the whole list. Sweep wraps From/To on the selected byte.";
        ApplyTransmitDlcState();
    }

    private void UpdateTransmitButtonState()
    {
        bool isConnected = api is not null;
        bool isLoopRunning = activeTransmitPlan is not null || activeTransmitSequence is not null;
        bool hasQueue = transmitQueue.Count > 0;
        bool hasQueueSelection = txQueueGrid.SelectedRows.Count > 0;

        txIdTextBox.Enabled = !isLoopRunning;
        txDlcUpDown.Enabled = !isLoopRunning;
        txIntervalUpDown.Enabled = !isLoopRunning;
        sweepEnabledCheckBox.Enabled = !isLoopRunning;
        captureRowButton.Enabled = !isLoopRunning && frameGrid.Rows.Count > 0;
        addTxButton.Enabled = !isLoopRunning;
        updateTxButton.Enabled = !isLoopRunning && hasQueueSelection;
        removeTxButton.Enabled = !isLoopRunning && hasQueueSelection;
        clearTxListButton.Enabled = !isLoopRunning && hasQueue;
        loadTxListButton.Enabled = !isLoopRunning;
        saveTxListButton.Enabled = !isLoopRunning && hasQueue;
        sendOnceButton.Enabled = isConnected && !isLoopRunning;
        sendListButton.Enabled = isConnected && !isLoopRunning && hasQueue;
        startTransmitButton.Enabled = isConnected && !isLoopRunning;
        stopTransmitButton.Enabled = isLoopRunning;
        ApplyTransmitDlcState();
    }

    private void UpdateLogButtonState()
    {
        bool isConnected = api is not null;
        logToggleButton.Enabled = isConnected || sessionLogWriter is not null;
        logToggleButton.Text = sessionLogWriter is null ? "Start Log" : "Stop Log";
    }

    private void ResetReceiveIndicator()
    {
        totalReceivedFrames = 0;
        lastReceivedAtUtc = DateTime.MinValue;
        lastReceivedSummary = null;
        rxInfoLabel.Text = "RX: 0";
    }

    private void UpdateReceiveIndicator(int receivedFrames, string busName, CanFrame lastFrame)
    {
        totalReceivedFrames += receivedFrames;
        lastReceivedAtUtc = lastFrame.ReceivedAtUtc;
        lastReceivedSummary = $"{busName} {FormatId(lastFrame.Id, lastFrame.IsExtended)} [{lastFrame.Dlc}]";
        rxInfoLabel.Text = $"RX: {totalReceivedFrames.ToString(CultureInfo.InvariantCulture)} | Last: {lastReceivedAtUtc.ToLocalTime():HH:mm:ss.fff} {lastReceivedSummary}";
    }

    private void ToggleFrameLogging()
    {
        if (sessionLogWriter is not null)
        {
            AppendLog("LOG   stopped");
            StopSessionLog();
            UpdateLogButtonState();
            return;
        }

        if (api is null)
        {
            MessageBox.Show(this, "Connect to the CAN bus before starting the log.", "CanScanmatik", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        StartSessionLog();
        UpdateLogButtonState();
    }

    private void ClearConnectionDetails()
    {
        connectedDriver = null;
        connectedBitrate = null;
        connectedFrameFormat = null;
        connectedBusProfile = null;
        connectedFirmwareVersion = null;
        connectedDllVersion = null;
        connectedApiVersion = null;
    }

    private static string FormatBusProfileLabel(BusProfileOption busProfile)
    {
        return busProfile.DisplayName;
    }

    private FrameSnapshot? GetSelectedFrameSnapshot()
    {
        if (frameGrid.SelectedRows.Count == 0)
        {
            return null;
        }

        string? key = frameGrid.SelectedRows[0].Tag as string;
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        BusWorkspace workspace = GetOrCreateWorkspace(GetSelectedBusName());
        return workspace.Frames.TryGetValue(key, out FrameSnapshot? snapshot) ? snapshot : null;
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
            newApi.ConnectCan(bitrate.BaudRate, frameFormat.UseExtendedIdentifiers, busProfile.GetConnectPins());

            api = newApi;
            activeConnectionBusName = busProfile.Name;
            connectedDriver = driver;
            connectedBitrate = bitrate;
            connectedFrameFormat = frameFormat;
            connectedBusProfile = busProfile;
            connectedFirmwareVersion = firmwareVersion;
            connectedDllVersion = dllVersion;
            connectedApiVersion = apiVersion;
            ResetReceiveIndicator();
            readLoopCancellation = new CancellationTokenSource();
            readLoopTask = Task.Run(() => ReadLoopAsync(newApi, activeConnectionBusName, readLoopCancellation.Token));
            uiTimer.Start();
            UpdateTransmitHint();
            UpdateTransmitButtonState();
            UpdateLogButtonState();

            connectButton.Text = "Disconnect";
            statusLabel.Text = $"Connected: {driver.DisplayName} | {FormatBusProfileLabel(busProfile)} | {bitrate.DisplayName} | {(frameFormat.UseExtendedIdentifiers ? "29-bit" : "11-bit")}";
            adapterInfoLabel.Text = $"Firmware: {firmwareVersion} | DLL: {dllVersion} | API: {apiVersion} | {driver.FunctionLibrary}";
            AppendLog($"OPEN  {driver.DisplayName}");
            AppendLog($"BUS   {FormatBusProfileLabel(busProfile)}");
            AppendLog($"ROUTE {(busProfile.UsesDefaultCanChannel ? "base CAN" : "CAN_PS")} -> {busProfile.PinDisplay}");
            AppendLog($"LINK  {bitrate.DisplayName}, {(frameFormat.UseExtendedIdentifiers ? "extended 29-bit" : "standard 11-bit")}");
        }
        catch (Exception ex)
        {
            newApi?.Dispose();
            api = null;
            activeConnectionBusName = null;
            ClearConnectionDetails();
            StopSessionLog();
            statusLabel.Text = "Connection failed.";
            MessageBox.Show(this, BuildConnectionErrorMessage(ex, busProfile), "Scanmatik / J2534 Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        StopTransmitLoop();

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
        ClearConnectionDetails();
        StopSessionLog();
        RestoreControlsAfterDisconnect();
    }

    private async Task ReadLoopAsync(PassThruApi currentApi, string busName, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IReadOnlyList<CanFrame> frames = currentApi.ReadCanFrames(timeoutMs: ReadLoopTimeoutMs, batchSize: ReadLoopBatchSize);
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
                    await Task.Delay(ReadLoopIdleDelayMs, cancellationToken).ConfigureAwait(false);
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
        bool captureFrameTrace = sessionLogWriter is not null;
        StringBuilder? logBatchBuilder = null;
        int processed = 0;
        CanFrame? lastProcessedFrame = null;
        string? lastProcessedBusName = null;

        while (processed < GridRefreshBatchSize && pendingFrames.TryDequeue(out QueuedFrame queuedFrame))
        {
            processed++;
            UpdateSnapshot(queuedFrame.BusName, queuedFrame.Frame);
            lastProcessedFrame = queuedFrame.Frame;
            lastProcessedBusName = queuedFrame.BusName;
            if (captureFrameTrace)
            {
                logBatchBuilder ??= new StringBuilder(GridRefreshBatchSize * 32);
                AppendFrameLog(logBatchBuilder, queuedFrame.BusName, queuedFrame.Frame);
            }
            if (string.Equals(queuedFrame.BusName, activeBusName, StringComparison.OrdinalIgnoreCase))
            {
                changedKeys.Add(GetFrameKey(queuedFrame.Frame.Id, queuedFrame.Frame.IsExtended));
            }
        }

        if (logBatchBuilder is not null && logBatchBuilder.Length > 0)
        {
            AppendSessionLogText(logBatchBuilder.ToString());
        }

        if (processed > 0 && lastProcessedFrame.HasValue && !string.IsNullOrWhiteSpace(lastProcessedBusName))
        {
            UpdateReceiveIndicator(processed, lastProcessedBusName!, lastProcessedFrame.Value);
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
        UpdateTransmitButtonState();
    }

    private void ClearActiveBus()
    {
        BusWorkspace workspace = GetOrCreateWorkspace(GetSelectedBusName());
        workspace.Frames.Clear();
        workspace.VisibleRows.Clear();
        frameGrid.Rows.Clear();
        logBox.Clear();
        AppendLog($"CLEAR {workspace.Name}");
        UpdateTransmitButtonState();
    }

    private void AddTransmitPlanToQueue()
    {
        if (!TryBuildTransmitPlan(out TransmitPlan? plan, requireConnection: false))
        {
            return;
        }

        transmitQueue.Add(plan!);
        RefreshTransmitQueueGrid();
        txQueueGrid.ClearSelection();
        txQueueGrid.Rows[^1].Selected = true;
        AppendLog($"TXADD {FormatId(plan!.Id, plan.UseExtendedIdentifiers)}");
    }

    private void UpdateSelectedTransmitPlanInQueue()
    {
        if (!TryBuildTransmitPlan(out TransmitPlan? plan, requireConnection: false))
        {
            return;
        }

        int index = GetSelectedTransmitQueueIndex();
        if (index < 0)
        {
            MessageBox.Show(this, "Select a TX row from the list first.", "CanScanmatik", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        transmitQueue[index] = plan!;
        RefreshTransmitQueueGrid();
        txQueueGrid.ClearSelection();
        txQueueGrid.Rows[index].Selected = true;
        AppendLog($"TXUPD {FormatId(plan!.Id, plan.UseExtendedIdentifiers)}");
    }

    private void RemoveSelectedTransmitPlanFromQueue()
    {
        int index = GetSelectedTransmitQueueIndex();
        if (index < 0)
        {
            MessageBox.Show(this, "Select a TX row from the list first.", "CanScanmatik", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        TransmitPlan removed = transmitQueue[index];
        transmitQueue.RemoveAt(index);
        RefreshTransmitQueueGrid();
        AppendLog($"TXDEL {FormatId(removed.Id, removed.UseExtendedIdentifiers)}");
    }

    private void ClearTransmitQueue()
    {
        if (transmitQueue.Count == 0)
        {
            return;
        }

        transmitQueue.Clear();
        RefreshTransmitQueueGrid();
        AppendLog("TXCLR list");
    }

    private void SaveTransmitQueueToFile()
    {
        if (transmitQueue.Count == 0)
        {
            MessageBox.Show(this, "The TX list is empty.", "CanScanmatik", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using SaveFileDialog dialog = new()
        {
            Filter = "TX list (*.json)|*.json|All files (*.*)|*.*",
            FileName = $"tx_list_{DateTime.Now:yyyyMMdd_HHmmss}.json",
            Title = "Save TX list"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        List<TransmitPlanFileModel> fileModels = transmitQueue
            .Select(static item => TransmitPlanFileModel.FromPlan(item))
            .ToList();

        JsonSerializerOptions options = new()
        {
            WriteIndented = true
        };

        string json = JsonSerializer.Serialize(fileModels, options);
        File.WriteAllText(dialog.FileName, json, new UTF8Encoding(false));
        AppendLog($"TXSAVE {dialog.FileName}");
    }

    private void LoadTransmitQueueFromFile()
    {
        using OpenFileDialog dialog = new()
        {
            Filter = "TX list (*.json)|*.json|All files (*.*)|*.*",
            Title = "Load TX list"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(dialog.FileName, Encoding.UTF8);
            List<TransmitPlanFileModel>? fileModels = JsonSerializer.Deserialize<List<TransmitPlanFileModel>>(json);
            if (fileModels is null || fileModels.Count == 0)
            {
                MessageBox.Show(this, "The selected file does not contain any TX frames.", "CanScanmatik", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            transmitQueue.Clear();
            foreach (TransmitPlanFileModel fileModel in fileModels)
            {
                transmitQueue.Add(fileModel.ToPlan());
            }

            RefreshTransmitQueueGrid();
            txQueueGrid.ClearSelection();
            txQueueGrid.Rows[0].Selected = true;
            LoadTransmitPlanIntoEditor(transmitQueue[0]);
            AppendLog($"TXLOAD {dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to load TX list", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadSelectedTransmitPlanIntoEditor()
    {
        if (activeTransmitPlan is not null || activeTransmitSequence is not null)
        {
            return;
        }

        int index = GetSelectedTransmitQueueIndex();
        if (index < 0)
        {
            return;
        }

        LoadTransmitPlanIntoEditor(transmitQueue[index]);
    }

    private void LoadTransmitPlanIntoEditor(TransmitPlan plan)
    {
        if (frameFormatCombo.Enabled)
        {
            frameFormatCombo.SelectedIndex = plan.UseExtendedIdentifiers ? 1 : 0;
        }

        txIdTextBox.Text = FormatId(plan.Id, plan.UseExtendedIdentifiers);
        txDlcUpDown.Value = plan.Dlc;
        for (int index = 0; index < VisibleByteColumns; index++)
        {
            txByteTextBoxes[index].Text = index < plan.Data.Length
                ? plan.Data[index].ToString("X2", CultureInfo.InvariantCulture)
                : "00";
        }

        txIntervalUpDown.Value = Math.Clamp(plan.IntervalMs, Decimal.ToInt32(txIntervalUpDown.Minimum), Decimal.ToInt32(txIntervalUpDown.Maximum));
        sweepEnabledCheckBox.Checked = plan.SweepEnabled;
        sweepByteCombo.SelectedIndex = plan.SweepByteIndex;
        sweepFromTextBox.Text = plan.SweepFrom.ToString("X2", CultureInfo.InvariantCulture);
        sweepToTextBox.Text = plan.SweepTo.ToString("X2", CultureInfo.InvariantCulture);
        sweepStepTextBox.Text = plan.SweepStep.ToString("X2", CultureInfo.InvariantCulture);
        ApplyTransmitDlcState();
        UpdateTransmitHint();
    }

    private void RefreshTransmitQueueGrid()
    {
        txQueueGrid.SuspendLayout();
        txQueueGrid.Rows.Clear();

        for (int index = 0; index < transmitQueue.Count; index++)
        {
            TransmitPlan plan = transmitQueue[index];
            txQueueGrid.Rows.Add(
                (index + 1).ToString(CultureInfo.InvariantCulture),
                FormatId(plan.Id, plan.UseExtendedIdentifiers),
                plan.UseExtendedIdentifiers ? "29" : "11",
                plan.Dlc.ToString(CultureInfo.InvariantCulture),
                FormatTransmitData(plan),
                plan.IntervalMs.ToString(CultureInfo.InvariantCulture),
                BuildTransmitModeSummary(plan));
        }

        txQueueGrid.ResumeLayout();
        UpdateTransmitButtonState();
    }

    private void SendTransmitQueueOnce()
    {
        if (api is null)
        {
            MessageBox.Show(this, "Connect to the CAN bus before transmitting.", "CanScanmatik", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (transmitQueue.Count == 0)
        {
            MessageBox.Show(this, "The TX list is empty.", "CanScanmatik", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            foreach (TransmitPlan plan in transmitQueue.Select(static item => item.Clone()))
            {
                plan.ResetRuntimeState();
                SendTransmitFrame(plan, useSweepValue: plan.SweepEnabled);
            }

            AppendLog($"TXALL {transmitQueue.Count.ToString(CultureInfo.InvariantCulture)} frames");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "CAN Transmit Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private int GetSelectedTransmitQueueIndex()
    {
        return txQueueGrid.SelectedRows.Count == 0 ? -1 : txQueueGrid.SelectedRows[0].Index;
    }

    private void SendTransmitFrameOnce()
    {
        if (!TryBuildTransmitPlan(out TransmitPlan? plan))
        {
            return;
        }

        try
        {
            SendTransmitFrame(plan!, useSweepValue: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "CAN Transmit Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StartTransmitLoop()
    {
        if (activeTransmitPlan is not null || activeTransmitSequence is not null)
        {
            return;
        }

        if (transmitQueue.Count > 0)
        {
            activeTransmitSequence = transmitQueue.Select(static item => item.Clone()).ToList();
            foreach (TransmitPlan plan in activeTransmitSequence)
            {
                plan.ResetRuntimeState();
            }

            activeTransmitSequenceIndex = 0;
            transmitTimer.Interval = activeTransmitSequence[0].IntervalMs;
            transmitTimer.Start();
            UpdateTransmitButtonState();

            try
            {
                TransmitPlan plan = activeTransmitSequence[activeTransmitSequenceIndex];
                SendTransmitFrame(plan, useSweepValue: plan.SweepEnabled);
                AdvanceActiveTransmitSequence();
                AppendLog($"TXRUN list {activeTransmitSequence.Count.ToString(CultureInfo.InvariantCulture)} frames");
            }
            catch (Exception ex)
            {
                StopTransmitLoop();
                MessageBox.Show(this, ex.Message, "CAN Transmit Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return;
        }

        if (!TryBuildTransmitPlan(out TransmitPlan? singlePlan))
        {
            return;
        }

        TransmitPlan activeSinglePlan = singlePlan!;
        activeTransmitPlan = activeSinglePlan;
        activeSinglePlan.ResetRuntimeState();
        transmitTimer.Interval = activeSinglePlan.IntervalMs;
        transmitTimer.Start();
        UpdateTransmitButtonState();

        try
        {
            SendTransmitFrame(activeSinglePlan, useSweepValue: activeSinglePlan.SweepEnabled);
            AppendLog($"TXRUN {GetSelectedBusName(),-4}  every {activeSinglePlan.IntervalMs.ToString(CultureInfo.InvariantCulture)} ms");
        }
        catch (Exception ex)
        {
            StopTransmitLoop();
            MessageBox.Show(this, ex.Message, "CAN Transmit Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopTransmitLoop()
    {
        bool hadActiveLoop = activeTransmitPlan is not null || activeTransmitSequence is not null;
        transmitTimer.Stop();
        activeTransmitPlan = null;
        activeTransmitSequence = null;
        activeTransmitSequenceIndex = 0;
        UpdateTransmitButtonState();

        if (hadActiveLoop)
        {
            AppendLog($"TXSTOP {GetSelectedBusName(),-4}");
        }
    }

    private void HandleTransmitTimerTick()
    {
        if (activeTransmitSequence is null && activeTransmitPlan is null)
        {
            transmitTimer.Stop();
            UpdateTransmitButtonState();
            return;
        }

        try
        {
            if (activeTransmitSequence is not null)
            {
                TransmitPlan plan = activeTransmitSequence[activeTransmitSequenceIndex];
                SendTransmitFrame(plan, useSweepValue: plan.SweepEnabled);
                AdvanceActiveTransmitSequence();
            }
            else if (activeTransmitPlan is not null)
            {
                SendTransmitFrame(activeTransmitPlan, useSweepValue: activeTransmitPlan.SweepEnabled);
            }
        }
        catch (Exception ex)
        {
            StopTransmitLoop();
            MessageBox.Show(this, ex.Message, "CAN Transmit Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SendTransmitFrame(TransmitPlan plan, bool useSweepValue)
    {
        PassThruApi currentApi = api ?? throw new InvalidOperationException("Connect to the CAN bus before transmitting.");

        byte[] payload = plan.Data.ToArray();
        string? sweepSuffix = null;
        if (useSweepValue && plan.SweepEnabled)
        {
            payload[plan.SweepByteIndex] = (byte)plan.CurrentSweepValue;
            txByteTextBoxes[plan.SweepByteIndex].Text = payload[plan.SweepByteIndex].ToString("X2", CultureInfo.InvariantCulture);
            sweepSuffix = $"  sweep D{plan.SweepByteIndex}={payload[plan.SweepByteIndex].ToString("X2", CultureInfo.InvariantCulture)}";
            plan.CurrentSweepValue = GetNextSweepValue(plan, plan.CurrentSweepValue);
        }

        currentApi.WriteCanFrame(plan.Id, plan.UseExtendedIdentifiers, payload, plan.Dlc, timeoutMs: 50);
        AppendTransmitLog(plan, payload, sweepSuffix);
    }

    private void AdvanceActiveTransmitSequence()
    {
        if (activeTransmitSequence is null || activeTransmitSequence.Count == 0)
        {
            return;
        }

        activeTransmitSequenceIndex++;
        if (activeTransmitSequenceIndex >= activeTransmitSequence.Count)
        {
            activeTransmitSequenceIndex = 0;
        }

        transmitTimer.Interval = activeTransmitSequence[activeTransmitSequenceIndex].IntervalMs;
    }

    private static int GetNextSweepValue(TransmitPlan plan, int currentValue)
    {
        if (!plan.SweepEnabled)
        {
            return currentValue;
        }

        if (plan.SweepFrom <= plan.SweepTo)
        {
            int nextValue = currentValue + plan.SweepStep;
            return nextValue > plan.SweepTo ? plan.SweepFrom : nextValue;
        }

        int descendingValue = currentValue - plan.SweepStep;
        return descendingValue < plan.SweepTo ? plan.SweepFrom : descendingValue;
    }

    private void AppendTransmitLog(TransmitPlan plan, byte[] payload, string? sweepSuffix)
    {
        string dataText = plan.Dlc == 0
            ? "-"
            : string.Join(' ', payload.Take(plan.Dlc).Select(static value => value.ToString("X2", CultureInfo.InvariantCulture)));

        AppendLog($"TX    {GetSelectedBusName(),-4}  {FormatId(plan.Id, plan.UseExtendedIdentifiers),-8}  [{plan.Dlc}]  {dataText}{sweepSuffix}");
    }

    private bool TryBuildTransmitPlan(out TransmitPlan? plan, bool requireConnection = true)
    {
        plan = null;
        if (requireConnection && api is null)
        {
            MessageBox.Show(this, "Connect to the CAN bus before transmitting.", "CanScanmatik", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        FrameFormatOption? frameFormat = frameFormatCombo.SelectedItem as FrameFormatOption;
        bool useExtendedIdentifiers = frameFormat?.UseExtendedIdentifiers ?? false;
        if (!TryParseIdentifier(txIdTextBox.Text, useExtendedIdentifiers, out uint identifier))
        {
            string formatExample = useExtendedIdentifiers ? "18DAF110" : "208";
            MessageBox.Show(this, $"Invalid CAN ID. Expected hex like {formatExample}.", "CanScanmatik", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            txIdTextBox.Focus();
            txIdTextBox.SelectAll();
            return false;
        }

        int dlc = Decimal.ToInt32(txDlcUpDown.Value);
        byte[] data = new byte[VisibleByteColumns];
        for (int index = 0; index < VisibleByteColumns; index++)
        {
            if (!TryParseHexByte(txByteTextBoxes[index].Text, out data[index]))
            {
                MessageBox.Show(this, $"Invalid value in D{index}. Use hex bytes from 00 to FF.", "CanScanmatik", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txByteTextBoxes[index].Focus();
                txByteTextBoxes[index].SelectAll();
                return false;
            }
        }

        bool sweepEnabled = sweepEnabledCheckBox.Checked;
        int sweepByteIndex = sweepByteCombo.SelectedIndex >= 0 ? sweepByteCombo.SelectedIndex : 0;
        byte sweepFrom = 0;
        byte sweepTo = 0;
        byte sweepStep = 1;

        if (sweepEnabled)
        {
            if (sweepByteIndex >= dlc)
            {
                MessageBox.Show(this, "Sweep byte must be inside the selected DLC.", "CanScanmatik", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                sweepByteCombo.Focus();
                return false;
            }

            if (!TryParseHexByte(sweepFromTextBox.Text, out sweepFrom) ||
                !TryParseHexByte(sweepToTextBox.Text, out sweepTo) ||
                !TryParseHexByte(sweepStepTextBox.Text, out sweepStep) ||
                sweepStep == 0)
            {
                MessageBox.Show(this, "Sweep expects valid hex values in From / To / Step. Step cannot be 00.", "CanScanmatik", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                sweepFromTextBox.Focus();
                sweepFromTextBox.SelectAll();
                return false;
            }
        }

        int intervalMs = Decimal.ToInt32(txIntervalUpDown.Value);
        plan = new TransmitPlan(identifier, useExtendedIdentifiers, dlc, data, intervalMs, sweepEnabled, sweepByteIndex, sweepFrom, sweepTo, sweepStep);
        plan.ResetRuntimeState();
        return true;
    }

    private void CaptureSelectedFrameToTransmitPanel(bool showMessageWhenMissingSelection)
    {
        if (activeTransmitPlan is not null || activeTransmitSequence is not null)
        {
            return;
        }

        FrameSnapshot? snapshot = GetSelectedFrameSnapshot();
        if (snapshot is null)
        {
            if (showMessageWhenMissingSelection)
            {
                MessageBox.Show(this, "Select a row in the CAN table first.", "CanScanmatik", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            return;
        }

        TransmitPlan capturedPlan = new(snapshot.Id, snapshot.IsExtended, Math.Clamp(snapshot.Dlc, 0, VisibleByteColumns), BuildCaptureData(snapshot.Data), 100, false, 0, 0x00, 0xFF, 0x01);
        LoadTransmitPlanIntoEditor(capturedPlan);
        AppendLog($"TXCAP {snapshot.BusName,-4}  {FormatId(snapshot.Id, snapshot.IsExtended)}");
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

    private void StartSessionLog()
    {
        StopSessionLog();

        if (connectedDriver is null || connectedBitrate is null || connectedFrameFormat is null || connectedBusProfile is null)
        {
            return;
        }

        string logsDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logsDirectory);
        sessionLogFilePath = Path.Combine(logsDirectory, $"{connectedBusProfile.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        sessionLogWriter = new StreamWriter(sessionLogFilePath, append: false, new UTF8Encoding(false))
        {
            AutoFlush = true
        };

        sessionLogWriter.WriteLine($"# Start: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sessionLogWriter.WriteLine($"# Adapter: {connectedDriver.DisplayName}");
        sessionLogWriter.WriteLine($"# Bus: {FormatBusProfileLabel(connectedBusProfile)}");
        sessionLogWriter.WriteLine($"# Bitrate: {connectedBitrate.DisplayName}");
        sessionLogWriter.WriteLine($"# Frames: {(connectedFrameFormat.UseExtendedIdentifiers ? "29-bit" : "11-bit")}");
        sessionLogWriter.WriteLine($"# Firmware: {connectedFirmwareVersion}");
        sessionLogWriter.WriteLine($"# DLL: {connectedDllVersion}");
        sessionLogWriter.WriteLine($"# API: {connectedApiVersion}");
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
        UpdateLogButtonState();
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
        ResetReceiveIndicator();
        UpdateAdapterDescription();
        UpdateBusHint();
        UpdateTransmitHint();
        UpdateTransmitButtonState();
        UpdateLogButtonState();
    }

    private static string BuildConnectionErrorMessage(Exception exception, BusProfileOption? busProfile)
    {
        if (busProfile is null)
        {
            return exception.Message;
        }

        if (exception is PassThruException passThruException &&
            busProfile.RequiresPinSelection &&
            passThruException.Status is PassThruStatus.ErrNotSupported or
                PassThruStatus.ErrInvalidProtocolId or
                PassThruStatus.ErrPinInvalid or
                PassThruStatus.ErrInvalidIoctlValue)
        {
            return $"{exception.Message}{Environment.NewLine}{Environment.NewLine}Selected bus: {FormatBusProfileLabel(busProfile)}.{Environment.NewLine}This route uses J2534-2 CAN_PS + J1962_PINS. The current Scanmatik hardware or driver rejected that bus/pin pair.";
        }

        return exception.Message;
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

    private void AppendFrameLog(StringBuilder builder, string busName, CanFrame frame)
    {
        string dataText = string.Join(' ', frame.Data.Select(static value => value.ToString("X2", CultureInfo.InvariantCulture)));
        if (string.IsNullOrWhiteSpace(dataText))
        {
            dataText = "-";
        }

        builder.Append(frame.ReceivedAtUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append("  ")
            .Append(busName.PadRight(4))
            .Append("  ")
            .Append(FormatId(frame.Id, frame.IsExtended).PadRight(8))
            .Append("  [")
            .Append(frame.Dlc.ToString(CultureInfo.InvariantCulture))
            .Append("]  ")
            .Append(dataText)
            .Append(Environment.NewLine);
    }

    private void AppendLog(string line)
    {
        AppendLogText($"{line}{Environment.NewLine}");
    }

    private void AppendSessionLogText(string text)
    {
        sessionLogWriter?.Write(text);
    }

    private void AppendLogText(string text)
    {
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

    private static string FormatTransmitData(TransmitPlan plan)
    {
        return plan.Dlc == 0
            ? "-"
            : string.Join(' ', plan.Data.Take(plan.Dlc).Select(static value => value.ToString("X2", CultureInfo.InvariantCulture)));
    }

    private static string BuildTransmitModeSummary(TransmitPlan plan)
    {
        return plan.SweepEnabled
            ? $"Sweep D{plan.SweepByteIndex} {plan.SweepFrom.ToString("X2", CultureInfo.InvariantCulture)}-{plan.SweepTo.ToString("X2", CultureInfo.InvariantCulture)} step {plan.SweepStep.ToString("X2", CultureInfo.InvariantCulture)}"
            : "Fixed data";
    }

    private static byte[] BuildCaptureData(byte[] source)
    {
        byte[] data = new byte[VisibleByteColumns];
        Array.Copy(source, data, Math.Min(source.Length, VisibleByteColumns));
        return data;
    }

    private static string? NormalizeHexFilter(string text)
    {
        string normalized = text.Trim().Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized.ToUpperInvariant();
    }

    private static bool TryParseIdentifier(string text, bool useExtendedIdentifiers, out uint identifier)
    {
        string normalized = text.Trim().Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (!uint.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out identifier))
        {
            return false;
        }

        return useExtendedIdentifiers ? identifier <= 0x1FFFFFFF : identifier <= 0x7FF;
    }

    private static bool TryParseHexByte(string text, out byte value)
    {
        string normalized = text.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            value = 0;
            return true;
        }

        normalized = normalized.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);
        return byte.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static void NormalizeHexByteTextBox(TextBox textBox, string fallbackValue = "00")
    {
        if (TryParseHexByte(textBox.Text, out byte value))
        {
            textBox.Text = value.ToString("X2", CultureInfo.InvariantCulture);
        }
        else
        {
            textBox.Text = fallbackValue;
        }
    }

    private static TextBox[] CreateTransmitByteTextBoxes()
    {
        TextBox[] boxes = new TextBox[VisibleByteColumns];
        for (int index = 0; index < boxes.Length; index++)
        {
            boxes[index] = new TextBox
            {
                Width = 38,
                MaxLength = 2,
                Text = "00"
            };
        }

        return boxes;
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
        public BusProfileOption(string name, string displayName, int primaryPin, int secondaryPin, bool usesDefaultCanChannel = false)
        {
            Name = name;
            DisplayName = displayName;
            PrimaryPin = primaryPin;
            SecondaryPin = secondaryPin;
            UsesDefaultCanChannel = usesDefaultCanChannel;
        }

        public string Name { get; }
        public string DisplayName { get; }
        public int PrimaryPin { get; }
        public int SecondaryPin { get; }
        public bool UsesDefaultCanChannel { get; }
        public bool RequiresPinSelection => !UsesDefaultCanChannel;
        public string PinDisplay => $"{PrimaryPin}-{SecondaryPin}";

        public override string ToString()
        {
            return DisplayName;
        }

        public uint? GetConnectPins()
        {
            if (UsesDefaultCanChannel)
            {
                return null;
            }

            return ((uint)PrimaryPin << 8) | (uint)SecondaryPin;
        }

        public static List<BusProfileOption> CreateDefaults()
        {
            return
            [
                new BusProfileOption("CAN1", "CAN1 6-14", 6, 14, usesDefaultCanChannel: true),
                new BusProfileOption("CAN2", "CAN2 3-11", 3, 11),
                new BusProfileOption("CAN3", "CAN3 12-13", 12, 13),
                new BusProfileOption("CAN4", "CAN4 1-9", 1, 9),
                new BusProfileOption("CAN5", "CAN5 2-10", 2, 10)
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

    private sealed class TransmitPlan
    {
        public TransmitPlan(uint id, bool useExtendedIdentifiers, int dlc, byte[] data, int intervalMs, bool sweepEnabled, int sweepByteIndex, byte sweepFrom, byte sweepTo, byte sweepStep)
        {
            Id = id;
            UseExtendedIdentifiers = useExtendedIdentifiers;
            Dlc = dlc;
            Data = data;
            IntervalMs = intervalMs;
            SweepEnabled = sweepEnabled;
            SweepByteIndex = sweepByteIndex;
            SweepFrom = sweepFrom;
            SweepTo = sweepTo;
            SweepStep = sweepStep;
            CurrentSweepValue = sweepFrom;
        }

        public uint Id { get; }
        public bool UseExtendedIdentifiers { get; }
        public int Dlc { get; }
        public byte[] Data { get; }
        public int IntervalMs { get; }
        public bool SweepEnabled { get; }
        public int SweepByteIndex { get; }
        public byte SweepFrom { get; }
        public byte SweepTo { get; }
        public byte SweepStep { get; }
        public int CurrentSweepValue { get; set; }

        public void ResetRuntimeState()
        {
            CurrentSweepValue = SweepFrom;
        }

        public TransmitPlan Clone()
        {
            return new TransmitPlan(Id, UseExtendedIdentifiers, Dlc, Data.ToArray(), IntervalMs, SweepEnabled, SweepByteIndex, SweepFrom, SweepTo, SweepStep);
        }
    }

    private sealed class TransmitPlanFileModel
    {
        public string Id { get; set; } = string.Empty;
        public bool UseExtendedIdentifiers { get; set; }
        public int Dlc { get; set; }
        public string[] Data { get; set; } = [];
        public int IntervalMs { get; set; }
        public bool SweepEnabled { get; set; }
        public int SweepByteIndex { get; set; }
        public string SweepFrom { get; set; } = "00";
        public string SweepTo { get; set; } = "FF";
        public string SweepStep { get; set; } = "01";

        public static TransmitPlanFileModel FromPlan(TransmitPlan plan)
        {
            return new TransmitPlanFileModel
            {
                Id = FormatId(plan.Id, plan.UseExtendedIdentifiers),
                UseExtendedIdentifiers = plan.UseExtendedIdentifiers,
                Dlc = plan.Dlc,
                Data = plan.Data.Select(static value => value.ToString("X2", CultureInfo.InvariantCulture)).ToArray(),
                IntervalMs = plan.IntervalMs,
                SweepEnabled = plan.SweepEnabled,
                SweepByteIndex = plan.SweepByteIndex,
                SweepFrom = plan.SweepFrom.ToString("X2", CultureInfo.InvariantCulture),
                SweepTo = plan.SweepTo.ToString("X2", CultureInfo.InvariantCulture),
                SweepStep = plan.SweepStep.ToString("X2", CultureInfo.InvariantCulture)
            };
        }

        public TransmitPlan ToPlan()
        {
            if (!TryParseIdentifier(Id, UseExtendedIdentifiers, out uint identifier))
            {
                throw new InvalidDataException($"Invalid TX ID in file: {Id}");
            }

            int normalizedDlc = Math.Clamp(Dlc, 0, VisibleByteColumns);
            byte[] data = new byte[VisibleByteColumns];
            for (int index = 0; index < Math.Min(Data.Length, VisibleByteColumns); index++)
            {
                if (!TryParseHexByte(Data[index], out data[index]))
                {
                    throw new InvalidDataException($"Invalid TX data byte in file at D{index}: {Data[index]}");
                }
            }

            if (!TryParseHexByte(SweepFrom, out byte sweepFrom))
            {
                throw new InvalidDataException($"Invalid SweepFrom value in file: {SweepFrom}");
            }

            if (!TryParseHexByte(SweepTo, out byte sweepTo))
            {
                throw new InvalidDataException($"Invalid SweepTo value in file: {SweepTo}");
            }

            if (!TryParseHexByte(SweepStep, out byte sweepStep) || sweepStep == 0)
            {
                throw new InvalidDataException($"Invalid SweepStep value in file: {SweepStep}");
            }

            int normalizedInterval = Math.Clamp(IntervalMs, 5, 5000);
            int normalizedSweepIndex = Math.Clamp(SweepByteIndex, 0, VisibleByteColumns - 1);
            TransmitPlan plan = new(identifier, UseExtendedIdentifiers, normalizedDlc, data, normalizedInterval, SweepEnabled, normalizedSweepIndex, sweepFrom, sweepTo, sweepStep);
            plan.ResetRuntimeState();
            return plan;
        }
    }

    private sealed class BufferedDataGridView : DataGridView
    {
        public BufferedDataGridView()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
            UpdateStyles();
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
