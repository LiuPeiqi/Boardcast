using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Boardcast.Windows;

public partial class MainWindow
{
    private TextBlock SettingText(string text, bool muted = false) => new()
    {
        Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)FindResource(muted ? "Muted" : "Ink"),
        VerticalAlignment = VerticalAlignment.Center
    };

    private StackPanel SettingDescription(string title, string? detail = null)
    {
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var label = SettingText(title); label.FontSize = 14;
        content.Children.Add(label);
        if (!string.IsNullOrEmpty(detail))
        {
            var note = SettingText(detail, true); note.FontSize = 12; note.Margin = new Thickness(0, 4, 0, 0);
            content.Children.Add(note);
        }
        return content;
    }

    private Button SettingAction(string label, Func<Task> callback, bool primary = false, bool quiet = false)
    {
        var button = Action(label, callback);
        button.Style = (Style)FindResource(primary ? "SettingPrimary" : quiet ? "SettingQuiet" : "SettingButton");
        AutomationProperties.SetName(button, label);
        return button;
    }

    private Grid SettingRow(UIElement content, UIElement? trailing = null)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(content);
        if (trailing != null)
        {
            var host = new Border { Child = trailing, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(host, 1); row.Children.Add(host);
        }
        return row;
    }

    private StackPanel SettingGroup(string title, params UIElement[] rows)
    {
        var group = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        if (!string.IsNullOrEmpty(title))
        {
            var heading = SettingText(title); heading.FontWeight = FontWeights.SemiBold;
            heading.Margin = new Thickness(2, 0, 0, 8); group.Children.Add(heading);
        }
        var list = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
            list.Children.Add(new Border
            {
                Child = rows[i], Padding = new Thickness(14, 10, 14, 10),
                BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0)
            });
        group.Children.Add(new Border
        {
            Child = list, Background = Brushes.White, BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8)
        });
        return group;
    }

    private TextBox SettingInput(string label, string value)
    {
        var input = new TextBox { Text = value, Style = (Style)FindResource("SettingInput") };
        AutomationProperties.SetName(input, label);
        return input;
    }

    private CheckBox SettingSwitch(string label, bool value)
    {
        var toggle = new CheckBox { IsChecked = value, Style = (Style)FindResource("SettingSwitch"), ToolTip = label };
        AutomationProperties.SetName(toggle, label);
        return toggle;
    }

    private UIElement SettingField(string label, UIElement control)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(SettingText(label));
        Grid.SetColumn(control, 1); row.Children.Add(control);
        return row;
    }

    private static StackPanel SettingActions(params UIElement[] actions)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < actions.Length; i++)
        {
            if (i > 0 && actions[i] is FrameworkElement element) element.Margin = new Thickness(6, 0, 0, 0);
            panel.Children.Add(actions[i]);
        }
        return panel;
    }
}
