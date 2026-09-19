using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VideoShowBoothButtons
{
    /// <summary>
    /// 5 个硬件按钮热键的自定义设置视图：逐项选择修饰键（Ctrl/Alt/Shift）与按键，
    /// 点击「保存并应用」后立即重新注册。整个视图用纯代码构建（不依赖 XAML 资源，
    /// 避免插件 ALC 环境下 pack URI / baml 解析问题）。
    /// </summary>
    public sealed class BoothButtonsSettingsView : UserControl
    {
        private sealed class RowState
        {
            public HotkeyBinding Binding;
            public CheckBox CtrlBox;
            public CheckBox AltBox;
            public CheckBox ShiftBox;
            public TextBox KeyBox;

            public Key CurrentKey;
        }

        private readonly BoothButtonsPlugin _plugin;
        private readonly StackPanel _rowsPanel;
        private readonly TextBlock _statusText;
        private readonly List<RowState> _rows = new List<RowState>();

        public BoothButtonsSettingsView(BoothButtonsPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(new TextBlock
            {
                Text = "视频展台硬件按钮热键",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
            });

            root.Children.Add(new TextBlock
            {
                Text = "硬件把 5 个物理按键模拟为全局热键（默认 Ctrl+Alt+Shift+M/O/I/U/P）。" +
                       "若硬件发送的组合与默认不同，可在此修改每个功能的修饰键与按键，保存后立即生效。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                Margin = new Thickness(0, 4, 0, 8),
            });

            _rowsPanel = new StackPanel();
            root.Children.Add(_rowsPanel);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            var resetButton = new Button { Content = "恢复默认", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 10, 0) };
            resetButton.Click += ResetButton_Click;
            var saveButton = new Button { Content = "保存并应用", Padding = new Thickness(14, 6, 14, 6) };
            saveButton.Click += SaveButton_Click;
            buttons.Children.Add(resetButton);
            buttons.Children.Add(saveButton);
            root.Children.Add(buttons);

            _statusText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
                Margin = new Thickness(0, 10, 0, 0),
            };
            root.Children.Add(_statusText);

            Content = root;
            RebuildRows();
        }

        private void RebuildRows()
        {
            _rowsPanel.Children.Clear();
            _rows.Clear();

            foreach (var binding in _plugin.CurrentBindings)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };

                row.Children.Add(new TextBlock
                {
                    Text = binding.Label,
                    Width = 76,
                    VerticalAlignment = VerticalAlignment.Center,
                });

                var state = new RowState { Binding = binding };

                state.CurrentKey = KeyInterop.KeyFromVirtualKey((int)binding.Key);
                state.CtrlBox = new CheckBox { Content = "Ctrl", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, IsChecked = (binding.Modifiers & 2) != 0 };
                state.AltBox = new CheckBox { Content = "Alt", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, IsChecked = (binding.Modifiers & 1) != 0 };
                state.ShiftBox = new CheckBox { Content = "Shift", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, IsChecked = (binding.Modifiers & 4) != 0 };
                row.Children.Add(state.CtrlBox);
                row.Children.Add(state.AltBox);
                row.Children.Add(state.ShiftBox);

                state.KeyBox = new TextBox
                {
                    IsReadOnly = true,
                    IsReadOnlyCaretVisible = false,
                    Width = 150,
                    Padding = new Thickness(8, 4, 8, 4),
                    TextAlignment = TextAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Text = KeyToDisplayText(binding.Key),
                };
                state.KeyBox.PreviewKeyDown += KeyBox_PreviewKeyDown;
                row.Children.Add(state.KeyBox);

                row.Children.Add(new TextBlock
                {
                    Text = "（点击输入框后按下新按键）",
                    Foreground = Brushes.Gray,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                    FontSize = 11,
                });

                _rowsPanel.Children.Add(row);
                _rows.Add(state);
            }
        }

        private void KeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!(sender is TextBox keyBox)) return;
            var state = FindRow(keyBox);
            if (state == null) return;

            // AltGr 等系统键：用 SystemKey 取真实按键
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            // 纯修饰键的按下不结束录制，等其他按键
            if (IsModifierKey(key))
            {
                e.Handled = true;
                return;
            }

            e.Handled = true;
            state.CurrentKey = key;
            keyBox.Text = key.ToString();
            keyBox.CaretIndex = keyBox.Text.Length;
        }

        private RowState FindRow(TextBox keyBox)
        {
            foreach (var row in _rows)
            {
                if (ReferenceEquals(row.KeyBox, keyBox)) return row;
            }
            return null;
        }

        private static bool IsModifierKey(Key key)
        {
            switch (key)
            {
                case Key.LeftCtrl:
                case Key.RightCtrl:
                case Key.LeftAlt:
                case Key.RightAlt:
                case Key.LeftShift:
                case Key.RightShift:
                case Key.LWin:
                case Key.RWin:
                case Key.System:
                    return true;
                default:
                    return false;
            }
        }

        private static string KeyToDisplayText(uint virtualKey)
        {
            try
            {
                var key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
                return key == Key.None ? virtualKey.ToString("X2") : key.ToString();
            }
            catch
            {
                return virtualKey.ToString("X2");
            }
        }

        private List<HotkeyBinding> CollectBindings()
        {
            var result = new List<HotkeyBinding>();
            foreach (var row in _rows)
            {
                uint modifiers = 0;
                if (row.CtrlBox.IsChecked == true) modifiers |= 2;
                if (row.AltBox.IsChecked == true) modifiers |= 1;
                if (row.ShiftBox.IsChecked == true) modifiers |= 4;

                var binding = row.Binding;
                var key = KeyInterop.VirtualKeyFromKey(row.CurrentKey);

                result.Add(new HotkeyBinding
                {
                    Id = binding.Id,
                    Label = binding.Label,
                    Modifiers = modifiers,
                    Key = (uint)key,
                });
            }
            return result;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var bindings = CollectBindings();

            // 校验：每个功能都必须至少选一个修饰键
            var invalid = bindings.Find(b => b.Modifiers == 0);
            if (invalid != null)
            {
                _statusText.Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
                _statusText.Text = $"「{invalid.Label}」至少需要勾选一个修饰键，未保存。";
                return;
            }

            if (_plugin.ApplyBindings(bindings, out var message))
            {
                _statusText.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
                _statusText.Text = "已保存并重新注册热键。";
            }
            else
            {
                _statusText.Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
                _statusText.Text = message;
            }
            RebuildRows();
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin.ApplyBindings(BoothButtonsPlugin.CreateDefaultBindings(), out var message))
            {
                _statusText.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
                _statusText.Text = "已恢复默认值并重新注册热键。";
            }
            else
            {
                _statusText.Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
                _statusText.Text = message;
            }
            RebuildRows();
        }
    }
}