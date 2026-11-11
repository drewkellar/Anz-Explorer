using System.Text.Json;
using System.Text.Json.Serialization;
using AnzExplorer.Contracts;

namespace AnzExplorer.Core;

public interface ISettingsStore
{
    Task<ExplorerSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ExplorerSettings settings, CancellationToken cancellationToken = default);
}

public sealed class AtomicJsonSettingsStore(AppPaths paths) : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // A null where the model is non-nullable (e.g. "sidebarGroups": null) is a
        // corrupt document, not something to surface later as a NullReferenceException.
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private const int ReplaceAttempts = 4;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private enum LoadOutcome
    {
        Loaded,
        Missing,
        Unreadable,
        Unusable
    }

    public async Task<ExplorerSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (primaryOutcome, primary) = await TryLoadAsync(paths.SettingsPath, cancellationToken)
                .ConfigureAwait(false);
            if (primary is not null) return primary;

            // An unusable primary must be moved aside. Otherwise the next save's
            // File.Replace rotates it into the backup slot, destroying the last good copy.
            if (primaryOutcome == LoadOutcome.Unusable) Quarantine(paths.SettingsPath);

            var (backupOutcome, backup) = await TryLoadAsync(paths.SettingsBackupPath, cancellationToken)
                .ConfigureAwait(false);
            if (backup is not null) return backup;
            if (backupOutcome == LoadOutcome.Unusable) Quarantine(paths.SettingsBackupPath);

            if (primaryOutcome == LoadOutcome.Unreadable || backupOutcome == LoadOutcome.Unreadable)
            {
                // The files exist but are locked or inaccessible. Returning defaults would let
                // the caller overwrite them, so surface the failure instead.
                throw new IOException("The settings file could not be read.");
            }

            return CreateDefaults();
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task SaveAsync(ExplorerSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        paths.EnsureCreated();
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temporaryPath = paths.SettingsPath + ".tmp";
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(paths.SettingsPath))
                    {
                        File.Replace(temporaryPath, paths.SettingsPath, paths.SettingsBackupPath, true);
                    }
                    else
                    {
                        File.Move(temporaryPath, paths.SettingsPath);
                        File.Copy(paths.SettingsPath, paths.SettingsBackupPath, true);
                    }

                    break;
                }
                catch (Exception exception) when (
                    attempt < ReplaceAttempts && exception is IOException or UnauthorizedAccessException)
                {
                    // Antivirus and sync clients briefly open settings.json without
                    // FILE_SHARE_DELETE; a short retry usually clears the lock.
                    await Task.Delay(50 * attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public static ExplorerSettings CreateDefaults()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var downloads = Path.Combine(home, "Downloads");
        var groupId = Guid.NewGuid();

        var targets = new[]
        {
            ("Home", home, "Home"),
            ("Desktop", desktop, "Desktop"),
            ("Documents", documents, "Document"),
            ("Downloads", downloads, "Download")
        };

        var items = targets
            .Where(item => !string.IsNullOrWhiteSpace(item.Item2))
            .Select((item, order) => new SidebarItem(
                Guid.NewGuid(), item.Item1, item.Item2, SidebarTargetType.KnownFolder, item.Item3, order))
            .ToArray();

        return new ExplorerSettings
        {
            SidebarGroups = [new SidebarGroup(groupId, "Pinned", 0, false, items)],
            FixedHomePath = home,
            IndexedRoots = [home]
        };
    }

    private static async Task<(LoadOutcome Outcome, ExplorerSettings? Settings)> TryLoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return (LoadOutcome.Missing, null);
        }

        ExplorerSettings? settings;
        try
        {
            await using var stream = File.OpenRead(path);
            settings = await JsonSerializer.DeserializeAsync<ExplorerSettings>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return (LoadOutcome.Unusable, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (LoadOutcome.Unreadable, null);
        }

        if (settings is null) return (LoadOutcome.Unusable, null);
        try
        {
            Validate(settings);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            return (LoadOutcome.Unusable, null);
        }

        return (LoadOutcome.Loaded, Migrate(settings));
    }

    private void Quarantine(string path)
    {
        if (!File.Exists(path)) return;
        var name = $"{Path.GetFileNameWithoutExtension(path)}.unreadable-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json";
        try
        {
            File.Move(path, Path.Combine(paths.DataRoot, name));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // If the file cannot be moved aside, leave it; the caller still gets a usable result.
        }
    }

    private static void Validate(ExplorerSettings settings)
    {
        if (settings.SchemaVersion > ExplorerSettings.CurrentSchemaVersion)
        {
            throw new NotSupportedException(
                $"Settings schema {settings.SchemaVersion} is newer than supported schema {ExplorerSettings.CurrentSchemaVersion}.");
        }

        var duplicateIds = settings.SidebarGroups
            .SelectMany(group => group.Items.Select(item => item.Id).Append(group.Id))
            .GroupBy(id => id)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateIds is not null)
        {
            throw new InvalidDataException($"Sidebar ID {duplicateIds.Key} is duplicated.");
        }
    }

    private static ExplorerSettings Migrate(ExplorerSettings settings)
    {
        if (settings.SchemaVersion < 2)
        {
            // Session restoration did not have a user-facing preference in v1.
            // Adopt the new, speed-focused default until the user explicitly enables it.
            settings = settings with
            {
                SchemaVersion = 2,
                RestorePreviousSession = false
            };
        }

        if (settings.SchemaVersion < 3)
        {
            // Existing installations always displayed this confirmation. Preserve
            // that behavior until the user explicitly chooses "Do not ask again".
            settings = settings with
            {
                SchemaVersion = 3,
                ConfirmRecycleBinDeletes = true
            };
        }

        if (settings.SchemaVersion < 4)
        {
            settings = settings with
            {
                SchemaVersion = 4,
                DisplayScale = 1.0
            };
        }

        if (settings.SchemaVersion < 5) settings = settings with { SchemaVersion = 5 };
        return settings;
    }
}
