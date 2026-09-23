using System.Globalization;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.ImageSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using UltrasoundApp.Core.Logging;
using UltrasoundApp.Core.Models;
using CoreImage = UltrasoundApp.Core.Models.Image;

namespace UltrasoundApp.Dicom;

/// <summary>
/// Parses a single DICOM (.dcm) file: extracts patient/study/image
/// metadata and converts its pixel data to a viewable image file plus a
/// thumbnail on disk under <c>data/images-extracted/</c>.
///
/// This is parsing/extraction only — nothing here touches the database,
/// builds UI, or performs the insert workflow. That is a later step.
/// </summary>
public sealed class DicomFileParser
{
    internal const string LogCategory = "DICOM";

    /// <summary>
    /// Bounding box (longest side) each thumbnail is resized to. Raised
    /// from the original 200px so the Patient Detail page's live print
    /// preview (which reuses these same thumbnail files rather than
    /// loading full-resolution images, so toggling a selection stays
    /// instant) looks reasonably sharp instead of visibly soft/pixelated.
    /// 480px is a deliberate middle ground: still small enough to load
    /// instantly and keep the images-list/preview panes responsive, but
    /// large enough to hold up in the preview grid's larger cells (e.g. a
    /// 1×1 or 2×2 layout). Only affects images extracted after this
    /// change — studies already on disk keep their existing 200px
    /// thumbnails until re-extracted.
    /// </summary>
    private const int ThumbnailMaxDimension = 480;

    /// <summary>
    /// Parses <paramref name="dicomFilePath"/>, writes the rendered image
    /// and a thumbnail under <c>data/images-extracted/{StudyInstanceUID}/</c>,
    /// and returns the extracted metadata plus the paths written.
    /// </summary>
    public DicomParseResult Parse(string dicomFilePath)
    {
        if (!File.Exists(dicomFilePath))
        {
            AppLog.Error(LogCategory, $"DICOM file not found: '{dicomFilePath}'.");
            throw new DicomParseException($"The file could not be found:\n{dicomFilePath}");
        }

        FoDicomBootstrapper.EnsureInitialized();

        DicomFile dicomFile;
        try
        {
            dicomFile = DicomFile.Open(dicomFilePath);
        }
        catch (Exception ex)
        {
            // By far the most common real-world failure: a file that isn't
            // DICOM at all (a JPEG or a renamed .txt), or a truncated
            // transfer. fo-dicom's own message ("Unable to read preamble",
            // etc.) is meaningless to a sonographer, so translate it here
            // and keep the technical detail in the log.
            AppLog.Error(LogCategory, $"Failed to open DICOM file '{dicomFilePath}'.", ex);
            throw new DicomParseException(
                $"'{Path.GetFileName(dicomFilePath)}' is not a valid DICOM file, or is damaged. " +
                "Check that it came from the ultrasound machine and was copied completely.", ex);
        }

        DicomDataset dataset = dicomFile.Dataset;

        Patient patient;
        Study study;
        string sopInstanceUid;
        try
        {
            patient = ExtractPatient(dataset);
            study = ExtractStudy(dataset);
            sopInstanceUid = dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategory, $"Failed to read metadata from '{dicomFilePath}'.", ex);
            throw new DicomParseException(
                $"'{Path.GetFileName(dicomFilePath)}' opened, but its patient/study details could not be read. " +
                "The file may be incomplete or use an unsupported format.", ex);
        }

        // A study with no StudyInstanceUID can't be grouped, stored or
        // reprinted — catching it here gives a clear message instead of a
        // confusing foreign-key failure two layers down in the repository.
        if (string.IsNullOrWhiteSpace(study.StudyInstanceUID))
        {
            AppLog.Error(LogCategory, $"'{dicomFilePath}' has no StudyInstanceUID — cannot store it.");
            throw new DicomParseException(
                $"'{Path.GetFileName(dicomFilePath)}' is missing its Study Instance UID, " +
                "so it cannot be filed against a study. The file may be damaged.");
        }

        string imagePath;
        string thumbnailPath;
        try
        {
            (imagePath, thumbnailPath) = RenderAndSaveImages(dataset, study.StudyInstanceUID, sopInstanceUid);
        }
        catch (Exception ex)
        {
            // Distinct from the metadata failures above: the file is valid
            // DICOM but its pixel data can't be rendered (unsupported
            // transfer syntax / compression), or the extracted-images
            // folder isn't writable.
            AppLog.Error(LogCategory, $"Failed to render pixel data from '{dicomFilePath}'.", ex);
            throw new DicomParseException(
                $"The images in '{Path.GetFileName(dicomFilePath)}' could not be extracted. " +
                "The file may use a compression format this app doesn't support, " +
                "or the images folder may not be writable.", ex);
        }

        var image = new CoreImage
        {
            SOPInstanceUID = sopInstanceUid,
            StudyInstanceUID = study.StudyInstanceUID,
            FilePath = imagePath,
            ThumbnailPath = thumbnailPath
        };

        AppLog.Info(
            LogCategory,
            $"Parsed '{Path.GetFileName(dicomFilePath)}': patient '{patient.PatientID}', " +
            $"study '{study.StudyInstanceUID}', SOP '{sopInstanceUid}'.");

        return new DicomParseResult
        {
            Patient = patient,
            Study = study,
            Image = image
        };
    }

    private static Patient ExtractPatient(DicomDataset dataset)
    {
        string patientId = dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);
        string patientName = dataset.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty);
        string sex = dataset.GetSingleValueOrDefault(DicomTag.PatientSex, string.Empty);
        string ageString = dataset.GetSingleValueOrDefault(DicomTag.PatientAge, string.Empty);
        string dobString = dataset.GetSingleValueOrDefault(DicomTag.PatientBirthDate, string.Empty);

        return new Patient
        {
            PatientID = patientId,
            PatientName = patientName,
            Sex = string.IsNullOrWhiteSpace(sex) ? null : sex,
            DateOfBirth = ParseDicomDate(dobString),
            Age = ParseAge(ageString)
        };
    }

    private static Study ExtractStudy(DicomDataset dataset)
    {
        string studyInstanceUid = dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);
        string patientId = dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);
        string studyDateString = dataset.GetSingleValueOrDefault(DicomTag.StudyDate, string.Empty);
        string studyTimeString = dataset.GetSingleValueOrDefault(DicomTag.StudyTime, string.Empty);

        // No single DICOM tag maps cleanly to "ExamType". StudyDescription
        // is the closest standard fit (e.g. "Abdomen US", "OB Ultrasound");
        // fall back to Modality if the study wasn't described.
        string examType = dataset.GetSingleValueOrDefault(DicomTag.StudyDescription, string.Empty);
        if (string.IsNullOrWhiteSpace(examType))
        {
            examType = dataset.GetSingleValueOrDefault(DicomTag.Modality, string.Empty);
        }

        return new Study
        {
            StudyInstanceUID = studyInstanceUid,
            PatientID = patientId,
            ExamType = examType,
            StudyDateTime = ParseDicomDateTime(studyDateString, studyTimeString),
            // Status is a workflow concept, not something DICOM encodes.
            // Left blank here for the future processing/insert service to set.
            Status = string.Empty
        };
    }

    /// <summary>
    /// Renders the dataset's pixel data (first frame) to a PNG, plus a
    /// bounded-size thumbnail PNG, under
    /// <c>data/images-extracted/{studyInstanceUid}/</c>.
    /// </summary>
    private static (string ImagePath, string ThumbnailPath) RenderAndSaveImages(
        DicomDataset dataset, string studyInstanceUid, string sopInstanceUid)
    {
        string studyFolderName = string.IsNullOrWhiteSpace(studyInstanceUid) ? "unknown-study" : studyInstanceUid;
        string fileBaseName = string.IsNullOrWhiteSpace(sopInstanceUid) ? Guid.NewGuid().ToString("n") : sopInstanceUid;

        string studyDirectory = Path.Combine(ImageStoragePaths.GetImagesExtractedDirectory(), studyFolderName);
        Directory.CreateDirectory(studyDirectory);

        string imagePath = Path.Combine(studyDirectory, $"{fileBaseName}.png");
        string thumbnailPath = Path.Combine(studyDirectory, $"{fileBaseName}_thumb.png");

        var dicomImage = new DicomImage(dataset);
        FellowOakDicom.Imaging.IImage rendered = dicomImage.RenderImage();

        using SixLabors.ImageSharp.Image sharpImage = rendered.AsSharpImage();
        sharpImage.SaveAsPng(imagePath);

        using SixLabors.ImageSharp.Image thumbnail = sharpImage.Clone(context => context.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max,
            Size = new Size(ThumbnailMaxDimension, ThumbnailMaxDimension)
        }));
        thumbnail.SaveAsPng(thumbnailPath);

        return (imagePath, thumbnailPath);
    }

    /// <summary>Parses a DICOM DA-format date string (yyyyMMdd), or null if absent/invalid.</summary>
    private static DateTime? ParseDicomDate(string? dateString)
    {
        if (string.IsNullOrWhiteSpace(dateString))
        {
            return null;
        }

        return DateTime.TryParseExact(
            dateString, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
            ? parsed
            : null;
    }

    /// <summary>Combines DICOM DA (date) + TM (time) format strings into a single DateTime.</summary>
    private static DateTime ParseDicomDateTime(string? dateString, string? timeString)
    {
        DateTime date = ParseDicomDate(dateString) ?? DateTime.MinValue;

        TimeSpan time = TimeSpan.Zero;
        if (!string.IsNullOrWhiteSpace(timeString))
        {
            // TM can be "HHmmss", "HHmmss.ffffff", or a truncated prefix of that.
            string digitsOnly = timeString.Split('.')[0].PadRight(6, '0')[..6];
            if (TimeSpan.TryParseExact(digitsOnly, "hhmmss", CultureInfo.InvariantCulture, out TimeSpan parsedTime))
            {
                time = parsedTime;
            }
        }

        return date + time;
    }

    /// <summary>
    /// Parses a DICOM AS (age string) value, e.g. "032Y", "015M", "003W", "045D".
    /// Only whole-year precision is returned; sub-year units are converted
    /// with integer division (e.g. "006M" -> 0 years), since <see cref="Patient.Age"/>
    /// is a coarse fallback for when <see cref="Patient.DateOfBirth"/> isn't available.
    /// </summary>
    private static int? ParseAge(string? ageString)
    {
        if (string.IsNullOrWhiteSpace(ageString) || ageString.Length < 4)
        {
            return null;
        }

        if (!int.TryParse(ageString.AsSpan(0, 3), out int value))
        {
            return null;
        }

        return char.ToUpperInvariant(ageString[3]) switch
        {
            'Y' => value,
            'M' => value / 12,
            'W' => value / 52,
            'D' => value / 365,
            _ => (int?)null
        };
    }
}
