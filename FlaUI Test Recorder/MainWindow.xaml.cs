using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;

namespace FlaUI_Test_Recorder
{
    public partial class MainWindow : Window
    {
        private const int WhMouseLl = 14;
        private const int WhKeyboardLl = 13;
        private const int WmLButtonDown = 0x0201;
        private const int WmRButtonDown = 0x0204;
        private const int WmMButtonDown = 0x0207;
        private const int WmMouseWheel = 0x020A;
        private const int WmKeyDown = 0x0100;
        private const int WmSysKeyDown = 0x0104;
        private const int ElementWaitTimeoutSeconds = 10;
        private const int WaitPollMilliseconds = 200;

        private readonly ObservableCollection<RecordedInteraction> _recordedInteractions = [];
        private Process? _targetProcess;
        private string? _targetExecutablePath;
        private bool _isRecording;
        private IntPtr _mouseHookHandle;
        private IntPtr _keyboardHookHandle;
        private LowLevelMouseProc? _mouseProc;
        private LowLevelKeyboardProc? _keyboardProc;

        public MainWindow()
        {
            InitializeComponent();
            EventsListView.ItemsSource = _recordedInteractions;
            StopRecordingButton.IsEnabled = false;
            UpdateStatus("Idle");
            RefreshGeneratedCode();
            Closed += MainWindow_Closed;
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Title = "Select target WPF executable",
                Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (openFileDialog.ShowDialog(this) != true)
            {
                return;
            }

            _targetExecutablePath = openFileDialog.FileName;
            TargetPathTextBox.Text = _targetExecutablePath;
            UpdateStatus("Target selected");
            RefreshGeneratedCode();
        }

        private void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            var targetPath = TargetPathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
            {
                MessageBox.Show(this, "Select a valid target executable path.", "Invalid Target", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    MessageBox.Show(this, "Could not launch target process.", "Launch Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                AttachToTargetProcess(process, targetPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to launch target process.\n{ex.Message}", "Launch Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StartRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureTargetReady())
            {
                return;
            }

            if (!InstallHooks())
            {
                MessageBox.Show(this, "Failed to install global input hooks.", "Recording Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _isRecording = true;
            StartRecordingButton.IsEnabled = false;
            StopRecordingButton.IsEnabled = true;
            UpdateStatus("Recording");
            AddRecorderEvent(RecorderEventType.Recorder, "Recording started");
        }

        private void StopRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRecording)
            {
                return;
            }

            _isRecording = false;
            UninstallHooks();
            StartRecordingButton.IsEnabled = true;
            StopRecordingButton.IsEnabled = false;
            UpdateStatus("Stopped");
            AddRecorderEvent(RecorderEventType.Recorder, "Recording stopped");
            RefreshGeneratedCode();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            var hadEvents = _recordedInteractions.Count > 0;
            _recordedInteractions.Clear();
            RefreshGeneratedCode();
            UpdateStatus(hadEvents ? "Recording cleared" : (_isRecording ? "Recording" : "Idle"));
        }

        private void RegenerateButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshGeneratedCode();
            UpdateStatus(_isRecording ? "Recording" : "Code regenerated");
        }

        private void CopyCodeButton_Click(object sender, RoutedEventArgs e)
        {
            var code = GeneratedCodeTextBox.Text;
            if (string.IsNullOrWhiteSpace(code))
            {
                return;
            }

            Clipboard.SetText(code);
            UpdateStatus("Code copied");
        }

        private bool EnsureTargetReady()
        {
            if (_targetProcess is not null && !_targetProcess.HasExited)
            {
                return true;
            }

            var targetPath = TargetPathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
            {
                MessageBox.Show(this, "Launch a target process or choose a valid executable first.", "Target Required", MessageBoxButton.OK, MessageBoxImage.Information);
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
            TargetPathTextBox.Text = executablePath;
            AddRecorderEvent(RecorderEventType.Process, $"Target started: {Path.GetFileName(executablePath)} (PID {_targetProcess.Id})");
            UpdateStatus("Target launched");
            RefreshGeneratedCode();
        }

        private void TargetProcess_Exited(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (_isRecording)
                {
                    _isRecording = false;
                    UninstallHooks();
                    StartRecordingButton.IsEnabled = true;
                    StopRecordingButton.IsEnabled = false;
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
            if (nCode >= 0 && _isRecording && IsTargetForeground())
            {
                var message = wParam.ToInt32();
                if (message is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmMouseWheel)
                {
                    var hookData = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
                    RecordMouseEvent(message, hookData.Point.X, hookData.Point.Y, hookData.MouseData);
                }
            }

            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _isRecording && IsTargetForeground())
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

        private void RecordMouseEvent(int message, int x, int y, uint mouseData)
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

            var elementVariableName = $"element{_recordedInteractions.Count + 1}";
            var locator = targetInfo is null ? null : BuildFlaUiLocator(targetInfo);
            var textAssertionCode = BuildTextAssertionCode(targetInfo, elementVariableName);

            var codeLine = action switch
            {
                "LeftClick" when locator is not null => $"var {elementVariableName} = Retry.WhileNull(() => {locator}, TimeSpan.FromSeconds({ElementWaitTimeoutSeconds}), TimeSpan.FromMilliseconds({WaitPollMilliseconds})).Result; if ({elementVariableName} != null) Mouse.Click({elementVariableName}.GetClickablePoint()); Wait.UntilInputIsProcessed(); {textAssertionCode}",
                "RightClick" when locator is not null => $"var {elementVariableName} = Retry.WhileNull(() => {locator}, TimeSpan.FromSeconds({ElementWaitTimeoutSeconds}), TimeSpan.FromMilliseconds({WaitPollMilliseconds})).Result; if ({elementVariableName} != null) Mouse.Click({elementVariableName}.GetClickablePoint(), MouseButton.Right); Wait.UntilInputIsProcessed(); {textAssertionCode}",
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

        private static string BuildTextAssertionCode(TargetElementInfo? target, string elementVariableName)
        {
            if (target is null || string.IsNullOrWhiteSpace(target.TextContent))
            {
                return string.Empty;
            }

            var expectedText = EscapeForCode(target.TextContent);
            return $"if ({elementVariableName} != null && !string.Equals({elementVariableName}.Name, \"{expectedText}\", StringComparison.Ordinal)) throw new Exception(\"Text assertion failed for {elementVariableName}.\");";
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
            if (key == Key.None)
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

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            _isRecording = false;
            UninstallHooks();

            if (_targetProcess is not null)
            {
                _targetProcess.Exited -= TargetProcess_Exited;
            }
        }

        private void AddRecorderEvent(RecorderEventType eventType, string description)
        {
            AddRecorderEvent(eventType, description, $"// {EscapeForCode(description)}");
        }

        private void AddRecorderEvent(RecorderEventType eventType, string description, string codeLine)
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.InvokeAsync(() => AddRecorderEvent(eventType, description, codeLine));
                return;
            }

            _recordedInteractions.Add(new RecordedInteraction
            {
                Index = _recordedInteractions.Count + 1,
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
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.InvokeAsync(RefreshGeneratedCode);
                return;
            }

            GeneratedCodeTextBox.Text = GenerateFlaUiCode();
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

            if (_recordedInteractions.Count == 0)
            {
                builder.AppendLine("        // No interactions recorded yet.");
            }
            else
            {
                foreach (var interaction in _recordedInteractions)
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
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.InvokeAsync(() => UpdateStatus(status));
                return;
            }

            StatusTextBlock.Text = status;
        }

        private sealed class RecordedInteraction
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
    }
}