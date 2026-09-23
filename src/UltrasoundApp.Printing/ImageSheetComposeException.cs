namespace UltrasoundApp.Printing;

/// <summary>
/// Thrown by <see cref="ImageSheetComposer.Compose"/> when the PDF itself
/// could not be generated (as opposed to the input validation failures,
/// which stay as <see cref="ArgumentException"/>/<see cref="FileNotFoundException"/>).
///
/// <para>
/// Like <c>UltrasoundApp.Dicom.DicomParseException</c>, the
/// <see cref="Exception.Message"/> here is written for a sonographer and is
/// safe to show directly in the UI; the technical cause is preserved as
/// <see cref="Exception.InnerException"/> and already written to
/// <c>data/logs/</c> by the composer.
/// </para>
/// </summary>
public sealed class ImageSheetComposeException : Exception
{
    public ImageSheetComposeException(string message) : base(message)
    {
    }

    public ImageSheetComposeException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
