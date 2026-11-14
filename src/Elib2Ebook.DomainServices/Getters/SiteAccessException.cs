namespace Elib2Ebook.DomainServices.Getters;

public enum SiteAccessReason { AuthenticationRequired, AgeConfirmationRequired, AccessDenied }

/// <summary>Подтверждённое ограничение сайта, которое не следует принимать за сбой разбора книги.</summary>
public sealed class SiteAccessException(SiteAccessReason reason, string message) : InvalidOperationException(message)
{
    public SiteAccessReason Reason { get; } = reason;
}
