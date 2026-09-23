using FellowOakDicom;
using FellowOakDicom.Imaging;
using Microsoft.Extensions.DependencyInjection;

namespace UltrasoundApp.Dicom;

/// <summary>
/// fo-dicom needs an <see cref="IImageManager"/> registered before
/// <c>DicomImage.RenderImage()</c> can produce a usable bitmap. This wires
/// up the ImageSharp-backed manager (cross-platform, no GDI/Windows
/// dependency) exactly once per process.
/// </summary>
internal static class FoDicomBootstrapper
{
    private static readonly Lazy<bool> Initialization = new(() =>
    {
        new DicomSetupBuilder()
            .RegisterServices(services => services
                .AddFellowOakDicom()
                .AddImageManager<FellowOakDicom.Imaging.ImageSharpImageManager>())
            .Build();

        return true;
    });

    public static void EnsureInitialized()
    {
        _ = Initialization.Value;
    }
}
