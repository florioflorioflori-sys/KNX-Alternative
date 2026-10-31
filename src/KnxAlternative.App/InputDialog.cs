using System.Windows;
using System.Windows.Controls;

namespace KnxAlternative.App;

/// <summary>Small modal dialog with one or more labelled text fields.</summary>
public sealed class InputDialog : Window
{
    readonly List<TextBox> _boxes = new();

    InputDialog(string title, IReadOnlyList<(string Label, string Default)> fields, Window owner, bool password)
    {
        Title = title;
        Owner = owner;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(14), MinWidth = 340 };
        foreach (var (label, def) in fields)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
            var tb = new TextBox { Text = def, Margin = new Thickness(0, 0, 0, 10) };
            _boxes.Add(tb);
            if (password)
            {
                var pb = new PasswordBox { Margin = new Thickness(0, 0, 0, 10) };
                pb.PasswordChanged += (_, _) => tb.Text = pb.Password;
                panel.Children.Add(pb);
            }
            else panel.Children.Add(tb);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, MinWidth = 80 };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) =>
        {
            if (panel.Children.OfType<Control>().FirstOrDefault(c => c is TextBox or PasswordBox) is { } first) first.Focus();
        };
    }

    public static string? Ask(Window owner, string title, string label, string def = "", bool password = false)
    {
        var d = new InputDialog(title, [(label, def)], owner, password);
        return d.ShowDialog() == true ? d._boxes[0].Text.Trim() : null;
    }

    public static string[]? AskMany(Window owner, string title, params (string Label, string Default)[] fields)
    {
        var d = new InputDialog(title, fields, owner, false);
        return d.ShowDialog() == true ? d._boxes.Select(b => b.Text.Trim()).ToArray() : null;
    }
}
