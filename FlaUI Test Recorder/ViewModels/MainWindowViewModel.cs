using FlaUI_Test_Recorder.Commands;
using Microsoft.Win32;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;

namespace FlaUI_Test_Recorder.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private const int WhMouseLl = 14;
    private const int WhKeyboardLl = 13;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WmMouseWheel = 0x020A;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkControl = 0x11;
    private const int ElementWaitTimeoutSeconds = 10;
    private const int WaitPollMilliseconds = 200;
    private const int ProcessSwitchTimeoutSeconds = 8;
    private const int ProcessSwitchPollMilliseconds = 200;
    private const uint Th32CsSnapProcess = 0x00000002;

    private readonly Dispatcher _dispatcher;
    private readonly RelayCommand _launchCommand;
    private readonly RelayCommand _attachCommand;
    private readonly RelayCommand _selectRunningProcessCommand;
    private readonly RelayCommand _startRecordingCommand;
    private readonly RelayCommand _stopRecordingCommand;
    private readonly RelayCommand _copyCodeCommand;

    private Process? _targetProcess;
    private string? _targetExecutablePath;
    private int? _attachedProcessId;
    private bool _useAttachInGeneratedCode;
    private bool _isRecording;
    private IntPtr _mouseHookHandle;
    private IntPtr _keyboardHookHandle;
    private LowLevelMouseProc? _mouseProc;
    private LowLevelKeyboardProc? _keyboardProc;
    private string _targetPath = string.Empty;
    private string _targetProcessIdInput = string.Empty;
    private string _statusText = "Idle";
    private string _generatedCode = string.Empty;

    public MainWindowViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        RecordedInteractions = [];

        BrowseCommand = new RelayCommand(Browse);
        _launchCommand = new RelayCommand(LaunchTarget, CanLaunchTarget);
        _attachCommand = new RelayCommand(AttachToRunningProcess, CanAttachToRunningProcess);
        _selectRunningProcessCommand = new RelayCommand(SelectRunningProcess, CanSelectRunningProcess);
        _startRecordingCommand = new RelayCommand(StartRecording, CanStartRecording);
        _stopRecordingCommand = new RelayCommand(StopRecording, () => IsRecording);
        ClearCommand = new RelayCommand(Clear);
        RegenerateCommand = new RelayCommand(Regenerate);
        _copyCodeCommand = new RelayCommand(CopyCode, () => !string.IsNullOrWhiteSpace(GeneratedCode));

        LaunchCommand = _launchCommand;
        AttachCommand = _attachCommand;
        SelectRunningProcessCommand = _selectRunningProcessCommand;
        StartRecordingCommand = _startRecordingCommand;
        StopRecordingCommand = _stopRecordingCommand;
        CopyCodeCommand = _copyCodeCommand;

        RefreshGeneratedCode();
    }

    public string TargetProcessIdInput
    {
        get => _targetProcessIdInput;
        set
        {
            if (SetProperty(ref _targetProcessIdInput, value))
            {
                RaiseCommandStates();
                RefreshGeneratedCode();
            }
        }
    }

    public ObservableCollection<RecordedInteraction> RecordedInteractions { get; }

    public string TargetPath
    {
        get => _targetPath;
        set
        {
            if (SetProperty(ref _targetPath, value))
            {
                RaiseCommandStates();
                RefreshGeneratedCode();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string GeneratedCode
    {
        get => _generatedCode;
        private set
        {
            if (SetProperty(ref _generatedCode, value))
            {
                _copyCodeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (SetProperty(ref _isRecording, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public ICommand BrowseCommand { get; }
    public ICommand LaunchCommand { get; }
    public ICommand AttachCommand { get; }
    public ICommand SelectRunningProcessCommand { get; }
    public ICommand StartRecordingCommand { get; }
    public ICommand StopRecordingCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand RegenerateCommand { get; }
    public ICommand CopyCodeCommand { get; }

    public void OnWindowClosed()
    {
        IsRecording = false;
        UninstallHooks();

        if (_targetProcess is not null)
        {
            _targetProcess.Exited -= TargetProcess_Exited;
        }
    }

    private void Browse()
    {
        var openFileDialog = new OpenFileDialog
        {
            Title = "Select target WPF executable",
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (openFileDialog.ShowDialog() != true)
        {
            return;
        }

        _targetExecutablePath = openFileDialog.FileName;
        TargetPath = _targetExecutablePath;
        UpdateStatus("Target selected");
        RefreshGeneratedCode();
    }

    private bool CanLaunchTarget()
    {
        var targetPath = TargetPath.Trim();
        return !string.IsNullOrWhiteSpace(targetPath) && File.Exists(targetPath);
    }

    private void LaunchTarget()
    {
        var targetPath = TargetPath.Trim();
        if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
        {
            MessageBox.Show("Select a valid target executable path.", "Invalid Target", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo(targetPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory
            };

            var process = Process.Start(startInfo);
            if (process is null)
            {
                MessageBox.Show("Could not launch target process.", "Launch Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            AttachToTargetProcess(process, targetPath, false);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to launch target process.\n{ex.Message}", "Launch Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool CanAttachToRunningProcess()
    {
        if (IsRecording)
        {
            return false;
        }

        return int.TryParse(TargetProcessIdInput.Trim(), out var processId) && processId > 0;
    }

    private bool CanSelectRunningProcess()
    {
        return !IsRecording;
    }

    private void SelectRunningProcess()
    {
        var processPicker = new ProcessPickerWindow
        {
            Owner = Application.Current.MainWindow
        };

        if (processPicker.ShowDialog() != true || processPicker.SelectedProcessId is null)
        {
            return;
        }

        TargetProcessIdInput = processPicker.SelectedProcessId.Value.ToString();
        UpdateStatus($"Selected PID {TargetProcessIdInput}");
    }

    private void AttachToRunningProcess()
    {
        if (!int.TryParse(TargetProcessIdInput.Trim(), out var processId) || processId <= 0)
        {
            MessageBox.Show("Enter a valid process id (PID).", "Invalid PID", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                MessageBox.Show("The selected process has already exited.", "Attach Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var executablePath = TryGetProcessExecutablePath(process);
            AttachToTargetProcess(process, executablePath, true);
            UpdateStatus($"Attached to PID {process.Id}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to attach to process {processId}.\n{ex.Message}", "Attach Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool CanStartRecording()
    {
        if (IsRecording)
        {
            return false;
        }

        if (_targetProcess is not null && !_targetProcess.HasExited)
        {
            return true;
        }

        var targetPath = TargetPath.Trim();
        return !string.IsNullOrWhiteSpace(targetPath) && File.Exists(targetPath);
    }

    private void StartRecording()
    {
        if (!EnsureTargetReady())
        {
            return;
        }

        if (!InstallHooks())
        {
            MessageBox.Show("Failed to install global input hooks.", "Recording Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        IsRecording = true;
        UpdateStatus("Recording");
        AddRecorderEvent(RecorderEventType.Recorder, "Recording started");
    }

    private void StopRecording()
    {
        if (!IsRecording)
        {
            return;
        }

        IsRecording = false;
        UninstallHooks();
        UpdateStatus("Stopped");
        AddRecorderEvent(RecorderEventType.Recorder, "Recording stopped");
        RefreshGeneratedCode();
    }

    private void Clear()
    {
        var hadEvents = RecordedInteractions.Count > 0;
        RecordedInteractions.Clear();
        RefreshGeneratedCode();
        UpdateStatus(hadEvents ? "Recording cleared" : (IsRecording ? "Recording" : "Idle"));
    }

    private void Regenerate()
    {
        RefreshGeneratedCode();
        UpdateStatus(IsRecording ? "Recording" : "Code regenerated");
    }

    private void CopyCode()
    {
        if (string.IsNullOrWhiteSpace(GeneratedCode))
        {
            return;
        }

        Clipboard.SetText(GeneratedCode);
        UpdateStatus("Code copied");
    }

    private bool EnsureTargetReady()
    {
        if (_targetProcess is not null && !_targetProcess.HasExited)
        {
            return true;
        }

        var targetPath = TargetPath.Trim();
        if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
        {
            MessageBox.Show("Launch a target process or choose a valid executable first.", "Target Required", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        _targetExecutablePath = targetPath;
        return true;
    }

    private void AttachToTargetProcess(Process process, string? executablePath, bool useAttachInGeneratedCode, bool updateGeneratedEntryPoint = true)
    {
        if (_targetProcess is not null)
        {
            _targetProcess.Exited -= TargetProcess_Exited;
        }

        _targetProcess = process;
        _targetProcess.EnableRaisingEvents = true;
        _targetProcess.Exited += TargetProcess_Exited;
        if (updateGeneratedEntryPoint)
        {
            _targetExecutablePath = executablePath;
            _attachedProcessId = _targetProcess.Id;
            _useAttachInGeneratedCode = useAttachInGeneratedCode;
        }

        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            TargetPath = executablePath;
        }

        TargetProcessIdInput = _targetProcess.Id.ToString();

        var processLabel = !string.IsNullOrWhiteSpace(executablePath)
            ? Path.GetFileName(executablePath)
            : _targetProcess.ProcessName;
        AddRecorderEvent(RecorderEventType.Process, $"Target attached: {processLabel} (PID {_targetProcess.Id})");
        if (!useAttachInGeneratedCode)
        {
            UpdateStatus("Target launched");
        }

        RefreshGeneratedCode();
    }

    private void TargetProcess_Exited(object? sender, EventArgs e)
    {
        var exitedProcess = sender as Process;
        var replacementProcess = exitedProcess is null ? null : WaitForReplacementProcess(exitedProcess.Id);

        _dispatcher.Invoke(() =>
        {
            if (replacementProcess is not null && !replacementProcess.HasExited)
            {
                var replacementPath = TryGetProcessExecutablePath(replacementProcess);
                AttachToTargetProcess(replacementProcess, replacementPath, useAttachInGeneratedCode: true, updateGeneratedEntryPoint: false);

                var replacementLabel = !string.IsNullOrWhiteSpace(replacementPath)
                    ? Path.GetFileName(replacementPath)
                    : replacementProcess.ProcessName;
                var switchCodeLine = BuildProcessHandoffCodeLine(replacementPath, replacementProcess.ProcessName);
                AddRecorderEvent(RecorderEventType.Process, $"Process switched to {replacementLabel} (PID {replacementProcess.Id})", switchCodeLine);

                UpdateStatus(IsRecording ? "Recording (process switched)" : "Process switched");
                RefreshGeneratedCode();
                return;
            }

            _targetProcess = null;
            if (IsRecording)
            {
                IsRecording = false;
                UninstallHooks();
                AddRecorderEvent(RecorderEventType.Recorder, "Recording stopped because target exited");
            }

            AddRecorderEvent(RecorderEventType.Process, "Target process exited");
            UpdateStatus("Target exited");
            RefreshGeneratedCode();
        });
    }

    private static string BuildProcessHandoffCodeLine(string? executablePath, string processName)
    {
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            return $"var nextProcess = Retry.WhileNull(() => FindProcessByPath(\"{EscapeForCode(executablePath)}\"), TimeSpan.FromSeconds({ProcessSwitchTimeoutSeconds}), TimeSpan.FromMilliseconds({ProcessSwitchPollMilliseconds})).Result; Assert.IsNotNull(nextProcess, \"Successor process not found.\"); app.Dispose(); app = Application.Attach(nextProcess!.Id); window = Retry.WhileNull(() => app.GetMainWindow(automation), TimeSpan.FromSeconds({ElementWaitTimeoutSeconds}), TimeSpan.FromMilliseconds({WaitPollMilliseconds})).Result ?? throw new InvalidOperationException(\"Main window not found.\"); window.Focus();";
        }

        return $"var nextProcess = Retry.WhileNull(() => FindNewestProcessByName(\"{EscapeForCode(processName)}\"), TimeSpan.FromSeconds({ProcessSwitchTimeoutSeconds}), TimeSpan.FromMilliseconds({ProcessSwitchPollMilliseconds})).Result; Assert.IsNotNull(nextProcess, \"Successor process not found.\"); app.Dispose(); app = Application.Attach(nextProcess!.Id); window = Retry.WhileNull(() => app.GetMainWindow(automation), TimeSpan.FromSeconds({ElementWaitTimeoutSeconds}), TimeSpan.FromMilliseconds({WaitPollMilliseconds})).Result ?? throw new InvalidOperationException(\"Main window not found.\"); window.Focus();";
    }

    private static Process? WaitForReplacementProcess(int exitedProcessId)
    {
        var timeout = TimeSpan.FromSeconds(ProcessSwitchTimeoutSeconds);
        var startedAt = Stopwatch.StartNew();

        while (startedAt.Elapsed < timeout)
        {
            var replacement = FindNewestChildProcess(exitedProcessId);
            if (replacement is not null)
            {
                return replacement;
            }

            Thread.Sleep(ProcessSwitchPollMilliseconds);
        }

        return null;
    }

    private static Process? FindNewestChildProcess(int parentProcessId)
    {
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            var processEntry = new ProcessEntry32
            {
                DwSize = (uint)Marshal.SizeOf<ProcessEntry32>()
            };

            var childCandidates = new List<Process>();
            if (!Process32First(snapshot, ref processEntry))
            {
                return null;
            }

            do
            {
                if (processEntry.Th32ParentProcessId != (uint)parentProcessId)
                {
                    continue;
                }

                try
                {
                    var process = Process.GetProcessById((int)processEntry.Th32ProcessId);
                    if (!process.HasExited)
                    {
                        childCandidates.Add(process);
                    }
                }
                catch
                {
                    // ignore stale process ids
                }
            }
            while (Process32Next(snapshot, ref processEntry));

            return childCandidates
                .OrderByDescending(GetProcessStartTimeSafe)
                .FirstOrDefault();
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }

    private static DateTime GetProcessStartTimeSafe(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private bool InstallHooks()
    {
        if (_mouseHookHandle != IntPtr.Zero || _keyboardHookHandle != IntPtr.Zero)
        {
            return true;
        }

        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;

        _mouseHookHandle = SetWindowsHookEx(WhMouseLl, _mouseProc, IntPtr.Zero, 0);
        _keyboardHookHandle = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, IntPtr.Zero, 0);

        if (_mouseHookHandle != IntPtr.Zero && _keyboardHookHandle != IntPtr.Zero)
        {
            return true;
        }

        UninstallHooks();
        return false;
    }

    private void UninstallHooks()
    {
        if (_mouseHookHandle != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = IntPtr.Zero;
        }

        if (_keyboardHookHandle != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_keyboardHookHandle);
            _keyboardHookHandle = IntPtr.Zero;
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsRecording && IsTargetForeground())
        {
            var message = wParam.ToInt32();
            if (message is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmMouseWheel)
            {
                var hookData = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
                var isCtrlPressed = IsControlPressed();
                RecordMouseEvent(message, hookData.Point.X, hookData.Point.Y, hookData.MouseData, isCtrlPressed);
            }
        }

        return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsRecording && IsTargetForeground())
        {
            var message = wParam.ToInt32();
            if (message is WmKeyDown or WmSysKeyDown)
            {
                var hookData = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                var key = KeyInterop.KeyFromVirtualKey((int)hookData.VirtualKeyCode);
                RecordKeyboardEvent(key);
            }
        }

        return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    private bool IsTargetForeground()
    {
        if (_targetProcess is null || _targetProcess.HasExited)
        {
            return false;
        }

        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foregroundWindow, out var processId);
        return processId == (uint)_targetProcess.Id;
    }

    private void RecordMouseEvent(int message, int x, int y, uint mouseData, bool isCtrlPressed)
    {
        var targetInfo = ResolveElementAtPoint(x, y);
        var action = message switch
        {
            WmLButtonDown => "LeftClick",
            WmRButtonDown => "RightClick",
            WmMButtonDown => "MiddleClick",
            WmMouseWheel => ((short)((mouseData >> 16) & 0xFFFF)) > 0 ? "WheelUp" : "WheelDown",
            _ => "Mouse"
        };

        var elementSummary = targetInfo is null
            ? "UnknownElement"
            : $"{targetInfo.ControlTypeName} Name='{targetInfo.Name ?? ""}' Id='{targetInfo.AutomationId ?? ""}' Text='{targetInfo.TextContent ?? ""}'";

        var elementVariableName = $"element{RecordedInteractions.Count + 1}";
        var locator = targetInfo is null ? null : BuildFlaUiLocator(targetInfo);
        var assertionVariableName = $"actualText{RecordedInteractions.Count + 1}";

        if (isCtrlPressed && action == "LeftClick")
        {
            var assertionCode = BuildCtrlLeftClickAssertionCode(targetInfo, locator, elementVariableName, assertionVariableName);
            var assertionDescription = targetInfo is null
                ? "Ctrl+LeftClick assert text (no target element metadata found)"
                : $"Ctrl+LeftClick assert text equals '{targetInfo.TextContent ?? targetInfo.Name ?? string.Empty}' on {elementSummary}";

            AddRecorderEvent(RecorderEventType.Mouse, assertionDescription, assertionCode);
            RefreshGeneratedCode();
            return;
        }

        var codeLine = action switch
        {
            "LeftClick" when locator is not null => $"var {elementVariableName} = Retry.WhileNull(() => {locator}, TimeSpan.FromSeconds({ElementWaitTimeoutSeconds}), TimeSpan.FromMilliseconds({WaitPollMilliseconds})).Result; if ({elementVariableName} != null) Mouse.Click({elementVariableName}.GetClickablePoint()); Wait.UntilInputIsProcessed();",
            "RightClick" when locator is not null => $"var {elementVariableName} = Retry.WhileNull(() => {locator}, TimeSpan.FromSeconds({ElementWaitTimeoutSeconds}), TimeSpan.FromMilliseconds({WaitPollMilliseconds})).Result; if ({elementVariableName} != null) Mouse.Click({elementVariableName}.GetClickablePoint(), MouseButton.Right); Wait.UntilInputIsProcessed();",
            "MiddleClick" when locator is not null => $"var {elementVariableName} = Retry.WhileNull(() => {locator}, TimeSpan.FromSeconds({ElementWaitTimeoutSeconds}), TimeSpan.FromMilliseconds({WaitPollMilliseconds})).Result; // Middle click on {elementVariableName} - customize if needed; Wait.UntilInputIsProcessed();",
            "WheelUp" when locator is not null => $"var {elementVariableName} = Retry.WhileNull(() => {locator}, TimeSpan.FromSeconds({ElementWaitTimeoutSeconds}), TimeSpan.FromMilliseconds({WaitPollMilliseconds})).Result; // Wheel up on {elementVariableName}; Wait.UntilInputIsProcessed();",
            "WheelDown" when locator is not null => $"var {elementVariableName} = Retry.WhileNull(() => {locator}, TimeSpan.FromSeconds({ElementWaitTimeoutSeconds}), TimeSpan.FromMilliseconds({WaitPollMilliseconds})).Result; // Wheel down on {elementVariableName}; Wait.UntilInputIsProcessed();",
            "LeftClick" => "// Left click captured but no UIA Name/AutomationId was found for the target element",
            "RightClick" => "// Right click captured but no UIA Name/AutomationId was found for the target element",
            _ => "// Mouse action captured but no UIA locator could be generated"
        };

        AddRecorderEvent(RecorderEventType.Mouse, $"{action} on {elementSummary}", codeLine);
        RefreshGeneratedCode();
    }

    private static string BuildCtrlLeftClickAssertionCode(TargetElementInfo? targetInfo, string? locator, string elementVariableName, string assertionVariableName)
    {
        if (targetInfo is null || string.IsNullOrWhiteSpace(locator))
        {
            return "// Ctrl+LeftClick assertion requested but no UIA Name/AutomationId was found for the target element";
        }

        var expectedText = targetInfo.TextContent ?? targetInfo.Name;
        if (string.IsNullOrWhiteSpace(expectedText))
        {
            return "// Ctrl+LeftClick assertion requested but the target element has no readable text content";
        }

        return $"var {elementVariableName} = Retry.WhileNull(() => {locator}, TimeSpan.FromSeconds({ElementWaitTimeoutSeconds}), TimeSpan.FromMilliseconds({WaitPollMilliseconds})).Result; Assert.IsNotNull({elementVariableName}, \"Assertion target not found.\"); var {assertionVariableName} = {elementVariableName}!.Patterns.Value.PatternOrDefault?.Value ?? {elementVariableName}.Name ?? string.Empty; Assert.AreEqual(\"{EscapeForCode(expectedText)}\", {assertionVariableName}, \"Text assertion failed.\");";
    }

    private static bool IsControlPressed()
    {
        return (GetKeyState(VkControl) & 0x8000) != 0;
    }

    private static TargetElementInfo? ResolveElementAtPoint(int x, int y)
    {
        try
        {
            var point = new System.Windows.Point(x, y);
            var element = AutomationElement.FromPoint(point);
            if (element is null)
            {
                return null;
            }

            var automationId = element.Current.AutomationId?.Trim();
            var name = element.Current.Name?.Trim();
            var controlTypeName = element.Current.ControlType?.ProgrammaticName?.Replace("ControlType.", string.Empty) ?? "Element";
            string? textContent = null;
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObj) && valuePatternObj is ValuePattern valuePattern)
            {
                textContent = valuePattern.Current.Value?.Trim();
            }

            if (string.IsNullOrWhiteSpace(textContent))
            {
                textContent = name;
            }

            if (string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            return new TargetElementInfo
            {
                AutomationId = string.IsNullOrWhiteSpace(automationId) ? null : automationId,
                Name = string.IsNullOrWhiteSpace(name) ? null : name,
                ControlTypeName = controlTypeName,
                TextContent = string.IsNullOrWhiteSpace(textContent) ? null : textContent
            };
        }
        catch
        {
            return null;
        }
    }

    private static string BuildFlaUiLocator(TargetElementInfo target)
    {
        if (!string.IsNullOrWhiteSpace(target.AutomationId) && !string.IsNullOrWhiteSpace(target.Name))
        {
            return $"window.FindFirstDescendant(cf => cf.ByAutomationId(\"{EscapeForCode(target.AutomationId)}\")) ?? window.FindFirstDescendant(cf => cf.ByName(\"{EscapeForCode(target.Name)}\"))";
        }

        if (!string.IsNullOrWhiteSpace(target.AutomationId))
        {
            return $"window.FindFirstDescendant(cf => cf.ByAutomationId(\"{EscapeForCode(target.AutomationId)}\"))";
        }

        return $"window.FindFirstDescendant(cf => cf.ByName(\"{EscapeForCode(target.Name ?? string.Empty)}\"))";
    }

    private void RecordKeyboardEvent(Key key)
    {
        if (key == Key.None || key == Key.LeftCtrl)
        {
            return;
        }

        var codeLine = key switch
        {
            Key.Enter => "window.Focus(); Keyboard.Type(VirtualKeyShort.RETURN); Wait.UntilInputIsProcessed();",
            Key.Tab => "window.Focus(); Keyboard.Type(VirtualKeyShort.TAB); Wait.UntilInputIsProcessed();",
            Key.Escape => "window.Focus(); Keyboard.Type(VirtualKeyShort.ESCAPE); Wait.UntilInputIsProcessed();",
            _ => $"window.Focus(); Keyboard.Type(\"{EscapeForCode(key.ToString())}\"); Wait.UntilInputIsProcessed();"
        };

        AddRecorderEvent(RecorderEventType.Keyboard, $"KeyDown: {key}", codeLine);
        RefreshGeneratedCode();
    }

    private void AddRecorderEvent(RecorderEventType eventType, string description)
    {
        AddRecorderEvent(eventType, description, $"// {EscapeForCode(description)}");
    }

    private void AddRecorderEvent(RecorderEventType eventType, string description, string codeLine)
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.InvokeAsync(() => AddRecorderEvent(eventType, description, codeLine));
            return;
        }

        RecordedInteractions.Add(new RecordedInteraction
        {
            Index = RecordedInteractions.Count + 1,
            EventType = eventType.ToString(),
            Description = description,
            Timestamp = DateTime.Now,
            CodeLine = codeLine
        });
    }

    private static string EscapeForCode(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private void RefreshGeneratedCode()
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.InvokeAsync(RefreshGeneratedCode);
            return;
        }

        GeneratedCode = GenerateFlaUiCode();
    }

    private string GenerateFlaUiCode()
    {
        var builder = new StringBuilder();
        builder.AppendLine("using FlaUI.Core;");
        builder.AppendLine("using FlaUI.Core.AutomationElements;");
        builder.AppendLine("using FlaUI.Core.Input;");
        builder.AppendLine("using FlaUI.Core.Tools;");
        builder.AppendLine("using FlaUI.Core.WindowsAPI;");
        builder.AppendLine("using Microsoft.VisualStudio.TestTools.UnitTesting;");
        builder.AppendLine("using System;");
        builder.AppendLine("using System.Diagnostics;");
        builder.AppendLine("using System.Linq;");
        builder.AppendLine("using FlaUI.UIA3;");
        builder.AppendLine();
        builder.AppendLine("public static class RecordedTest");
        builder.AppendLine("{");
        builder.AppendLine("    public static void Run()");
        builder.AppendLine("    {");
        if (!_useAttachInGeneratedCode && !string.IsNullOrWhiteSpace(_targetExecutablePath))
        {
            builder.AppendLine($"        var app = Application.Launch(@\"{_targetExecutablePath.Replace("\\", "\\\\")}\");");
        }
        else if (_attachedProcessId is int attachedProcessId && attachedProcessId > 0)
        {
            builder.AppendLine($"        var app = Application.Attach({attachedProcessId});");
        }
        else
        {
            builder.AppendLine("        var app = Application.Attach(12345); // Replace with the target process id");
        }

        builder.AppendLine("        using var automation = new UIA3Automation();");
        builder.AppendLine($"        var window = Retry.WhileNull(() => app.GetMainWindow(automation), TimeSpan.FromSeconds({ElementWaitTimeoutSeconds}), TimeSpan.FromMilliseconds({WaitPollMilliseconds})).Result ?? throw new InvalidOperationException(\"Main window not found.\");\n");
        builder.AppendLine("        window.Focus();\n");

        if (RecordedInteractions.Count == 0)
        {
            builder.AppendLine("        // No interactions recorded yet.");
        }
        else
        {
            foreach (var interaction in RecordedInteractions)
            {
                builder.AppendLine($"        // [{interaction.Timestamp:HH:mm:ss}] {interaction.Description}");
                builder.AppendLine($"        {interaction.CodeLine}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("        app.Dispose();");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static Process? FindProcessByPath(string executablePath)");
        builder.AppendLine("    {");
        builder.AppendLine("        return Process.GetProcesses().FirstOrDefault(process =>");
        builder.AppendLine("        {");
        builder.AppendLine("            try");
        builder.AppendLine("            {");
        builder.AppendLine("                return !process.HasExited && string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase);");
        builder.AppendLine("            }");
        builder.AppendLine("            catch");
        builder.AppendLine("            {");
        builder.AppendLine("                return false;");
        builder.AppendLine("            }");
        builder.AppendLine("        });");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static Process? FindNewestProcessByName(string processName)");
        builder.AppendLine("    {");
        builder.AppendLine("        return Process.GetProcessesByName(processName)");
        builder.AppendLine("            .OrderByDescending(process =>");
        builder.AppendLine("            {");
        builder.AppendLine("                try");
        builder.AppendLine("                {");
        builder.AppendLine("                    return process.StartTime;");
        builder.AppendLine("                }");
        builder.AppendLine("                catch");
        builder.AppendLine("                {");
        builder.AppendLine("                    return DateTime.MinValue;");
        builder.AppendLine("                }");
        builder.AppendLine("            })");
        builder.AppendLine("            .FirstOrDefault();");
        builder.AppendLine("    }");
        builder.AppendLine("}");

        return builder.ToString();
    }

    private void UpdateStatus(string status)
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.InvokeAsync(() => UpdateStatus(status));
            return;
        }

        StatusText = status;
    }

    private void RaiseCommandStates()
    {
        _launchCommand.RaiseCanExecuteChanged();
        _attachCommand.RaiseCanExecuteChanged();
        _selectRunningProcessCommand.RaiseCanExecuteChanged();
        _startRecordingCommand.RaiseCanExecuteChanged();
        _stopRecordingCommand.RaiseCanExecuteChanged();
    }

    private static string? TryGetProcessExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    public sealed class RecordedInteraction
    {
        public required int Index { get; init; }
        public required string EventType { get; init; }
        public required string Description { get; init; }
        public required DateTime Timestamp { get; init; }
        public required string CodeLine { get; init; }
    }

    private sealed class TargetElementInfo
    {
        public string? AutomationId { get; init; }
        public string? Name { get; init; }
        public required string ControlTypeName { get; init; }
        public string? TextContent { get; init; }
    }

    private enum RecorderEventType
    {
        Recorder,
        Process,
        Mouse,
        Keyboard
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public Point Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct ProcessEntry32
    {
        public uint DwSize;
        public uint CntUsage;
        public uint Th32ProcessId;
        public UIntPtr Th32DefaultHeapId;
        public uint Th32ModuleId;
        public uint CntThreads;
        public uint Th32ParentProcessId;
        public int PcPriClassBase;
        public uint DwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string SzExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VirtualKeyCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookType, Delegate callback, IntPtr moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hookHandle, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKeyCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 processEntry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 processEntry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
