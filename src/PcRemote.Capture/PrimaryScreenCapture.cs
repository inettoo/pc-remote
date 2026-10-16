using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;
using PcRemote.Contracts;

namespace PcRemote.Capture;

public sealed class PrimaryScreenCapture : IScreenCapture
{
    public Task<ScreenFrame> CaptureAsync(CancellationToken cancellationToken) => Task.Run(CaptureFrame, cancellationToken);

    private static ScreenFrame CaptureFrame()
    {
        Rectangle bounds = Screen.PrimaryScreen?.Bounds ?? throw new IOException("No active primary screen.");
        if (bounds.Width is < 1 or > 8192 || bounds.Height is < 1 or > 8192 || (long)bounds.Width * bounds.Height > 33_177_600)
            throw new IOException("Unsupported primary screen size.");
        int width = Math.Min(1280, bounds.Width);
        int height = Math.Max(1, (int)((long)bounds.Height * width / bounds.Width));
        if (height > 1920) throw new IOException("Unsupported screen aspect ratio.");
        using var source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(source))
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        using var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        }
        using var output = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().Single(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 60L);
        scaled.Save(output, encoder, parameters);
        return new ScreenFrame(width, height, output.ToArray());
    }
}
