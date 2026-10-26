/* E2E для Android — тема, скрытие нижней панели при открытой клавиатуре, мелкие помощники
   [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md. */
/* E2E for Android — тема (логика из app.js веб-версии) и мелкие помощники для Blazor. */
(function () {
    const THEME_KEY = 'elib-theme';
    const systemDark = window.matchMedia('(prefers-color-scheme: dark)');

    function saved() {
        try { return localStorage.getItem(THEME_KEY); } catch (e) { return null; }
    }

    function effective(pref) {
        return pref === 'light' || pref === 'dark' ? pref : (systemDark.matches ? 'dark' : 'light');
    }

    function apply(pref) {
        const theme = effective(pref);
        document.documentElement.setAttribute('data-theme', theme);
        document.documentElement.style.colorScheme = theme;
        return theme;
    }

    // Сообщаем приложению, чтобы перекрасить фон под системными панелями и значки статус-бара
    function notifyNative(theme) {
        if (window.DotNet && window.DotNet.invokeMethodAsync) {
            window.DotNet.invokeMethodAsync('E2E.Droid', 'ApplyNativeTheme', theme === 'dark').catch(() => {});
        }
    }

    apply(saved());

    systemDark.addEventListener('change', () => {
        const pref = saved();
        if (pref !== 'light' && pref !== 'dark') {
            notifyNative(apply('system'));
        }
    });

    // Пока открыта клавиатура, прячем нижнюю панель вкладок — иначе она всплывает над клавиатурой
    function isTextField(el) {
        return el && (el.tagName === 'TEXTAREA' || (el.tagName === 'INPUT' && !['checkbox', 'radio', 'button'].includes(el.type)));
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
        getThemeMode: () => {
            const pref = saved();
            return pref === 'light' || pref === 'dark' ? pref : 'system';
        },
        // Возвращает итоговую тему: 'light' | 'dark'
        setThemeMode: mode => {
            try {
                if (mode === 'light' || mode === 'dark') localStorage.setItem(THEME_KEY, mode);
                else localStorage.removeItem(THEME_KEY);
            } catch (e) { }
            return apply(mode);
        },
        effectiveTheme: () => effective(saved()),
        blur: () => { if (document.activeElement) document.activeElement.blur(); },
        scrollToBottom: id => {
            const el = document.getElementById(id);
            if (el) el.scrollTop = el.scrollHeight;
        },
        scrollIntoView: id => {
            const el = document.getElementById(id);
            if (el) el.scrollIntoView({ behavior: 'smooth', block: 'start' });
        }
    };
})();
