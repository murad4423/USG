using UltrasoundApp.Core.Models;

namespace UltrasoundApp.Core.Repositories;

/// <summary>
/// Persistence abstraction for <see cref="Study"/> rows.
/// See <see cref="IPatientRepository"/> for why this lives in Core.
/// </summary>
public interface IStudyRepository
{
    Study? GetById(string studyInstanceUid);
    IReadOnlyList<Study> GetAll();
    IReadOnlyList<Study> GetByPatient(string patientId);
    void Add(Study study);
    void Update(Study study);
    void Delete(string studyInstanceUid);
}
