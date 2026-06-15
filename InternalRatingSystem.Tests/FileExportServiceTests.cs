using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.InternalRating.ExternalSync;
using Xunit;

namespace Jellyfin.Plugin.InternalRating.Tests
{
    public class FileExportServiceTests
    {
        private static readonly ExternalRating[] SampleRatings = new[]
        {
            new ExternalRating("tt1", 1, null, "The Matrix", 1999, "movie", 4.5, new DateTime(2024, 3, 2)),
            new ExternalRating(null, null, null, "Movie, with comma", 2001, "movie", 3.0, new DateTime(2024, 3, 3)),
        };

        // ------------------------------------------------------------------ //
        // BuildLetterboxdCsv
        // ------------------------------------------------------------------ //

        [Fact]
        public void BuildLetterboxdCsv_FirstLine_IsHeader()
        {
            var svc = new FileExportService();
            var csv = svc.BuildLetterboxdCsv(SampleRatings);
            var lines = csv.Replace("\r\n", "\n").Trim().Split('\n');
            Assert.Equal("Date,Name,Year,Rating", lines[0]);
        }

        [Fact]
        public void BuildLetterboxdCsv_DataRow_FormatIsCorrect()
        {
            var svc = new FileExportService();
            var csv = svc.BuildLetterboxdCsv(SampleRatings);
            var lines = csv.Replace("\r\n", "\n").Trim().Split('\n');
            // first data row
            Assert.Contains("2024-03-02,The Matrix,1999,4.5", lines[1]);
        }

        [Fact]
        public void BuildLetterboxdCsv_TitleWithComma_IsQuoted()
        {
            var svc = new FileExportService();
            var csv = svc.BuildLetterboxdCsv(SampleRatings);
            var lines = csv.Replace("\r\n", "\n").Trim().Split('\n');
            Assert.Contains("\"Movie, with comma\"", lines[2]);
        }

        [Fact]
        public void BuildLetterboxdCsv_TitleWithQuote_IsDoubledAndQuoted()
        {
            var ratings = new[]
            {
                new ExternalRating(null, null, null, "Say \"Hello\"", 2020, "movie", 2.5, new DateTime(2024, 1, 1))
            };
            var svc = new FileExportService();
            var csv = svc.BuildLetterboxdCsv(ratings);
            var lines = csv.Replace("\r\n", "\n").Trim().Split('\n');
            // The title field should be: "Say ""Hello"""
            Assert.Contains("\"Say \"\"Hello\"\"\"", lines[1]);
        }

        [Fact]
        public void BuildLetterboxdCsv_NullYear_IsBlank()
        {
            var ratings = new[]
            {
                new ExternalRating(null, null, null, "Unknown Year", null, "movie", 3.5, new DateTime(2024, 6, 1))
            };
            var svc = new FileExportService();
            var csv = svc.BuildLetterboxdCsv(ratings);
            var lines = csv.Replace("\r\n", "\n").Trim().Split('\n');
            // line: 2024-06-01,Unknown Year,,3.5
            Assert.Contains("2024-06-01,Unknown Year,,3.5", lines[1]);
        }

        [Fact]
        public void BuildLetterboxdCsv_Stars_UsesInvariantCulture()
        {
            var ratings = new[]
            {
                new ExternalRating(null, null, null, "Test", 2000, "movie", 3.0, new DateTime(2024, 1, 1))
            };
            var svc = new FileExportService();
            var csv = svc.BuildLetterboxdCsv(ratings);
            // "3" not "3,0" (French locale) or "3.00"
            var lines = csv.Replace("\r\n", "\n").Trim().Split('\n');
            Assert.EndsWith(",3", lines[1]);
        }

        // ------------------------------------------------------------------ //
        // BuildJson
        // ------------------------------------------------------------------ //

        [Fact]
        public void BuildJson_IsValidJson_WithCorrectCount()
        {
            var svc = new FileExportService();
            var json = svc.BuildJson(SampleRatings);
            var list = JsonSerializer.Deserialize<List<ExternalRating>>(json);
            Assert.NotNull(list);
            Assert.Equal(SampleRatings.Length, list!.Count);
        }

        // ------------------------------------------------------------------ //
        // ParseCsv
        // ------------------------------------------------------------------ //

        [Fact]
        public void ParseCsv_RoundTrips_Count()
        {
            var svc = new FileExportService();
            var csv = svc.BuildLetterboxdCsv(SampleRatings);
            var parsed = svc.ParseCsv(csv);
            Assert.Equal(SampleRatings.Length, parsed.Count);
        }

        [Fact]
        public void ParseCsv_RoundTrips_Stars()
        {
            var svc = new FileExportService();
            var csv = svc.BuildLetterboxdCsv(SampleRatings);
            var parsed = svc.ParseCsv(csv);
            Assert.Equal(SampleRatings[0].Stars, parsed[0].Stars);
            Assert.Equal(SampleRatings[1].Stars, parsed[1].Stars);
        }

        [Fact]
        public void ParseCsv_RoundTrips_TitleWithComma()
        {
            var svc = new FileExportService();
            var csv = svc.BuildLetterboxdCsv(SampleRatings);
            var parsed = svc.ParseCsv(csv);
            Assert.Equal("Movie, with comma", parsed[1].Title);
        }

        [Fact]
        public void ParseCsv_HandlesHeaderRow()
        {
            var svc = new FileExportService();
            var csv = svc.BuildLetterboxdCsv(SampleRatings);
            // ParseCsv must skip the header — count should be 2, not 3
            var parsed = svc.ParseCsv(csv);
            Assert.Equal(2, parsed.Count);
        }

        [Fact]
        public void BuildLetterboxdCsv_FormulaInjectionTitle_IsPrefixedWithSingleQuote()
        {
            var ratings = new[]
            {
                new ExternalRating(null, null, null, "=cmd()", 2020, "movie", 3.0, new DateTime(2024, 1, 1))
            };
            var svc = new FileExportService();
            var csv = svc.BuildLetterboxdCsv(ratings);
            var lines = csv.Replace("\r\n", "\n").Trim().Split('\n');
            // The title field must start with a single quote to neutralise formula injection
            Assert.Contains("'=cmd()", lines[1]);
        }

        // ------------------------------------------------------------------ //
        // ParseJson
        // ------------------------------------------------------------------ //

        [Fact]
        public void ParseJson_RoundTrips_Count()
        {
            var svc = new FileExportService();
            var json = svc.BuildJson(SampleRatings);
            var parsed = svc.ParseJson(json);
            Assert.Equal(SampleRatings.Length, parsed.Count);
        }

        [Fact]
        public void ParseJson_RoundTrips_Stars_And_Title()
        {
            var svc = new FileExportService();
            var json = svc.BuildJson(SampleRatings);
            var parsed = svc.ParseJson(json);
            Assert.Equal(SampleRatings[0].Stars, parsed[0].Stars);
            Assert.Equal(SampleRatings[0].Title, parsed[0].Title);
        }
    }
}
