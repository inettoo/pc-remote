using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PcRemote.Contracts;
using PcRemote.Controller;
using PcRemote.Protocol;

namespace PcRemote.App;

public partial class RemoteWindow : Window
{
    private readonly RemoteDesktopClient client;
    private readonly DispatcherTimer renderTimer;
    private DesktopUpdate? pending;
    private DesktopImage? current;
    private long lastFrame, lastMove;
    private bool connected, staleReported;
    private int mouseButtons;

    public RemoteWindow(RemoteDesktopClient client, DiscoveredDevice device)
    {
        InitializeComponent();
        this.client = client;
        DeviceText.Text = device.Name;
        Title = $"{device.Name} · PC Remote";
        renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Render, Render, Dispatcher);
        Loaded += async (_, _) => await client.RunAsync(device.Address, update => Interlocked.Exchange(ref pending, update));
        Deactivated += (_, _) =>
        {
            client.TryInput(new(RemoteInputKind.ReleaseAll));
            mouseButtons = 0;
            if (Mouse.Captured == ScreenImage) Mouse.Capture(null);
        };
        Closed += async (_, _) =>
        {
            renderTimer.Stop();
            await client.DisposeAsync();
        };
    }

    private void Render(object? sender, EventArgs args)
    {
        var update = Interlocked.Exchange(ref pending, null);
        if (update is not null)
        {
            connected = update.Connected;
            StatusText.Text = update.Status;
            current = update.Image;
            if (current is not null)
            {
                try
                {
                    using var headerStream = new MemoryStream(current.Jpeg, writable: false);
                    var decoder = BitmapDecoder.Create(headerStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth != current.Width || decoder.Frames[0].PixelHeight != current.Height)
                        throw new InvalidDataException("Unexpected JPEG dimensions.");
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = Math.Min(1280, current.Width);
                    image.StreamSource = new MemoryStream(current.Jpeg, writable: false);
                    image.EndInit();
                    image.Freeze();
                    ScreenImage.Source = image;
                    Overlay.Visibility = Visibility.Collapsed;
                    lastFrame = Stopwatch.GetTimestamp();
                    staleReported = false;
                }
                catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException)
                {
                    client.Stop();
                    current = null;
                    Overlay.Text = "无法解码远程画面，连接已停止。";
                    Overlay.Visibility = Visibility.Visible;
                }
            }
            else
            {
                ScreenImage.Source = null;
                Overlay.Text = update.Status;
                Overlay.Visibility = Visibility.Visible;
            }
        }
        if (current is not null && Stopwatch.GetElapsedTime(lastFrame) > TimeSpan.FromSeconds(2))
        {
            if (!staleReported) { client.TryInput(new(RemoteInputKind.ReleaseAll)); staleReported = true; }
            Overlay.Text = "正在等待新的远程画面…";
            Overlay.Visibility = Visibility.Visible;
        }
    }

    private bool Coordinates(MouseEventArgs args, out int x, out int y)
    {
        x = y = 0;
        if (!IsActive || !connected || current is null || Stopwatch.GetElapsedTime(lastFrame) > TimeSpan.FromSeconds(2)) return false;
        double scale = Math.Min(ScreenImage.ActualWidth / current.Width, ScreenImage.ActualHeight / current.Height);
        if (scale <= 0) return false;
        var point = args.GetPosition(ScreenImage);
        double width = current.Width * scale, height = current.Height * scale;
        double left = (ScreenImage.ActualWidth - width) / 2, top = (ScreenImage.ActualHeight - height) / 2;
        if (mouseButtons == 0 && (point.X < left || point.X > left + width || point.Y < top || point.Y > top + height)) return false;
        x = (int)Math.Clamp((point.X - left) * 65535 / width, 0, 65535);
        y = (int)Math.Clamp((point.Y - top) * 65535 / height, 0, 65535);
        return true;
    }

    private void OnMouseMove(object sender, MouseEventArgs args)
    {
        if (Stopwatch.GetElapsedTime(lastMove) < TimeSpan.FromMilliseconds(33)) return;
        if (Coordinates(args, out int x, out int y))
        {
            lastMove = Stopwatch.GetTimestamp();
            client.TryInput(new(RemoteInputKind.Move, x, y));
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (!Coordinates(args, out int x, out int y)) return;
        ScreenImage.Focus();
        client.TryInput(new(RemoteInputKind.Move, x, y));
        if (args.ChangedButton == MouseButton.Left) { client.TryInput(new(RemoteInputKind.LeftDown)); mouseButtons |= 1; }
        if (args.ChangedButton == MouseButton.Right) { client.TryInput(new(RemoteInputKind.RightDown)); mouseButtons |= 2; }
        if (mouseButtons != 0) Mouse.Capture(ScreenImage);
        args.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton == MouseButton.Left) { client.TryInput(new(RemoteInputKind.LeftUp)); mouseButtons &= ~1; }
        if (args.ChangedButton == MouseButton.Right) { client.TryInput(new(RemoteInputKind.RightUp)); mouseButtons &= ~2; }
        if (mouseButtons == 0) Mouse.Capture(null);
        args.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (Coordinates(args, out _, out _)) client.TryInput(new(RemoteInputKind.Wheel, Delta: Math.Clamp(args.Delta, -1200, 1200) / 120 * 120));
        args.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs args) => Key(args, RemoteInputKind.KeyDown);
    private void OnKeyUp(object sender, KeyEventArgs args) => Key(args, RemoteInputKind.KeyUp);
    private void Key(KeyEventArgs args, RemoteInputKind kind)
    {
        if (!ScreenImage.IsKeyboardFocused) return;
        int key = KeyInterop.VirtualKeyFromKey(args.Key == System.Windows.Input.Key.System ? args.SystemKey : args.Key);
        if (key is >= 1 and <= 254) client.TryInput(new(kind, Key: key));
        args.Handled = true;
    }

    private void DisconnectClick(object sender, RoutedEventArgs args) => Close();
}
