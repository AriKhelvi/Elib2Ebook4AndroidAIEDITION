namespace Elib2Ebook.DomainServices.Configs;

/// <summary>Минимальные данные браузерного входа; refresh-токен остаётся у защищённого хранилища приложения.</summary>
public sealed record BrowserAuthSession(string AccessToken, string DeviceId)
{
    public override string ToString() => "BrowserAuthSession { данные скрыты }";
}
