// ============================================================================
// E2E для Android — состояние формы на главном экране между переходами по вкладкам
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

namespace E2E.Droid.Services;

/// <summary>Состояние формы на главной: переживает переходы между вкладками.</summary>
public sealed class FormState
{
    public string Urls { get; set; } = string.Empty;
    public string Login { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool RememberCredentials { get; set; } = true;
    public bool ShowAuth { get; set; }
    public bool ShowRange { get; set; }
    public bool RangeByName { get; set; }
    public string StartNum { get; set; } = string.Empty;
    public string EndNum { get; set; } = string.Empty;
    public string StartName { get; set; } = string.Empty;
    public string EndName { get; set; } = string.Empty;

    /// <summary>Хост, для которого в форму подставлены сохранённые логин и пароль.</summary>
    public string CredentialsHost { get; set; }

    /// <summary>Логин в форме подставлен из хранилища, а не введён вручную.</summary>
    public bool CredentialsAutoFilled { get; set; }
}
