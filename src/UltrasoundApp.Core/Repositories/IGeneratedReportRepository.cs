using UltrasoundApp.Core.Models;

namespace UltrasoundApp.Core.Repositories;

/// <summary>
/// Persistence abstraction for <see cref="GeneratedReport"/> rows.
/// See <see cref="IPatientRepository"/> for why this lives in Core.
///
/// (Previously declared directly in
/// <c>UltrasoundApp.Data.Repositories.GeneratedReportRepository</c> — moved
/// here now that <see cref="Services.ReportRenderService"/>, a Core
/// service, needs to depend on it without Core referencing Data.)
/// </summary>
public interface IGeneratedReportRepository
{
    GeneratedReport? GetByStudy(string studyInstanceUid);
    IReadOnlyList<GeneratedReport> GetAll();
    void Add(GeneratedReport report);
    void Update(GeneratedReport report);
    void Delete(string studyInstanceUid);
}
