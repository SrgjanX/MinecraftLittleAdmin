using System.ComponentModel;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MinecraftLittleAdmin;

public partial class MainWindow : Window
{
    private const int PreferredPort = 3000;
    private const int PortAttempts = 10;

    private static readonly Dictionary<string, string> FriendlyMessages = new()
    {
        ["time set day"] = "Good morning! The sun is up!",
        ["time set night"] = "Good night! Watch out for mobs!",
        ["weather rain"] = "Splish splash, here comes the rain!",
        ["weather clear"] = "Bye-bye rain! Clear skies!",
        ["weather thunder"] = "Boom! A big storm is coming!",
        ["gamemode survival @s"] = "Survival mode! Be brave and stay safe!",
        ["gamemode creative @s"] = "Creative mode! Build anything you want!",
    };

    private MinecraftBridge? _bridge;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _bridge = StartBridge();
        if (_bridge == null)
        {
            StatusText.Text = "Can't start";
            ShowFeedback($"Ports {PreferredPort}-{PreferredPort + PortAttempts - 1} are all busy. Close other apps and try again.");
            SetButtonsEnabled(false);
            return;
        }

        _bridge.ConnectionChanged += connected => Dispatcher.Invoke(() => UpdateConnection(connected));
        ConnectCommandText.Text = $"/connect 127.0.0.1:{_bridge.Port}";
        UpdateConnection(false);
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_bridge != null)
            await _bridge.DisposeAsync();
    }

    private static MinecraftBridge? StartBridge()
    {
        for (var port = PreferredPort; port < PreferredPort + PortAttempts; port++)
        {
            var bridge = new MinecraftBridge(port);
            try
            {
                bridge.Start();
                return bridge;
            }
            catch (SocketException)
            {
                // Port in use, try the next one.
            }
        }
        return null;
    }

    private void UpdateConnection(bool connected)
    {
        StatusPill.Background = new SolidColorBrush(connected
            ? Color.FromRgb(0x43, 0xA0, 0x47)
            : Color.FromRgb(0xE5, 0x39, 0x35));
        StatusText.Text = connected ? "Connected" : "Not connected";
        HelpPanel.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        ConnectedHint.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        SetButtonsEnabled(connected);

        if (connected)
            ShowFeedback("Hooray! Minecraft is connected!");
    }

    private void SetButtonsEnabled(bool enabled)
    {
        foreach (var button in new[] { DayButton, NightButton, RainButton, ClearButton, StormButton, SurvivalButton, CreativeButton })
            button.IsEnabled = enabled;
    }

    private async void WorldButton_Click(object sender, RoutedEventArgs e)
    {
        if (_bridge == null || sender is not Button { Tag: string command })
            return;

        var result = await _bridge.SendCommandAsync(command);
        if (result.Success)
            ShowFeedback(FriendlyMessages.GetValueOrDefault(command, "Done!"));
        else
            ShowFeedback($"Oops! {result.Message}");
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_bridge == null)
            return;

        var hwnd = MinecraftWindow.Find();
        if (hwnd == IntPtr.Zero)
        {
            ShowFeedback("Start Minecraft and open your world first!");
            return;
        }

        ConnectButton.IsEnabled = false;
        try
        {
            // Minecraft may show its pause menu when it gets focus back, so if the first
            // try doesn't connect, close that menu with Esc and try once more.
            foreach (var closeMenuFirst in new[] { false, true })
            {
                await MinecraftWindow.SendChatCommandAsync(hwnd, ConnectCommandText.Text, closeMenuFirst);
                if (await WaitForConnectionAsync(TimeSpan.FromSeconds(4)))
                    return;
            }

            ShowFeedback("Couldn't connect. Make sure you're in your world with cheats on.");
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private async Task<bool> WaitForConnectionAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_bridge?.IsConnected == true)
                return true;
            await Task.Delay(100);
        }
        return false;
    }

    private void ShowFeedback(string message)
    {
        FeedbackText.Text = message;

        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.15))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.6))));
        FeedbackBubble.BeginAnimation(OpacityProperty, fade);
    }
}
