using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.ExternalSync;
using MediaBrowser.Common.Configuration;
using Xunit;

namespace Jellyfin.Plugin.InternalRating.Tests
{
    /// <summary>Minimal IApplicationPaths stub that returns a unique temp directory.</summary>
    internal sealed class TestPaths : IApplicationPaths, IDisposable
    {
        public string DataPath { get; } =
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(DataPath))
                Directory.Delete(DataPath, recursive: true);
        }

        // All other members unused by ExternalSyncSettingsRepository
        public string ProgramDataPath  => throw new NotImplementedException();
        public string VirtualDataPath  => throw new NotImplementedException();
        public string CachePath        => throw new NotImplementedException();
        public string WebPath          => throw new NotImplementedException();
        public string ImageCachePath   => throw new NotImplementedException();
        public string PluginsPath      => throw new NotImplementedException();
        public string PluginConfigurationsPath => throw new NotImplementedException();
        public string LogDirectoryPath => throw new NotImplementedException();
        public string RootFolderPath   => throw new NotImplementedException();
        public string DefaultUserViewsPath => throw new NotImplementedException();
        public string InternalMetadataPath => throw new NotImplementedException();
        public string TranscodingTempPath  => throw new NotImplementedException();
        public string TempDirectory        => throw new NotImplementedException();
        // Additional members required by this version of IApplicationPaths
        public string ProgramSystemPath          => throw new NotImplementedException();
        public string ConfigurationDirectoryPath => throw new NotImplementedException();
        public string SystemConfigurationFilePath => throw new NotImplementedException();
        public string TrickplayPath => throw new NotImplementedException();
        public string BackupPath    => throw new NotImplementedException();
        public void MakeSanityCheckOrThrow() => throw new NotImplementedException();
        public void CreateAndCheckMarker(string markerPath, string productVersion, bool isNew) => throw new NotImplementedException();
    }

    public class ExternalSyncSettingsRepositoryTests
    {
        [Fact]
        public async Task SaveThenGet_RoundTrips()
        {
            using var paths = new TestPaths();
            var repo = new ExternalSyncSettingsRepository(paths);
            await repo.SetConnectionAsync("user1", "Trakt", new ProviderConnection { Direction = SyncDirection.TwoWay, AccessToken = "tok" });
            var got = await repo.GetConnectionAsync("user1", "Trakt");
            Assert.NotNull(got);
            Assert.Equal(SyncDirection.TwoWay, got!.Direction);
            Assert.Equal("tok", got.AccessToken);
        }

        [Fact]
        public async Task RemoveThenGet_ReturnsNull()
        {
            using var paths = new TestPaths();
            var repo = new ExternalSyncSettingsRepository(paths);
            await repo.SetConnectionAsync("user2", "Simkl", new ProviderConnection { Direction = SyncDirection.ImportOnly, AccessToken = "abc" });
            await repo.RemoveConnectionAsync("user2", "Simkl");
            var got = await repo.GetConnectionAsync("user2", "Simkl");
            Assert.Null(got);
        }

        [Fact]
        public async Task GetAllAsync_ReturnsSavedUser()
        {
            using var paths = new TestPaths();
            var repo = new ExternalSyncSettingsRepository(paths);
            await repo.SetConnectionAsync("user3", "Trakt", new ProviderConnection { Direction = SyncDirection.ExportOnly });
            var all = await repo.GetAllAsync();
            Assert.True(all.Users.ContainsKey("user3"));
            Assert.True(all.Users["user3"].Providers.ContainsKey("Trakt"));
        }
    }
}
