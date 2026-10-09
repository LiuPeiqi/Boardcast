using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Boardcast.Core;

namespace Boardcast.Windows;

public partial class MainWindow
{
    private void RenderRulesContent()
    {
        RuleContent.Children.Clear();
        var rules = _snapshot.Settings.Rules;
        var header = SettingRow(
            SettingDescription($"{rules.Count} 条接收规则", "按顺序匹配，未匹配时询问；自动接收需设备信任与许可。"),
            SettingAction("新建规则", () => EditRuleAsync(null)));
        header.Margin = new Thickness(0, 0, 0, 14);
        RuleContent.Children.Add(header);

        if (rules.Count == 0)
        {
            RuleContent.Children.Add(SettingGroup("",
                SettingDescription("尚未添加规则", "收到文件时将逐次询问。可按内容类型设置接收方式和目录。")));
            return;
        }

        var rows = new List<UIElement>();
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var content = new StackPanel();
            var name = SettingText($"{i + 1:00}  {rule.Name}");
            name.FontSize = 14;
            name.FontWeight = FontWeights.Medium;
            name.TextWrapping = TextWrapping.NoWrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.ToolTip = rule.Name;

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var edit = SettingAction("编辑", () => EditRuleAsync(rule), quiet: true);
            AutomationProperties.SetName(edit, $"编辑规则：{rule.Name}");
            actions.Children.Add(edit);
            actions.Children.Add(RuleMoreAction(rule, i));
            content.Children.Add(SettingRow(name, actions));

            var criteria = SettingText((rule.Enabled ? "" : "已停用 · ") + $"{ScopeLabel(rule.Scope)} · " +
                (rule.Extensions.Count == 0 ? "所有后缀" : string.Join(", ", rule.Extensions)) +
                (rule.FolderNamePattern.Length > 0 ? $" · 文件夹：{rule.FolderNamePattern}" : ""), true);
            criteria.FontSize = 12;
            criteria.Margin = new Thickness(0, 1, 0, 0);
            content.Children.Add(criteria);

            var destination = SettingText((rule.AutoAccept ? "允许受信设备自动接收" : "每次确认") +
                " · " + (rule.Destination.Length == 0 ? "默认接收目录" : rule.Destination), true);
            destination.FontSize = 12;
            destination.Margin = new Thickness(0, 3, 0, 0);
            content.Children.Add(destination);
            rows.Add(content);
        }
        RuleContent.Children.Add(SettingGroup("", rows.ToArray()));
    }

    private MenuItem RuleMenuAction(string label, string ruleName, Func<Task> callback)
    {
        var item = new MenuItem { Header = label };
        AutomationProperties.SetName(item, $"{label}规则：{ruleName}");
        item.Click += async (_, _) =>
        {
            item.IsEnabled = false;
            try { await callback(); }
            catch (Exception ex) { SetStatus(ex.Message); }
            finally { item.IsEnabled = true; }
        };
        return item;
    }

    private Button RuleMoreAction(ReceiveRule rule, int index)
    {
        var menu = new ContextMenu();
        var moveUp = RuleMenuAction("上移", rule.Name, async () =>
        {
            var settings = CloneSettings();
            var currentIndex = settings.Rules.FindIndex(item => item.Id == rule.Id);
            if (currentIndex <= 0) return;
            (settings.Rules[currentIndex - 1], settings.Rules[currentIndex]) =
                (settings.Rules[currentIndex], settings.Rules[currentIndex - 1]);
            if (await SaveSettingsAsync(settings)) RenderRules();
        });
        moveUp.IsEnabled = index > 0;
        menu.Items.Add(moveUp);

        var delete = RuleMenuAction("删除", rule.Name, async () =>
        {
            var settings = CloneSettings();
            settings.Rules.RemoveAll(item => item.Id == rule.Id);
            if (await SaveSettingsAsync(settings)) RenderRules();
        });
        menu.Items.Add(delete);
        menu.Opened += (_, _) => Keyboard.Focus(moveUp.IsEnabled ? moveUp : delete);

        var more = SettingAction("···", () =>
        {
            menu.IsOpen = true;
            return Task.CompletedTask;
        }, quiet: true);
        more.Width = 32;
        more.Padding = new Thickness(0);
        more.Margin = new Thickness(4, 0, 0, 0);
        more.ToolTip = "更多操作";
        more.ContextMenu = menu;
        menu.Placement = PlacementMode.Bottom;
        menu.PlacementTarget = more;
        AutomationProperties.SetName(more, $"规则“{rule.Name}”的更多操作");
        more.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Down) return;
            menu.IsOpen = true;
            e.Handled = true;
        };
        return more;
    }
}
