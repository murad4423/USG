using UltrasoundApp.Core.Models;

namespace UltrasoundApp.Core.Repositories;

/// <summary>
/// Persistence abstraction for <see cref="Patient"/> rows.
///
/// Defined here in Core (rather than in UltrasoundApp.Data, where the
/// concrete implementation lives) so that Core-level business logic —
/// e.g. <see cref="Services.DicomProcessingService"/> — can depend on it
/// without Core having to reference the Data project. Data implements
/// this interface; it never needs to be referenced the other way.
/// </summary>
public interface IPatientRepository
{
    Patient? GetById(string patientId);
    IReadOnlyList<Patient> GetAll();
    void Add(Patient patient);
    void Update(Patient patient);
    void Delete(string patientId);
}
