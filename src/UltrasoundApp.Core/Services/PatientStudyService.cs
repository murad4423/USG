using UltrasoundApp.Core.Models;
using UltrasoundApp.Core.Repositories;

namespace UltrasoundApp.Core.Services;

/// <summary>
/// Read-only query service over the Patient/Study/Image data already
/// written by <see cref="DicomProcessingService"/>. Produces one
/// <see cref="PatientStudySummary"/> per Study — with its owning Patient
/// and every Image grouped underneath it — optionally narrowed by a
/// Patient Name/ID search and/or a study-date range.
///
/// This is a query service, with ONE exception: <see cref="MarkImagePrinted"/>
/// moves a study's <see cref="Study.Status"/> forward once its images have
/// been printed. Nothing else here inserts, updates, or deletes anything.
/// </summary>
public sealed class PatientStudyService
{
    private readonly IPatientRepository _patientRepository;
    private readonly IStudyRepository _studyRepository;
    private readonly IImageRepository _imageRepository;

    public PatientStudyService(
        IPatientRepository patientRepository,
        IStudyRepository studyRepository,
        IImageRepository imageRepository)
    {
        _patientRepository = patientRepository;
        _studyRepository = studyRepository;
        _imageRepository = imageRepository;
    }

    /// <summary>
    /// Records that the study's images have been printed: sets
    /// <see cref="Study.Status"/> to <see cref="StudyStatuses.ImagePrinted"/>
    /// (which moves the patient from the "Waiting Study" tab to the "Image
    /// Printed" tab of the Patient List). A study that is already past that
    /// step (report printed / completed) is left alone. Returns true if the
    /// status was changed, false if nothing needed changing or the study
    /// doesn't exist.
    /// </summary>
    public bool MarkImagePrinted(string studyInstanceUid)
    {
        if (string.IsNullOrWhiteSpace(studyInstanceUid))
        {
            return false;
        }

        Study? study = _studyRepository.GetById(studyInstanceUid);
        if (study is null || StudyStatuses.IsImagePrintedOrLater(study.Status))
        {
            return false;
        }

        study.Status = StudyStatuses.ImagePrinted;
        _studyRepository.Update(study);
        return true;
    }

    /// <summary>Every study, most recent first, each with its patient and images.</summary>
    public IReadOnlyList<PatientStudySummary> GetAll()
    {
        return Query(searchText: null, startDate: null, endDate: null);
    }

    /// <summary>
    /// Studies whose patient's name or patient ID contains
    /// <paramref name="searchText"/> (case-insensitive). An empty/blank
    /// search text matches everything, same as <see cref="GetAll"/>.
    /// </summary>
    public IReadOnlyList<PatientStudySummary> Search(string searchText)
    {
        return Query(searchText, startDate: null, endDate: null);
    }

    /// <summary>
    /// Studies whose <see cref="Study.StudyDateTime"/> falls within
    /// [<paramref name="startDate"/>, <paramref name="endDate"/>]
    /// (inclusive on both ends; either bound may be null to leave that
    /// side open). Only the date portion of each bound is considered —
    /// <paramref name="endDate"/> covers the whole of that calendar day.
    /// </summary>
    public IReadOnlyList<PatientStudySummary> FilterByDateRange(DateTime? startDate, DateTime? endDate)
    {
        return Query(searchText: null, startDate, endDate);
    }

    /// <summary>
    /// The single study/patient/images row for <paramref name="studyInstanceUid"/>,
    /// or null if no such study exists (or its patient can't be resolved —
    /// same defensive check as <see cref="Query"/>). Used by reprint flows,
    /// which only need one study's already-extracted images rather than
    /// the whole list.
    /// </summary>
    public PatientStudySummary? GetByStudy(string studyInstanceUid)
    {
        Study? study = _studyRepository.GetById(studyInstanceUid);
        if (study is null)
        {
            return null;
        }

        Patient? patient = _patientRepository.GetById(study.PatientID);
        if (patient is null)
        {
            return null;
        }

        IReadOnlyList<Image> images = _imageRepository.GetByStudy(study.StudyInstanceUID);
        return new PatientStudySummary { Patient = patient, Study = study, Images = images };
    }

    /// <summary>
    /// The single underlying query all the methods above delegate to —
    /// applies a Patient Name/ID search and/or a study-date range
    /// together. Passing every parameter as null is equivalent to
    /// <see cref="GetAll"/>.
    /// </summary>
    public IReadOnlyList<PatientStudySummary> Query(string? searchText, DateTime? startDate, DateTime? endDate)
    {
        IEnumerable<Study> studies = _studyRepository.GetAll();

        if (startDate is not null)
        {
            DateTime startOfDay = startDate.Value.Date;
            studies = studies.Where(study => study.StudyDateTime >= startOfDay);
        }

        if (endDate is not null)
        {
            // Inclusive end date: everything up to (but not including)
            // the start of the following day.
            DateTime endOfDayExclusive = endDate.Value.Date.AddDays(1);
            studies = studies.Where(study => study.StudyDateTime < endOfDayExclusive);
        }

        string? trimmedSearch = string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim();

        var results = new List<PatientStudySummary>();
        foreach (Study study in studies)
        {
            Patient? patient = _patientRepository.GetById(study.PatientID);
            if (patient is null)
            {
                // Defensive: the FK should always resolve given how
                // DicomProcessingService writes data, but a study whose
                // patient can't be found can't be shown meaningfully —
                // skip rather than fail the whole query.
                continue;
            }

            if (trimmedSearch is not null && !MatchesSearch(patient, trimmedSearch))
            {
                continue;
            }

            IReadOnlyList<Image> images = _imageRepository.GetByStudy(study.StudyInstanceUID);

            results.Add(new PatientStudySummary
            {
                Patient = patient,
                Study = study,
                Images = images
            });
        }

        return results;
    }

    private static bool MatchesSearch(Patient patient, string trimmedSearchText)
    {
        return patient.PatientName.Contains(trimmedSearchText, StringComparison.OrdinalIgnoreCase)
            || patient.PatientID.Contains(trimmedSearchText, StringComparison.OrdinalIgnoreCase);
    }
}
