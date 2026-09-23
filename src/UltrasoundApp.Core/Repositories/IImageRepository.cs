using UltrasoundApp.Core.Models;

namespace UltrasoundApp.Core.Repositories;

/// <summary>
/// Persistence abstraction for <see cref="Image"/> rows.
/// See <see cref="IPatientRepository"/> for why this lives in Core.
/// </summary>
public interface IImageRepository
{
    Image? GetById(string sopInstanceUid);
    IReadOnlyList<Image> GetByStudy(string studyInstanceUid);
    void Add(Image image);
    void Update(Image image);
    void Delete(string sopInstanceUid);
}
