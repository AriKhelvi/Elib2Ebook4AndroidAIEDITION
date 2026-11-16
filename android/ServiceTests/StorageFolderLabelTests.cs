using E2E.Droid.Services;

namespace ServiceTests;

public sealed class StorageFolderLabelTests
{
    private const string ExternalStorage = "com.android.externalstorage.documents";

    [Theory]
    [InlineData("primary:ranobe/Elib", null, "Внутренняя память/ranobe/Elib")]
    [InlineData("primary:ranobe/Elib", "Общее хранилище", "Внутренняя память/ranobe/Elib")]
    [InlineData("ABCD-1234:ranobe/Elib", "SD-карта", "SD-карта (ABCD-1234)/ranobe/Elib")]
    [InlineData("ABCD-1234:ranobe/Elib", "USB-накопитель", "USB-накопитель (ABCD-1234)/ranobe/Elib")]
    [InlineData("ABCD-1234:ranobe/Elib", null, "Накопитель ABCD-1234/ranobe/Elib")]
    [InlineData("ABCD-1234:ranobe/Elib", "  ", "Накопитель ABCD-1234/ranobe/Elib")]
    [InlineData("ABCD-1234:ranobe/Elib", " SD-карта ", "SD-карта (ABCD-1234)/ranobe/Elib")]
    public void ShowsStorageAndCompletePathInsteadOfOnlyLeaf(string id, string description, string expected)
        => Assert.Equal(expected, StorageFolderLabel.Format(ExternalStorage, id, "Elib", description));

    [Theory]
    [InlineData("primary:", null, "Внутренняя память")]
    [InlineData("primary:/", null, "Внутренняя память")]
    [InlineData("ABCD-1234:", "SD-карта", "SD-карта (ABCD-1234)")]
    [InlineData("ABCD-1234:", null, "Накопитель ABCD-1234")]
    [InlineData("primary:/ranobe/Elib/", null, "Внутренняя память/ranobe/Elib")]
    [InlineData("primary:Книги/Моё ранобэ/Элиб", null, "Внутренняя память/Книги/Моё ранобэ/Элиб")]
    [InlineData("primary:Books/part:one/Elib", null, "Внутренняя память/Books/part:one/Elib")]
    [InlineData("primary:Books/100%25/part%2Fone+two", null, "Внутренняя память/Books/100%25/part%2Fone+two")]
    [InlineData("primary:Books//Elib", null, "Внутренняя память/Books//Elib")]
    [InlineData("primary:Books\\Elib", null, "Внутренняя память/Books\\Elib")]
    public void HandlesRootsAndKeepsDecodedFolderNames(string id, string description, string expected)
        => Assert.Equal(expected, StorageFolderLabel.Format(ExternalStorage, id, "Последняя папка", description));

    [Theory]
    [InlineData("com.google.android.apps.docs.storage", "primary:opaque-account/private-id", "Мои книги", "Мои книги")]
    [InlineData("com.android.providers.downloads.documents", "raw:/storage/emulated/0/Download", "Загрузки", "Загрузки")]
    [InlineData("com.example.documents", "ABCD-1234:fake/path", null, "Выбранная папка")]
    [InlineData(null, "primary:Books", "Папка", "Папка")]
    [InlineData("", "primary:Books", " ", "Выбранная папка")]
    public void DoesNotInterpretOtherProvidersOpaqueIds(string authority, string id, string name, string expected)
        => Assert.Equal(expected, StorageFolderLabel.Format(authority, id, name, "SD-карта"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("primary")]
    [InlineData(":Books")]
    [InlineData(" :Books")]
    [InlineData("primary/other:Books")]
    [InlineData("primary%3ABooks")]
    [InlineData("primary:Books\nOther")]
    public void MalformedIdsUseDisplayNameOrNeutralFallback(string id)
    {
        Assert.Equal("Elib", StorageFolderLabel.Format(ExternalStorage, id, "Elib"));
        Assert.Equal("Выбранная папка", StorageFolderLabel.Format(ExternalStorage, id, null));
    }
}
