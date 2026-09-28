using MusicDraft.Core.Catalog;

namespace MusicDraft.Tests;

public class LibraryImporterTests
{
    [Fact]
    public void Imports_folder_reports_bad_files_and_skips_unsupported()
    {
        var dir = TestAudio.TempDir();
        var sub = Directory.CreateDirectory(Path.Combine(dir, "sub")).FullName;
        TestAudio.Wav(dir, "one.wav", 1.5);
        TestAudio.Wav(sub, "two.wav", 0.5);
        File.WriteAllBytes(Path.Combine(dir, "broken.mp3"), new byte[512]);
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "hi");

        using var db = new CatalogDb(Path.Combine(TestAudio.TempDir(), "db.sqlite"));
        var r = LibraryImporter.Import(db, [dir]);
        Assert.Equal(2, r.Added.Count);
        Assert.Single(r.Failed);
        Assert.Equal(1, r.SkippedUnsupported);
        Assert.InRange(r.Added.Single(t => t.Title == "one").DurationSeconds, 1.49, 1.51);

        var again = LibraryImporter.Import(db, [dir]);
        Assert.Equal(2, db.GetTracks().Count);
        Assert.Equal(2, again.Added.Count);
    }
}
