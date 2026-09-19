using Vortice.MediaFoundation;

namespace InnAwareSupport.Agent;

internal sealed record H264Capability(bool Available, bool Hardware);

internal static class H264CapabilityProbe
{
    private static readonly Lazy<H264Capability> Cached = new(Probe);

    public static H264Capability Current => Cached.Value;

    private static H264Capability Probe()
    {
        try
        {
            MediaFactory.MFStartup(false).CheckError();
            try
            {
                var output = new RegisterTypeInfo
                {
                    GuidMajorType = MediaTypeGuids.Video,
                    GuidSubtype = VideoFormatGuids.H264
                };

                var hardwareFlags = (uint)(
                    EnumFlag.EnumFlagHardware |
                    EnumFlag.EnumFlagSortandfilter);

                using (var hardware = MediaFactory.MFTEnumEx(
                    TransformCategoryGuids.VideoEncoder,
                    hardwareFlags,
                    null,
                    output))
                {
                    if (hardware.Any())
                        return new H264Capability(true, true);
                }

                var fallbackFlags = (uint)(
                    EnumFlag.EnumFlagSyncmft |
                    EnumFlag.EnumFlagAsyncmft |
                    EnumFlag.EnumFlagLocalmft |
                    EnumFlag.EnumFlagSortandfilter);

                using var fallback = MediaFactory.MFTEnumEx(
                    TransformCategoryGuids.VideoEncoder,
                    fallbackFlags,
                    null,
                    output);

                return new H264Capability(fallback.Any(), false);
            }
            finally
            {
                MediaFactory.MFShutdown();
            }
        }
        catch
        {
            return new H264Capability(false, false);
        }
    }
}
