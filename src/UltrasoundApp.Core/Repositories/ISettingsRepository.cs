using UltrasoundApp.Core.Models;

namespace UltrasoundApp.Core.Repositories;

/// <summary>
/// Persistence abstraction for the single <see cref="AppSettings"/> row.
/// See <see cref="IPatientRepository"/> for why this lives in Core.
///
/// Unlike every other repository in this app, there is exactly one
/// settings row, never zero-or-many keyed by some natural ID, so the
/// surface here is just Get/Save rather than the usual
/// Add/Update/Delete-by-key.
/// </summary>
public interface ISettingsRepository
{
    /// <summary>The current settings row, or null if <see cref="Save"/> has never been called (a fresh database).</summary>
    AppSettings? Get();

    /// <summary>
    /// Persists <paramref name="settings"/> as the app's current settings
    /// — an upsert: inserts the row if none exists yet, or overwrites
    /// every column if one already does. There is only ever one row, so
    /// there's no key to pass separately.
    /// </summary>
    void Save(AppSettings settings);
}
