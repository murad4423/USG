using UltrasoundApp.Core.Logging;
using UltrasoundApp.Core.Models;
using UltrasoundApp.Core.Repositories;

namespace UltrasoundApp.Core.Services;

/// <summary>
/// Takes the already-parsed metadata for a single DICOM image (patient,
/// study, image — as produced by <c>UltrasoundApp.Dicom.DicomFileParser</c>)
/// and writes it into the database via the repository interfaces.
///
/// This class does not reference the DICOM parsing project directly —
/// it only depends on the plain <see cref="Patient"/>/<see cref="Study"/>/
/// <see cref="Image"/> models and repository interfaces that already live
/// in Core, so Core has no dependency on either the Dicom project or the
/// Data project. The caller (the Host app) is responsible for parsing a
/// .dcm file first and then passing the resulting Patient/Study/Image
/// into <see cref="Process"/>.
///
/// Idempotency: this is keyed off DICOM's own natural identifiers —
/// Patient ID, Study Instance UID, and SOP Instance UID — rather than any
/// upload/session state. Processing the same file (or another file from
/// the same study) any number of times will never create a duplicate
/// Patient or Study row, and re-processing the exact same image (same
/// SOP Instance UID) will never create a duplicate Image row either.
/// </summary>
public sealed class DicomProcessingService
{
    private const string LogCategory = "DICOM";

    private readonly IPatientRepository _patientRepository;
    private readonly IStudyRepository _studyRepository;
    private readonly IImageRepository _imageRepository;

    public DicomProcessingService(
        IPatientRepository patientRepository,
        IStudyRepository studyRepository,
        IImageRepository imageRepository)
    {
        _patientRepository = patientRepository;
        _studyRepository = studyRepository;
        _imageRepository = imageRepository;
    }

    /// <summary>
    /// Ensures the given patient, study, and image are reflected in the
    /// database, creating only whatever rows don't already exist.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown if the parsed DICOM data is missing one of the identifiers
    /// (Patient ID, Study Instance UID, or SOP Instance UID) needed to
    /// process it safely.
    /// </exception>
    public DicomProcessingResult Process(Patient patient, Study study, Image image)
    {
        ValidateIdentifiers(patient, study, image);

        Patient effectivePatient;
        bool patientWasCreated;
        Study effectiveStudy;
        bool studyWasCreated;
        bool imageWasCreated;

        try
        {
            (effectivePatient, patientWasCreated) = EnsurePatient(patient);
            (effectiveStudy, studyWasCreated) = EnsureStudy(study, effectivePatient.PatientID);
            imageWasCreated = EnsureImage(image, effectiveStudy.StudyInstanceUID);
        }
        catch (Exception ex)
        {
            // Database-level failures (locked file, disk full, a schema
            // mismatch after a partial upgrade). Logged here because this
            // is the one place that knows which study was being stored;
            // rethrown unchanged so the caller's own handler still decides
            // what the operator sees.
            AppLog.Error(
                LogCategory,
                $"Failed to store study '{study.StudyInstanceUID}' (SOP '{image.SOPInstanceUID}') in the database.",
                ex);
            throw;
        }

        AppLog.Info(
            LogCategory,
            $"Stored SOP '{image.SOPInstanceUID}' under study '{effectiveStudy.StudyInstanceUID}' " +
            $"for patient '{effectivePatient.PatientID}' — " +
            $"patient {(patientWasCreated ? "created" : "existing")}, " +
            $"study {(studyWasCreated ? "created" : "existing")}, " +
            $"image {(imageWasCreated ? "added" : "already present (no duplicate)")}.");

        return new DicomProcessingResult
        {
            Patient = effectivePatient,
            Study = effectiveStudy,
            Image = image,
            PatientWasCreated = patientWasCreated,
            StudyWasCreated = studyWasCreated,
            ImageWasCreated = imageWasCreated
        };
    }

    private static void ValidateIdentifiers(Patient patient, Study study, Image image)
    {
        if (string.IsNullOrWhiteSpace(patient.PatientID))
        {
            throw new ArgumentException(
                "The DICOM file has no Patient ID (0010,0020) and cannot be imported.", nameof(patient));
        }

        if (string.IsNullOrWhiteSpace(study.StudyInstanceUID))
        {
            throw new ArgumentException(
                "The DICOM file has no Study Instance UID (0020,000D) and cannot be imported.", nameof(study));
        }

        if (string.IsNullOrWhiteSpace(image.SOPInstanceUID))
        {
            throw new ArgumentException(
                "The DICOM file has no SOP Instance UID (0008,0018) and cannot be imported.", nameof(image));
        }
    }

    /// <summary>
    /// Looks up the patient by <see cref="Patient.PatientID"/>. If one
    /// already exists, that existing row wins as-is (a later upload for
    /// the same patient never overwrites or duplicates it) — only a
    /// genuinely new Patient ID results in an insert.
    /// </summary>
    private (Patient Patient, bool WasCreated) EnsurePatient(Patient patient)
    {
        Patient? existing = _patientRepository.GetById(patient.PatientID);
        if (existing is not null)
        {
            return (existing, false);
        }

        _patientRepository.Add(patient);
        return (patient, true);
    }

    /// <summary>
    /// Looks up the study by <see cref="Study.StudyInstanceUID"/>. If one
    /// already exists, that existing row wins as-is — a study is only
    /// ever created once, and every subsequent image for that Study
    /// Instance UID is added under the same row.
    /// </summary>
    private (Study Study, bool WasCreated) EnsureStudy(Study study, string ownerPatientId)
    {
        Study? existing = _studyRepository.GetById(study.StudyInstanceUID);
        if (existing is not null)
        {
            return (existing, false);
        }

        study.PatientID = ownerPatientId;
        if (string.IsNullOrWhiteSpace(study.Status))
        {
            // DICOM has no workflow-status concept; a freshly-imported
            // study starts life as "Received" until a later workflow
            // step (out of scope here) moves it along.
            study.Status = "Received";
        }

        _studyRepository.Add(study);
        return (study, true);
    }

    /// <summary>
    /// Looks up the image by <see cref="Image.SOPInstanceUID"/>. Only
    /// inserts if it isn't already there, so re-processing the exact same
    /// DICOM instance (e.g. the user selects the same file twice) is a
    /// harmless no-op rather than a duplicate-key failure.
    /// </summary>
    private bool EnsureImage(Image image, string ownerStudyInstanceUid)
    {
        Image? existing = _imageRepository.GetById(image.SOPInstanceUID);
        if (existing is not null)
        {
            return false;
        }

        image.StudyInstanceUID = ownerStudyInstanceUid;
        _imageRepository.Add(image);
        return true;
    }
}
