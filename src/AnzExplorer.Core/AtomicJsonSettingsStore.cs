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
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<ExplorerSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var primary = await TryLoadAsync(paths.SettingsPath, cancellationToken).ConfigureAwait(false);
        if (primary is not null)
        {
            Validate(primary);
            return Migrate(primary);
        }

        var backup = await TryLoadAsync(paths.SettingsBackupPath, cancellationToken).ConfigureAwait(false);
        if (backup is not null)
        {
            Validate(backup);
            return Migrate(backup);
        }

        return CreateDefaults();
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

            if (File.Exists(paths.SettingsPath))
            {
                File.Replace(temporaryPath, paths.SettingsPath, paths.SettingsBackupPath, true);
            }
            else
            {
                File.Move(temporaryPath, paths.SettingsPath);
                File.Copy(paths.SettingsPath, paths.SettingsBackupPath, true);
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

    private static async Task<ExplorerSettings?> TryLoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<ExplorerSettings>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
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

        return settings;
    }
}
