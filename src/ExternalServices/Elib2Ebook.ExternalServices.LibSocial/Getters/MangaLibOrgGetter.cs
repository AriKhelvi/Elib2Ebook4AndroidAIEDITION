using Elib2Ebook.DomainServices.Configs;

namespace Elib2Ebook.ExternalServices.LibSocial.Getters;

public class MangaLibOrgGetter(BookGetterConfig config) : MangaLibGetter(config)
{
    // Оба домена используют общий API и CDN страниц; старый img33.imgslib.link отдаёт чужой TLS-сертификат.
    protected override Uri SystemUrl => new("https://mangalib.org/");
}
