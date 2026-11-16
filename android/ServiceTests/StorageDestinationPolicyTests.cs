using E2E.Droid.Services;

namespace ServiceTests;

public sealed class StorageDestinationPolicyTests
{
    private const string Books = "content://com.android.externalstorage.documents/tree/ABCD-1234%3Aranobe%2Felib";
    private const string Logs = "content://com.android.externalstorage.documents/tree/primary%3ADocuments%2Flogs";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DefaultDownloadsAreUsedOnlyWhenNoFolderWasSelected(string selected)
    {
        var result = StorageDestinationPolicy.ResolveForWrite(selected, null,
            _ => throw new InvalidOperationException("Для папки по умолчанию SAF не проверяется"));

        Assert.Null(result);
    }

    [Fact]
    public void ValidSelectionKeepsTheExactUriRatherThanReconstructingItFromDisplayName()
    {
        var checkedUris = new List<string>();
        var result = StorageDestinationPolicy.ResolveForWrite(Books, "SD-карта/ranobe/elib", uri =>
        {
            checkedUris.Add(uri);
            return uri == Books;
        });

        Assert.Equal(Books, result);
        Assert.Equal(new[] { Books }, checkedUris);
    }

    [Fact]
    public void RevokedPermissionFailsWithoutSwitchingToDownloadsAndTheSameSelectionCanBeReauthorized()
    {
        var allowed = true;
        Assert.Equal(Books, StorageDestinationPolicy.ResolveForWrite(Books, "SD-карта/ranobe/elib", _ => allowed));
        allowed = false;

        var error = Assert.Throws<InvalidOperationException>(() =>
            StorageDestinationPolicy.ResolveForWrite(Books, "SD-карта/ranobe/elib", _ => allowed));

        Assert.Contains("SD-карта/ranobe/elib", error.Message);
        Assert.Contains("Выберите папку заново", error.Message);
        Assert.Contains("Место сохранения не изменено", error.Message);
        Assert.DoesNotContain(Books, error.Message);
        allowed = true;
        Assert.Equal(Books, StorageDestinationPolicy.ResolveForWrite(Books, "SD-карта/ranobe/elib", _ => allowed));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("relative-folder")]
    [InlineData("file:///sdcard/ranobe/elib")]
    [InlineData("https://example.test/folder")]
    [InlineData("content:///tree/folder")]
    public void DamagedSelectionDoesNotSilentlyBecomeDefaultDownloads(string selected)
    {
        var calls = 0;
        Assert.Throws<InvalidOperationException>(() => StorageDestinationPolicy.ResolveForWrite(selected, "elib", _ =>
        {
            calls++;
            return true;
        }));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void MissingBookPermissionDoesNotAffectIndependentLogDestination()
    {
        bool HasGrant(string uri) => uri == Logs;

        Assert.Throws<InvalidOperationException>(() => StorageDestinationPolicy.ResolveForWrite(Books, "Книги на SD", HasGrant));
        Assert.Equal(Logs, StorageDestinationPolicy.ResolveForWrite(Logs, "Журналы", HasGrant));
        Assert.Null(StorageDestinationPolicy.ResolveForWrite(null, null, HasGrant));
    }

    [Theory]
    [InlineData(null, null, null, false)]
    [InlineData("", null, null, false)]
    [InlineData(Books, Books, null, false)]
    [InlineData(Books, Logs, Books, false)]
    [InlineData(Books, null, Books, false)]
    [InlineData(Books, Logs, null, true)]
    [InlineData(Books, null, Logs, true)]
    public void PermissionIsReleasedOnlyWhenNeitherDestinationUsesThePreviousUri(
        string previous, string next, string other, bool expected)
    {
        Assert.Equal(expected, StorageDestinationPolicy.ShouldReleasePermission(previous, next, other));
    }

    [Fact]
    public void SharedFolderGrantSurvivesFirstResetAndIsReleasedAfterTheLastDestinationLeaves()
    {
        // Обе настройки указывают на одну папку. Сброс книг не должен лишать журналы доступа.
        Assert.False(StorageDestinationPolicy.ShouldReleasePermission(Books, null, Books));
        // После сброса книг этой папкой пользуются только журналы: теперь её grant можно отпустить.
        Assert.True(StorageDestinationPolicy.ShouldReleasePermission(Books, null, null));
    }
}
