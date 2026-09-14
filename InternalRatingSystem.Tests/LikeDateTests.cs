using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.InternalRating.Data;
using Xunit;

namespace Jellyfin.Plugin.InternalRating.Tests
{
    /// <summary>
    /// The liked page is ordered by when each like was made. The first full
    /// Letterboxd sync stamped "now" on every like it imported, so twenty
    /// films the member liked over three years arrived as one block at the
    /// top, in library order — "the liked page is messed up". An import that
    /// knows the real date must be able to say so.
    /// </summary>
    public class LikeDateTests
    {
        [Fact]
        public async Task ALikeCarriesTheDateTheImportKnows()
        {
            using var paths = new TestPaths();
            var repo = new UserInteractionsRepository(paths);
            var when = new DateTime(2024, 3, 9, 0, 0, 0, DateTimeKind.Utc);

            Assert.True(await repo.AddLikeAsync("u1", "item-a", when));
            var liked = await repo.GetLikedAsync("u1");
            Assert.Equal(when, liked.Single().LikedAt);
        }

        [Fact]
        public async Task WithoutADateALikeIsNow()
        {
            using var paths = new TestPaths();
            var repo = new UserInteractionsRepository(paths);
            var before = DateTime.UtcNow.AddSeconds(-1);

            Assert.True(await repo.AddLikeAsync("u1", "item-a"));
            Assert.True((await repo.GetLikedAsync("u1")).Single().LikedAt >= before);
        }

        [Fact]
        public async Task AnExistingLikeKeepsItsDate()
        {
            using var paths = new TestPaths();
            var repo = new UserInteractionsRepository(paths);
            var first = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            await repo.AddLikeAsync("u1", "item-a", first);
            Assert.False(await repo.AddLikeAsync("u1", "item-a", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.Equal(first, (await repo.GetLikedAsync("u1")).Single().LikedAt);
        }
    }
}
