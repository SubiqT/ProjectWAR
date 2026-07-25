using System;
using System.IO;
using System.IO.Compression;

namespace FrameWork.Misc
{
    /// <summary>
    /// Minimal PNG decoder for the zone bitmaps shipped with the client data.
    /// System.Drawing is Windows only on .NET 6 and later, and the server only
    /// needs raw channel values, so the image is decoded straight into RGBA bytes.
    /// Supports 8 bit non interlaced grayscale, palette, RGB and RGBA images.
    /// </summary>
    public sealed class PngImage
    {
        private const int BytesPerPixel = 4;

        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        private readonly byte[] _rgba;

        public int Width { get; }

        public int Height { get; }

        private PngImage(int width, int height, byte[] rgba)
        {
            Width = width;
            Height = height;
            _rgba = rgba;
        }

        public static PngImage Load(string path)
        {
            using (FileStream stream = File.OpenRead(path))
                return Load(stream);
        }

        public static PngImage Load(Stream stream)
        {
            var reader = new BigEndianReader(stream);

            foreach (byte expected in Signature)
            {
                if (reader.ReadByte() != expected)
                    throw new InvalidDataException("Not a PNG file.");
            }

            PngHeader header = null;
            byte[] palette = null;

            using (var compressed = new MemoryStream())
            {
                while (true)
                {
                    int length = reader.ReadInt32();
                    string type = reader.ReadChunkType();

                    switch (type)
                    {
                        case "IHDR":
                            header = PngHeader.Read(reader);
                            break;

                        case "PLTE":
                            palette = reader.ReadBytes(length);
                            break;

                        case "IDAT":
                            compressed.Write(reader.ReadBytes(length), 0, length);
                            break;

                        case "IEND":
                            if (header == null)
                                throw new InvalidDataException("PNG is missing its IHDR chunk.");

                            compressed.Position = 0;
                            return Decode(header, palette, compressed);

                        default:
                            reader.Skip(length);
                            break;
                    }

                    // Skip the chunk CRC.
                    reader.Skip(4);
                }
            }
        }

        public byte GetRed(int x, int y) => _rgba[Offset(x, y)];

        public byte GetGreen(int x, int y) => _rgba[Offset(x, y) + 1];

        public byte GetBlue(int x, int y) => _rgba[Offset(x, y) + 2];

        public byte GetAlpha(int x, int y) => _rgba[Offset(x, y) + 3];

        private int Offset(int x, int y)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
                throw new ArgumentOutOfRangeException(nameof(x), $"Pixel {x},{y} is outside the {Width}x{Height} image.");

            return (y * Width + x) * BytesPerPixel;
        }

        /// <summary>Copies a single channel into a flat array, one byte per pixel.</summary>
        public byte[] ExtractChannel(int channel)
        {
            if (channel < 0 || channel >= BytesPerPixel)
                throw new ArgumentOutOfRangeException(nameof(channel));

            byte[] values = new byte[Width * Height];

            for (int i = 0; i < values.Length; i++)
                values[i] = _rgba[i * BytesPerPixel + channel];

            return values;
        }

        private static PngImage Decode(PngHeader header, byte[] palette, Stream compressed)
        {
            int samples = header.SamplesPerPixel;
            int stride = header.Width * samples;
            byte[] raw = new byte[stride * header.Height];

            using (var inflate = new ZLibStream(compressed, CompressionMode.Decompress))
                Unfilter(inflate, raw, header.Height, stride, samples);

            return new PngImage(header.Width, header.Height, ToRgba(header, palette, raw));
        }

        /// <summary>Reverses the per scanline filters defined by the PNG specification.</summary>
        private static void Unfilter(Stream inflate, byte[] raw, int height, int stride, int samples)
        {
            byte[] scanline = new byte[stride];
            byte[] previous = new byte[stride];

            for (int y = 0; y < height; y++)
            {
                int filter = inflate.ReadByte();

                if (filter < 0)
                    throw new InvalidDataException("PNG image data ended early.");

                ReadExactly(inflate, scanline, stride);

                for (int i = 0; i < stride; i++)
                {
                    byte left = i >= samples ? scanline[i - samples] : (byte)0;
                    byte up = previous[i];
                    byte upperLeft = i >= samples ? previous[i - samples] : (byte)0;

                    switch (filter)
                    {
                        case 0:
                            break;
                        case 1:
                            scanline[i] += left;
                            break;
                        case 2:
                            scanline[i] += up;
                            break;
                        case 3:
                            scanline[i] += (byte)((left + up) / 2);
                            break;
                        case 4:
                            scanline[i] += Paeth(left, up, upperLeft);
                            break;
                        default:
                            throw new InvalidDataException($"Unsupported PNG filter type {filter}.");
                    }
                }

                Buffer.BlockCopy(scanline, 0, raw, y * stride, stride);
                Buffer.BlockCopy(scanline, 0, previous, 0, stride);
            }
        }

        private static byte Paeth(byte left, byte up, byte upperLeft)
        {
            int estimate = left + up - upperLeft;
            int distanceLeft = Math.Abs(estimate - left);
            int distanceUp = Math.Abs(estimate - up);
            int distanceUpperLeft = Math.Abs(estimate - upperLeft);

            if (distanceLeft <= distanceUp && distanceLeft <= distanceUpperLeft)
                return left;

            return distanceUp <= distanceUpperLeft ? up : upperLeft;
        }

        private static byte[] ToRgba(PngHeader header, byte[] palette, byte[] raw)
        {
            byte[] rgba = new byte[header.Width * header.Height * BytesPerPixel];
            int samples = header.SamplesPerPixel;

            for (int pixel = 0; pixel < header.Width * header.Height; pixel++)
            {
                int source = pixel * samples;
                int target = pixel * BytesPerPixel;

                switch (header.ColorType)
                {
                    case PngColorType.Grayscale:
                    case PngColorType.GrayscaleAlpha:
                        rgba[target] = rgba[target + 1] = rgba[target + 2] = raw[source];
                        rgba[target + 3] = header.ColorType == PngColorType.GrayscaleAlpha ? raw[source + 1] : byte.MaxValue;
                        break;

                    case PngColorType.Rgb:
                    case PngColorType.Rgba:
                        rgba[target] = raw[source];
                        rgba[target + 1] = raw[source + 1];
                        rgba[target + 2] = raw[source + 2];
                        rgba[target + 3] = header.ColorType == PngColorType.Rgba ? raw[source + 3] : byte.MaxValue;
                        break;

                    case PngColorType.Palette:
                        if (palette == null)
                            throw new InvalidDataException("Palette PNG is missing its PLTE chunk.");

                        int entry = raw[source] * 3;

                        if (entry + 2 >= palette.Length)
                            throw new InvalidDataException($"Palette index {raw[source]} is outside the PLTE chunk.");

                        rgba[target] = palette[entry];
                        rgba[target + 1] = palette[entry + 1];
                        rgba[target + 2] = palette[entry + 2];
                        rgba[target + 3] = byte.MaxValue;
                        break;

                    default:
                        throw new InvalidDataException($"Unsupported PNG colour type {header.ColorType}.");
                }
            }

            return rgba;
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int read = 0;

            while (read < count)
            {
                int chunk = stream.Read(buffer, read, count - read);

                if (chunk <= 0)
                    throw new InvalidDataException("PNG image data ended early.");

                read += chunk;
            }
        }

        private enum PngColorType
        {
            Grayscale = 0,
            Rgb = 2,
            Palette = 3,
            GrayscaleAlpha = 4,
            Rgba = 6
        }

        private sealed class PngHeader
        {
            public int Width { get; private set; }

            public int Height { get; private set; }

            public PngColorType ColorType { get; private set; }

            public int SamplesPerPixel
            {
                get
                {
                    switch (ColorType)
                    {
                        case PngColorType.Grayscale:
                        case PngColorType.Palette:
                            return 1;
                        case PngColorType.GrayscaleAlpha:
                            return 2;
                        case PngColorType.Rgb:
                            return 3;
                        case PngColorType.Rgba:
                            return 4;
                        default:
                            throw new InvalidDataException($"Unsupported PNG colour type {ColorType}.");
                    }
                }
            }

            public static PngHeader Read(BigEndianReader reader)
            {
                var header = new PngHeader
                {
                    Width = reader.ReadInt32(),
                    Height = reader.ReadInt32()
                };

                int bitDepth = reader.ReadByte();
                header.ColorType = (PngColorType)reader.ReadByte();

                int compression = reader.ReadByte();
                int filter = reader.ReadByte();
                int interlace = reader.ReadByte();

                if (bitDepth != 8)
                    throw new InvalidDataException($"Only 8 bit PNG images are supported, this one is {bitDepth} bit.");

                if (compression != 0 || filter != 0)
                    throw new InvalidDataException("PNG uses a non standard compression or filter method.");

                if (interlace != 0)
                    throw new InvalidDataException("Interlaced PNG images are not supported.");

                if (header.Width <= 0 || header.Height <= 0)
                    throw new InvalidDataException($"PNG reports invalid dimensions {header.Width}x{header.Height}.");

                return header;
            }
        }

        private sealed class BigEndianReader
        {
            private readonly Stream _stream;

            public BigEndianReader(Stream stream) => _stream = stream;

            public byte ReadByte()
            {
                int value = _stream.ReadByte();

                if (value < 0)
                    throw new InvalidDataException("PNG file ended unexpectedly.");

                return (byte)value;
            }

            public int ReadInt32()
            {
                return (ReadByte() << 24) | (ReadByte() << 16) | (ReadByte() << 8) | ReadByte();
            }

            public string ReadChunkType()
            {
                char[] type = new char[4];

                for (int i = 0; i < type.Length; i++)
                    type[i] = (char)ReadByte();

                return new string(type);
            }

            public byte[] ReadBytes(int count)
            {
                byte[] buffer = new byte[count];
                ReadExactly(_stream, buffer, count);

                return buffer;
            }

            public void Skip(int count)
            {
                ReadBytes(count);
            }
        }
    }
}
