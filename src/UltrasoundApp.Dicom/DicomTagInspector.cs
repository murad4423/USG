using FellowOakDicom;
using UltrasoundApp.Core.Logging;

namespace UltrasoundApp.Dicom;

/// <summary>
/// Read-only DICOM tag/value browser used by the Settings screen's "DICOM
/// Data Check" tool. Unlike <see cref="DicomFileParser"/>, this never
/// writes anything to disk or the database, and never requires
/// StudyInstanceUID/patient identifiers to be present — it just opens
/// whatever file is picked and reports every tag it finds, including
/// tags whose value is present but empty.
///
/// This is exploratory tooling: it lets an operator pick any .dcm file
/// and see exactly which field names/keywords are actually available in
/// it (e.g. to know which tokens could later be wired into a report
/// template), and which of those fields are blank for that particular
/// file even though the tag itself exists.
/// </summary>
public sealed class DicomTagInspector
{
    private const string LogCategory = "DICOM";

    /// <summary>Tags treated as "Patient" for grouping, keyed by their fo-dicom dictionary keyword.</summary>
    private static readonly HashSet<string> PatientKeywords = new(StringComparer.Ordinal)
    {
        "PatientName", "PatientID", "PatientBirthDate", "PatientSex", "PatientAge",
        "PatientWeight", "PatientSize", "PatientAddress", "PatientTelephoneNumbers",
        "EthnicGroup", "PatientComments", "OtherPatientIDs", "PatientBirthTime",
        "PatientInsurancePlanCodeSequence", "PatientMotherBirthName"
    };

    /// <summary>Tags treated as "Study / Exam" for grouping.</summary>
    private static readonly HashSet<string> StudyExamKeywords = new(StringComparer.Ordinal)
    {
        "StudyInstanceUID", "StudyDate", "StudyTime", "StudyDescription", "AccessionNumber",
        "ReferringPhysicianName", "Modality", "SeriesInstanceUID", "SeriesDescription",
        "SeriesNumber", "InstanceNumber", "InstitutionName", "InstitutionAddress",
        "StationName", "PerformingPhysicianName", "OperatorsName", "ManufacturerModelName",
        "Manufacturer", "BodyPartExamined", "SOPInstanceUID", "SOPClassUID"
    };

    /// <summary>
    /// Opens <paramref name="filePath"/> and returns every tag it
    /// contains, flattened out of any sequences and categorized for
    /// display. Throws <see cref="DicomParseException"/> (same exception
    /// type <see cref="DicomFileParser"/> uses) if the file can't be
    /// found or isn't valid DICOM.
    /// </summary>
    public DicomInspectionResult Inspect(string filePath)
    {
        if (!File.Exists(filePath))
        {
            AppLog.Error(LogCategory, $"DICOM file not found for inspection: '{filePath}'.");
            throw new DicomParseException($"The file could not be found:\n{filePath}");
        }

        FoDicomBootstrapper.EnsureInitialized();

        DicomFile dicomFile;
        try
        {
            dicomFile = DicomFile.Open(filePath);
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategory, $"Failed to open DICOM file '{filePath}' for inspection.", ex);
            throw new DicomParseException(
                $"'{Path.GetFileName(filePath)}' is not a valid DICOM file, or is damaged.", ex);
        }

        var fields = new List<DicomFieldInfo>();
        CollectFields(dicomFile.Dataset, fields, path: string.Empty);

        AppLog.Info(LogCategory, $"Inspected '{Path.GetFileName(filePath)}': {fields.Count} tag(s) found.");

        return new DicomInspectionResult
        {
            FileName = Path.GetFileName(filePath),
            Fields = fields
        };
    }

    /// <summary>Walks one dataset's items, recursing into sequences so nested (e.g. measurement) data is included too.</summary>
    private static void CollectFields(DicomDataset dataset, List<DicomFieldInfo> fields, string path)
    {
        foreach (DicomItem item in dataset)
        {
            DicomDictionaryEntry entry = DicomDictionary.Default[item.Tag];
            string keyword = string.IsNullOrWhiteSpace(entry.Keyword) ? item.Tag.ToString() : entry.Keyword;
            string name = string.IsNullOrWhiteSpace(entry.Name) ? keyword : entry.Name;
            string tagString = item.Tag.ToString();
            string vr = item.ValueRepresentation?.Code ?? string.Empty;

            if (item is DicomSequence sequence)
            {
                List<DicomDataset> items = sequence.Items.ToList();

                fields.Add(new DicomFieldInfo
                {
                    Tag = tagString,
                    Keyword = keyword,
                    Name = name,
                    Vr = vr,
                    Value = $"[{items.Count} item(s)]",
                    Category = "Measurements",
                    Path = path
                });

                for (int i = 0; i < items.Count; i++)
                {
                    string childPath = path.Length > 0 ? $"{path} > {name}[{i}]" : $"{name}[{i}]";
                    CollectFields(items[i], fields, childPath);
                }

                continue;
            }

            fields.Add(new DicomFieldInfo
            {
                Tag = tagString,
                Keyword = keyword,
                Name = name,
                Vr = vr,
                Value = ReadValueAsString(dataset, item, vr),
                Category = CategoryFor(keyword, path),
                Path = path
            });
        }
    }

    /// <summary>
    /// Renders a non-sequence element's value as text. Binary VRs (pixel
    /// data, overlays, unknown private data) are never meaningfully
    /// displayable, so those report a placeholder instead of raw bytes.
    /// An element present with no value at all (VM=0) comes back as "" —
    /// deliberately, so the UI can show the field name with a blank value
    /// rather than omitting the row.
    /// </summary>
    private static string ReadValueAsString(DicomDataset dataset, DicomItem item, string vr)
    {
        if (vr is "OB" or "OW" or "OF" or "OD" or "OL" or "OV" or "UN")
        {
            return "[binary data]";
        }

        try
        {
            string[] values = dataset.GetValues<string>(item.Tag);
            return values is { Length: > 0 } ? string.Join(", ", values) : string.Empty;
        }
        catch
        {
            // A handful of VRs fo-dicom can't stringify generically —
            // treat the same as "no readable value" rather than failing
            // the whole inspection over one odd tag.
            return string.Empty;
        }
    }

    private static string CategoryFor(string keyword, string parentPath)
    {
        // Anything nested inside a sequence (Content Sequence, Measurement
        // Sequence, Sequence of Ultrasound Regions, etc.) is grouped as
        // measurement/structured data rather than flat patient/study info,
        // regardless of its own keyword.
        if (parentPath.Length > 0)
        {
            return "Measurements";
        }

        if (PatientKeywords.Contains(keyword))
        {
            return "Patient";
        }

        if (StudyExamKeywords.Contains(keyword))
        {
            return "Study / Exam";
        }

        if (keyword.Contains("Measurement", StringComparison.OrdinalIgnoreCase)
            || keyword.Contains("Region", StringComparison.OrdinalIgnoreCase))
        {
            return "Measurements";
        }

        return "Other";
    }
}
