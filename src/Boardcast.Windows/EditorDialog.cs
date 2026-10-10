using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Boardcast.Windows;

public class EditorDialog : Window
{
    protected readonly StackPanel _fields = new() { Margin = new Thickness(24, 22, 24, 8) };

    public EditorDialog(Window owner, string title, string confirmLabel = "确定")
    {
        Style = (Style)FindResource(typeof(Window));
        Owner = owner;
        Title = title;
        Width = 590;
        Height = 610;
        MinWidth = 460;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;

        // Keep buttons added by derived editors consistent with the form actions.
        var buttonStyle = new Style(typeof(Button), (Style)FindResource("SettingButton"));
        buttonStyle.Setters.Add(new Setter(MarginProperty, new Thickness(0, 0, 8, 0)));
        Resources[typeof(Button)] = buttonStyle;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new ScrollViewer
        {
            Content = _fields,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(24, 14, 24, 14)
        };
        var cancel = new Button
        {
            Content = "取消",
            IsCancel = true,
            MinWidth = 72,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var save = new Button
        {
            Content = confirmLabel,
            IsDefault = true,
            Style = (Style)FindResource("SettingPrimary"),
            MinWidth = 72,
            Height = 32,
            Margin = new Thickness(0)
        };
        save.Click += (_, _) => { if (TryAccept()) DialogResult = true; };
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        var footer = new Border
        {
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = actions
        };
        Grid.SetRow(footer, 1);
        grid.Children.Add(footer);
        Content = grid;
    }

    protected virtual bool TryAccept() => true;

    public void AddNote(string text) => _fields.Children.Add(new TextBlock
    {
        Text = text,
        Foreground = (Brush)FindResource("Muted"),
        FontSize = 13,
        LineHeight = 20,
        Margin = new Thickness(0, 0, 0, 16),
        TextWrapping = TextWrapping.Wrap
    });

    public TextBox AddText(string label, string value, bool multiline = false)
    {
        AddLabel(label);
        var box = CreateTextBox(label, value, multiline);
        box.Margin = new Thickness(0, 0, 0, 14);
        _fields.Children.Add(box);
        return box;
    }

    public CheckBox AddCheck(string label, bool value)
    {
        var box = new CheckBox
        {
            Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
            IsChecked = value,
            FontSize = 13,
            Margin = new Thickness(0, 2, 0, 14)
        };
        AutomationProperties.SetName(box, label);
        _fields.Children.Add(box);
        return box;
    }

    public ComboBox AddChoice(string label, string[] options, int index)
    {
        AddLabel(label);
        var box = new ComboBox
        {
            ItemsSource = options,
            SelectedIndex = index,
            FontSize = 13,
            Padding = new Thickness(9, 5, 9, 5),
            MinHeight = 32,
            Margin = new Thickness(0, 0, 0, 14)
        };
        AutomationProperties.SetName(box, label);
        _fields.Children.Add(box);
        return box;
    }

    public TextBox AddFolder(string label, string value)
    {
        AddLabel(label);
        var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = CreateTextBox(label, value);
        box.Margin = new Thickness(0, 0, 8, 0);
        var button = new Button
        {
            Content = "选择文件夹",
            Height = 32,
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top
        };
        AutomationProperties.SetName(button, label + "：选择文件夹");
        button.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog();
            if (picker.ShowDialog(this) == true) box.Text = picker.FolderName;
        };
        Grid.SetColumn(button, 1);
        row.Children.Add(box);
        row.Children.Add(button);
        _fields.Children.Add(row);
        return box;
    }

    private void AddLabel(string label) => _fields.Children.Add(new TextBlock
    {
        Text = label,
        FontSize = 13,
        Foreground = (Brush)FindResource("Muted"),
        Margin = new Thickness(0, 0, 0, 5)
    });

    private TextBox CreateTextBox(string label, string value, bool multiline = false)
    {
        var box = new TextBox
        {
            Style = (Style)FindResource("SettingInput"),
            Text = value,
            FontSize = 13,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            Height = multiline ? 96 : 32
        };
        AutomationProperties.SetName(box, label);
        return box;
    }
}
