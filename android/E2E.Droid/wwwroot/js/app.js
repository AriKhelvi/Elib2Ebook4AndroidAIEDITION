/* E2E для Android — тема, скрытие нижней панели при открытой клавиатуре, мелкие помощники
   [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.

   Темы: system (как в системе) | light | dark | amoled | eink. Выбор хранится в localStorage
   (ключ тот же, что раньше, — старые значения light/dark подхватываются), на <html> ставится
   data-theme с итоговой темой — по нему css/m3.css выбирает цвета. */
(function () {
    const THEME_KEY = 'elib-theme';
    const MODES = ['system', 'light', 'dark', 'amoled', 'eink'];
    const DARK_THEMES = ['dark', 'amoled'];
    const systemDark = window.matchMedia('(prefers-color-scheme: dark)');

    function savedMode() {
        try {
            const value = localStorage.getItem(THEME_KEY);
            return MODES.indexOf(value) > 0 ? value : 'system';
        } catch (e) {
            return 'system';
        }
    }

    function effective(mode) {
        return mode === 'system' ? (systemDark.matches ? 'dark' : 'light') : mode;
    }

    function apply(mode) {
        const theme = effective(mode);
        const root = document.documentElement;
        root.setAttribute('data-theme', theme);
        root.style.colorScheme = DARK_THEMES.indexOf(theme) >= 0 ? 'dark' : 'light';
        return theme;
    }

    // Сообщаем приложению цвет фона темы — им красятся системные панели и фон под ними (ThemeInterop.cs)
    function syncNative() {
        const theme = document.documentElement.getAttribute('data-theme');
        const background = getComputedStyle(document.documentElement).getPropertyValue('--md-surface').trim();
        if (window.DotNet && window.DotNet.invokeMethodAsync) {
            window.DotNet.invokeMethodAsync('E2E.Droid', 'ApplyNativeTheme', DARK_THEMES.indexOf(theme) >= 0, background)
                .catch(() => { });
        }
    }

    // До первой отрисовки, чтобы не мигнуть чужой темой
    apply(savedMode());

    systemDark.addEventListener('change', () => {
        if (savedMode() === 'system') {
            apply('system');
            syncNative();
        }
    });

    // Пока открыта клавиатура, прячем нижнюю панель вкладок — иначе она всплывает над клавиатурой
    function isTextField(el) {
        return el && (el.tagName === 'TEXTAREA' || (el.tagName === 'INPUT' && ['checkbox', 'radio', 'button'].indexOf(el.type) < 0));
    }
    document.addEventListener('focusin', e => {
        if (isTextField(e.target)) document.body.classList.add('kb-open');
    });
    document.addEventListener('focusout', () => {
        setTimeout(() => {
            if (!isTextField(document.activeElement)) document.body.classList.remove('kb-open');
        }, 50);
    });

    window.e2e = {
        // 'system' | 'light' | 'dark' | 'amoled' | 'eink'
        getThemeMode: () => savedMode(),
        // Запоминает выбор и возвращает итоговую тему (без 'system')
        setThemeMode: mode => {
            try {
                if (mode === 'system' || MODES.indexOf(mode) < 0) localStorage.removeItem(THEME_KEY);
                else localStorage.setItem(THEME_KEY, mode);
            } catch (e) { }
            const theme = apply(mode);
            syncNative();
            return theme;
        },
        syncNative: syncNative,
        blur: () => { if (document.activeElement) document.activeElement.blur(); },
        scrollToBottom: id => {
            const el = document.getElementById(id);
            if (el) el.scrollTop = el.scrollHeight;
        }
    };
})();
