namespace UltrasoundApp.Dicom;

/// <summary>
/// Thrown by <see cref="DicomFileParser.Parse"/> when a file can't be read
/// as DICOM, its metadata can't be extracted, or its pixel data can't be
/// rendered.
///
/// <para>
/// The distinguishing property of this exception is that its
/// <see cref="Exception.Message"/> is written for a sonographer, not a
/// developer: it says what's wrong with the file and what to try, and
/// never contains a stack trace, a tag number or an fo-dicom internal
/// message. Callers can therefore surface <c>ex.Message</c> straight to
/// the UI. The underlying technical exception is preserved as
/// <see cref="Exception.InnerException"/> (and already written to
/// <c>data/logs/</c> by the parser) for diagnosis.
/// </para>
///
/// <para>
/// Catch this specifically — rather than <see cref="Exception"/> — at call
/// sites that show a message to the operator, so that genuinely unexpected
/// bugs stay distinguishable from expected bad-input failures.
/// </para>
/// </summary>
public sealed class DicomParseException : Exception
{
    public DicomParseException(string message) : base(message)
    {
    }

    public DicomParseException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
