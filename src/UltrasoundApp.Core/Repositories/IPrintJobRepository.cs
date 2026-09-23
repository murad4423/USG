using UltrasoundApp.Core.Models;

namespace UltrasoundApp.Core.Repositories;

/// <summary>
/// Persistence abstraction for <see cref="PrintJob"/> rows — an
/// append-only log of print operations (original prints and reprints
/// alike). See <see cref="IPatientRepository"/> for why this lives in
/// Core.
///
/// (Previously declared directly in
/// <c>UltrasoundApp.Data.Repositories.PrintJobRepository</c> — moved here,
/// the same way <see cref="IGeneratedReportRepository"/> was, so
/// <c>UltrasoundApp.Host</c>'s WebViewBridge can depend on the abstraction
/// rather than the concrete Data type.)
///
/// No natural unique key exists on <see cref="PrintJob"/> (a study can be
/// printed/reprinted any number of times), so only add/read operations are
/// exposed here — there is intentionally no Update/Delete of individual
/// print history entries. A reprint always calls <see cref="Add"/> and
/// never touches a prior row, so earlier print/reprint history is never
/// overwritten.
/// </summary>
public interface IPrintJobRepository
{
    IReadOnlyList<PrintJob> GetAll();
    IReadOnlyList<PrintJob> GetByStudy(string studyInstanceUid);
    void Add(PrintJob printJob);
}
