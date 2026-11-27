# Regenerate the normalized PNG and eight-frame ICO from the approved source.
# Windows PowerShell 5.1 and System.Drawing; no external imaging dependencies.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class NetworkIconBuilder
{
    public static void Build(string source, string png, string ico)
    {
        using (Bitmap original = new Bitmap(source))
        {
            int width = original.Width, height = original.Height;
            bool[] visible = new bool[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    visible[y * width + x] = original.GetPixel(x, y).A != 0;
            // Keep the largest connected nontransparent component, not detached
            // export speckles. The original PNG is retained unchanged.
            bool[] visited = new bool[visible.Length];
            int[] queue = new int[visible.Length];
            int[] largest = new int[0];
            for (int start = 0; start < visible.Length; start++)
            {
                if (!visible[start] || visited[start]) continue;
                int head = 0, count = 1;
                queue[0] = start; visited[start] = true;
                while (head < count)
                {
                    int p = queue[head++], x = p % width, y = p / width;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                            int next = ny * width + nx;
                            if (!visible[next] || visited[next]) continue;
                            visited[next] = true; queue[count++] = next;
                        }
                }
                if (count > largest.Length)
                {
                    largest = new int[count];
                    Array.Copy(queue, largest, count);
                }
            }
            if (largest.Length == 0) throw new InvalidDataException("The source image is transparent.");
            int left = width, top = height, right = 0, bottom = 0;
            foreach (int p in largest)
            {
                int x = p % width, y = p / width;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
            int side = Math.Max(right - left + 1, bottom - top + 1);
            int padding = Math.Max(1, (int)Math.Ceiling(side * 0.01));
            using (Bitmap square = new Bitmap(side + padding * 2, side + padding * 2, PixelFormat.Format32bppArgb))
            {
                int offsetX = padding + (side - (right - left + 1)) / 2;
                int offsetY = padding + (side - (bottom - top + 1)) / 2;
                foreach (int p in largest)
                {
                    int x = p % width, y = p / width;
                    square.SetPixel(x - left + offsetX, y - top + offsetY, original.GetPixel(x, y));
                }
                square.Save(png, ImageFormat.Png);
                int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };
                byte[][] frames = new byte[sizes.Length][];
                for (int i = 0; i < sizes.Length; i++)
                    using (Bitmap frame = Resize(square, sizes[i])) frames[i] = Dib(frame);
                using (BinaryWriter writer = new BinaryWriter(File.Create(ico)))
                {
                    writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
                    int offset = 6 + 16 * sizes.Length;
                    for (int i = 0; i < sizes.Length; i++)
                    {
                        writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                        writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                        writer.Write((byte)0); writer.Write((byte)0);
                        writer.Write((ushort)1); writer.Write((ushort)32);
                        writer.Write(frames[i].Length); writer.Write(offset);
                        offset += frames[i].Length;
                    }
                    foreach (byte[] frame in frames) writer.Write(frame);
                }
            }
            Console.WriteLine("Source: {0}x{1}; retained bounds: {2},{3}–{4},{5}; frames: 16,20,24,32,48,64,128,256.",
                width, height, left, top, right, bottom);
        }
    }

    private static Bitmap Resize(Bitmap source, int size)
    {
        Bitmap result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(result))
        using (ImageAttributes attributes = new ImageAttributes())
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0,
                source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }
        return result;
    }

    private static byte[] Dib(Bitmap image)
    {
        int size = image.Width, maskStride = ((size + 31) / 32) * 4;
        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            writer.Write(40); writer.Write(size); writer.Write(size * 2);
            writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(0); writer.Write(size * size * 4 + maskStride * size);
            writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
            for (int y = size - 1; y >= 0; y--)
                for (int x = 0; x < size; x++)
                {
                    Color pixel = image.GetPixel(x, y);
                    writer.Write(pixel.B); writer.Write(pixel.G); writer.Write(pixel.R); writer.Write(pixel.A);
                }
            for (int y = size - 1; y >= 0; y--)
            {
                byte[] mask = new byte[maskStride];
                for (int x = 0; x < size; x++)
                    if (image.GetPixel(x, y).A == 0) mask[x / 8] |= (byte)(128 >> (x % 8));
                writer.Write(mask);
            }
            return stream.ToArray();
        }
    }
}
'@
[NetworkIconBuilder]::Build(
    (Join-Path $PSScriptRoot 'NetworkPlugin.Source.png'),
    (Join-Path $PSScriptRoot 'NetworkPlugin.png'),
    (Join-Path $PSScriptRoot 'NetworkPlugin.ico'))
