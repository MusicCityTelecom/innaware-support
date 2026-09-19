using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace InnAwareSupport.Agent;

internal sealed record H264EncodedFrame(byte[] Data, bool KeyFrame, long TimestampMicroseconds);

internal sealed class H264SoftwareEncoder : IDisposable
{
    private const int NeedMoreInputHResult = unchecked((int)0xC00D6D72);
    private const int ProgressiveInterlaceMode = 2;
    private const int BaselineProfile = 66;

    private readonly IMFActivate _activation;
    private readonly IMFTransform _transform;
    private readonly bool _providesSamples;
    private readonly int _outputBufferSize;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private byte[]? _sequenceHeader;
    private bool _disposed;

    public int Width => _width;
    public int Height => _height;
    public int Fps => _fps;
    public string Name { get; }

    private H264SoftwareEncoder(
        IMFActivate activation,
        IMFTransform transform,
        string name,
        int width,
        int height,
        int fps,
        int bitrate)
    {
        _activation = activation;
        _transform = transform;
        Name = name;
        _width = width;
        _height = height;
        _fps = fps;

        ConfigureTransform(transform, width, height, fps, bitrate);

        var streamInfo = transform.GetOutputStreamInfo(0);
        _providesSamples =
            (streamInfo.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
        _outputBufferSize = Math.Max(streamInfo.Size, Math.Max(1 << 20, width * height));

        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    public static H264SoftwareEncoder? TryCreate(
        int width,
        int height,
        int fps,
        int bitrate)
    {
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0)
            return null;

        MediaFoundationRuntime.EnsureStarted();

        var output = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264
        };

        var flags = (uint)(
            EnumFlag.EnumFlagSyncmft |
            EnumFlag.EnumFlagLocalmft |
            EnumFlag.EnumFlagSortandfilter);

        using var candidates = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            flags,
            null,
            output);

        foreach (var candidate in candidates)
        {
            IMFActivate? retained = null;
            IMFTransform? transform = null;
            try
            {
                // MFTEnumEx owns the collection references. Keep our own activation reference
                // by querying through the native pointer before the collection is disposed.
                retained = new IMFActivate(candidate.NativePointer);
                Marshal.AddRef(retained.NativePointer);

                transform = candidate.ActivateObject<IMFTransform>();
                var attributes = transform.Attributes;
                var asyncResult = attributes.GetUInt32(
                    TransformAttributeKeys.TransformAsync,
                    out var isAsync);
                if (asyncResult.Success && isAsync != 0)
                {
                    transform.Dispose();
                    transform = null;
                    retained.Dispose();
                    retained = null;
                    continue;
                }

                var name = ReadFriendlyName(candidate);
                var encoder = new H264SoftwareEncoder(
                    retained,
                    transform,
                    name,
                    width,
                    height,
                    Math.Clamp(fps, 2, 30),
                    Math.Clamp(bitrate, 250_000, 20_000_000));

                retained = null;
                transform = null;
                return encoder;
            }
            catch
            {
                transform?.Dispose();
                if (retained is not null)
                {
                    try { retained.ShutdownObject(); } catch { }
                    retained.Dispose();
                }
            }
        }

        return null;
    }

    public H264EncodedFrame? Encode(Bitmap bitmap, long timestampMicroseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (bitmap.Width != _width || bitmap.Height != _height)
            throw new ArgumentException("Bitmap dimensions do not match the H.264 encoder.");

        using var input = CreateInputSample(bitmap, timestampMicroseconds);
        try
        {
            _transform.ProcessInput(0, input, 0);
        }
        catch (SharpGenException)
        {
            return null;
        }

        var encoded = TryReadOutput(timestampMicroseconds);
        if (encoded is null)
            return null;

        if (_sequenceHeader is null)
            _sequenceHeader = ReadSequenceHeader();

        var annexB = H264AnnexB.Normalize(encoded.Value.Data);
        var keyFrame = encoded.Value.KeyFrame || H264AnnexB.ContainsNalType(annexB, 5);

        if (keyFrame && _sequenceHeader is { Length: > 0 } header &&
            (!H264AnnexB.ContainsNalType(annexB, 7) || !H264AnnexB.ContainsNalType(annexB, 8)))
        {
            var normalizedHeader = H264AnnexB.Normalize(header);
            var combined = new byte[normalizedHeader.Length + annexB.Length];
            Buffer.BlockCopy(normalizedHeader, 0, combined, 0, normalizedHeader.Length);
            Buffer.BlockCopy(annexB, 0, combined, normalizedHeader.Length, annexB.Length);
            annexB = combined;
        }

        return new H264EncodedFrame(annexB, keyFrame, timestampMicroseconds);
    }

    private (byte[] Data, bool KeyFrame)? TryReadOutput(long timestampMicroseconds)
    {
        IMFSample? clientSample = null;
        var output = new OutputDataBuffer
        {
            StreamID = 0,
            Status = 0,
            Sample = null!,
            Events = null!
        };

        try
        {
            if (!_providesSamples)
            {
                clientSample = MediaFactory.MFCreateSample();
                using var buffer = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
                clientSample.AddBuffer(buffer);
                output.Sample = clientSample;
            }

            var result = _transform.ProcessOutput(
                ProcessOutputFlags.None,
                1,
                ref output,
                out _);

            if (result.Failure)
            {
                if (result.Code == NeedMoreInputHResult)
                    return null;

                result.CheckError();
            }

            var sample = output.Sample ?? clientSample;
            if (sample is null || sample.TotalLength <= 0)
                return null;

            using var contiguous = sample.ConvertToContiguousBuffer();
            var length = contiguous.CurrentLength;
            if (length <= 0)
                return null;

            var bytes = new byte[length];
            contiguous.Lock(out var source, out _, out _);
            try
            {
                Marshal.Copy(source, bytes, 0, length);
            }
            finally
            {
                contiguous.Unlock();
            }

            var cleanPoint =
                sample.GetUInt32(SampleAttributeKeys.CleanPoint, out var clean).Success &&
                clean != 0;

            return (bytes, cleanPoint);
        }
        finally
        {
            output.Events?.Dispose();

            if (output.Sample is not null &&
                !ReferenceEquals(output.Sample, clientSample))
                output.Sample.Dispose();

            clientSample?.Dispose();
        }
    }

    private IMFSample CreateInputSample(Bitmap source, long timestampMicroseconds)
    {
        var nv12 = ConvertToNv12(source);

        var sample = MediaFactory.MFCreateSample();
        IMFMediaBuffer? buffer = null;
        try
        {
            buffer = MediaFactory.MFCreateMemoryBuffer(nv12.Length);
            buffer.Lock(out var destination, out _, out _);
            try
            {
                Marshal.Copy(nv12, 0, destination, nv12.Length);
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.CurrentLength = nv12.Length;
            sample.AddBuffer(buffer);
            sample.SampleTime = timestampMicroseconds * 10;
            sample.SampleDuration = 10_000_000L / _fps;
            return sample;
        }
        catch
        {
            sample.Dispose();
            throw;
        }
        finally
        {
            buffer?.Dispose();
        }
    }

    private static void ConfigureTransform(
        IMFTransform transform,
        int width,
        int height,
        int fps,
        int bitrate)
    {
        using var output = MediaFactory.MFCreateMediaType();
        SetVideoType(output, VideoFormatGuids.H264, width, height, fps);
        output.Set(MediaTypeAttributeKeys.AvgBitrate, checked((uint)bitrate)).CheckError();
        output.Set(MediaTypeAttributeKeys.Mpeg2Profile, checked((uint)BaselineProfile)).CheckError();
        transform.SetOutputType(0, output, 0);

        using var input = MediaFactory.MFCreateMediaType();
        SetVideoType(input, VideoFormatGuids.NV12, width, height, fps);
        transform.SetInputType(0, input, 0);
    }

    private static void SetVideoType(
        IMFMediaType mediaType,
        Guid subtype,
        int width,
        int height,
        int fps)
    {
        mediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        mediaType.Set(MediaTypeAttributeKeys.Subtype, subtype).CheckError();
        MediaFactory.MFSetAttributeSize(
            mediaType,
            MediaTypeAttributeKeys.FrameSize,
            checked((uint)width),
            checked((uint)height)).CheckError();
        MediaFactory.MFSetAttributeRatio(
            mediaType,
            MediaTypeAttributeKeys.FrameRate,
            checked((uint)fps),
            1).CheckError();
        MediaFactory.MFSetAttributeRatio(
            mediaType,
            MediaTypeAttributeKeys.PixelAspectRatio,
            1,
            1).CheckError();
        mediaType.Set(
            MediaTypeAttributeKeys.InterlaceMode,
            checked((uint)ProgressiveInterlaceMode)).CheckError();
    }

    private byte[]? ReadSequenceHeader()
    {
        try
        {
            using var output = _transform.GetOutputCurrentType(0);
            return output.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadFriendlyName(IMFActivate activation)
    {
        try
        {
            return activation.GetString(
                TransformAttributeKeys.MftFriendlyNameAttribute);
        }
        catch
        {
            return "Media Foundation H.264 encoder";
        }
    }

    private static unsafe byte[] ConvertToNv12(Bitmap bitmap)
    {
        using var rgba = Ensure32Bpp(bitmap);
        var rect = new Rectangle(0, 0, rgba.Width, rgba.Height);
        var data = rgba.LockBits(
            rect,
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var width = rgba.Width;
            var height = rgba.Height;
            var ySize = width * height;
            var output = new byte[ySize + ySize / 2];
            var srcBase = data.Scan0;

            Parallel.For(0, height, y =>
            {
                var row = (byte*)srcBase + y * data.Stride;
                var yRow = y * width;
                for (var x = 0; x < width; x++)
                {
                    var p = row + x * 4;
                    var b = p[0];
                    var g = p[1];
                    var r = p[2];
                    var value = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                    output[yRow + x] = (byte)Math.Clamp(value, 0, 255);
                }
            });

            Parallel.For(0, height / 2, yHalf =>
            {
                var y = yHalf * 2;
                var row0 = (byte*)srcBase + y * data.Stride;
                var row1 = (byte*)srcBase + Math.Min(y + 1, height - 1) * data.Stride;
                var uvRow = ySize + yHalf * width;

                for (var x = 0; x < width; x += 2)
                {
                    var p00 = row0 + x * 4;
                    var p01 = row0 + Math.Min(x + 1, width - 1) * 4;
                    var p10 = row1 + x * 4;
                    var p11 = row1 + Math.Min(x + 1, width - 1) * 4;

                    var b = (p00[0] + p01[0] + p10[0] + p11[0]) >> 2;
                    var g = (p00[1] + p01[1] + p10[1] + p11[1]) >> 2;
                    var r = (p00[2] + p01[2] + p10[2] + p11[2]) >> 2;

                    var u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                    var v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;

                    output[uvRow + x] = (byte)Math.Clamp(u, 0, 255);
                    output[uvRow + x + 1] = (byte)Math.Clamp(v, 0, 255);
                }
            });

            return output;
        }
        finally
        {
            rgba.UnlockBits(data);
        }
    }

    private static Bitmap Ensure32Bpp(Bitmap source)
    {
        var copy = new Bitmap(
            source.Width & ~1,
            source.Height & ~1,
            PixelFormat.Format32bppArgb);

        using var graphics = Graphics.FromImage(copy);
        graphics.DrawImage(
            source,
            new Rectangle(0, 0, copy.Width, copy.Height),
            new Rectangle(0, 0, copy.Width, copy.Height),
            GraphicsUnit.Pixel);

        return copy;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _transform.ProcessMessage(
                TMessageType.MessageNotifyEndOfStream,
                UIntPtr.Zero);
            _transform.ProcessMessage(
                TMessageType.MessageNotifyEndStreaming,
                UIntPtr.Zero);
        }
        catch { }

        _transform.Dispose();
        try { _activation.ShutdownObject(); } catch { }
        _activation.Dispose();
    }
}
