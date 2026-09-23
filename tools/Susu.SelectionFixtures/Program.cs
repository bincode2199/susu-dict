// SEL01 fixture targets: a WPF TextBox/PasswordBox and a WinForms RichTextBox/password TextBox,
// each preloaded with synthetic marker text. The selection harness selects via real keyboard input.
using System;
using System.Windows;
using System.Windows.Controls;

internal static class Program
{
    public const string Marker = "Su-Su selection marker alpha beta 中文 gamma";

    [STAThread]
    private static int Main(string[] args)
    {
        string framework = args.Length > 0 ? args[0] : "wpf";
        bool password = args.Length > 1 && args[1] == "password";
        return framework == "winforms" ? RunWinForms(password) : RunWpf(password);
    }

    private static int RunWpf(bool password)
    {
        var app = new System.Windows.Application();
        var panel = new StackPanel { Margin = new Thickness(12) };
        var text = new System.Windows.Controls.TextBox { Text = Marker + " (WPF)", AcceptsReturn = true, Height = 80, FontSize = 16 };
        var secret = new PasswordBox { Password = "synthetic-password-123", FontSize = 16, Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(text);
        panel.Children.Add(secret);
        var window = new Window { Title = $"Su-Su SEL fixture WPF {(password ? "password" : "text")}", Width = 520, Height = 220, Content = panel, Left = 100, Top = 100 };
        window.Loaded += (_, _) => { if (password) secret.Focus(); else text.Focus(); };
        return app.Run(window);
    }

    private static int RunWinForms(bool password)
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        var form = new System.Windows.Forms.Form { Text = $"Su-Su SEL fixture WinForms {(password ? "password" : "text")}", Width = 520, Height = 220, StartPosition = System.Windows.Forms.FormStartPosition.Manual, Left = 100, Top = 100 };
        var rich = new System.Windows.Forms.RichTextBox { Text = Marker + " (WinForms RichEdit)", Dock = System.Windows.Forms.DockStyle.Top, Height = 80, Font = new System.Drawing.Font("Segoe UI", 12) };
        var secret = new System.Windows.Forms.TextBox { UseSystemPasswordChar = true, Text = "synthetic-password-123", Dock = System.Windows.Forms.DockStyle.Bottom, Font = new System.Drawing.Font("Segoe UI", 12) };
        form.Controls.Add(rich);
        form.Controls.Add(secret);
        form.Shown += (_, _) => { if (password) secret.Focus(); else rich.Focus(); };
        System.Windows.Forms.Application.Run(form);
        return 0;
    }
}
