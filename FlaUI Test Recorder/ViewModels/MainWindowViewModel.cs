using FlaUI_Test_Recorder.Commands;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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

    private readonly Dispatcher _dispatcher;
    private readonly RelayCommand _launchCommand;
    private readonly RelayCommand _startRecordingCommand;
    private readonly RelayCommand _stopRecordingCommand;
    private readonly RelayCommand _copyCodeCommand;

    private Process? _targetProcess;
    private string? _targetExecutablePath;
    private bool _isRecording;
    private IntPtr _mouseHookHandle;
    private IntPtr _keyboardHookHandle;
    private LowLevelMouseProc? _mouseProc;
    private LowLevelKeyboardProc? _keyboardProc;
    private string _targetPath = string.Empty;
    private string _statusText = "Idle";
    private string _generatedCode = string.Empty;

    public MainWindowViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        RecordedInteractions = [];

        BrowseCommand = new RelayCommand(Browse);
        _launchCommand = new RelayCommand(LaunchTarget, CanLaunchTarget);
        _startRecordingCommand = new RelayCommand(StartRecording, CanStartRecording);
        _stopRecordingCommand = new RelayCommand(StopRecording, () => IsRecording);
        ClearCommand = new RelayCommand(Clear);
        RegenerateCommand = new RelayCommand(Regenerate);
        _copyCodeCommand = new RelayCommand(CopyCode, () => !string.IsNullOrWhiteSpace(GeneratedCode));

        LaunchCommand = _launchCommand;
        StartRecordingCommand = _startRecordingCommand;
        StopRecordingCommand = _stopRecordingCommand;
        CopyCodeCommand = _copyCodeCommand;

        RefreshGeneratedCode();
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

            AttachToTargetProcess(process, targetPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to launch target process.\n{ex.Message}", "Launch Failed", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void AttachToTargetProcess(Process process, string executablePath)
    {
        if (_targetProcess is not null)
        {
            _targetProcess.Exited -= TargetProcess_Exited;
        }

        _targetProcess = process;
        _targetProcess.EnableRaisingEvents = true;
        _targetProcess.Exited += TargetProcess_Exited;
        _targetExecutablePath = executablePath;
        TargetPath = executablePath;
        AddRecorderEvent(RecorderEventType.Process, $"Target started: {Path.GetFileName(executablePath)} (PID {_targetProcess.Id})");
        UpdateStatus("Target launched");
        RefreshGeneratedCode();
    }

    private void TargetProcess_Exited(object? sender, EventArgs e)
    {
        _dispatcher.Invoke(() =>
        {
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

        return $"var {elementVariableName} = Retry.WhileNull(() => {locator}, TimeSpan.FromSeconds({ElementWaitTimeoutSeconds}), TimeSpan.FromMilliseconds({WaitPollMilliseconds})).Result; if ({elementVariableName} == null) throw new Exception(\"Assertion target not found.\"); var {assertionVariableName} = {elementVariableName}.Patterns.Value.PatternOrDefault?.Value ?? {elementVariableName}.Name ?? string.Empty; if (!string.Equals({assertionVariableName}, \"{EscapeForCode(expectedText)}\", StringComparison.Ordinal)) throw new Exception($\"Text assertion failed. Expected '{EscapeForCode(expectedText)}' but was '{{{assertionVariableName}}}'.\");";
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
        builder.AppendLine("using System;");
        builder.AppendLine("using FlaUI.UIA3;");
        builder.AppendLine();
        builder.AppendLine("public static class RecordedTest");
        builder.AppendLine("{");
        builder.AppendLine("    public static void Run()");
        builder.AppendLine("    {");
        if (!string.IsNullOrWhiteSpace(_targetExecutablePath))
        {
            builder.AppendLine($"        using var app = Application.Launch(@\"{_targetExecutablePath.Replace("\\", "\\\\")}\");");
        }
        else
        {
            builder.AppendLine("        using var app = Application.Attach(12345); // Replace with the target process id");
        }

        builder.AppendLine("        using var automation = new UIA3Automation();");
        builder.AppendLine("        var window = app.GetMainWindow(automation);\n");
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
        _startRecordingCommand.RaiseCanExecuteChanged();
        _stopRecordingCommand.RaiseCanExecuteChanged();
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
}
