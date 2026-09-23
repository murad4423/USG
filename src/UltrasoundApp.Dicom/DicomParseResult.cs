using UltrasoundApp.Core.Models;

namespace UltrasoundApp.Dicom;

/// <summary>
/// In-memory result of parsing a single .dcm file. This is NOT persisted
/// anywhere by this project — the future processing/insert service is
/// responsible for taking this and writing it to the database.
///
/// Reuses the existing <c>UltrasoundApp.Core.Models</c> types since the
/// fields line up directly with what was extracted; this is just a plain
/// data-transfer bundle, not a database entity.
/// </summary>
public sealed class DicomParseResult
{
    /// <summary>Patient metadata extracted from the DICOM file.</summary>
    public required Patient Patient { get; init; }

    /// <summary>Study metadata extracted from the DICOM file.</summary>
    public required Study Study { get; init; }

    /// <summary>
    /// Image metadata, including the on-disk paths of the converted image
    /// and thumbnail this parser just wrote under
    /// <c>data/images-extracted/</c>.
    /// </summary>
    public required Image Image { get; init; }
}
