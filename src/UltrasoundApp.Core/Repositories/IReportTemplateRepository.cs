using UltrasoundApp.Core.Models;

namespace UltrasoundApp.Core.Repositories;

/// <summary>
/// Persistence abstraction for <see cref="ReportTemplate"/> rows.
/// See <see cref="IPatientRepository"/> for why this lives in Core.
///
/// (Previously declared directly in
/// <c>UltrasoundApp.Data.Repositories.ReportTemplateRepository</c> — moved
/// here now that <see cref="Services.ReportRenderService"/>, a Core
/// service, needs to depend on it without Core referencing Data.)
/// </summary>
public interface IReportTemplateRepository
{
    ReportTemplate? GetByExamType(string examType);
    IReadOnlyList<ReportTemplate> GetAll();
    void Add(ReportTemplate template);
    void Update(ReportTemplate template);
    void Delete(string examType);
}
